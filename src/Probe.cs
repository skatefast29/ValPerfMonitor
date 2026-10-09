using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace ValPerfMonitor
{
    internal enum CaptureMode
    {
        Baseline = 0,    // no hooks attached at all: the game's true frame rate with this monitor present
        Timing = 1,      // CPU self-time per mod method and per game system
        Allocations = 2, // managed memory allocated per mod method and per game system (GC pressure)
        Trace = 3,       // timing plus a full call record of slow frames, saved as flame graphs
    }

    /// <summary>
    /// The hot path. A Harmony prefix/finalizer pair is attached to every measured method (each mod's
    /// Harmony patch methods, each mod's Unity callbacks and, optionally, the game's own update loops).
    /// A call stack is kept so nested measured calls are subtracted out: every method is charged only
    /// its own "self" cost, and the probe overhead of the children it called is subtracted as well,
    /// so the monitor's own cost is never billed to a mod or to the game.
    /// Only main-thread calls are recorded; that is the thread that decides frame time.
    /// </summary>
    internal static class Probe
    {
        internal static bool Recording;
        internal static int MainThreadId;
        internal static CaptureMode Mode = CaptureMode.Timing;

        /// <summary>
        /// Cost of one probed call that lands inside the caller's measurement, in ticks or bytes for the
        /// current mode, times 1024. Fixed point because one timer tick (100 ns on Windows) is about the
        /// size of the whole probe.
        /// </summary>
        internal static long ChildOverheadFx;

        /// <summary>Reads the allocation counter for Allocations mode. Picked at startup by AllocCounter.</summary>
        internal static Func<long> ReadAllocated = () => 0;

        private static readonly Dictionary<MethodBase, int> Ids = new Dictionary<MethodBase, int>();

        // Per measured method ("source"), indexed by source id. Totals over counted frames since last reset.
        internal static int SourceCount;
        internal static int[] SourceMod = new int[1024];
        internal static long[] SourceCalls = new long[1024];
        internal static long[] SourceSelf = new long[1024];  // ticks (Timing/Trace) or bytes (Allocations)
        internal static long[] SourceMax = new long[1024];   // worst single call, same unit

        // The current frame's per-source numbers. Committed to the totals only if the frame is counted,
        // so warm-up and other excluded frames never reach the per-method results.
        private static int[] _frameCalls = new int[1024];
        private static long[] _frameSelf = new long[1024];
        private static long[] _frameMax = new long[1024];
        private static int[] _frameStamp = new int[1024];
        private static int[] _touched = new int[1024];
        private static int _touchedCount, _stamp = 1;

        // Per owner (mod or game system), accumulated over the current frame and rolled up at end of frame.
        internal static long[] ModFrame = new long[256];
        internal static int[] ModFrameCalls = new int[256];

        private const int MaxDepth = 256;
        private static readonly long[] StackStart = new long[MaxDepth];
        private static readonly long[] StackChild = new long[MaxDepth];
        private static readonly int[] StackChildCount = new int[MaxDepth];
        private static int _depth;

        // Trace mode: every probed call of the current frame, as open/close events.
        internal const int TraceCapacity = 400_000;
        internal static readonly int[] TraceSource = new int[TraceCapacity];  // source id; negative (~id) = close event
        internal static readonly long[] TraceTicks = new long[TraceCapacity];
        internal static int TraceCount;
        internal static bool TraceOverflow;

        internal static readonly MethodInfo PrefixMethod = typeof(Probe).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
        internal static readonly MethodInfo FinalizerMethod = typeof(Probe).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic);

        private static void Prefix(MethodBase __originalMethod, out int __state)
        {
            __state = -1;
            if (!Recording || _depth >= MaxDepth || Thread.CurrentThread.ManagedThreadId != MainThreadId)
                return;
            if (!Ids.TryGetValue(__originalMethod, out int id))
                return;
            __state = id;
            int d = _depth++;
            StackChild[d] = 0;
            StackChildCount[d] = 0;
            if (Mode == CaptureMode.Allocations)
            {
                StackStart[d] = ReadAllocated();
                return;
            }
            long now = Stopwatch.GetTimestamp(); // last, so our own bookkeeping isn't timed
            StackStart[d] = now;
            if (Mode == CaptureMode.Trace)
            {
                int i = TraceCount;
                if (i < TraceCapacity) { TraceSource[i] = id; TraceTicks[i] = now; TraceCount = i + 1; }
                else TraceOverflow = true;
            }
        }

        // A finalizer (not a postfix) so the stack stays balanced when the measured method throws.
        // Returning void without taking __exception means Harmony rethrows the original exception.
        private static void Finalizer(int __state)
        {
            if (__state < 0)
                return;
            long now = Mode == CaptureMode.Allocations ? ReadAllocated() : Stopwatch.GetTimestamp();
            int d = --_depth;
            long incl = now - StackStart[d];
            long self = incl - StackChild[d] - ((StackChildCount[d] * ChildOverheadFx) >> 10);
            if (d > 0)
            {
                StackChild[d - 1] += incl;
                StackChildCount[d - 1]++;
            }
            // No clamping at zero: with 100 ns timer ticks a tiny method's self time is noise around its
            // true value, and clamping each call would bias totals upward. Totals are clamped when shown.

            if (Mode == CaptureMode.Trace)
            {
                int i = TraceCount;
                if (i < TraceCapacity) { TraceSource[i] = ~__state; TraceTicks[i] = now; TraceCount = i + 1; }
                else TraceOverflow = true;
            }

            if (_frameStamp[__state] != _stamp)
            {
                _frameStamp[__state] = _stamp;
                _frameCalls[__state] = 0;
                _frameSelf[__state] = 0;
                _frameMax[__state] = 0;
                _touched[_touchedCount++] = __state;
            }
            _frameCalls[__state]++;
            _frameSelf[__state] += self;
            if (self > _frameMax[__state])
                _frameMax[__state] = self;

            int mod = SourceMod[__state];
            ModFrame[mod] += self;
            ModFrameCalls[mod]++;
        }

        internal static bool IsRegistered(MethodBase method) => Ids.ContainsKey(method);

        /// <summary>Main thread only. Registers a method before it is patched, returns its source id.</summary>
        internal static int Register(MethodBase method, int modIndex)
        {
            if (Ids.TryGetValue(method, out int existing))
                return existing;
            int id = SourceCount++;
            if (id >= SourceMod.Length)
            {
                int size = SourceMod.Length * 2;
                Array.Resize(ref SourceMod, size);
                Array.Resize(ref SourceCalls, size);
                Array.Resize(ref SourceSelf, size);
                Array.Resize(ref SourceMax, size);
                Array.Resize(ref _frameCalls, size);
                Array.Resize(ref _frameSelf, size);
                Array.Resize(ref _frameMax, size);
                Array.Resize(ref _frameStamp, size);
                Array.Resize(ref _touched, size);
            }
            SourceMod[id] = modIndex;
            Ids[method] = id;
            return id;
        }

        internal static void EnsureModCapacity(int modCount)
        {
            if (modCount <= ModFrame.Length)
                return;
            int size = Math.Max(modCount, ModFrame.Length * 2);
            Array.Resize(ref ModFrame, size);
            Array.Resize(ref ModFrameCalls, size);
        }

        /// <summary>True when no probed call is in progress. Mode switches only happen then.</summary>
        internal static bool Idle => _depth == 0;

        internal static void ResetSources()
        {
            Array.Clear(SourceCalls, 0, SourceCalls.Length);
            Array.Clear(SourceSelf, 0, SourceSelf.Length);
            Array.Clear(SourceMax, 0, SourceMax.Length);
        }

        /// <summary>End of frame: add this frame's per-source numbers to the totals if the frame counts.</summary>
        internal static void CommitSources(bool counted)
        {
            if (counted)
            {
                for (int i = 0; i < _touchedCount; i++)
                {
                    int id = _touched[i];
                    SourceCalls[id] += _frameCalls[id];
                    SourceSelf[id] += _frameSelf[id];
                    if (_frameMax[id] > SourceMax[id]) SourceMax[id] = _frameMax[id];
                }
            }
            _touchedCount = 0;
            _stamp++;
        }

        internal static void ClearFrame()
        {
            Array.Clear(ModFrame, 0, ModFrame.Length);
            Array.Clear(ModFrameCalls, 0, ModFrameCalls.Length);
            TraceCount = 0;
            TraceOverflow = false;
        }
    }
}

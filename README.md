# ValPerfMonitor

A testing tool. It measures what each installed mod and each part of Valheim itself costs, and takes its own cost out of the results. Install it in a test profile, measure, then remove it.

It answers questions like: which mod costs the most frame time? Which one makes garbage collection stutter? What was that freeze? How long does each mod add to startup? Is something leaking memory?

## Requirements
- Valheim 1.0 (Unity 6, Mono) with BepInExPack_Valheim 5.4.23 or newer.
- Client side only. It measures whatever mods are installed; it needs none of them.

## Recommended: ValAgentBridge
The monitor works on its own, and [ValAgentBridge](https://github.com/skatefast29/ValAgentBridge) works without it, but they go well together. ValAgentBridge lets an AI agent (or a script) drive the running game: start it, load a world, move the player, pin the weather. With both installed, the agent can run the monitor's tests, read the results and repeat them the same way every time, with no one at the keyboard. See [Remote control](#remote-control-valagentbridge) for the commands the monitor adds, and `examples/` for a test script to start from.

## Installation
The release zip has two parts, and both are needed:
- `plugins/ValPerfMonitor.dll`: the monitor. It shows up twice in the BepInEx log: `ValPerfMonitor` and `ValPerfMonitor early start`, a small part that starts before every other mod so that errors while mods load are counted, and the [startup split](#also-recorded) can time them when it is on.
- `patchers/ValPerfMonitor.Preloader.dll`: a tiny helper that starts before any mod loads, so each mod's startup time can be measured, the previous session's log can be saved before BepInEx empties it, and a session that didn't close normally is noticed. It patches nothing.

**Gale / r2modman:** *Import → local mod* and pick the zip. **Manual:** copy `ValPerfMonitor.dll` to `BepInEx/plugins/ValPerfMonitor/` and `ValPerfMonitor.Preloader.dll` to `BepInEx/patchers/ValPerfMonitor/`.

Use a separate test profile. The monitor attaches thousands of small hooks while it measures; it is not meant to stay installed for normal play.

## Quick start
1. Launch, load a world, go to the spot you want to test. A few seconds after you load in, the monitor attaches its hooks: the game stutters for about 10 s (those frames are never counted).
2. Press **F11**, type a label such as `all mods`, choose **Full suite**, press **Start**, and stand still for about 4 minutes.
3. Change one thing (remove a mod, change a setting), go to the same spot, run the suite again with a new label.
4. Open `BepInEx/ValPerfMonitor/compare.html` and pick the two runs.

For before/after comparisons, set the game's FPS limit to unlimited and vsync off: a frame cap hides CPU savings.

## Capture modes
Each capture records in one mode. Every capture gets its own folder and report.

| Mode | What it records | Hooks attached |
|---|---|---|
| **Baseline** | True FPS, frame-time percentiles, garbage collections (GC), render counters, world context | none |
| **Timing** | CPU self-time per mod method and per game system (about 200 vanilla update loops) | all |
| **Allocations** | Memory each mod and game system allocates per frame (what drives GC stutter) | all |
| **Trace** | Timing, plus a full flame graph of every slow frame (open at speedscope.app) | all |

The modes are kept separate on purpose. Allocation counting and trace recording each add their own overhead, so mixing them would blur the timing numbers. The **full suite** (Shift+F11) runs all four back to back at the same spot, 60 s of counted time each.

## Test panel (F11)
F11 opens the test panel. While it is open the mouse is free, the game ignores movement, camera and attack input (as when typing on a sign), and no frames are counted. Press F11 again or ✕ to close it.

- **Run a test**: choose **Full suite**, **Single test** (one mode for a set time), **Continuous recording** (until you press Stop), **Most expensive objects** or **Memory census** (both below), pick the mode, type a label (e.g. `all mods`, `without Seasons`), set the duration, and press **Start**. The panel closes and the test runs. A timed test saves its results and returns to idle when it finishes. Tests need a loaded world, and a test stops (keeping what it recorded) if you log out or die.
- **Live view**: the graph and the ranked table of mods and game systems. Click a row to see its heaviest methods.
- **Results**: captures finished this session (FPS, 1% low, heaviest mod) with a button for each report, plus **Open comparison page** and **Open logs folder**.
- **Load times & memory**: startup time (and each mod's share), every world load and logout this session, and the memory leak check.
- A **Stop** button in the panel header ends whatever is running and saves it.
- Optional **live HUD**: a small readout of the top mods while you play (toggle in the panel).

On launch it starts a continuous Timing recording by default (config `Record on launch`). Turn that off if you want it to wait idle until you start a test. While idle (nothing recording) the monitor costs next to nothing: its probes stay attached but do no work, and the once-a-minute checks pause. Only the leak check still runs every 5 minutes in a world (a short stutter; `Leak check every (seconds) when not recording`, 0 = off), plus the always-on warnings below, which cost almost nothing.

**Full play recording** (Run tab, or `Full play recording on launch`; off by default): records everything while you play normally, until you press Stop. Timing (10 min), Allocations (3 min) and Trace (2 min) take turns in blocks, a memory census runs between blocks every 30 min, and the server connection, network messages, memory and log errors are logged the whole time. It pauses while you are out of a world or a test runs, and carries on after. One combined summary, `play_summary.txt` in the session folder, is rewritten after every block: FPS by place (biome, at a base or not), the heaviest mods and game systems, the most garbage, slow frames with their causes and flame graphs, memory growth, the server connection and log errors; `play_blocks.csv` has one row per block. Heavier than the usual recording (more stutter, lower FPS in the Allocations and Trace blocks); its blocks are left out of `compare.html`. Block lengths: config section `8 - Full play recording`. It only runs in the game, never on a dedicated server.

Hotkeys still work without the panel: **Ctrl+F11** starts a new capture in the same mode, and **Shift+F11** starts or stops the full suite.

## Also recorded
- **Load times.** Launch to a settled main menu, split into steps, plus each mod's own loading time (needs `ValPerfMonitor.Preloader.dll` in `BepInEx\patchers`, included). Every world load: until you are in the world, until the loading screen is gone, and until the game stops hitching (3 s without a frame over 100 ms). Every logout.
- **Loading steps.** Startup, world load and logout split by the game's own big steps (item database, prefab registry, world file, locations, player spawn...), each including the mod patches on it, and the slow startup and world-load steps are split into each mod's own patches on them and the game's own code (`load_steps.csv`, `load_step_mods.csv`).
- **A slow mod's startup, split into its own methods.** Name mods in the config (`6 - Startup split`: mod names or GUIDs, or "the N slowest mods from the last launch") and on the next launch every method of their own code is timed while they start up: total time (with what it calls) and own time. Results in the log, `startup_methods.csv` and `perf/load_times`; the timers are removed once the main menu has settled. Bundled helper libraries (ItemManager, ServerSync...) are not timed; their time shows in the mod method that calls them.
- **Coroutines.** Mods' step-by-step background routines are timed like their other code (hook type `Coroutine`).
- **Network messages.** Every RPC by name, with the mod that handles it, how often and how many KB, plus the connection's total KB/s. In single player nothing crosses a network, but the messages still cost CPU.
- **Dedicated server connection** (on a server, whether or not you record). Once a second: network ping, ping through the game and how long the server took to answer it (its own frame time shows here), connection quality, upload and download KB/s, upload backlog (the game holds back world updates once 10 KB are waiting), world objects sent and received, and every 5 s how many of the objects and creatures around you your PC runs. **Server hitches**: no answer for over a second, marked "server busy" when the connection stayed good (the server itself froze: a world save, a mod's work) or "connection trouble". **What fills your upload**: every world object your PC sends, per type of object (sent per second, KB/s, changes per second), and the game or mod code that changed them, from a few call-stack samples a second (`upload.csv`, the visit summary). A summary per visit in the log and `server_visits.csv`; the live overlay (Full) and the F11 Live tab show a line on it; `perf/connection`.
- **Joining a server, step by step**: connecting, handshake, password, accepted, first world data, in the world, settled, plus everything the server sent meanwhile per message and mod, including each mod's config sync (`join_times.csv`, the log). Its hooks come off once you are in the world.
- **Server mode.** On a dedicated server the monitor only logs, once a second: frame rate, worst frame, players, world objects, traffic and the worst player's ping (`server.csv`), each player's ping, whether they are still joining, traffic, backlog and position every 5 s (`server_players.csv`), every frame over 100 ms and whether a world save caused it (`server_hitches.csv`), world saves, and network messages per mod (`server_network.csv`). No mod timing, no panel, no tests. Every 10 minutes the server log gets a summary with the time the monitor used (`7 - Dedicated server` settings). Files in `BepInEx\ValPerfMonitor\server_<date>\` on the server.
- **Memory leak check.** Once a minute while recording, and every 5 minutes in a world while not recording (never during tests): game objects, textures, meshes, materials, sounds, scripts per mod, and memory. A leak is a count that keeps rising while the number of loaded objects stays about the same; when game objects or materials do that, a "Possible leak" warning appears on screen and in the log.
- **Log floods.** When one message is written more than 20 times in one second, a warning names the mod and the message. The main-thread time spent writing log lines is in `frames.csv` and each capture's summary.
- **Pathfinding links.** Navigation tiles and the links between them every 10 s (`pathfinding.csv`), with a warning at 75% of Unity's limit of about 65,535 links.
- **Events.** Teleports and portals, entering and leaving a world, season changes (Seasonality) and ValAgentBridge commands (`events.csv`, `frames.csv`), named on slow frames shortly after them. Frames in which ValAgentBridge ran a command are left out of captures.
- **First-open stutter.** Every opening of the inventory, the map, the build menu, a crafting station or a container is timed, so the first open of the session (often a one-off load of icons and lists) can be compared with later ones (`first_opens.csv`, `perf/first_opens`).
- **Past session logs.** BepInEx empties its log at every launch; the monitor first saves the previous one (crashed sessions too), so earlier sessions can still be read.
- **Crashes.** If a session didn't close normally, the next launch says so (log, and a note at the main menu): whether it crashed (with Unity's crash report and the code that was running, including the first mod in it) or was ended otherwise (froze and was closed, Task Manager, PC shut down), plus the last errors. Only a real crash names a mod, and that is where it happened, not always the cause. Kept in `last_crash.txt`.
- **Errors per mod.** Errors and warnings in the log, per mod: a mod's own messages by its name, game and Unity errors by the first mod in their stack trace. Errors per minute and the worst mod are in every report and in `compare.html`. A mod throwing an error every frame costs CPU.
- **Most expensive objects** (Run tab, about half a minute): each object's per-frame code is timed per object for 20 s, then objects and prefabs are ranked, e.g. all loaded torches together, or the one creature that costs the most. Times include the mod patches on that code; the game's central update loop shows as one row, as its own work can't be split per object. The normal recording pauses while it runs and is not saved as a run (its timers add a little to every call).
- **Memory census** (Run tab, a few seconds of stutter): counts every object kept in memory, grouped by mod, by static field or component type and by object type, plus Unity's game objects by scene and root object and materials by name, splits Harmony's copies of patched methods between the mods patching them, and times a full garbage collection. Each census is compared with the previous one this session, so a mod whose objects keep growing stands out. It ends a running recording first (saved), as its forced garbage collections would change that recording's numbers.
- **Likely cause of every slow frame**: garbage collection, world save, teleport, objects loading, a recent event (season change, entering a world, a ValAgentBridge command), one mod or game system, or engine/rendering.

## Output: `BepInEx\ValPerfMonitor\`
- `compare.html`: every capture from every session (A/B test turns are left out: their result is in `ab_tests.csv`). Pick runs A and B to see FPS / 1% low changes, which mods were added or removed between them (and a warning when the game version differs), and per-mod changes. **Start here after testing.**
- `runs.csv`: the same data for Excel.
- `session_logs\`: a copy of each earlier session's BepInEx log (`LogOutput.log`), named by when that session ended, kept 14 days (`Keep past session logs`). `_crashed` or `_not-closed` in the name: that session didn't close normally. `last_crash.txt`: the report on the last one that didn't.
- `<session>\log_errors.csv`: every distinct error and warning this session, with its mod and count. `<session>\object_costs.csv`: the most expensive objects and prefabs.
- `<session>\load_steps.csv`: the loading steps per startup, world load and logout.
- `startup_times.csv`, `world_load_times.csv`, `logout_times.csv`: one row per launch / load / logout, across sessions. `join_times.csv`, `server_visits.csv`: one row per server join / visit.
- `<session>\connection.csv`: the server connection, one row per second. `<session>\server_hitches.csv`: every time the server did not answer for over a second, with the likely cause. `<session>\upload.csv`: what filled the upload, per type of object, with the code that changed it. `<session>\join_steps_<time>.csv`, `join_traffic_<time>.csv`: one join.
- `<session>\startup_mods.csv`: each mod's loading time. `<session>\startup_methods.csv`: the startup split, when it is on. `<session>\memory.csv` (with `live_after_gc_mb`, mod and game memory at its lowest point after a garbage collection, and `location_objects`, game objects in locations and dungeon rooms, which exploring loads and the leak check leaves out), `memory_by_mod.csv`: the leak check. `<session>\memory_census_<time>.txt`: each memory census.
- `<session>\events.csv`: teleports, logins and logouts, season changes and ValAgentBridge commands. `<session>\pathfinding.csv`: navigation tiles and links every 10 s. `<session>\first_opens.csv`: every opening of the inventory, map, build menu, crafting stations and containers, with its frame time.
- `<session>\<NN_Mode_label>\`, one folder per capture:
  - `report.html`: charts and sortable tables.
  - `frames.csv`: per 10 s: FPS, p50/p95/p99/max frame time, monitor cost, GC, heap, draw calls, triangles, CPU/GPU time (when the game exposes it), biome, object count, nearby building pieces, weather, log lines written and the time spent writing them, and the events in those 10 s.
  - `timeline.csv`: per 10 s, per mod or game system.
  - `summary_owners.csv` / `summary_methods.csv`: ranked totals for the capture.
  - `spikes.csv`: every frame over 50 ms with its likely cause, top 3 owners and a GC flag.
  - `network.csv` (per 10 s) and `summary_network.csv`: network messages.
  - `traces\*.speedscope.json` (Trace mode only): drag the file onto https://www.speedscope.app.

## How false readings are kept out
1. **Self time with overhead removed.** Each method is charged only for its own work. The calibrated cost of the probes on anything it calls is subtracted, so a mod is never billed for another mod's or the monitor's work.
2. **Frame time is corrected.** The monitor times its own main-thread work (overlay, rollups, logging) and subtracts it and the probe overhead from every frame. Reported FPS is the estimate without the monitor.
3. **Contaminated frames are excluded, not corrected.** Frames are left out when the monitor attached hooks, wrote reports, used more than 1 ms itself, or was warming up (5 s after hooking while the game compiles the rebuilt methods, 2 s after a mode switch).
4. **Test tooling is left out.** Frames in which ValAgentBridge ran a command (read from the bridge's own record with ValAgentBridge 1.8.0 and later) are not counted; each capture's summary lists them per command. The exception is an A/B test's own action script, whose frames are what it measures.
5. **GC is handled conservatively.** A garbage collection in a frame where the monitor was busy (or its writer thread was building a file) is excluded. All disk writing happens on a background thread.
6. **Baseline cross-check.** Compare the Baseline FPS with the Timing FPS from the same suite. A small gap means the correction worked.
7. **Self-checks at startup.** The log reports each probe's measured cost, which allocation counter was found (and whether it is exact), and a warning if the probe itself allocates memory on this runtime.

## Not measured
- Transpiler edits: they change game code in place, so they are counted but not timed.
- Background threads.
- GPU cost: no per-mod GPU number is possible, but render counters and GPU time (when available) show the graphics side.

## If the game crashes while it hooks
Hooking a method makes the game compile it again, and on Unity's Mono runtime a few methods abort the game when that happens. The monitor writes the method it is about to hook to `BepInEx/ValPerfMonitor/hooking-in-progress.txt` first. After a crash, the next launch adds that method to `skip-methods.txt` and never hooks it again, so each bad method can crash the game at most once. Leave both files alone; delete a line from `skip-methods.txt` to retry that method.

## Remote control (ValAgentBridge)
With [ValAgentBridge](https://github.com/skatefast29/ValAgentBridge) installed and the game started through it, an agent can run tests: `perf/status`, `perf/start_suite` (label and seconds per mode, for that suite only), `perf/stop`, `perf/results`, `perf/load_times`, `perf/split_startup` (which mods to split on the next launch), `perf/patches_on`, `perf/alloc_check`, `perf/slow_objects`, `perf/objects` (what is loaded right now, by prefab), `perf/worldgen_cost` (how much a world-generation mod such as Riverheim adds to the alternative biome check during world load), `perf/object_costs` / `perf/object_costs_result` (the most expensive objects and prefabs), `perf/log_errors` (errors per mod this session, log floods, time spent writing the log), `perf/memory_census` / `perf/memory_census_result` (which mods, static fields and component types keep the most live objects and MB, how much of Harmony's patch memory each mod causes, what grew since the previous census, and how long a full garbage collection takes; also in F11 > Run), `perf/start_capture` / `perf/stop_capture` (a labelled recording in one call, with its result), `perf/start_play` / `perf/play_summary` (the full play recording and its summary so far), `perf/alarms` (pathfinding links, the latest memory sample, log floods and recent events), `perf/relog_test` / `perf/relog_result` (log out and in a few times, 3 by default: errors that start only after a relog, and what grows every cycle; `examples/relog-test.json`), `perf/first_opens` (first against later opens of the inventory, map, build menu and crafting stations), `perf/session_logs` (past sessions: how each ended, their errors, search, last lines), and A/B tests with `perf/ab_test` / `perf/ab_result` (take a mod's patches off, or switch an object or every copy of a prefab off, and put it back in turns, to see whether it really costs frame time; rounds where the weather changed, the player moved or objects loaded are left out, and extra rounds are added when the answer is unclear; with `script`, a ValAgentBridge test script runs over and over in every turn, so the cost is measured while opening windows or building instead of standing still; `examples/ab-actions-example.json`). `perf/alloc_check` and `perf/worldgen_cost` refuse to run during a test, and the frames of the heavier tools are never counted. Without ValAgentBridge these do nothing. `examples/ab-test-example.json` is a test script to start from.

## Configuration
`BepInEx/config/valheimmods.ValPerfMonitor.cfg`, created on first launch. Everything has a working default; the most useful settings:
- `Record on launch`: start a continuous Timing recording when a world loads (on by default).
- `Full play recording on launch` (off): start the full play recording instead; `Timing minutes`, `Allocations minutes`, `Trace minutes`, `Memory census every (minutes)` set its blocks.
- `Live overlay`: Off / Compact / Full readout while playing (also Alt+F11).
- `Seconds per mode`: length of each step of the full suite.
- `Excluded mod GUIDs`: mods to leave unmeasured.
- `Measure mod coroutines`, `Track network messages`, `Memory check every (seconds)`: switch the extra recordings off.
- `Leak check every (seconds) when not recording` (300): the leak check while nothing records; 0 = only while recording.
- `Log flood: lines per second` (20): when one message is written more often than this, a warning names the mod.
- `Keep past session logs` (on by default) and `Days to keep past session logs` (14): on each launch, save the previous session's `LogOutput.log` in `BepInEx/ValPerfMonitor/session_logs`, named by when it ended, so earlier sessions (crashed ones too) can still be read. Copies older than the limit are deleted. Turn it off to keep only BepInEx's usual single log.
- `Split startup of these mods`, `Split startup of the slowest mods`: the startup split for the next launch (off by default).
- `Fast hooking budget ms per frame` (40) and `Hooking budget ms per frame` (8): how much time per frame goes into attaching hooks, when they all go on at once (entering a world, tests) and for small top-ups while playing. Higher = done sooner, with a heavier stutter.

## Build from source
Needs the .NET SDK (any recent version), Valheim, and a BepInEx profile (the DLLs are referenced from the game and the profile, never copied into this repository).

```
dotnet build -c Release
dotnet build -c Release -p:Deploy=true
python package.py
```
- The first builds both DLLs (`bin/Release/net472/` and `preloader/bin/Release/net472/`).
- `Deploy=true` also copies them into the profile's `plugins` and `patchers` folders.
- `package.py` (Python 3) makes the Thunderstore-format zip in `dist/`.

Paths default to a Steam install and Gale's `Default` profile. Override them on the command line:
```
dotnet build -c Release -p:ValheimDir="D:\Games\Valheim" -p:GaleProfile="C:\path\to\profile"
```
`GaleProfile` is the folder that contains `BepInEx`. The project compiles against the real game DLLs (not publicized ones), so reaching a private game member is a compile error and goes through `AccessTools` instead.

## Source layout
| Folder | What |
|---|---|
| `src/` | The plugin: `Plugin.cs` (frame loop, captures, suite), `Probe.cs` (the per-call hook), `Instrumenter.cs` (finds and hooks methods), `SafetyGuard.cs` (crash protection while hooking), `Stats.cs`, `Capture.cs`, `Report.cs` (CSV and HTML), `Overlay.cs` (F11 panel and live HUD), `LoadTimes.cs`, `LoadSteps.cs` and `LoadPatches.cs` (loading split by step and by mod), `StartupDetail.cs` (startup split and the early-start plugin), `SpawnParts.cs` and `ObjectLoads.cs` (slow spawns, loaded objects), `NetworkStats.cs`, `MemoryTracker.cs`, `AbTest.cs`, `AllocCheck.cs`, `WorldGenCost.cs`, `ObjectCosts.cs` (most expensive objects), `LogWatch.cs` (errors per mod, log floods), `Alarms.cs` (events, leak and pathfinding warnings), `UnityCensus.cs` (Unity objects in the memory census), `RelogTest.cs`, `FirstOpens.cs` (first-open stutter), `BridgeScript.cs` (runs a ValAgentBridge script for the A/B test), `SessionCheck.cs` (crash report at launch), `BridgeCommands.cs` (the perf/* commands) |
| `preloader/` | The startup-timing helper and the past-session logs and crash check (a BepInEx preloader patcher) |

## License
MIT, see [LICENSE](LICENSE).

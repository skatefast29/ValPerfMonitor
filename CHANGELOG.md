# Changelog

## Unreleased

- **Memory census** (`perf/memory_census` / `perf/memory_census_result`): counts every object reachable from static fields and from all components and ScriptableObjects, and groups them by mod, by root (one static field, or all components of one type) and by object type, with approximate MB. Then it times a full garbage collection, whose length grows with the number of live objects. Results go to the log in plain English and to `memory_census_<time>.txt`. Static fields are read straight from memory, so no mod's setup code is ever run early (a self-test checks this on every run). The work is spread over frames (about 4 s at up to 100 ms per frame) and those frames are left out of captures. The census's own bookkeeping is never counted, and the monitor's own memory is shown on a separate line.

## 1.1.0

- **Most expensive objects** (new in the F11 Run tab, and `perf/object_costs` / `perf/object_costs_result`): for 20 s, each object's per-frame code (the game's update loops with the mod patches on them, and mods' own Update) is timed per object, then objects and prefabs are ranked (all loaded torches together, the single worst creature...). Results in the log and `object_costs.csv`, with an instanceId for ValAgentBridge's debug tools. The normal recording pauses while it runs (and is not saved as a run, since the timers add a little to every call); its timers come off afterwards.
- **A/B test for objects**: `perf/ab_test` can now switch an object off and on in turns (`objectId`, `objectPath`) or every loaded copy of a prefab (`prefab`), to see whether it really costs frame time. Refuses the player, the game's core objects and anything holding mods' plugins.
- **Errors per mod**: errors and warnings in the BepInEx log are counted per mod (a mod's own log name, or for game and Unity errors the first mod in the stack trace). Each capture's report and `compare.html` show errors per minute and the mod with the most; `log_errors.csv` lists every distinct message; `perf/log_errors` for the bridge. A mod throwing an error every frame costs CPU.
- **Crash report at launch**: if the last session didn't close normally, the next launch says so in the log and on screen at the main menu, with Unity's crash report folder when it crashed, the code that was running (and the first mod in it; only a real crash names a mod), and the last errors. Saved in `last_crash.txt`; the saved session log is named `_crashed` or `_not-closed`. `perf/session_logs` lists past sessions (how each ended, errors per log name), searches them and reads their last lines.

## 1.0.5

- **Past session logs are kept** (new setting "Keep past session logs" under "3 - Logging", on by default): BepInEx empties `LogOutput.log` every time the game starts, so only the last session's log is ever there. Now each launch first saves the previous session's log in `BepInEx/ValPerfMonitor/session_logs`, named by when that session ended, including sessions that ended in a crash. Copies older than "Days to keep past session logs" (14) are deleted. Done by the preloader, before any mod loads; nothing runs while you play. Switch it off to go back to BepInEx's single log.

## 1.0.4

- Minor README fixes: the companion bridge's current name and link (ValAgentBridge), the hooking and startup split settings, and the full source file list. No changes to how the mod works.

## 1.0.3

- **Faster hooking on entering a world:** attaching all probes at once (a few seconds after you load in) now uses the fast budget too, so it takes about 9-10 s instead of 14-17 s. The game stutters briefly while it runs; small top-ups while playing still use the gentle 8 ms per frame. The setting is now called "Fast hooking budget ms per frame".

## 1.0.2

- **Startup split for slow mods:** name mods in the config (or pick "the N slowest from the last launch") and on the next launch their startup is broken down into their own methods, so you can see what inside a mod makes it slow to load. Results in the log, `startup_methods.csv` and `perf/load_times`; `perf/split_startup` sets it from ValBridge. Off by default.
- **Faster hooking in tests:** while a test suite or A/B test runs, probes are attached with up to 40 ms per frame instead of 8 (new setting "Hooking budget ms per frame during tests"). The game stutters until hooking is done; those frames were never counted anyway.
- **No repeated rebuilds:** game methods rebuilt the first time probes went on are not rebuilt again when the probes come back after a Baseline step. Later suites hook noticeably faster.
- The log now says how long hooking took and where the time went.
- Build files default to Gale's `Default` profile.

## 1.0.1

- No changes to the mod. Full changelog and source: https://github.com/skatefast29/ValPerfMonitor/blob/main/CHANGELOG.md

## 1.0.0

First public release.

- **Per-mod cost while playing:** CPU time and garbage (memory allocations) per mod and per game system, in four capture modes (Baseline, Timing, Allocations, Trace), with an F11 test panel, a live overlay, CSV logs, an HTML report per capture and a page comparing runs.
- **Slow frames explained:** each slow frame is tagged with its likely cause (garbage collection, world save, teleport, objects loading, one mod or system, engine/rendering); Trace mode records flame graphs.
- **Loading:** startup, world loads and logouts timed and split by the game's own steps, and each step split again into every mod's patches on it and the game's own code. Each mod's own loading time at startup (with the preloader).
- **Objects:** which objects are slow to spawn (and which script or mod patch on them), and what is loaded right now by prefab, including copies stacked on top of each other.
- **Network and memory:** every network message by name with the mod that handles it; object counts and memory once a minute to spot leaks.
- **Remote control with ValBridge (optional):** status, full suites, results, load times, which mods patch a method, an exact garbage check for one method, an A/B test that takes a mod's patches off and puts them back without a restart (rounds where conditions changed are left out, extra rounds added when unsure), and the cost a world-generation mod adds to world loading.
- Every run is stamped with the game version and a fingerprint of the mod list.
- Crash-safe hooking: a method that crashes the game while being hooked is remembered and skipped from then on.
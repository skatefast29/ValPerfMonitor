# Changelog

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
# Changelog

## 1.6.0

- Full play recording (F11 -> Run, off by default): Timing, Allocations and Trace take turns while you play, with memory censuses and one combined summary (play_summary.txt).
- Server: startup freezes no longer blamed on garbage collection.
- Server: the hourly asset clean-up counts as the cause when its freeze lands on the frame that asked for it.
- Server: worst ping leaves out players still joining (in server.csv too); server_players.csv marks them.
- Server: always-empty Steam columns removed from server_players.csv.
- Network messages that only had a number get their name from the mods' code.
- Server: installs or updates its patcher in BepInEx/patchers by itself (works from the next restart).
- Each session's log is also saved in that session's own folder (server_<date> or the game's session folder), next to its CSVs.

## 1.5.1

- Server: frame times come from the monitor's own clock. Unity's put the slow startup frames on the wrong row.

## 1.5.0

- The game's asset clean-up (after world load, every 20-60 min, hourly on servers) is timed and named on slow frames, in the game and on servers.
- Materials only count as a possible leak if they are still there after an asset clean-up.
- Harmony's copies of methods are freed once the monitor's probes come off, so later tests have no extra garbage collection cost.
- Memory census names the mod or menu new UI objects belong to.
- Server: slow frames say whether a garbage collection or the asset clean-up ran in them; summaries count slow frames by cause.
- Server: optional memory census once per run while nobody is on (off by default).
- The "Hooking took" line is no longer skipped when the last probe used up a frame's time.

## 1.4.1

- Server summary's worst ping leaves out each player's first 30 s (joining).
- Server summary's busiest network messages count only that summary's minutes, not the whole session.
- Server startup is reported once on its own and left out of the summaries.
- Server startup split by mod: loading steps and each slow startup frame, with every mod's patch on them (`server_startup_*.csv`).

## 1.4.0

- **Dedicated server connection.** Ping, how long the server takes to answer, connection quality, traffic, upload backlog and who runs the objects around you, once a second (`connection.csv`, `perf/connection`, overlay line).
- **What fills your upload.** World objects your PC sends to the server, per type of object, with the game or mod code that changes them (`upload.csv`).
- **Server hitches.** Every time the server stops answering for over a second, marked server busy or connection trouble (`server_hitches.csv`).
- **Join timing.** Each step of joining a server and what the server sent meanwhile, config sync per mod (`join_times.csv`).
- **Server mode.** Installed on a dedicated server, it only logs frame rate, slow frames, world saves, players' connections and network messages per mod; no mod timing.
- Received network messages show their names (ConfigSync, ZDOData...) instead of numbers where known.
- No more HarmonyX warnings every 15 s in profiles without ValAgentBridge.

## 1.3.3

- **Season marker sees every season change.** With Seasonality 3.8.5 or later it reads the season in play, so changes from the `seasonality set` command or a server sync are marked too, not only changes to the config setting.
- **No false "Possible leak" when exploring.** The leak check now only compares moments at the same spot (within 100 m), leaves out game objects in locations and dungeon rooms (which exploring loads), and gives materials that grow while exploring a note instead of a warning.

## 1.3.2

- **Log flood warning counts only lines that reach the log file.** Levels BepInEx drops (Debug in the default `LogLevels` of `BepInEx.cfg`) no longer trigger it, and the warning names the real level (info, message, warning, error).

## 1.3.1

- **Fix: possible hitch when working out which mod an error belongs to.** Every line under an error or warning was looked up as if it were a line of a stack trace, including plain text (such as the monitor's own report on the last session). Each such line made the lookup search every type of every loaded mod, 0.1-0.3 s on the main thread the first time it was seen, and logged "AccessTools.TypeByName: Could not find type named ...". Now only real stack-trace lines are looked up, with a quick lookup in each loaded assembly, and the monitor's own messages are filed under the monitor.

## 1.3.0

- **Log flood warning**: when one message is written more than 20 times in one second (setting "Log flood: lines per second"), a warning appears in the log (and on screen once you are in a world), naming the mod and the message. Before, a mod repeating an error every frame (for example 55,000 "Failed to allocate NavMeshLink" errors in one session) was only noticed afterwards in an 11 MB log. Floods are listed in `perf/log_errors`.
- **Time spent writing the log**: the main-thread time BepInEx spends writing log lines is measured: per 10 s in `frames.csv` (`log_lines`, `log_write_ms`), per capture in the log, and per minute in `perf/log_errors`. Unity's own cost of building an error comes on top, so this is a lower bound.
- **Leak warning, also during normal play**: game objects, materials and mod/game memory (its lowest point after a garbage collection, also in `memory.csv` as `live_after_gc_mb`) are compared with an earlier moment that had about as many loaded world objects; when they grew a lot while the world around you did not, a warning appears ("Possible leak"). The memory check now also runs every 5 minutes while nothing is recording (setting "Leak check every (seconds) when not recording"; 0 = only while recording), with a short stutter each time.
- **Memory census lists Unity's own objects**: game objects by scene and by root object, and materials by name (with how many are runtime copies), compared with the previous census. That shows *which* objects or materials pile up.
- **Pathfinding links**: every 10 s in a world, the navigation tiles, the links between them and the links waiting to be removed go to `pathfinding.csv`. A warning appears at 75% of Unity's limit of about 65,535 links; at the limit the game logs "Failed to allocate NavMeshLink" every frame.
- **Events**: teleports and portals, entering and leaving a world, season changes (Seasonality) and ValAgentBridge commands are kept in `events.csv`, listed per 10 s in `frames.csv`, and named on slow frames up to 3 s after them ("after season change to Winter (0.4 s)").
- **Test tooling left out**: frames in which ValAgentBridge ran a command are left out of captures, like the monitor's own work (read from the bridge's own record with ValAgentBridge 1.8.0 and later). Each capture's log summary lists them per command with the slowest frame.
- **Labelled recording in one call**: `perf/start_capture` (`label`, `mode`) and `perf/stop_capture`, which returns the result (FPS, 1% low, heaviest mod, report).
- `perf/alarms` (new): pathfinding counts, the latest memory sample, log floods and recent events.
- **Relog test** (`perf/relog_test` / `perf/relog_result`, script `examples/relog-test.json`): log out and back in a few times (`relogs`, default 3; then it stops, so later logins are left alone); after each login the monitor records a cycle (error counts, game objects, materials and a memory census). The result lists errors that first appeared after a relog, with how many in each cycle, and static fields, component types and Unity objects that grew in every cycle: the signs of a mod keeping old data across logouts.
- **A/B test while doing something**: `perf/ab_test` takes `script`, a ValAgentBridge test script run over and over in every turn (open the inventory and the map, place pieces...), instead of only standing still. Frames of that script are counted; `examples/ab-actions-example.json` is a start.
- Messages the game stamps with the time are counted as one message, so repeats group together in `log_errors.csv` and the flood warning.
- **First-open stutter** (`perf/first_opens`, `first_opens.csv`): every opening of the inventory, the map, the build menu, a crafting station or a container is timed (the frame it opened in and the two after, and the opening code itself), so the first open of the session can be compared with later ones, and with the first open after a relog. A slow first open (50 ms or more) is noted in the log.

## 1.2.0

- **Memory census** (new in the F11 Run tab, and `perf/memory_census` / `perf/memory_census_result`): counts every object kept in memory (reachable from static fields and from all components and ScriptableObjects) and groups them by mod, by root (one static field, or all components of one type) and by object type, with approximate MB. Then it times a full garbage collection, whose length grows with the number of live objects. Results go to the log in plain English and to `memory_census_<time>.txt`, which has column headers and a summary on top. Static fields are read straight from memory, so no mod's setup code is ever run early (a self-test checks this on every run). The work is spread over a few seconds of frames, which are left out of captures; a running recording is ended (and saved) first. The monitor's own memory is shown on a separate line.
  - **What grew** (leak finder): each census is compared with the previous one this session, by owner and by root, so a mod whose objects keep growing while you stand in one place stands out.
  - **Harmony's share by mod**: Harmony's copies of patched methods (the biggest single holder in a modded game) are split over the mods patching each method, with the monitor's own probes on their own line.
- **Cheaper when idle**: when a recording or test ends, the probes and the network counting stop doing any work. Before, they kept costing about 0.3Ã¢â‚¬â€œ1 ms per frame until the game closed. The once-a-minute object scan, memory check (`memory.csv`) and 10-second world sample now only run while something is recording, so idle play no longer stutters once a minute.
- **Tests stay clean**:
  - `perf/alloc_check` and `perf/worldgen_cost` refuse to run during a suite, timed test, A/B test, object costs or memory census. The frames of those tools and of `perf/objects`, `perf/slow_objects` and `perf/session_logs` are never counted.
  - Splitting slow spawns into parts no longer adds timers in the middle of a test or a Baseline capture.
  - A test stops (keeping what it recorded) when you log out or die, instead of carrying on at the main menu.
  - The full suite (Shift+F11 or the panel) needs a loaded world, like the other tests; before, it could start its Baseline step at the main menu.
- **`perf/worldgen_cost` is fairer**: the monitor's own probes on the mod's world-generator patches come off while it times them. Before, they made the mod look slower than it is.
- **A/B test turns stay out of the run history**: each turn still gets its own folder and report, but no longer adds rows to `runs.csv` and `compare.html`; the result is in `ab_tests.csv` as before.
- `perf/start_suite` no longer saves its label and seconds as your "Run label" and "Seconds per mode" settings; they apply to that suite only.
- Removed the unused "Compact" setting under "5 - Overlay" (the live overlay's own Compact mode is unchanged).

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
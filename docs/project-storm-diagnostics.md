# Project Storm performance diagnostics

The paired `manage_diagnostics` tool lives in the Python server and the Unity package, in the **profiling** group. Enable that group and approve Project Automation locally in Unity. It does not require arbitrary Debug Execution. Restart the separately configured Python MCP server after updating it; refreshing Unity reloads the embedded package. Do not copy Server into Project Storm.

## Workflow and contracts

Pass the action and an `options` object. Generated artifacts live in **Library/McpDiagnostics** (local, not source-controlled); preserve wanted captures elsewhere before deleting Library. Artifact arguments are returned filenames, not arbitrary paths.

| Action | Options and evidence |
| --- | --- |
| `capabilities` | Artifact directory and supported adapters. |
| `settings_capture` | Compact identity/counts plus JSON artifact. `include_details=true` includes full data. Captures actual pipeline/quality, resolution, dynamic scale, VSync, frame settings and blended volumes for evaluated HDRP cameras, serialized overrides/custom passes, loaded scenes/terrain tiles, build GUID and dirty-scene warning. |
| `settings_compare` | `baseline,candidate` settings artifacts. Reports changed fields. Asset references use stable GUID/local identity rather than volatile instance IDs. |
| `benchmark_start` | Unpaused Editor Play Mode; `duration_seconds` 1–600 (default 60). Run the same route and inputs during each capture. Returns immediately. Refuses an existing binary recording; saves/restores profiler state. |
| `benchmark_status / benchmark_stop` | Poll completion or end early. Domain reload, editor close, pause or leaving Play Mode ends the capture with a reason. |
| `benchmark_analyze` | `artifact,allow_large_recording=true`. Explicitly import/analyze recordings above 512 MiB; smaller recordings are analyzed automatically at stop. Analysis is synchronous, appends profiler history and can consume several GB of RAM. |
| `benchmark_report` | `artifact,offset,limit`. Every game-frame interval includes 60/90 FPS misses and missing-frame count. Summary includes percentiles and worst interval. A separate profiler_frames list contains available CPU/GPU frame timings. Full JSON and binary .raw recording remain on disk. |
| `benchmark_compare` | `baseline,candidate` benchmark artifacts. Summaries, before/after settings mismatch, and settings drift during each run. |
| `frame_breakdown` | `frame,limit`, optionally benchmark `artifact` for retained CPU-hitch context. Use a profiler_frame ID, **not** a game_frame ID. Per-thread CPU self/inclusive markers, fixed steps and CPU waits; GPU hierarchy columns where gathered. Raw .raw recordings preserve full samples beyond compact context limits. Load an older raw recording in Unity Profiler to inspect frames outside retained context. |
| `memory_objects` | `snapshot_path,offset,limit`. Ranked captured native objects and crawled managed arrays/objects. |
| `memory_references` | `snapshot_path,kind=native\|managed,index,depth=4,limit=64`. Bounded incoming-reference chains with cycles, shared paths, truncation and depth limits. Use an index from that exact capture. |
| `memory_compare` | `snapshot_a,snapshot_b,offset,limit`. Real object type/name-group growth and count deltas, plus complete diff JSON. No file-size comparison fallback. |
| `storm_diagnostics` | Loaded wind/weather/RCC/rain diagnostics with sample timestamps and measurement scope. |
| `renderer_audit` | `offset,limit`. Loaded prefab families ranked by all-LOD submesh slots, materials, LOD index reductions, shadow-capable renderers, shared textures and instancing candidates. |
| `visual_start` | Unpaused Play Mode with tagged MainCamera. `count=12,interval_frames=30`; optional `baseline` sequence replays its camera poses/cadence. |
| `visual_status / visual_stop` | Poll/cancel the sequence. Camera pose/FOV is restored after replay; scenes are not saved. |
| `visual_compare` | `baseline,candidate,offset,limit`. Before/after PNGs, absolute RGB difference PNGs and mean error. Context mismatches block pixel comparisons by default. `allow_mismatch=true` permits explicitly non-matched artistic review and preserves mismatch warnings. |

All durations are milliseconds unless named seconds. Game-frame timings use a temporary PostLateUpdate sampler and monotonic clock, restored/removed at stop without replacing other systems' player-loop callbacks. They include waits and editor stalls, are independent of Time.timeScale, exclude the first partial interval and are not CPU self-time or presented-frame telemetry. Unity's default 300-frame profiler-history limit does not truncate this full-run interval list. Profiler sample coverage is reported separately; there is no invented mapping between game-frame and profiler-frame IDs. Default pagination is 50, maximum 500 (visual comparison maximum 120).

## Interpretation and boundaries

- Editor Play Mode timings include editor/profiler overhead. They are not a standalone-player certification, presented-frame telemetry or automatic input/route replay. Every analyzed budget miss is retained in JSON; missing profiler frames are reported, not silently treated as fast frames. JSON/stack analysis runs after binary recording ends. Compact hitch context is capped at 128 frames (20 self-time samples per thread); full data remains in .raw. Monitor disk space and explicitly opt into large-recording analysis when needed.
- CPU nested timings overlap; use self time for ranking. GPU pass times come from Unity GPU profiler columns and availability flags, not draw counts or CPU waits. Unsupported APIs, disabled GPU collection and missing samples report unavailable with a reason.
- Memory adapter targets optional **Memory Profiler 1.1**, exercised against 1.1.9 / Unity 2022.3. Captures must be local .snap files inside the project or Unity's temporary cache; links/network paths are rejected. Files above 512 MiB require `allow_large_snapshot=true`. Parsing is synchronous and can freeze Unity/use several times the file size in RAM. Capture/analyse large snapshots separately from timed benchmarks.
- Native tracked bytes and crawled managed object sizes are **not measured physical/GPU residency**. Native and managed Unity-object wrappers can refer to the same logical asset. Grouping duplicate names is intentional, not persistent cross-session object identity. A reference-chain leaf is not proof of a GC root.
- Wind due/overdue/bypass counts cover the last fixed-step round-robin candidates, excluding critical/immediate receivers. Existing BudgetFailures is cumulative. LastPhysicsSeconds and WeatherSeconds share the weather clock.
- RCC bytes estimate distinct mesh-vertex, wheel-state and contact-array payload. Headers, octree nodes and residency are excluded. Rain measures each renderer's most recent CPU submission, with frame identity; it is not GPU time, blur/composite cost, or a frame-wide total.
- Renderer/shadow counts describe configuration across all LODs, not simultaneous draws. Instancing/batching candidates require shader, lightmap, property-block, motion and target-build validation.
- Visual camera replay does **not** rewind weather, tornadoes, foliage, rain RNG, input, physics or destruction. Reset the same scene/seed/clock and replay inputs externally for a matched simulation. Render-observed pose, weather and clock mismatches are flagged. A before/after PNG sequence is provided; video encoding is not required or installed.

## Existing tool corrections

`manage_profiler.memory_compare_snapshots` now reads object contents using the same adapter; it explicitly fails if the reader is missing/incompatible. For large captures use `manage_diagnostics.memory_compare` with explicit large-snapshot opt-in.

`manage_graphics.stats_get` rendering counters now return objects:
`{status:"available",value:123,unit:"Count",sample:"last_completed_frame"}`
or `{status:"unavailable",value:null,reason:"..."}`.
This intentionally changes the old numeric response contract. First read may be unavailable until a complete frame arrives. Zero is valid only when supplied by a valid sampled recorder.

## Validation and release

Python: `cd Server; uv run --extra dev pytest tests/test_manage_diagnostics.py tests/test_manage_profiler.py tests/test_manage_graphics.py tests/test_tool_annotations.py tests/test_tool_test_symmetry.py`.

Unity: run `DiagnosticsToolTests` in EditMode using Unity 2022.3. The real snapshot test requires Memory Profiler 1.1; use a small isolated test project, not a large unsaved game scene. Benchmark tests enter Play Mode and restore profiler state.

Follow AGENTS.md: validate source fork, commit/push it, sync the embedded package, verify exact mirror, and submit package/manifest/lock together through Unity Version Control. The two Storm runtime instrumentation changes must accompany this feature's project submission.

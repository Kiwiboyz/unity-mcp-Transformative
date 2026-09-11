# Project Storm designer tools

Four paired tools in the `vfx` group connect to the version 1 Project Storm Editor bridge:

| Inspection | Project Automation |
|---|---|
| `get_tornado_catalog` | `manage_tornado` |
| `get_storm_wind_catalog` | `manage_storm_wind` |

The package is portable: without the compatible `ProjectStorm.Tornado.Editor` assembly it returns an explicit unavailable result. There is no player assembly dependency or arbitrary reflection target. Unity enforces Inspection / Project Automation approval at dispatch; these tools cannot grant approval or a debug-execution lease.

Enable the `vfx` tool group and restart the Python server after installing new wrapper modules. Existing sessions do not import new modules merely because Unity refreshes. The server runs from this fork's `Server` directory; only `MCPForUnity` is distributed in Project Storm.

## Asset or saved scene edits

1. `catalog` returns paged exact asset identities; `instances` returns scene-controller GlobalObjectId identities. `describe` exposes the semantic fields and validation result.
2. Send `preview_goal` with `spec: {schema_version: 1, operations: [{target: <identity>, fields: {<semantic field>: <value>}}]}`. Optional `destination` creates a clone at an unused Assets-relative `.asset` path. Each persistent file may occur once in a goal.
3. Inspect the returned impact and candidate hash. Explicitly send `commit_goal` with that `goal_id` and exact `candidate_hash`.
4. Run the affected checks and use `capture` / `profile_start` / `profile_stop`. These collect evidence; they do not approve it.
5. `restore_goal` restores the transaction only while its committed files and metadata remain unchanged. Later edits cause a conflict instead of being overwritten. Journals live in `Library/StormV10/Goals`; retain this directory while rollback is needed.

Fields use declared names and units, typed enum values, exact object identities, arrays and explicit curve keys. Raw serialized paths, executable code, invalid references and direct qualification flags are rejected. Use the wind pair for shared-wind profiles.

## Development laboratory

Read `inspect`, `sample_batch`, `laboratory`, `cost`, `validation_impact` and exact hashed JSON `artifact` records. `control` accepts the documented Project Storm scenario commands with an exact live controller identity. This is an explicitly requested development scenario operation: reset/formation/merge change the temporary encounter and may clear round-local destruction. Recorded scrub changes presentation only; it does not rewind gameplay or destruction.

`laboratory` sets bounded tracers (32768 / 131072 / 262144), vector layers, streamlines, slices and recording. Tracers consume the production signed GPU field, never drive simulation, and share a total budget across sources. Close the window or disable tracers to release diagnostic resources.

Inspection aliases were deliberately consolidated: `inspect` returns the bounded immutable encounter/instance snapshot, `describe` the authored contract, `artifact` exact evidence or release records. Unavailable evidence is not a pass. Stage 10 release migration/freeze and human/target-laptop acceptance remain separate gated work.

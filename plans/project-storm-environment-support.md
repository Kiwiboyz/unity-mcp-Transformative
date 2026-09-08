# Plan: Project Storm Environment MCP Support

Status: MCP-side implementation in progress; Project Storm installation/acceptance (step 11) explicitly deferred
Last updated: 2026-08-16

## Goal

Extend the Project Storm Unity MCP fork with complete environment-authoring support spanning Expanse 1.7.6, HDRP Volume Profiles, Project Storm weather and storm systems, and their precipitation, lightning, raindrop, wind, audio, and surrounding-particle bindings.

The calling agent must be able to start from either a natural-language description or a supplied reference screenshot, inspect the current environment authority graph, make bounded changes, capture the result, and iteratively adjust until the result is close enough. Accepted changes must be committed to their actual authored sources: persistent `sharedProfile` assets, Expanse scene/prefab controls, weather presets, storm visual profiles, pattern assets, or managed particle/VFX bindings.

Build and synthetically validate the tooling in `G:\unity-mcp-Transformative` before installing the fork into Project Storm for real-package and visual acceptance.

## Version Policy

V1 is the complete design in this plan, not an MVP, partial release, or reduced feature set. Internal implementation may proceed through small vertical milestones, but none of those milestones is called V1 or released as V1. The first released version must satisfy the complete V1 coverage and acceptance gate in D45.

## Non-Goals

- Do not embed computer vision or an image model in the Unity MCP server. The calling agent interprets screenshots.
- Do not expose or modify Expanse renderer internals, shaders, compute kernels, or unrelated third-party VFX internals.
- Do not support the broken Expanse `TornadoCloudVolume` extension in V1. Project Storm tornado weather patterns, storm simulation, visual profiles, prefabs, and particle effects remain in scope.
- Do not use fuzzy names as destructive targets.
- Do not edit `Volume.profile` clones as persistent authored state.
- Do not auto-save scenes or prefab stages.
- Do not claim atmospheric convergence from a generic pixel-difference score.
- Do not preserve `WeatherPatternAutoGenerator` as a parallel long-term authoring system.

## Resolved Decisions

1. **D1 — Agent-side intent interpretation.** The calling agent converts text or screenshot observations into a versioned declarative environment specification. Unity owns deterministic discovery, preflight, preview, mutation, verification, and recovery.

2. **D2 — One public environment tool pair.** Replace the earlier Expanse-only public boundary with `get_environment_catalog` and `manage_environment`, both in the existing `vfx` group. Keep internal adapters separate for Expanse, HDRP Volumes, weather, storms, profiles, and VFX.

3. **D3 — Full production-surface coverage.** Follow the ModernUI coverage-matrix model. Every installed production authoring family must be supported across discovery, inspection, creation/cloning, configuration, references, authority analysis, preview, apply/rollback, goal participation, and tests. A partial or unsupported production row prevents V1 completion unless explicitly moved to non-goals.

4. **D4 — Full support means authored controls.** Cover all Expanse Creative, Advanced, and Utility authoring components, presets, profiles, and production prefabs; required HDRP environment overrides; all three Project Storm weather-authoring paths; storm patterns/features/fronts/centres; and exposed precipitation, wind, audio, lightning, raindrop, and surrounding VFX bindings. Demo/example content is inspectable reference material, not a default production source.

5. **D5 — Dynamic environment contexts.** Never assume one Global Volume or infer ownership from a folder name. An environment context identifies the exact scene or prefab stage, camera/preview conditions, exact Volume and `sharedProfile`, additional exact Volume Profile assets, Expanse hierarchy, active weather/storm controller path, and relevant VFX bindings.

6. **D6 — Stable mutation identity.** Use asset GUID plus path for assets and `GlobalObjectId` plus hierarchy diagnostics for scene/prefab objects. Names are discovery hints only. Hold no live Unity object references across MCP calls.

7. **D7 — Persistent profile rule.** Persistent authoring resolves and edits the exact `Volume.sharedProfile` asset and its VolumeComponent subassets. Report instantiated `Volume.profile` clones but never treat them as the persistent baseline. Mark every changed component and profile dirty, then save at the explicit asset boundary.

8. **D8 — Multiple-profile operations are explicit.** A specification may target one or several exact profiles, including separate sky/fog and post-processing profiles. Cross-profile preflight resolves every target before any write. Ambiguous loaded Expanse volumes fail closed rather than copying Expanse's first-match behaviour.

9. **D9 — One versioned declarative contract.** Text-led, screenshot-led, direct-edit, setup creation, weather authoring, migration, and goal workflows use `environment_spec` schema version 1. The top-level contract contains `schema_version`, `contexts`, `goals`, `target_sets`, `changes`, `authority_policy`, `preview_policy`, `iteration_policy`, and `commit_policy`.

10. **D10 — Typed semantic changes.** Changes address semantic families and stable target IDs rather than arbitrary reflection paths. Family adapters translate semantic values into bounded serialized fields, curves, object references, collections, VolumeParameters, and known VFX properties. A raw-property escape hatch is not part of V1.

11. **D11 — Upstream authority graph.** Build a live graph from authored sources to runtime outputs. When a manager or Creative component owns a downstream value, authored commits target the upstream preset/control rather than the temporary output. Direct downstream editing is normally preview-only.

12. **D12 — Three weather-authoring paths.** Support `WeatherManager.WeatherParams`/weather states, `StoryDrivenWeatherManager.StoryWeatherPreset`, and `WeatherPatternPreset` plus `StormVisualProfile` consumed by `StormDrivenWeatherManager`/`StormFront`. Detect multiple simultaneously authoritative managers as a hard conflict.

13. **D13 — Complete weather effects graph.** Model how the active weather source controls Expanse clouds/fog/sun/moon, HDRP Fog, lights, rain controllers, surrounding Visual Effects, curtain effects, snow/dust, lightning, wind, and exposed audio curves. Manage the binding and authored exposed values without attempting to generalize every third-party VFX implementation.

14. **D14 — Creative-to-Advanced authority.** Prefer Creative controls when an active Creative component drives an Advanced component. An authored request addressing a driven Advanced value fails preflight and returns the controlling Creative target and semantic alternative. Never disable a driver automatically.

15. **D15 — Full setup and asset lifecycle.** V1 may inspect, create, clone, assign, repair, adopt, and configure Expanse setups, Volume Profiles, weather patterns, visual profiles, features, and their references. Creation always requires explicit destination and context; automatic best-guess prefab/profile selection is read-only suggestion material.

16. **D16 — Version and compatibility fingerprinting.** Treat the installed local type/member signatures as authoritative. Combine package/version hints with a bounded fingerprint of required Expanse, HDRP, and Project Storm weather shapes. Unknown fingerprints remain inspectable but fail closed for uncertain mutations.

17. **D17 — Bounded semantic catalog.** Catalog status, contexts, component families, profiles, presets, prefabs, curves, reference relationships, exposed VFX bindings, setup invariants, driver mappings, and compatibility findings. Paginate/filter large results and never return raw source, asset bytes, shaders, or unbounded serialized dumps.

18. **D18 — Agent-orchestrated convergence.** A visual goal is a recoverable multi-call protocol, not one long Unity command. The loop is begin, inspect/baseline, preview bounded changes, capture, agent-evaluate, repeat, then commit, restore, or cancel.

19. **D19 — Recoverable goal state.** Store capped goal metadata under `Library/MCPForUnity/EnvironmentGoals`, keyed by project and goal ID. Record exact targets, compatibility fingerprint, baseline hashes/values for controlled fields, allowed scope, preview ownership, iteration journal, proposed final spec, and lifecycle status. Do not store screenshot bytes or vendor assets there.

20. **D20 — Convergence criteria.** Combine agent visual judgment, explicit user constraints, serialized-state verification, maximum iterations, per-iteration change bounds, plateau/no-improvement termination, and failure/cancellation restoration. Return why the goal stopped and what remains different.

21. **D21 — Explicit controlled Play Mode.** Goal sessions may use controlled Play Mode only when the goal explicitly enables it and local Project Automation approval is active. Record whether the goal entered Play Mode; stop it automatically only if the goal owns it. Refuse to attach to an unrelated active Play Mode session unless explicitly requested.

22. **D22 — Reuse editor and camera tools.** The agent uses existing `manage_editor` play/pause/stop operations and existing camera/screenshot tooling. `manage_environment` tracks the preview lifecycle and applies runtime preview values; it does not duplicate general editor or screenshot control.

23. **D23 — Runtime preview isolation.** In Play Mode, preview against runtime scene instances and instantiated Volume profiles, never persistent assets. In Edit Mode, preview changes remain within an Undo/baseline-controlled staging scope and are restored before an authored commit. `commit_goal` is allowed only in a stable Edit Mode state.

24. **D24 — Preview/commit separation.** Every goal iteration produces a candidate spec and evidence without saving authoritative assets. The accepted final spec is re-preflighted against current hashes and applied fresh to authored sources. This prevents runtime observations from being mistaken for persistent state.

25. **D25 — Goal restoration.** `restore_goal` restores all controlled preview values and stops only goal-owned Play Mode. `cancel_goal` restores and closes the session. If exact restoration cannot be proven, return `recovery_required` with the baseline/change journal rather than claiming success.

26. **D26 — Stable public inspection operations.** `get_environment_catalog` exposes `status`, `catalog`, `describe`, `inspect_context`, `inspect_authority`, `inspect_profile`, `inspect_preset`, `preflight`, `coverage`, `resolve_target`, `goal_status`, and `refresh_cache`. Resolution may suggest fuzzy candidates, but preflight/mutation requires exact confirmed identity.

27. **D27 — Stable public mutation operations.** `manage_environment` exposes `apply`, `repair`, `begin_goal`, `preview_goal`, `commit_goal`, `restore_goal`, `cancel_goal`, and `migrate_legacy_weather`. `apply` and goal commits accept exactly one inline spec or validated `Assets/...` spec path.

28. **D28 — Project Automation authority.** `get_environment_catalog` declares `Inspection`; `manage_environment` declares `ProjectAutomation`. Play Mode still requires an explicit goal preview policy and the existing approved editor-control path. No arbitrary methods, code execution, unrestricted reflection, or unrestricted VFX property names are allowed.

29. **D29 — Generator replacement.** `WeatherPatternAutoGenerator` is a legacy system to be fully replaced by `manage_environment`. Do not retain generator and MCP authoring as parallel sources of truth.

30. **D30 — Authored weather destination.** Migrate `Assets/WeatherPatterns/AutoGenerated` into a stable authored hierarchy under `Assets/WeatherPatterns/Authored`, organized by semantic pattern/profile/feature families. Perform moves with Unity AssetDatabase operations that preserve GUIDs and validate every inbound reference afterward.

31. **D31 — Generator deprecation sequence.** First inventory and validate the existing generated graph; then create source specifications/manifests, move assets while preserving GUIDs, verify all `StormFront` lists and profile/feature/prefab references, disable the generator menu entry, run acceptance, and finally remove the obsolete generator. Do not delete it before migration verification.

32. **D32 — Managed source specifications.** Tool-created or migrated weather graphs receive versioned `*.environment.json` specifications and last-applied hashes. They become the reproducible authoring source for MCP-managed weather data. Existing unrelated Volume/Expanse assets remain user-owned unless explicitly adopted.

33. **D33 — Adoption and drift.** Support unmanaged one-off edits, explicit adoption with current state as baseline, and managed-spec application. If both a managed spec and its Unity assets changed since the last apply, fail on conflict by default and offer explicit `prefer_spec` or `prefer_project` resolution with a full change report.

34. **D34 — No destructive automatic cleanup.** Migration, repair, and replacement only remove or relocate assets that were explicitly preflighted and confirmed as legacy/managed. Never infer deletion ownership from a folder name alone. Source control remains the final recovery layer for post-save failures.

35. **D35 — Coordinated transaction boundary.** Preflight every scene, prefab, profile, preset, and reference before mutation. Use one Undo group for scene/prefab-stage changes, an in-memory journal for ScriptableObject/VolumeComponent changes, and one explicit asset-save boundary. Post-apply reread every intended value and reference.

36. **D36 — Honest partial failure.** Scene/prefab and asset changes cannot be truly atomic on disk. Return `complete`, `rolled_back`, `partial_failure`, or `recovery_required`, with exact changed targets, saved assets, dirty scenes, goal state, and recovery steps. Never auto-save a scene.

37. **D37 — Responsibility-based editor layout.** Add two thin Unity tool entry points under `Editor/Tools/Environment` with focused catalog/context, identity, profile, Expanse, weather, storm, VFX, authority, specification, transaction, goal, migration, and coverage services. Avoid a monolithic `ManageEnvironment` implementation.

38. **D38 — Thin Python wrappers.** Add `get_environment_catalog.py` and `manage_environment.py`, using existing instance routing and mutation readiness helpers. Python validates the public request shape and forwards deterministic commands; semantic work remains in Unity.

39. **D39 — No compile-time optional dependency.** Continue using reflection, `SerializedObject`, `SerializedProperty`, and bounded type discovery. Reuse/extract the existing internal reflected Volume primitives. Do not add Expanse, HDRP, or Project Storm assemblies to the MCP editor asmdef.

40. **D40 — Cache and invalidation.** Cache type fingerprints, schemas, context/profile indexes, prefab/preset indexes, and authority mappings per editor domain. Invalidate on domain reload, relevant asset imports/moves/deletes, scene/stage changes, or explicit refresh. Goal commits revalidate hashes regardless of cache state.

41. **D41 — Dynamic project evolution.** Scene/profile lists, weather assets, and controller assignments may change during development. Discovery must be live and identity-based. No Project Storm scene name, Tornado Valley path, preset count, or profile count becomes a hardcoded contract assumption.

42. **D42 — Licensed-asset-free synthetic tests.** MCP tests use synthetic test-only types and assets that reproduce required serialization and driver shapes without copying Expanse implementations or artwork. Real Expanse and Project Storm acceptance occurs only after the fork is installed into Project Storm.

43. **D43 — Paired fork deployment.** Treat the Python server and Unity package as a paired build from the same pinned commit and environment-schema version. Do not assume their currently different package version strings prove compatibility.

44. **D44 — Sequenced installation.** Build schemas, wrappers, catalog, contexts, goal state, transactions, synthetic adapters, and tests in the MCP fork first. Installation into Project Storm is an explicit later checkpoint, followed by real type coverage, migration dry-run, controlled Play Mode validation, and visual acceptance.

45. **D45 — Complete V1 gate.** V1 is complete only when every production coverage row is supported; synthetic Python/EditMode suites pass; the affected Unity assemblies compile; multi-profile exact targeting, authority routing, goal preview/restore/commit, generator migration, Undo/recovery, security, and bounded-response tests pass; and Project Storm acceptance validates representative clear, story-weather, storm, tornado-pattern, precipitation, and lightning contexts.

## Environment Specification V1

The public specification is declarative and semantic. Exact field names can evolve during implementation without changing these ownership rules.

```json
{
  "schema_version": 1,
  "contexts": [
    {
      "id": "context-id",
      "scene": { "asset_guid": "...", "path": "Assets/..." },
      "volume": { "global_object_id": "..." },
      "shared_profiles": [{ "asset_guid": "...", "path": "Assets/..." }],
      "camera": { "global_object_id": "..." }
    }
  ],
  "goals": [
    {
      "id": "goal-id",
      "description": "dark rotating supercell with readable foreground",
      "constraints": [],
      "target_context_ids": ["context-id"]
    }
  ],
  "target_sets": [],
  "changes": [],
  "authority_policy": {
    "prefer_upstream_authored_source": true,
    "on_conflict": "fail"
  },
  "preview_policy": {
    "mode": "edit_or_controlled_play",
    "allow_play_mode": true
  },
  "iteration_policy": {
    "max_iterations": 6,
    "stop_on_plateau": true
  },
  "commit_policy": {
    "save_assets": true,
    "save_scenes": false
  }
}
```

`changes` are typed semantic operations such as setting an Expanse atmosphere concept, editing a weather curve, assigning a visual profile, configuring an exposed precipitation binding, creating/cloning a profile, or adopting a weather graph. Each adapter publishes its supported change schema through the catalog.

## Initial Foundation Milestone (Not a Release)

Use one small vertical milestone to prove the architecture in the synthetic MCP test project before expanding every adapter. This milestone is not V1 and is not considered a releasable version:

1. Register `get_environment_catalog` and `manage_environment` in the `vfx` group with correct capability metadata.
2. Discover two synthetic environment contexts containing same-named but different Volume Profile assets.
3. Resolve the exact `sharedProfile` and report a deliberately instantiated `profile` clone without selecting it.
4. Model one Creative-to-Advanced driver and one upstream weather-preset-to-Expanse/VFX path.
5. Begin a recoverable goal, record the baseline, apply two preview iterations, inspect the journal, and restore successfully.
6. Re-preflight and commit the accepted spec to the exact persistent profile and upstream weather preset.
7. Verify changed values/references, dirty state, no scene save, and the full returned journal.
8. Exercise cancellation, hash drift, ambiguous context, missing approval, and partial-failure paths.

This proves the architecture end to end before expanding every family adapter. Work then continues through the complete implementation sequence, real Project Storm installation, generator replacement, full production coverage matrix, and D45 acceptance gate before V1 is complete.

## Old-System / Migration Strategy

Use full replacement, not parallel systems.

1. Catalog every asset under `Assets/WeatherPatterns/AutoGenerated`, its GUID, type, dependencies, and inbound references.
2. Run a dry migration that proposes stable destinations and reports collisions or broken references.
3. Generate/adopt versioned environment specifications from the current authored values.
4. Move patterns, visual profiles, features, and required prefabs into `Assets/WeatherPatterns/Authored` through AssetDatabase operations that preserve GUIDs.
5. Re-scan `StormFront` pattern lists, initial patterns, feature references, visual-profile references, and prefab bindings.
6. Mark the generator obsolete and disable its menu action so it cannot recreate competing outputs.
7. Validate representative storm types and severities in controlled Play Mode.
8. Remove `WeatherPatternAutoGenerator.cs` only after migration and runtime acceptance are green.

Rollback before the final deletion is source-control restore plus the migration journal. After migration, MCP-managed environment specifications and authored Unity assets are the supported source of truth.

## Implementation Sequence

1. Public Python schemas, wrappers, group registration, and characterization tests.
2. Unity tool entry points, exact identity model, compatibility fingerprint, context/profile catalog, and bounded responses.
3. Environment specification parser/validator and complete read-only preflight.
4. Shared Volume Profile service and persistent component-subasset handling.
5. Expanse family catalog and adapters, including Creative/Advanced relationships and setup/preset/prefab lifecycle.
6. Weather authority adapters for WeatherManager, StoryDrivenWeatherManager, StormVisualProfile, WeatherPatternPreset, StormFront, and feature definitions.
7. VFX/particle/lightning/raindrop/wind/audio binding adapters.
8. Transaction coordinator, journals, conflict hashes, adoption, and recovery statuses.
9. Goal store, Edit Mode preview, controlled Play Mode preview, convergence metadata, restoration, and commit.
10. Synthetic full-family coverage matrix and tests.
11. Explicit MCP installation into Project Storm and real compatibility/coverage scan.
12. Legacy generator migration dry-run, reviewed migration, generator retirement, and real-project acceptance.
13. Close every remaining partial/unsupported production coverage row before declaring V1 complete.

## Verification

### Python

- Tool registration, `vfx` grouping, annotations, literal actions, inline-vs-path exclusivity, schema validation, instance routing, mutation readiness, and structured error preservation.
- Goal lifecycle request validation and no accidental retries of non-idempotent mutations.

### Synthetic Unity Edit Mode

- Optional-package detection and unknown fingerprint behaviour.
- Context discovery, exact GUID/GlobalObjectId identity, same-name ambiguity rejection, and cache invalidation.
- `sharedProfile` persistence, instantiated-profile reporting/rejection, component-subasset dirty/save handling, and Play Mode commit rejection.
- Every synthetic Expanse/weather/storm/VFX adapter row in the coverage matrix.
- Creative/Advanced and upstream-weather authority routing.
- Curves, enums, collections, references, VolumeParameters, exposed VFX bindings, and prefab/profile creation/cloning.
- Goal begin/preview/status/restore/cancel/commit, hash drift, plateau metadata, session expiry, and crash/reload recovery.
- Transaction rollback, `recovery_required`, Undo grouping, no auto-scene-save, and bounded responses.
- Migration dry-run, GUID-preserving move plan, collision detection, inbound-reference validation, and idempotence.

### Project Storm acceptance after installation

- Enumerate all loaded and asset-only environment contexts without folder/name assumptions.
- Complete real Expanse and Project Storm production coverage matrices.
- Modify and restore an exact scene-specific sky/fog profile and a separate post-processing profile.
- Validate direct Expanse authoring with no weather manager.
- Validate WeatherManager, story preset, and storm-pattern authority paths.
- Run controlled Play Mode previews for rain, surrounding particles, lightning, wind, storm curtains, and representative tornado-pattern behaviour.
- Run a screenshot-led multi-iteration goal and verify only the accepted final spec reaches authored assets.
- Dry-run and execute the weather-generator migration, verify GUID/reference integrity, then confirm the retired generator cannot recreate competing data.
- Confirm source-control diff contains only intended MCP installation, environment tooling/migration, managed specs, and accepted environment changes.

## Assumptions

- **Verified:** Project Storm uses Unity 2022.3.62f2, HDRP 14.0.12, and local Expanse 1.7.6.
- **Verified:** Project Storm contains many scene-specific Volume Profile assets; a singular Global Volume assumption is invalid.
- **Verified:** `Volume.profile` creates/uses a clone while `sharedProfile` identifies the persistent asset.
- **Verified:** Expanse Creative and several Advanced components execute in Edit Mode and can overwrite downstream values.
- **Verified:** Project Storm has three distinct weather-authoring/controller paths and storm profiles that drive Expanse, HDRP fog, lights, and VFX.
- **Verified:** `WeatherPatternAutoGenerator` deletes and recreates generated assets and is unsafe as a parallel authoring source.
- **Verified:** Existing MCP architecture supports dynamic reflection, thin Python wrappers, Project Automation approval, tool grouping, mutation readiness, and screenshot/editor control.
- **Unverified until installation:** Exact real-project reflection coverage, controlled Play Mode visual stability, all current scene controller assignments, and generator migration reference integrity. These are explicit Project Storm acceptance gates rather than silent assumptions.

## Implementation Risks and Red-Team Results

1. **Wrong profile is edited.** Covered by explicit environment contexts, exact asset/object identity, and ambiguity rejection.
2. **A runtime clone is mistaken for authored state.** Covered by the strict `sharedProfile` rule and commit rejection in Play Mode.
3. **Creative or weather systems overwrite MCP changes.** Covered by the upstream authority graph and preview-only downstream edits.
4. **Multiple weather managers compete.** Covered by live writer discovery and hard preflight conflict.
5. **Goal preview leaks into project assets.** Covered by runtime clone/staging isolation and a fresh authored reapply at commit.
6. **Controlled Play Mode triggers unrelated gameplay or is stopped unexpectedly.** Covered by explicit opt-in, goal ownership, readiness checks, and refusal to attach automatically.
7. **Visual tuning oscillates indefinitely.** Covered by iteration caps, bounded deltas, plateau detection, journals, and explicit stop reasons.
8. **Generator recreation erases tuned weather.** Covered by full replacement, managed specifications, migration, menu retirement, and later generator removal.
9. **Migration breaks GUID references.** Covered by AssetDatabase moves, dry-run inventory, inbound-reference scans, post-move validation, and source-control recovery.
10. **Scene and asset writes partially succeed.** Accepted platform limitation; covered by save boundaries, journals, rollback where possible, honest recovery statuses, and no scene auto-save.
11. **Full coverage expands indefinitely.** Accepted because the user explicitly selected the ModernUI-style full-support gate. The implementation remains phased, but V1 is not declared complete with partial production rows.
12. **Project evolution invalidates hardcoded mappings.** Covered by live catalogs, fingerprints, schema versions, exact identities, cache invalidation, and fail-closed unknown semantics.
13. **A monolithic tool becomes unmaintainable.** Covered by adapter/service boundaries and thin public entry points.
14. **Catalog responses expose too much or become unusable.** Covered by semantic summaries, filtering, pagination, caching, and bounded output tests.

## Open Questions

None that materially change the plan. Field-level adapter details, default iteration counts, catalog pagination sizes, and final authored folder substructure are reversible implementation decisions and should be settled during implementation against tests and real-project discovery.

## Next Question

Keep this plan at the implementation-ready boundary, or continue exploring non-core edge cases before implementation.

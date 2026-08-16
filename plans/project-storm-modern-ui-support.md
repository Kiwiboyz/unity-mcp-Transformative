# Plan: Project Storm Modern UI Support

Status: Fork implementation and synthetic validation complete; Project Storm installation and real-package acceptance pending
Last updated: 2026-08-16

## Goal

Extend the Project Storm Unity MCP fork so Codex and Rider Codex CLI can inspect, create, configure, compose, and validate UI built with Michsky Modern UI Pack 5.5.16 in Unity 2022.3.62f2. Requests may be text-led or derived by the agent from a supplied reference image. Generated UI must follow normal Unity `RectTransform`, Canvas, responsive layout, margin, and safe-area requirements.

## Non-Goals

- Do not replace the existing UI Toolkit-oriented `manage_ui` tool.
- Do not add assembly definitions to or otherwise restructure the vendor-owned Modern UI Pack.
- Do not modify vendor prefabs or the vendor-default `MUIP Manager.asset` in place.
- Do not embed computer vision in the Unity MCP server; the calling agent interprets reference images into a declarative UI specification.
- Do not copy paid Modern UI Pack source, prefabs, artwork, fonts, or other licensed assets into the public MCP repository.
- Do not expose arbitrary reflection or arbitrary public-method execution through the Modern UI tools.

## Resolved Decisions

1. **D1 — Full production catalogue, demos retained as examples.** Support every installed production Modern UI component family. Catalogue demo scenes, prefabs, wiring, and scripts as `example_only` reference material rather than default production choices.
2. **D2 — Complete V1 delivery.** V1 is not complete until the full installed production component surface is supported. A representative vertical slice may be used only as internal implementation and verification order.
3. **D3 — Versioned declarative contract.** Use one versioned screen specification for image-led and text-led work. `get_modern_ui_catalog` supplies catalogues, schemas, diagnostics, and read-only preflight; `manage_modern_ui` applies the specification.
4. **D4 — Preflight plus atomic mutation.** Resolve and validate the entire request before mutation, report all preflight errors together, apply under one Undo group, and roll back an unexpected partial failure. Return per-node results and warnings.
5. **D5 — Stable IDs and explicit ownership.** Track MCP-managed nodes with stable local IDs and ownership modes. Support `create`, `merge`, `replace_managed`, `validate`, and `repair`; never replace unrelated developer-authored content.
6. **D6 — Demo content is inspectable reference material.** An explicit `include_examples` query exposes demo topology, configuration, manager relationships, and event patterns for the agent to learn from without treating demo-only content as production output.
7. **D7 — Use normal Project Storm folders.** Save assets into the existing structure with no `MCP` or `Generated` folders: UI prefabs under `Assets/Prefabs/UI`, textures under `Assets/Textures/UI`, explicitly requested scripts under `Assets/Scripts/UI`, and themes/configuration under `Assets/ScriptableObjects/UI`. Allow validated existing `Assets/...` subfolder overrides.
8. **D8 — Uniform adapter contract.** Every production component adapter supports discover, describe, create, configure, refresh, and validate. Simple types may use the bounded generic implementation; complex types add semantic behavior without changing the public schema.
9. **D9 — Explicit Project Storm theme ownership.** Reuse an existing theme or duplicate the vendor manager asset into the normal Project Storm ScriptableObjects structure, give it a unique name, and assign it explicitly to connectors. Never alter the vendor asset or introduce a competing Resources asset named `MUIP Manager`.
10. **D10 — Coverage matrix is a V1 gate.** Generate a version-specific matrix covering discovery, creation, configuration, references, collections, events, refresh, validation, and tests. Every installed production component must be `supported`; `partially_supported` and `unsupported` fail V1 completion.
11. **D11 — Unity-Inspector-compatible event binding.** Permit normal persistent bindings to project/package runtime objects when the public instance method and signature are UnityEvent-compatible. Reject editor-only, static, generic, compiler-generated, MCP-policy, host-sensitive, reflection, process, filesystem, and network targets. Preflight without invoking methods and edit only the addressed listener.
12. **D12 — Stable collection-item identities.** Model dropdown, selector, list, menu, window, chart, and similar collection entries as ordered nodes with stable IDs. Support merge, replace, append, and remove semantics; labels and indexes are not identities.
13. **D13 — Agent-side visual interpretation.** Codex converts an optional reference image into the same declarative specification. Text overrides conflicting image inference; images are not imported by default; actual asset references must resolve to validated `Assets/...` paths; screenshot validation supports correction passes.
14. **D14 — Responsive layout contract.** Inherit existing Canvas Scaler settings when editing. New full-screen canvases default to Scale With Screen Size at 1920x1080 and 0.5 width/height match. Use anchors, offsets, padding, spacing, layout groups, and a runtime safe-area fitter; validate 16:9, 16:10, ultrawide, 4:3, and narrow profiles.
15. **D15 — Three-way merge conflicts.** Keep a normal co-located `<ScreenName>.modernui.json` manifest with stable IDs and last-applied hashes. Preserve unowned content, fail on conflicting managed edits by default, and offer explicit `prefer_spec` or `prefer_project` resolution plus reported change details.
16. **D16 — Contextual style resolution.** Resolve presentation in this order: explicit specification, explicit style source, compatible nearby controls, compatible controls in the same screen/prefab, assigned UIManager theme, selected source-prefab defaults, then package defaults. Report the source of every inferred style group.
17. **D17 — Override flags and values are semantic units.** Preserve theme inheritance when a donor has color/font overrides disabled; copy compatible local values when overrides are enabled. Treat `overrideColors`, `overrideFonts`, `useCustomTextSize`, `useCustomIconSize`, `useCustomContent`, `isPreset`, and family-specific equivalents with their governed values.
18. **D18 — Nearby structure is part of style.** Inspect compatible peers for prefab variant, component/child structure, manager connector, content mode, colors/fonts/sprites/materials, transitions, animation, layout, navigation, and interaction conventions. Never copy labels, gameplay data, IDs, events, or object-specific references unless requested.
19. **D19 — Explicit style modes.** The specification supports `match_nearby`, `theme`, `copy`, and `explicit`, with independent inheritance controls for colors, fonts, sizing, layout, animation, and structure. Existing-UI additions default to nearby matching; context-free screens default to theme.
20. **D20 — Style validation warns without homogenizing.** Report inconsistent managers, override modes, missing assets, partial override sets, unintended package defaults, and unusual peer differences. Only runtime-breaking conditions are errors; intentional differences are not silently repaired.
21. **D21 — Natural and fuzzy style references.** Accept approximate names plus optional hierarchy, Canvas, component, scene, prefab-stage, and exact identity hints. Normalize case, whitespace, punctuation, separators, common UI suffixes, and minor spelling errors after exact resolution fails.
22. **D22 — Contextual candidate ranking.** Rank fuzzy candidates by name/token similarity, compatible component family, Canvas/container, active scene or prefab stage, hierarchy proximity, semantic role, and project/production status. A clearly superior result may be selected automatically.
23. **D23 — Ambiguity stops mutation.** Automatically use and disclose a clear typo match. A tie or low-confidence result returns the best three paths and component types before mutation; never accept Unity enumeration order as a decision.
24. **D24 — Fuzzy authority is bounded.** Automatic fuzzy selection is allowed for read-only style donors. Destructive targets require exact or previously confirmed identity. Parent and event targets may use fuzzy suggestions only when their exact resolved identity passes preflight. Default search stays in the active scene or prefab stage.
25. **D25 — Persist resolved style identity.** Store the donor GlobalObjectId, path, requested/actual names, and style signature. Future updates resolve stable identity, then path, then fuzzy fallback; report fallback use and stop on ambiguity.
26. **D26 — Version and structural fingerprint.** V1 is pinned to Modern UI Pack 5.5.16 and verifies expected production types, properties, lifecycle methods, and prefab families. Exact fingerprints may proceed with a missing version label warning; affected-family or broad incompatibility blocks unsafe mutation while diagnostics remain available.
27. **D27 — Adapter-owned refresh allowlists.** Apply serialized data, collections, themes, and overrides, then invoke only explicitly registered edit-safe refresh methods and revalidate. Runtime behavior methods such as open, close, animate, ripple, or selection changes are never used as implicit refresh operations.
28. **D28 — Explicit transaction boundary and recovery.** Hold no live object references across calls; reject mutation during compile/reload/stage transitions; validate before saving; write the manifest only after success; roll back Undo changes and operation-created assets on failure; report complete, rolled_back, or recovery_required.
29. **D29 — Preserve prefab connections.** Instantiate with PrefabUtility, retain vendor links and ordinary overrides, prefer permitted children or Project Storm variants, and never unpack automatically or apply overrides back to vendor assets. Return `unpack_required` and require explicit permission when unavoidable.
30. **D30 — Complete V1 acceptance gate.** Require an all-supported coverage matrix, a normal Project Storm testing scene/spec covering every family, typo-resolved contextual style, themes/overrides, collections/events, nested prefabs, responsive/safe-area behavior, merge/repair/Undo/security checks, asset-boundary and licensing checks, Python tests, focused EditMode tests, and affected compilation without a full player build.
31. **D31 — Both roots are in scope; installation is sequenced.** Planning and implementation may inspect and work in the MCP fork and Project Storm. Build and synthetically verify the fork first, then install it and create normal Project Storm `Assets` integration fixtures. The current absence of an installed package is a known state, not an access restriction; any package-link edit outside `Assets` remains an explicit installation checkpoint.
32. **D32 — Validate before a single commit; no full snapshots.** Use one Undo group for scene/prefab-stage work. For direct prefab assets, mutate and validate `LoadPrefabContents` in memory, then save exactly once. Keep only a compact changed-property/node journal, track operation-created assets individually, and do not auto-save scenes. On a save/integrity failure return `recovery_required`; Plastic/Git is the accepted recovery path for the rare post-write problem.
33. **D33 — GUID-owned manifests without automatic file movers.** Store the owner asset GUID in each co-located manifest and label it for bounded discovery. Resolve by GUID, report non-co-location and orphans, and allow explicit repair relocation. Duplicate or conflicting owner GUIDs stop mutation; deleting an owner does not silently delete its manifest.
34. **D34 — Proprietary introspection is summarized.** Return version, type/property/event/method metadata, constraints, paths/categories, summarized structural roles, and findings. Never return raw vendor source, full serialized dumps, asset bytes, or bulk reconstruction material; public-fork tests use synthetic fixtures.
35. **D35 — Discovery and responses are bounded.** Cache fingerprints, schemas, prefab indexes, and style metadata until domain reload or relevant asset changes. Scope nearby search, paginate/filter catalogues, return only top fuzzy candidates, support deterministic refresh, and performance-test large synthetic hierarchies.
36. **D36 — Responsibility-based code layout.** Add two thin Unity tool entry points under `Editor/Tools/ModernUI`, focused catalogue/spec/adapter/style/manifest/transaction/event/layout/prefab/theme services, family adapters, a runtime safe-area fitter, two Python wrappers, and matching test modules. Avoid another monolithic `ManageUI` implementation.
37. **D37 — Stable public operations.** `get_modern_ui_catalog` exposes status, catalog, describe, inspect, preflight, resolve_style_source, coverage, and refresh_cache. `manage_modern_ui` exposes apply, repair, create_theme_copy, and remove_managed. Apply accepts exactly one inline spec or validated `Assets/...` spec path, schema version 1, create/merge/replace_managed mode, target, conflicts, destinations, and declarative nodes.
38. **D38 — Existing UI ownership modes.** Existing Project Storm UI remains valid. Support explicitly generated/managed nodes, explicit adoption with the current state as baseline, and one-off unmanaged edits under Undo. Never auto-adopt a complete screen; repair and replacement affect only generated or explicitly adopted content.
39. **D39 — Licensed-asset-free synthetic tests.** Public tests use synthetic test-only `Michsky.MUIP` types and prefabs covering serialization, collections, events, overrides, connectors, and lifecycle patterns without reproducing vendor implementations or artwork. Real 5.5.16 coverage and acceptance run only after fork installation into Project Storm.
40. **D40 — Full implementation sequence.** Build schemas/wrappers; catalogue/fingerprint/coverage; smart resolution; ownership/transactions/prefabs; responsive layout/safe area; generic conversion; every family adapter; themes/collections/events; synthetic tests; scoped verification; explicit Project Storm installation; real acceptance scene; then resolve every partial/unsupported matrix row before V1 completion.

## Architecture Baseline

1. Keep the integration in this MCP fork. It may be deliberately coupled to Project Storm and Modern UI Pack 5.5.16 because this is the team's local fork.
2. Preserve the assembly boundary: the MCP editor assembly must discover and manipulate `Michsky.MUIP` components through Unity-supported asset/serialization APIs plus tightly bounded type discovery or reflection. Do not require a compile-time reference that would force changes to the vendor package.
3. Add two built-in MCP tools:
   - `get_modern_ui_catalog` as an `Inspection` capability for availability, version, prefab/component discovery, schemas, presets, and diagnostics.
   - `manage_modern_ui` as a `ProjectAutomation` capability for prefab-backed creation, composition, configuration, event binding, layout, validation, and repair.
4. Keep both tools built-in, with normal Python wrappers, because this fork's dynamic custom-tool registration excludes built-ins and these tools need stable first-class schemas.
5. Cover the complete installed component family through a hybrid adapter model:
   - specialized semantic adapters for complex and frequently used components;
   - a generic serialized-property schema for the remaining installed `Michsky.MUIP` components and future additive fields;
   - deny-by-default handling for unsupported types, properties, converters, and method calls.
6. Create UI prefab-first using the installed Modern UI prefabs, retain prefab links where practical, group each mutation under Unity Undo, and write generated reusable assets only to Project Storm-owned folders.
7. Model layout declaratively with anchors, pivot, offsets/margins, size constraints, anchored position, layout groups, padding, spacing, safe area, sibling order, Canvas sorting, and Canvas Scaler reference-resolution settings. Do not use transform scale as a substitute for responsive layout.
8. Treat image guidance as an agent-side input: Codex converts the image and accompanying text into the same declarative screen specification accepted for text-only work. Use existing screenshot/camera capture for visual iteration.
9. Theme changes require explicit ownership. Duplicate the Modern UI manager/theme asset into a Project Storm-owned location before mutation; never silently change the package default.
10. Enforce the fork's existing security policy:
    - inspection is read-only;
    - all creation and mutation requires the Unity-side Project Automation approval;
    - imported image/asset references are limited to validated `Assets/...` paths;
    - reflection is limited to the `Michsky.MUIP` namespace and explicit supported operations;
    - event targets and methods are validated and allowlisted rather than freely invoked.
11. Keep proprietary package content out of the public repository. Tests may use schemas, type names, mocks, and locally discovered fixtures, but not redistribute paid assets.
12. Verify with focused editor/unit tests and scoped Unity or .NET compilation. Do not run a full Unity player build solely to validate this tooling.

## Project Evidence

- Project Storm installs Modern UI Pack as an Asset Store package at `Assets/Modern UI Pack`, version 5.5.16, under namespace `Michsky.MUIP`; it is not a UPM package and has no package-owned assembly definition.
- Installed prefab categories include buttons, dropdowns, input fields, modal and movable windows, window managers, list views, notifications, progress indicators, sliders, switches, toggles, tooltips, charts, icons, context menus, selectors, scrollbars, spinners, and supporting components.
- Project Storm already references Modern UI components in production scripts including main-menu, new-game, loadout, inventory, save-selection, HUD, headquarters, and round-detail flows.
- The existing MCP `manage_ui` surface is aimed at UI Toolkit/UXML/USS, so merging Modern UI semantics into it would conflate two different UI systems.
- The fork's `AGENTS.md` requires explicit capability classification, Unity-side Project Automation approval for mutations, no arbitrary reflection/execution, `Assets/...` import boundaries, and scoped verification.

## Assumptions

- Verified: Modern UI Pack 5.5.16 and its prefab/component families exist in the Project Storm `Assets` tree.
- Verified: Unity 2022.3 editor APIs provide the `AssetDatabase`, `PrefabUtility`, `SerializedObject`, `SerializedProperty`, Undo, Canvas, and `RectTransform` facilities needed for editor-side construction.
- Verified: the MCP fork can host built-in inspection and Project Automation tools without changing its transport or authorization model.
- Verified: the fork contains separate editor and runtime assemblies, so a package-owned runtime safe-area fitter can be implemented without putting editor code in a player assembly.
- Verified: Project Storm currently does not load the local fork; installation is intentionally sequenced after fork implementation and synthetic verification.
- To verify in implementation: GlobalObjectId and manifest fallback behavior across scene/prefab rename, move, duplication, and nested-prefab replacement.
- To verify in implementation: multi-aspect isolated layout evaluation reproduces Unity layout behavior closely enough to detect the promised overflow and clipping failures.

## Open Questions

None that block implementation. Installation into Project Storm is an explicit later implementation checkpoint because the project does not currently load the fork.

## First Slice

V1 delivers the complete installed production component surface. Internally, establish the shared catalogue, schema, transaction, ownership, and layout pipeline with a representative responsive screen containing a button, data-bearing control, modal, and window manager; then complete every remaining adapter and coverage-matrix row before calling V1 ready.

## Old-System / Migration Strategy

This is an additive integration. Existing hand-authored Project Storm Modern UI screens remain valid, and existing `manage_ui` behavior remains unchanged. The new tools must be able to inspect and safely edit existing Modern UI hierarchies without forcing migration. Generated assets live beside—not inside—the vendor package.

## Implementation Risks

- Version drift can invalidate serialized-property mappings; catalog/version diagnostics and explicit unsupported-field responses must make this visible.
- Generic reflection could expand into unsafe or brittle authority; keep it bounded to installed Modern UI types, serialized data, explicit converters, and allowlisted lifecycle calls.
- Prefab variants and nested prefab instances can be unintentionally unpacked or overridden; define preservation and override rules before implementation.
- Responsive layouts can look correct at one resolution while failing elsewhere; validation must exercise multiple target aspect ratios and safe-area constraints.
- Event wiring can create broken persistent calls or expose unintended methods; validate targets, signatures, and allowed binding forms.
- A public fork can accidentally redistribute licensed assets through fixtures or examples; keep tests asset-free or local-discovery based.
- Accepted risk: without a full asset snapshot, a rare successful disk write followed by a newly discovered integrity problem cannot be automatically restored. The design minimizes this with preflight, in-memory validation, one save, a compact change journal, and source-control recovery.
- Co-located manifests can become organizationally separated from moved owners; GUID resolution and explicit repair prevent identity loss without adding an always-running file mover.
- Catalogue and contextual-style scans can become slow or token-heavy; bounded scopes, caches, filters, pagination, and synthetic performance tests are required.
- Project Storm currently has no installed fork, so real licensed-package acceptance cannot run until the explicit installation phase.

## Verification

- Catalog reports the installed version, supported prefab/component families, and actionable diagnostics when the pack is missing or incompatible.
- Creation retains intended prefab relationships, records Undo, writes only under approved Project Storm-owned `Assets/...` paths, and does not mutate vendor defaults.
- Configuration covers all installed component families through a specialized or generic serialized adapter and returns precise unsupported-property errors.
- Re-running or repairing a declarative screen does not silently duplicate hierarchy objects or destroy user-authored content.
- Layout validation checks anchors, offsets, Canvas Scaler settings, safe-area behavior, and representative aspect ratios.
- Security tests prove inspection versus Project Automation enforcement, reflection/type boundaries, event-binding validation, and asset-path restrictions.
- Scoped editor/unit tests and affected assembly compilation pass without a full Unity player build.

## Next Question

Install the local fork into Project Storm at the explicit package-link checkpoint, then run the real Modern UI Pack 5.5.16 acceptance matrix under `Assets` before declaring V1 ready.

## Implementation Handoff

- Added `get_modern_ui_catalog` (Inspection) and `manage_modern_ui` (ProjectAutomation), with matching Python MCP wrappers.
- The integration uses bounded namespace/type discovery, generic serialized-property configuration, allowlisted refresh, typo-tolerant style donor resolution, manifest ownership, conflict policies, safe-area layout support, project-owned theme duplication, and guarded persistent event binding.
- Direct prefab targets are edited through `PrefabUtility.LoadPrefabContents` and saved once after in-memory validation; ordinary scene work is one Undo group and does not auto-save scenes.
- Synthetic public tests deliberately use test-only `Michsky.MUIP` fixtures. Focused Unity 2022.3.62f2 EditMode validation passed 3/3, and focused Python wrapper validation passed 4/4.
- No paid Modern UI Pack assets or source were copied into the fork. Project Storm has not been modified or package-linked during this phase.

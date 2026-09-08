# Project Storm MCP Equipment Authoring Architecture Plan

## Status

Ready for implementation. The ThinkTank review resolved the authoring model, source boundaries, family presentation, transaction policy, and Alpha migration stance. The implementation begins with two proof-gated integration checks: runtime registry authority and reusable thumbnail rendering.

## Architecture Verdict

Adopt paired read-only `get_equipment_catalog` and ProjectAutomation-gated `manage_equipment` tools. Their Unity-side service owns every Project Storm invariant and all Unity asset mutation.

## Confidence

Medium-high. The local MCP fork already supplies the wrapper/Unity transport, authorization categories, local model import, and an editor thumbnail workflow. The remaining uncertainty is integration evidence rather than a product or architecture choice.

## Goal And Context

Provide Project Storm-specific MCP tooling that can inspect equipment authoring state and, with local Unity Project Automation approval, create fully registered equipment and performance-part content end to end.

The tooling must support three equipment sources:

1. An existing equipment prefab.
2. An imported local or marketplace model that has already passed through the existing approved model-import workflow.
3. A declarative composition from approved optimized prop prefabs under `Assets/Prefabs/Props` and safe primitives.

It must create or update the variant prefab, module, family membership, registrations, thumbnail, provenance record, and validation result as one deliberate authoring operation.

Marketplace acquisition is a prerequisite generic-import action. `manage_equipment` never searches, downloads, or licenses marketplace content; it consumes only the resulting project asset identity and recorded provenance.

## Prior Investigation Context

Local verified:

- The MCP server uses thin Python wrappers to route named commands to Unity-side C# handlers.
- Existing environment tooling already separates read-only discovery from approval-gated mutation.
- New Unity tools can declare `Inspection` and `ProjectAutomation` capabilities, and the fork blocks unclassified extension tools.
- `import_model_file` imports local model files and the game’s equipment thumbnail builder writes a transparent sprite and assigns `Equipment.icon` to the prefab. The new reusable thumbnail service will instead assign the player-facing `EquipmentFamily.icon` for true families.
- Equipment modules require registry membership in `EquipmentDatabase`; performance parts require registry membership in `SaveManager.allPerformanceParts`.

## Existing Project Context Or Starting Assumptions

Project Storm and the MCP fork are local, separately versioned workspaces. The authoritative game model will be the family/variant contract defined in the paired plan. MCP must use Unity’s editor APIs for all Unity asset mutations; Python must not write Unity YAML directly.

## Architecture Drivers

- A simple natural-language request must produce durable, registered content rather than disconnected imported assets.
- Discovery must be read-only and sufficient to prepare deterministic mutation requests.
- Mutations must honor the local Project Automation approval boundary and return stable approval errors if disabled.
- The system must only compose from project-local, catalogued optimized props rooted at `Assets/Prefabs/Props` and declared primitives; it must not execute generated arbitrary C#.
- The catalog must describe how approved props can be safely combined: category/tags, bounds, renderers and materials, anchor points, collider state, dependencies, and allowed composition roles. It must not treat unoptimized source meshes (including `Assets/Cargo/Meshes`) as composition inputs.
- Every mutation needs an auditable result, preflight diagnostics, and clear ownership of created or updated assets.

## Architecture Options

### Option A: Expose generic MCP tools only and let agents orchestrate them

Agents would chain generic model import, prefab manipulation, component add, asset creation, and thumbnail calls.

This maximizes reuse but leaves Project Storm registration, family invariants, and rollback to fragile agent reasoning. Reject for end-to-end content creation.

### Option B: One broad `manage_equipment` mutation tool plus one catalog tool

Expose `get_equipment_catalog` for inspection/preflight and `manage_equipment` for all content mutations. Use versioned declarative specifications and named actions such as create/update/import/compose/validate.

This matches the established environment-tool pattern and is the recommended option.

### Option C: A custom Unity-only editor window with no server wrapper

This would be useful for manual users but does not provide the requested MCP capability. It may be a later UI consumer of the same Unity authoring service, but is not sufficient on its own.

## Recommended Architecture

Adopt **Option B** with three layers:

| Layer | Responsibility |
| --- | --- |
| Python MCP wrappers | Stable schemas, typed tool metadata, request normalization, transport, and read-only/mutation annotations. |
| Unity Project Storm handlers | Validate schema against live project state; create Unity assets/prefabs; register content; invoke thumbnail generation; return structured outcomes. |
| Shared Unity equipment-authoring service | Game-specific invariant enforcement reused by MCP handlers and future editor UI; it is the only component that changes registries or creates family/variant assets. |

The public tool boundary is deliberately small:

- `get_equipment_catalog`: inspection-only status, catalog, search, describe templates, inspect assets/families/variants, resolve optimized Props-prefab candidates, propose compatible prop/primitive compositions, and preflight a proposed equipment or performance-part specification.
- `manage_equipment`: Project Automation actions for create/update/import/compose/thumbnail/validate/repair. It accepts exactly one versioned inline specification or an Assets-relative specification path.

`import_model_file` and marketplace import remain generic lower-level tools. The caller completes that approved import first; `manage_equipment` accepts its returned Assets-relative path/GUID and owns the resulting Project Storm equipment lifecycle.

## System Boundaries

| Boundary | Owns | Must not own |
| --- | --- | --- |
| Generic import tools | Bringing external/local model files into project assets | Project Storm module/family registration |
| Equipment catalog tool | Read-only live discovery and preflight | Asset or scene mutation |
| Equipment management tool | Project-approved orchestration request | Arbitrary code execution or self-approval |
| Shared Unity authoring service | Family/variant creation, prefab composition, registration, thumbnail invocation, validation | Transport protocol and natural-language interpretation |
| MCP Python layer | API contract and routing | Direct Unity YAML modification or business-rule duplication |

## Technology Choices

Use the existing FastMCP Python server, Unity editor C# tool discovery, and stdio-first local transport. Reuse the existing Project Automation authorization system. Reuse `AssetDatabase`, `PrefabUtility`, the existing model import pipeline, and the existing thumbnail rendering logic through an explicit reusable Unity service rather than automation of the editor window UI.

Use a JSON, versioned equipment specification. The specification describes intent and selected safe assets; it does not describe executable behavior. Composition permits only:

- Assets resolved by an exact catalog identity from the optimized `Assets/Prefabs/Props` source root (or another explicitly approved catalog root).
- A fixed allowlist of primitives with bounded dimensions, transforms, materials, and collider rules.
- A declared prop/primitive composition graph with explicit transforms, names, anchors, and safe material references/overrides.
- Approved equipment templates/components with documented fields.

The Unity-side preflight service must inspect every selected prop and primitive together before mutation. It validates bounds, renderer/material dependency closure, allowed components, hierarchy cycles, mount-wrapper fit, and any declared attachment relationship. It returns a proposed composition for review; it never silently chooses or mutates arbitrary props.

## Binding ThinkTank Decisions

### Content kinds and request contract

`manage_equipment` accepts one versioned, discriminated specification with these initial content kinds:

- `family_equipment`: one player-facing `EquipmentFamily` and one or more physical `EquipmentModule` variants.
- `legacy_equipment`: an exact-ID singleton module for content that must remain outside family ownership.
- `handheld_equipment`: an exact-ID handheld prefab/module; it does not enter vehicle family entitlement.
- `performance_part`: one registered `PerformanceObject` with a typed part schema.

Every create/update request carries stable logical IDs, canonical output paths, catalog revision, operation ID, and expected target revision. Create fails on an ID or canonical-path collision. Update, deprecate, repair, and replacement fail on revision drift unless the caller explicitly requests a reviewed conflict action. Operation IDs make safe retries idempotent; names and display labels never do.

`get_equipment_catalog` returns supported schema versions, approved source assets, template schemas, primitive/material rules, registry candidates, existing content identities, compatibility/reachability information, provenance, operation status, and preflight diagnostics. It remains Inspection-only even when preflighting a mutation spec.

### Approved visual-source model

`Assets/Prefabs/Props` is the only initial prop source root. The catalog exposes individual reviewed prefabs from that root, not an unrestricted filesystem browse. It excludes `Assets/Cargo/Meshes` and any other unapproved source roots.

The catalog describes each approved prop with a stable catalog ID, Unity GUID/path, category and composition roles, bounds, renderers/materials, collider state, optional anchors, dependency closure, and safe nesting status. It may recommend compatible prop-plus-primitive assemblies, but generated specs must still name every selected input and transform explicitly.

Primitives are limited to a catalog-owned allowlist of bounded shapes, dimensions, transforms, materials, and collider rules. Imported visual cores are geometry-only: unsafe scripts, cameras, lights, animation controllers, arbitrary colliders, and unapproved nested prefabs are rejected. The dependency validator rejects missing, cyclic, cross-family, or out-of-catalog asset references.

### Family variants, prefab topology, and thumbnails

True families use one family-owned, versioned nested visual core. Each physical mount variant is a mount-ready wrapper prefab that references that core and owns its exact `Equipment`/`MountableEquipment` root contract, variant ID, mount/vehicle compatibility, priority, local pose, and only approved mount-adapter overrides. A wrapper must contain exactly one authoritative mountable runtime target.

The shared core prevents visual drift; its updates enumerate every affected wrapper and require an explicit rebinding/review. Wrapper updates may modify only tool-owned nodes and fields. A detected human-owned child or override stops automatic reconstruction and returns a manual-resolution path.

`EquipmentFamily.icon` is the sole player-facing thumbnail for a true family. The service generates per-variant technical preview artifacts for authoring/result inspection but does not assign them as competing runtime `Equipment.icon` assets. Legacy singleton and handheld content retain their existing exact-prefab icon contract. A registered family needs either a validated generated family icon or an explicit existing manual family icon; thumbnail failure otherwise blocks registration. A visual-core revision invalidates a generated thumbnail unless regenerated in the same transaction or explicitly preserved as manual art.

### Templates and player-facing semantics

The initial catalog supports the current project template types only through documented, typed fields: generic vehicle mountables, cargo behavior, handheld behavior, currently declared Doppler equipment, and every current `PerformanceObject.PerformanceType`. It never permits arbitrary component additions, arbitrary serialized field writes, or generated C#.

Each template declares required prefab hierarchy/components, safe editable fields, required dependencies, and a behavior acceptance scenario. The MCP must not claim a gameplay capability that the project does not currently verify. In particular, a Doppler template is reported as a visual/mountable result until a functional sensor acceptance test proves an active radar contract.

Shop-visible family equipment requires explicit publication state and price. `shop`, `hidden`, and `internal` are distinct states; a shop-visible item cannot rely on default zero pricing. Performance-part specs declare the intended vehicle/stat effect and preflight returns predicted typed deltas; release verification applies the part to a representative compatible vehicle.

### Registry authority and mutations

The system does not hard-code a prefab name as a registry target. A project-owned authoring-registry configuration records the exact asset GUID/path and component/field identities for the approved `EquipmentDatabase` and `SaveManager` registries. `get_equipment_catalog` discovers candidates and reports all scene/prefab overrides. Mutation is blocked until the configuration identifies one proven authority; it never mirror-writes candidate manager prefabs.

Persistent mutations run only in a stable editor state: not Play Mode, not an unstable import/domain reload state, and not against a transient scene instance. The Unity service performs resolve and preflight, reserves IDs/paths, stages generated assets, validates the full asset and dependency closure, assigns family/module links, creates the thumbnail, commits registry references last, refreshes live caches, validates a clean lookup by ID, saves, and records the result.

Before its first `AssetDatabase` mutation, every operation writes a durable journal with operation ID, request hash, target revisions, lease, planned paths, created asset identities, provenance, registry before-state, and stage checkpoints. Locks are expiring manifest-backed leases. Recovery reports incomplete operations and requires explicit validated repair or cleanup after a domain reload; it never blindly replays a create request. In-process failure restores serialised registry state and deletes only transaction-created, tool-owned assets. Manual, adopted, and imported source assets are never automatically deleted.

Update defaults to deprecation: deprecated content is hidden from new authoring/shop selection but remains registered and resolvable until reference-free validation permits an explicit purge. Family membership, exact variant IDs, and performance IDs are treated as durable once released. Alpha does not require player-save migration or automatic legacy conversion; legacy conversion is an explicit manifest-backed action only.

### Validation and release gate

Preflight and release validation reject duplicate/colliding IDs, canonical path collisions after Windows-safe normalization, stale source/dependency revisions, invalid template fields, invalid prefab-root contracts, missing material/texture closure, incompatible mount/vehicle/priority combinations, unreachable requested vehicle slots, broken registry authority, thumbnail failure, unfinished operations, and invalid performance-part effects.

The release gate has three layers:

1. Python wrapper, routing, schema, and authorization tests. `get_equipment_catalog` must remain discoverable without approval; `manage_equipment` must produce the standard `approval_required` result when Project Automation is disabled.
2. Unity EditMode tests for catalog discovery, preflight, transaction recovery, generated prefab/module/family links, registry/cache refresh, thumbnail ownership, and performance lookup.
3. A Project Storm integration fixture proving a family appears once in shop with its icon/price, resolves to the intended physical variant for a real vehicle slot, rejects incompatible slots, mounts/unmounts successfully, and survives save/load. A performance fixture proves the declared part effect on a compatible vehicle.

## Implementation Proof Gates

1. Trace configured boot scenes and prefab instances to identify and configure the real runtime registry authority. Current serialized evidence contains multiple manager prefab candidates and does not justify a name-based default.
2. Extract the HDRP thumbnail capture path into a non-UI service and prove alpha, framing, Sprite import, and `EquipmentFamily.icon` assignment parity.
3. Prove the MCP package/handler deployment path in Project Storm: live tool discovery, schema compatibility, authorization dispatch, and a clean-domain registry lookup must work in the actual target project.

## Licensing And Cost Evidence

Local verified: the recommended solution reuses the project’s existing local MCP fork, Unity editor APIs, and in-project assets. It introduces no paid service or new vendor commitment.

Marketplace model import remains subject to its existing provider account, license, and token requirements. The equipment authoring tool must retain the generic importer’s provenance/licensing result when it uses that source; it must not make licensing claims on the caller’s behalf.

## Vendor And Reputation Evidence

No new vendor is proposed. Existing marketplace import is an optional lower-level dependency and must remain replaceable by local-file import or project-local props.

## Data Ownership, Privacy, And Compliance

All created content lives in project-owned Unity assets. Tool calls remain local through the approved MCP transport. External file import must continue to use the Unity-approved local root policy, and the authoring tool must never widen that root or approve it itself.

## Operational Constraints

- Inspection works without Project Automation approval; mutation returns the existing stable `approval_required` response when approval is absent.
- Asset creation must be Unity-side and use `Undo`/asset operations where applicable.
- A preflight must identify exact target paths, required registration assets, ID conflicts, missing prop references, invalid mount capabilities, unsupported behavior templates, and thumbnail eligibility before mutation.
- A mutation result must return created/updated assets, stable IDs, registry changes, thumbnail output, warnings, and a validation summary.
- The request must state whether to create one mount variant or all explicitly listed variants. It must never silently generate all variants.
- Failure handling must avoid leaving partially registered content. The authoring service should either validate before mutation or return explicit remediation/cleanup data for a failed atomic sequence.

## Rejected Alternatives

- Python writing `.prefab` or `.asset` YAML: unsafe and incompatible with Unity serialization/import behavior.
- One generic tool chain executed only by an agent: insufficient invariant enforcement.
- Arbitrary component selection/field setting or generated C#: expands trusted automation into debug execution and violates the local security model.
- A mutation tool that treats a universal mount as a wildcard: conflicts with the game-domain family plan.

## Risks And Mitigations

| Risk | Mitigation |
| --- | --- |
| Partial asset/registry creation | Preflight all references and registration targets; service returns a structured transaction report and supports repair/validation. |
| Asset provenance is unclear | Catalog records source path and importer provenance; marketplace imports retain source/license metadata supplied by the existing importer. |
| Generic model has unusable scale/materials | Reuse model import normalization; preflight and thumbnail stage surface renderer/bounds diagnostics. |
| Composition creates invalid hierarchy | Restrict inputs to catalogued optimized Props prefabs, allowlisted primitives, and declarative transforms; preflight the full composition graph, dependency closure, bounds, and mount fit before creation. |
| New handler bypasses policy | Both Unity mutation handler and wrapper are explicitly classified ProjectAutomation; tests verify denied dispatch and batch enforcement. |
| MCP/server and game model drift | Version the specification and expose a catalog status with supported schema/template versions. |
| Shared core changes every variant | Version cores, enumerate dependent wrappers, require explicit update scope, and invalidate generated thumbnails until regenerated. |
| Generated content is not player-usable | Validate real fleet/slot reachability, publication state, shop representation, mount lifecycle, and behavior effects in Project Storm integration fixtures. |
| Editor reload interrupts a transaction | Persist a checkpointed operation journal and expiring lease before the first mutation; surface explicit repair/cleanup rather than replaying blindly. |
| Human content is overwritten or deleted | Track tool-owned versus adopted/manual artifacts; fail on unmanaged wrapper edits and purge only reference-free tool-owned outputs. |

## Resolved Decisions

- Marketplace assets use the approved generic import flow first; normal equipment management has no network, credential, or licensing authority.
- The initial prop root is `Assets/Prefabs/Props`; `Assets/Cargo/Meshes` is excluded as unoptimized source content.
- Prop-prefab composition and bounded primitives are both supported and validated together.
- Multiple mount variants use a family-owned shared visual core with mount-ready wrapper prefabs, not copied visual hierarchies.
- A family owns the canonical runtime/shop icon; physical variant previews are technical artifacts only.
- One family unit is a single concurrently mountable physical unit. The game decides the concrete variant from the selected slot and compatibility data; the player does not buy separate mount forms.
- New runtime authoring is family-native. Alpha does not require migration of existing player saves or automatic conversion of existing singleton content.

## Delivery Order

1. Run the three implementation proof gates and record the configured registry authority/deployment contract.
2. Add schema/result models, Python wrappers, tool metadata, routing, and authorization coverage.
3. Add the Project Storm Unity-side catalog, registry configuration, typed templates, source/dependency inspection, and preflight service.
4. Add family/variant, legacy/handheld, and performance-part authoring transactions with journal, recovery, and cache refresh.
5. Extract thumbnail generation and wire canonical family-thumbnail ownership.
6. Add composition proposal/creation from approved Props plus bounded primitives.
7. Add EditMode, Python, and real Project Storm integration fixtures; complete the release-gate matrix.

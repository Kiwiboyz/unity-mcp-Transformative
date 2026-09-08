# Project Storm local-fork requirements

This fork is being hardened for local Codex and Rider Codex CLI use with Project Storm. The detailed design and rollout record is in [plans/project-storm-local-hardening.md](plans/project-storm-local-hardening.md).

## Project Storm package distribution

- `MCPForUnity/` in this fork is the sole source of truth. Its Project Storm release mirror is `C:\Users\Jordan\Documents\Transformitive Games\Project-Storm\Packages\com.coplaydev.unity-mcp`.
- Never edit the embedded Project Storm mirror directly. Make package changes here, validate them, commit and push this fork, then run `Project-Storm\Tools\Sync-ProjectStormMcpPackage.ps1 -Mode Sync` and verify it with the script's default `Verify` mode.
- The embedded mirror contains only `MCPForUnity/`. Do not copy `Server/` into the Unity project; developers who run MCP tools configure that server separately from their local fork checkout.
- Before submitting through Unity Version Control/SVN, submit the embedded mirror and Project Storm's `Packages/manifest.json` plus `Packages/packages-lock.json` together.

## Security and usability policy

- Preserve the intended workflow: Codex/Rider may inspect, edit scripts, modify scenes and assets, and add Project Storm-specific tools.
- Use **stdio** as the normal Project Storm transport. Treat HTTP as debug-only; remote-hosted operation is out of scope unless a separate security design is approved.
- Do not present arbitrary C# execution as a sandbox. `execute_code` must be disabled by default and require a locally approved, time-limited Debug Execution session.
- Project mutation is deliberate trusted automation. It requires a Unity-side **Project Automation** approval once per Unity session; approval survives domain reload and clears when Unity closes.
- A denied operation must return a stable, human-readable `approval_required` response that tells the agent which local Unity approval is needed. Agents must ask the developer rather than retrying or self-enabling a capability.
- Agents may call `get_mcp_policy_status` to inspect non-sensitive policy state; they must still ask the developer to change a local Unity approval.
- Enforce the policy in Unity at every dispatch path, including batch execution. Python tool groups and normal per-tool toggles are visibility/customization controls, not authorization.

## Adding tools

- Treat every MCP tool as a paired change: add a Python wrapper under `Server/src/services/tools/` and its matching Unity handler under `MCPForUnity/Editor/Tools/<Domain>/`. The wrapper owns the MCP schema, input validation, group, and approval annotations; the handler owns Unity-side work.
- Keep the tool name, input contract, tool group, and capability/approval level aligned across both halves. A wrapper without a handler, or a handler without a wrapper, is incomplete.
- Add focused Python and Unity EditMode coverage for every new tool. When testing locally, install this fork's `MCPForUnity/package.json` and run this fork's `Server` directory; do not validate against the public release package or server.
- New tools must declare a capability class when the policy infrastructure is introduced:
  - `Inspection` for read-only tools.
  - `ProjectAutomation` for normal Project Storm scene, asset, and script tools.
  - `DebugExecution` for immediate arbitrary C# execution.
  - `HostSensitive` for process/package control, external filesystem access, external network actions, or comparable workstation authority.
- Normal Expanse and scene tooling should use `ProjectAutomation`; do not create a manual central allowlist that makes ordinary tool development painful.
- Host-sensitive tools require explicit local opt-in. Missing or ambiguous high-impact classification must fail closed.
- MCP calls must never be able to raise their own profile, grant an execution lease, or change security policy.

## Data, filesystem, and supply chain

- Telemetry is off by default for Project Storm. Do not send raw exception text, project paths, tool parameters, or results to telemetry.
- Default logs contain metadata only; payload diagnostics require an explicit session-only local debug choice and redaction.
- Keep `Assets/...` imports working. Reject UNC, device, and network paths; external local imports require a Unity-approved local root.
- Run against the reviewed local fork/server source. Do not introduce moving `main`/`beta`, unversioned package references, silent updates, or shell-built server launch commands.

## Verification

- Add focused EditMode tests for policy defaults/migration, normal and batch dispatch enforcement, execution leases, approval responses, and external-import boundaries.
- Run affected Unity EditMode and Python tests. Do not use a full Unity player build solely for ordinary script or hardening verification.

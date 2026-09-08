"""Project-automation wrapper for Project Storm environment authoring and goal sessions."""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import send_mutation


@mcp_for_unity_tool(
    group="vfx",
    description=(
        "Applies versioned Project Storm environment specifications, manages reversible visual goals, "
        "and migrates legacy generated weather assets. This is Project Automation: preflight with "
        "get_environment_catalog first and require local Unity approval before mutation. "
        "Persistent changes always target authored assets; preview_goal never saves them."
    ),
    annotations=ToolAnnotations(title="Manage Environment", destructiveHint=True),
)
async def manage_environment(
    ctx: Context,
    action: Annotated[Literal[
        "apply", "repair", "begin_goal", "preview_goal", "commit_goal",
        "restore_goal", "cancel_goal", "migrate_legacy_weather",
    ], "Environment mutation action."],
    spec: Annotated[dict[str, Any] | None, "Inline environment_spec version 1."] = None,
    spec_path: Annotated[str | None, "Assets-relative JSON environment_spec path."] = None,
    goal_id: Annotated[str | None, "Goal identifier for preview, commit, restore, or cancel."] = None,
    execute: Annotated[bool | None, "Execute a destructive migration after dry-run preflight."] = None,
    confirm_play_mode: Annotated[bool | None, "Explicitly allow controlled Play Mode preview for this request."] = None,
    conflict_policy: Annotated[Literal["fail", "prefer_spec", "prefer_project"] | None, "Managed-spec drift policy."] = None,
    assessment: Annotated[dict[str, Any] | None, "Calling-AI visual assessment for preview_goal: score, close_enough, and note."] = None,
) -> dict[str, Any]:
    if spec is not None and spec_path is not None:
        return {"success": False, "message": "Provide exactly one of spec or spec_path."}
    params: dict[str, Any] = {"action": action}
    for key, value in {
        "spec": spec, "spec_path": spec_path, "goal_id": goal_id, "execute": execute,
        "confirm_play_mode": confirm_play_mode, "conflict_policy": conflict_policy,
        "assessment": assessment,
    }.items():
        if value is not None:
            params[key] = value
    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_mutation(ctx, unity_instance, "manage_environment", params)
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

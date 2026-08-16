"""Project-automation MCP wrapper for Modern UI Pack construction and repair."""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import send_mutation


@mcp_for_unity_tool(
    group="ui",
    description=(
        "Creates, merges, repairs, themes, and removes manifest-owned Michsky Modern UI Pack hierarchies. "
        "This is Project Automation: preflight with get_modern_ui_catalog first, then ask the developer for the local Unity approval when required."
    ),
    annotations=ToolAnnotations(title="Manage Modern UI", destructiveHint=True),
)
async def manage_modern_ui(
    ctx: Context,
    action: Annotated[Literal["apply", "repair", "create_theme_copy", "remove_managed"], "Mutation action."],
    spec: Annotated[dict[str, Any] | None, "Inline Modern UI specification version 1. Required by apply."] = None,
    spec_path: Annotated[str | None, "Assets-relative JSON specification path. Alternative to spec."] = None,
    target: Annotated[Any | None, "Exact target hierarchy/path reference for apply."] = None,
    package_path: Annotated[str | None, "Optional Assets-relative Modern UI Pack folder."] = None,
    mode: Annotated[Literal["create", "merge", "replace_managed"] | None, "Apply ownership mode."] = None,
    conflict_policy: Annotated[Literal["fail_on_conflict", "prefer_spec", "prefer_project"] | None, "Three-way merge conflict policy for managed nodes."] = None,
    owner_path: Annotated[str | None, "Assets-relative scene or prefab owner for repair/remove_managed."] = None,
    node_ids: Annotated[list[str] | None, "Managed node IDs to remove; omit to remove all managed nodes."] = None,
    source_path: Annotated[str | None, "Source UIManager asset for create_theme_copy."] = None,
    destination_path: Annotated[str | None, "Project-owned .asset destination for create_theme_copy."] = None,
    properties: Annotated[dict[str, Any] | None, "Optional serialized UIManager properties applied only to the newly copied theme."] = None,
) -> dict[str, Any]:
    if spec is not None and spec_path is not None:
        return {"success": False, "message": "Provide exactly one of spec or spec_path."}
    params: dict[str, Any] = {"action": action}
    for key, value in {
        "spec": spec, "spec_path": spec_path, "target": target, "package_path": package_path,
        "mode": mode, "conflict_policy": conflict_policy, "owner_path": owner_path, "node_ids": node_ids,
        "source_path": source_path, "destination_path": destination_path, "properties": properties,
    }.items():
        if value is not None:
            params[key] = value
    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_mutation(ctx, unity_instance, "manage_modern_ui", params)
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

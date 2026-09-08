"""Read-only discovery and preflight wrapper for Project Storm environment tooling."""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="vfx",
    description=(
        "Inspects Project Storm environment authoring without changing the project. "
        "Discovers HDRP shared Volume Profiles, Expanse controls, weather/storm presets, "
        "authority relationships, VFX bindings, compatibility, and preflight diagnostics. "
        "Use this before manage_environment. Screenshots are captured with manage_camera."
    ),
    annotations=ToolAnnotations(title="Get Environment Catalog", readOnlyHint=True, destructiveHint=False),
)
async def get_environment_catalog(
    ctx: Context,
    action: Annotated[Literal[
        "status", "catalog", "describe", "inspect_context", "inspect_authority",
        "inspect_profile", "inspect_preset", "preflight", "coverage",
        "resolve_target", "goal_status", "refresh_cache",
    ], "Read-only environment catalog action."],
    spec: Annotated[dict[str, Any] | None, "Inline environment_spec version 1 for preflight."] = None,
    spec_path: Annotated[str | None, "Assets-relative JSON environment_spec path for preflight."] = None,
    target: Annotated[Any | None, "Exact target identity, GlobalObjectId, asset path, or discovery hint."] = None,
    context: Annotated[Any | None, "Exact environment context identity for inspection."] = None,
    family: Annotated[str | None, "Optional component, asset, or environment family filter."] = None,
    component: Annotated[str | None, "Optional component type for describe."] = None,
    asset_path: Annotated[str | None, "Assets-relative profile or preset path."] = None,
    goal_id: Annotated[str | None, "Goal identifier for goal_status."] = None,
    query: Annotated[str | None, "Read-only discovery query for resolve_target."] = None,
    page: Annotated[int | None, "One-based result page."] = None,
    page_size: Annotated[int | None, "Result page size from 1 to 200."] = None,
) -> dict[str, Any]:
    if spec is not None and spec_path is not None:
        return {"success": False, "message": "Provide exactly one of spec or spec_path."}
    params: dict[str, Any] = {"action": action}
    for key, value in {
        "spec": spec, "spec_path": spec_path, "target": target, "context": context,
        "family": family, "component": component, "asset_path": asset_path,
        "goal_id": goal_id, "query": query, "page": page, "page_size": page_size,
    }.items():
        if value is not None:
            params[key] = value
    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "get_environment_catalog", params,
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

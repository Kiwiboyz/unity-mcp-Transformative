"""Read-only access to the work areas drawn with the Scene-view Work Area tool."""

from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance

ACTIONS = ("list", "get")


@mcp_for_unity_tool(
    group="core",
    description=(
        "Read the work areas the developer drew in the Unity Scene view with the MCP Work Area tool "
        "(Window > Transformative MCP for Project Storm > Work Area Tool). Call this FIRST whenever the developer says "
        "they have drawn or marked an area, instead of guessing from screenshots. Each area is a rectangle or polygon "
        "footprint treated as a vertical volume, returned in exact Unity world metres: vertices [x, y, z], centroid, "
        "bounds, area_m2, height_range (min_y/max_y), ground heights, the terrain tiles/scenes it overlaps (loaded or "
        "not) with overlap m2, and water coverage (Gaia 'Water Surface' level). One area is marked active: the default "
        "target. action='list' returns every area; action='get' returns one (area = id, name or 'active'; default active)."
    ),
    annotations=ToolAnnotations(title="Get Work Area", readOnlyHint=True, destructiveHint=False),
)
async def get_work_area(
    ctx: Context,
    action: Annotated[Literal["list", "get"], "list: every area (active flagged). get: one area (default: the active one)."] = "list",
    area: Annotated[str | None, "For get: area id, name (case-insensitive) or 'active'."] = None,
    water_level: Annotated[float | None, "Override the water level (world y) used for water coverage."] = None,
) -> dict[str, Any]:
    if action not in ACTIONS:
        return {"success": False, "message": f"Unknown action '{action}'. Use 'list' or 'get'."}
    params: dict[str, Any] = {"action": action}
    if area is not None:
        params["area"] = area
    if water_level is not None:
        params["water_level"] = water_level
    instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(async_send_command_with_retry, instance, "get_work_area", params)
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

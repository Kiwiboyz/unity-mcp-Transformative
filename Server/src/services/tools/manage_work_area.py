"""Create or adjust Scene-view work areas so the developer can refine an agent's proposal by hand."""

import math
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance

ACTIONS = ("create", "update", "delete", "set_active", "clear")
MAX_VERTICES = 256


def _finite(values: list[Any]) -> bool:
    return all(isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) for v in values)


def _validate(action: str, area: str | None, kind: str | None, vertices: list[list[float]] | None,
              center: list[float] | None, size: list[float] | None, y_range: list[float] | str | None) -> str | None:
    if action not in ACTIONS:
        return f"Unknown action '{action}'. Use create, update, delete, set_active or clear."
    if action in ("update", "delete", "set_active") and not area:
        return f"{action} needs area (id, name or 'active')."
    if kind is not None and kind not in ("rect", "polygon"):
        return "kind must be 'rect' or 'polygon'."
    if vertices is not None:
        if not 3 <= len(vertices) <= MAX_VERTICES:
            return f"A polygon needs 3 to {MAX_VERTICES} vertices."
        if any(not isinstance(v, (list, tuple)) or len(v) not in (2, 3) or not _finite(list(v)) for v in vertices):
            return "Each vertex must be [x, z] or [x, y, z] with finite numbers."
    if center is not None and (len(center) not in (2, 3) or not _finite(center)):
        return "center must be [x, z] or [x, y, z]."
    if size is not None and (len(size) != 2 or not _finite(size) or min(size) <= 0):
        return "size must be [width, length] in metres, both above zero."
    if action == "create":
        resolved = kind or ("polygon" if vertices is not None else "rect")
        if resolved == "polygon" and vertices is None:
            return "Creating a polygon needs vertices."
        if resolved == "rect" and (center is None or size is None):
            return "Creating a rect needs center and size."
    if isinstance(y_range, str) and y_range != "auto":
        return "y_range must be [min_y, max_y] or 'auto'."
    if isinstance(y_range, list) and (len(y_range) != 2 or not _finite(y_range) or y_range[1] <= y_range[0]):
        return "y_range must be [min_y, max_y] with max above min."
    return None


@mcp_for_unity_tool(
    group="core",
    description=(
        "Propose or adjust a Scene-view work area (the footprints read by get_work_area) so the developer can refine it "
        "by hand with the Work Area tool. Changes only a per-user UserSettings file, never a scene or asset, and each "
        "is a normal Unity Undo step. create: kind 'rect' (center [x, z], size [width, length] m, yaw degrees about +y) "
        "or 'polygon' (vertices [[x, z], ...] in order; give [x, y, z] to keep a height, otherwise corners snap to the "
        "ground). update: area plus any of name, vertices, center/size/yaw, y_range, height_margin, set_active. "
        "delete / set_active: area. clear: removes every area - only when the developer asks."
    ),
    annotations=ToolAnnotations(title="Manage Work Area", destructiveHint=True),
)
async def manage_work_area(
    ctx: Context,
    action: Annotated[Literal["create", "update", "delete", "set_active", "clear"], "What to do."],
    area: Annotated[str | None, "Target area id, name or 'active' (update, delete, set_active)."] = None,
    name: Annotated[str | None, "Area name (made unique; up to 64 characters)."] = None,
    kind: Annotated[Literal["rect", "polygon"] | None, "Shape for create (default: polygon when vertices are given, else rect)."] = None,
    vertices: Annotated[list[list[float]] | None, "Polygon corners in order: [[x, z], ...] or [[x, y, z], ...], world metres."] = None,
    center: Annotated[list[float] | None, "Rect centre [x, z] or [x, y, z]."] = None,
    size: Annotated[list[float] | None, "Rect [width, length] in metres (width along the yawed x axis)."] = None,
    yaw: Annotated[float | None, "Rect rotation in degrees about +y."] = None,
    y_range: Annotated[list[float] | Literal["auto"] | None, "[min_y, max_y] for a manual volume, or 'auto' (ground +/- margin)."] = None,
    height_margin: Annotated[float | None, "Auto volume margin above and below the ground, metres (default 5)."] = None,
    set_active: Annotated[bool | None, "Make this the active area (create defaults to true)."] = None,
) -> dict[str, Any]:
    error = _validate(action, area, kind, vertices, center, size, y_range)
    if error:
        return {"success": False, "message": error}
    params: dict[str, Any] = {"action": action}
    for key, value in {
        "area": area, "name": name, "kind": kind, "vertices": vertices, "center": center, "size": size,
        "yaw": yaw, "y_range": y_range, "height_margin": height_margin, "set_active": set_active,
    }.items():
        if value is not None:
            params[key] = value
    instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(async_send_command_with_retry, instance, "manage_work_area", params)
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

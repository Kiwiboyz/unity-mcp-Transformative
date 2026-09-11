"""Versioned semantic storm_wind inspection; paired Unity Editor bridge."""
from typing import Annotated, Any, Literal
from fastmcp import Context
from mcp.types import ToolAnnotations
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance

ACTIONS = ('status', 'catalog', 'describe', 'validate', 'preflight', 'coverage', 'instances', 'inspect', 'validation_impact', 'artifact', 'sample', 'sample_batch', 'laboratory', 'cost', 'goal_status')

@mcp_for_unity_tool(
    group="vfx",
    description="Inspect exact authored identities, typed fields, provenance, scenario state, bounded shared wind samples and cost. No mutations. Use before the matching manage tool.",
    annotations=ToolAnnotations(title="Get Storm Wind Catalog", readOnlyHint=True, destructiveHint=False),
)
async def get_storm_wind_catalog(
    ctx: Context,
    action: Annotated[Literal['status', 'catalog', 'describe', 'validate', 'preflight', 'coverage', 'instances', 'inspect', 'validation_impact', 'artifact', 'sample', 'sample_batch', 'laboratory', 'cost', 'goal_status'], "Semantic action."],
    target: Annotated[dict[str, Any] | None, "Exact catalog identity including hash; instance controls use instances identity."] = None,
    spec: Annotated[dict[str, Any] | None, "schema_version 1 with 1–32 operations: exact target, semantic fields and optional new asset destination."] = None,
    goal_id: Annotated[str | None, "Preview transaction id."] = None,
    candidate_hash: Annotated[str | None, "Exact preview SHA-256 required for explicit commit."] = None,
    command: Annotated[str | None, "Typed scenario command; never executable code."] = None,
    options: Annotated[dict[str, Any] | None, "Typed scenario/laboratory options."] = None,
    positions: Annotated[list[list[float]] | None, "1–256 absolute-world positions in metres for shared wind sampling."] = None,
    family: Annotated[str | None, "Exact catalog type filter."] = None,
    page: Annotated[int, "One-based catalog page."] = 1,
    page_size: Annotated[int, "Catalog items per page, 1–100."] = 30,
) -> dict[str, Any]:
    if action not in ACTIONS:
        return {"success": False, "message": "Unknown semantic action."}
    if page < 1 or not 1 <= page_size <= 100:
        return {"success": False, "message": "Invalid catalog page or page_size."}
    if positions is not None and (not 1 <= len(positions) <= 256 or any(len(p) != 3 for p in positions)):
        return {"success": False, "message": "Provide 1–256 three-coordinate positions."}
    if action == "preview_goal" and (not spec or spec.get("schema_version") != 1 or not isinstance(spec.get("operations"), list) or not 1 <= len(spec["operations"]) <= 32):
        return {"success": False, "message": "Expected schema_version 1 and 1–32 operations."}
    if action == "commit_goal" and (not goal_id or not candidate_hash or len(candidate_hash) != 64):
        return {"success": False, "message": "Explicit commit requires goal_id and candidate_hash from preview."}
    params = {"action": action, "page": page, "page_size": page_size}
    params.update({k: v for k, v in {"target": target, "spec": spec, "goal_id": goal_id, "candidate_hash": candidate_hash, "command": command, "options": options, "positions": positions, "family": family}.items() if v is not None})
    instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(async_send_command_with_retry, instance, "get_storm_wind_catalog", params)
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

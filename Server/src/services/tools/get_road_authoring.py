"""Read-only, versioned Project Storm RoadBuilder inspection."""

from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.road_authoring_contract import RoadAuthoringContractError, build_request
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="road_authoring",
    description="Inspect the opt-in Project Storm RoadBuilder catalog, network, selection, spatial anchors, validation, goal receipts and limits. Exact IDs and hashes are required for authoring.",
    annotations=ToolAnnotations(title="Get Road Authoring", readOnlyHint=True, destructiveHint=False),
)
async def get_road_authoring(
    ctx: Context,
    action: Annotated[Literal["status", "catalog", "network", "selection", "validate", "spatial_query", "profile_candidates", "goal_status", "receipt", "diagnostics"], "Read-only RoadBuilder action."],
    scene_guid: Annotated[str | None, "Exact 32-character Unity scene asset GUID for scene queries."] = None,
    operation_id: Annotated[str | None, "Stable UUID for goal_status or receipt lookup."] = None,
    expected_manifest_hash: Annotated[str | None, "Optional exact SHA-256 manifest version to validate."] = None,
    expected_revision_hashes: Annotated[list[dict[str, str]] | None, "Sorted unique {profileRevisionId, closureHash} entries, up to 64."] = None,
    payload: Annotated[dict[str, Any] | None, "Typed action-specific filters, exact IDs, bounded spatial query or page arguments."] = None,
) -> dict[str, Any]:
    try:
        request = build_request(
            action=action, mutation=False, scene_guid=scene_guid,
            operation_id=operation_id, expected_manifest_hash=expected_manifest_hash,
            expected_revision_hashes=expected_revision_hashes, payload=payload,
        )
    except RoadAuthoringContractError as exc:
        return {"success": False, "code": "road_invalid_request", "message": str(exc)}
    instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry, instance, "get_road_authoring", request,
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

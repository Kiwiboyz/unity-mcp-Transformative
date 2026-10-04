"""Versioned Project Storm RoadBuilder goals with explicit stage and receipt handling."""

from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import send_mutation
from services.tools.road_authoring_contract import RoadAuthoringContractError, build_request


@mcp_for_unity_tool(
    group="road_authoring",
    description=("Preview, exactly stage, commit, cancel or restore a typed Project Storm RoadBuilder goal. Use a stable UUID, "
                 "exact scene IDs and hashes; after an uncertain response inspect its receipt before retrying. Requires Unity "
                 "Project Automation approval. Drawn areas (no adopted scene or manifest needed, only scene_guid): "
                 "intent parking_draw draws a car park from an outline ({vertices:[{x,y,z}]} or {rectangle:{center,size:{x,z},"
                 "rotationDeg}}) that lays itself out, snaps to road footpaths and follows their slope; connect it with "
                 "connect:[{suggestion:i}] (indices from get_road_authoring drawn_areas or a preview) or change one by lotId "
                 "(settings, connect, disconnect:[entranceIndex], delete). Intent plaza draws the ground under buildings/yards "
                 "(surface, areas:[{surface, shape:polygon|path, points, widthMeters?}]) or changes one by plazaId (addAreas, "
                 "removeAreas, surface, delete). Intent farm_field draws a farm of one crop (crop: CornGreen|CornDry|"
                 "WheatGreen|WheatGolden|WheatStubble; seed?, name?, rules?:{minPlotHectares, maxPlotHectares, headland, "
                 "headlandFromHectares, headlandWidth, trackWidth, tramlines, paintTerrain}, fences?:[{aroundFarm:true}|"
                 "{points, closed?}], default one fence round it) whose plots, lanes, headland and gates are generated, or "
                 "changes one by farmId (crop, seed, rules, addFences, removeFences:[fenceId], delete). preview_goal is a dry "
                 "run that changes nothing; stage_goal returns the candidate_hash to commit."),
    annotations=ToolAnnotations(title="Manage Road Authoring", readOnlyHint=False, destructiveHint=True),
)
async def manage_road_authoring(
    ctx: Context,
    action: Annotated[Literal["preview_goal", "stage_goal", "commit_goal", "cancel_goal", "restore_goal"], "RoadBuilder goal transition."],
    operation_id: Annotated[str, "Stable UUID. Reusing it with different goal content is rejected."],
    scene_guid: Annotated[str | None, "Exact 32-character Unity scene asset GUID."] = None,
    expected_manifest_hash: Annotated[str | None, "Exact SHA-256 manifest hash observed during inspection."] = None,
    expected_revision_hashes: Annotated[list[dict[str, str]] | None, "Sorted unique {profileRevisionId, closureHash} entries, up to 64."] = None,
    candidate_hash: Annotated[str | None, "Exact SHA-256 candidate hash returned by stage_goal; required to commit."] = None,
    payload: Annotated[dict[str, Any] | None, "For preview/stage: {intent: asset|profile|road_path|reprofile|decoration|parking_lot|adopt|bake|repair_helpers|parking_draw|plaza|farm_field, spec: typed goal object}. Other actions use action-specific fields."] = None,
) -> dict[str, Any]:
    try:
        request = build_request(
            action=action, mutation=True, operation_id=operation_id,
            scene_guid=scene_guid, expected_manifest_hash=expected_manifest_hash,
            expected_revision_hashes=expected_revision_hashes,
            candidate_hash=candidate_hash, payload=payload,
        )
    except RoadAuthoringContractError as exc:
        return {"success": False, "code": "road_invalid_request", "message": str(exc)}
    instance = await get_unity_instance_from_context(ctx)
    # send_mutation never blindly retries after a connection loss. The caller
    # uses get_road_authoring(action="receipt") before deciding its next step.
    result = await send_mutation(ctx, instance, "manage_road_authoring", request)
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

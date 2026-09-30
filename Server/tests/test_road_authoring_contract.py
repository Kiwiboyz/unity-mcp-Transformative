"""Paired RoadBuilder wrapper contracts and replay-safe request boundaries."""

import asyncio
import importlib
import math
import uuid

import pytest

from services.registry import TOOL_GROUPS, get_registered_tools
from services.tools.road_authoring_contract import RoadAuthoringContractError, build_request
from .integration.test_helpers import DummyContext


SCENE = "a" * 32
HASH = "b" * 64
OPERATION = str(uuid.uuid4())


def test_group_is_opt_in_and_tools_are_paired():
    get_tool = importlib.import_module("services.tools.get_road_authoring")
    manage_tool = importlib.import_module("services.tools.manage_road_authoring")
    del get_tool, manage_tool
    assert "road_authoring" in TOOL_GROUPS
    tools = {entry["name"]: entry for entry in get_registered_tools()}
    assert tools["get_road_authoring"]["group"] == "road_authoring"
    assert tools["manage_road_authoring"]["group"] == "road_authoring"


@pytest.mark.parametrize("module_name,action", [
    ("get_road_authoring", "status"),
    ("manage_road_authoring", "cancel_goal"),
])
def test_wrapper_sends_exact_versioned_envelope(module_name, action, monkeypatch):
    module = importlib.import_module("services.tools." + module_name)
    captured = {}

    async def send(*args, **kwargs):
        captured["command"] = args[-2]
        captured["request"] = args[-1]
        return {"success": True, "data": {"status": "ok"}}

    monkeypatch.setattr(module, "send_mutation" if module_name.startswith("manage") else "send_with_unity_instance", send)
    kwargs = {"operation_id": OPERATION} if module_name.startswith("manage") else {}
    result = asyncio.run(getattr(module, module_name)(DummyContext(), action, **kwargs))
    assert result["success"]
    assert captured["command"] == module_name
    assert set(captured["request"]) == {
        "schemaVersion", "operationId", "sceneGuid", "action", "expectedManifestHash",
        "expectedRevisionHashes", "candidateHash", "payload",
    }
    assert captured["request"]["schemaVersion"] == 1
    assert captured["request"]["action"] == action


@pytest.mark.parametrize("bad", [
    {"operation_id": "not-a-uuid"},
    {"scene_guid": "not-a-guid"},
    {"expected_manifest_hash": "short"},
    {"candidate_hash": "short"},
])
def test_invalid_id_or_hash_rejected_before_transport(bad):
    with pytest.raises(RoadAuthoringContractError):
        args = {"operation_id": OPERATION, "candidate_hash": HASH, **bad}
        build_request(action="commit_goal", mutation=True, **args)


def test_commit_requires_hash_and_receipt_requires_operation_id():
    with pytest.raises(RoadAuthoringContractError, match="candidate_hash"):
        build_request(action="commit_goal", mutation=True, operation_id=OPERATION)
    with pytest.raises(RoadAuthoringContractError, match="operation_id"):
        build_request(action="receipt", mutation=False)


def test_scene_goal_requires_exact_scene_and_manifest_but_bootstrap_does_not():
    road = {"intent": "road_path", "spec": {"profileRevisionId": "r1", "operations": [
        {"kind": "span", "start": {"x": 0, "y": 0, "z": 0},
         "end": {"x": 10, "y": 0, "z": 0}},
    ]}}
    with pytest.raises(RoadAuthoringContractError, match="scene_guid"):
        build_request(action="stage_goal", mutation=True, operation_id=OPERATION, payload=road)
    adopt = {"intent": "adopt", "spec": {"includePrefabInstances": True}}
    request = build_request(action="stage_goal", mutation=True, operation_id=OPERATION,
                            scene_guid=SCENE, payload=adopt)
    assert request["expectedManifestHash"] is None


def test_revision_hashes_reject_duplicates_unsorted_or_bad_hash():
    entries = [
        {"profileRevisionId": "a", "closureHash": HASH},
        {"profileRevisionId": "b", "closureHash": HASH},
    ]
    assert build_request(action="status", mutation=False, expected_revision_hashes=entries)["expectedRevisionHashes"] == entries
    for invalid in (entries[::-1], entries[:1] * 2, [{"profileRevisionId": "a", "closureHash": "bad"}]):
        with pytest.raises(RoadAuthoringContractError):
            build_request(action="status", mutation=False, expected_revision_hashes=invalid)


def test_nested_nonfinite_and_oversized_payloads_rejected():
    for value in (math.nan, math.inf, -math.inf):
        with pytest.raises(RoadAuthoringContractError):
            build_request(action="spatial_query", mutation=False, scene_guid=SCENE,
                          payload={"center": {"x": 0, "y": value, "z": 0}, "radiusMeters": 10})
    with pytest.raises(RoadAuthoringContractError):
        build_request(action="profile_candidates", mutation=False, payload={"lanes": [{}] * 257})
    with pytest.raises(RoadAuthoringContractError):
        build_request(action="spatial_query", mutation=False, scene_guid=SCENE,
                      payload={"center": {"x": 10**1000, "y": 0, "z": 0}, "radiusMeters": 10})


def test_road_path_rejects_unknown_fields_and_nonfinite_geometry():
    goal = {"intent": "road_path", "spec": {"profileRevisionId": "r1", "operations": [
        {"kind": "span", "start": {"x": 0, "y": 0, "z": 0},
         "end": {"x": 10, "y": 0, "z": 0, "script": "no"}},
    ]}}
    args = {"action": "stage_goal", "mutation": True, "operation_id": OPERATION,
            "scene_guid": SCENE, "expected_manifest_hash": HASH}
    with pytest.raises(RoadAuthoringContractError, match="endpoints"):
        build_request(**args, payload=goal)
    goal["spec"]["operations"][0]["end"] = {"x": 10, "y": math.nan, "z": 0}
    with pytest.raises(RoadAuthoringContractError, match="coordinates"):
        build_request(**args, payload=goal)


def test_road_operations_support_demolition_and_require_profile_for_construction():
    args = {"action": "stage_goal", "mutation": True, "operation_id": OPERATION,
            "scene_guid": SCENE, "expected_manifest_hash": HASH}
    demolition = {"intent": "road_path", "spec": {"operations": [{"kind": "demolish", "targetId": "segment_a"}]}}
    assert build_request(**args, payload=demolition)["payload"] == demolition
    construct = {"intent": "road_path", "spec": {"operations": [{"kind": "roundabout",
        "center": {"x": 0, "y": 0, "z": 0}, "radiusMeters": 10}]}}
    with pytest.raises(RoadAuthoringContractError, match="profileRevisionId"):
        build_request(**args, payload=construct)
    construct["spec"]["operations"][0]["profileRevisionId"] = "roundabout_rev"
    assert build_request(**args, payload=construct)["payload"] == construct


def test_noncanonical_operation_id_and_wrong_scalar_types_rejected_cleanly():
    with pytest.raises(RoadAuthoringContractError, match="canonical"):
        build_request(action="cancel_goal", mutation=True, operation_id=OPERATION.replace("-", ""))
    for args in ({"scene_guid": 123}, {"expected_manifest_hash": []}, {"candidate_hash": 25}):
        with pytest.raises(RoadAuthoringContractError):
            build_request(action="status", mutation=False, **args)


def test_read_wrapper_rejects_mutation_action_without_transport():
    module = importlib.import_module("services.tools.get_road_authoring")
    result = asyncio.run(module.get_road_authoring(DummyContext(), "commit_goal"))
    assert result["success"] is False


def test_inspection_payloads_are_exact_and_bounded():
    valid = build_request(action="spatial_query", mutation=False, scene_guid=SCENE,
                          payload={"center": {"x": 1, "y": 2, "z": 3},
                                   "radiusMeters": 500, "kind": "anchors", "limit": 20})
    assert valid["payload"]["radiusMeters"] == 500
    invalid = [
        ("status", {"mutate": True}),
        ("catalog", {"limit": 101}),
        ("network", {"bounds": {"min": {"x": 2, "y": 0, "z": 0},
                                  "max": {"x": 1, "y": 0, "z": 0}}}),
        ("validate", {"scope": "save"}),
        ("spatial_query", {"center": {"x": 0, "y": 0, "z": 0}, "radiusMeters": math.nan}),
        ("profile_candidates", {"limit": 4}),
        ("diagnostics", {"severity": "fatal"}),
    ]
    for action, payload in invalid:
        with pytest.raises(RoadAuthoringContractError):
            build_request(action=action, mutation=False, scene_guid=SCENE, payload=payload)


def test_asset_profile_and_adoption_have_typed_shapes():
    args = {"action": "preview_goal", "mutation": True, "operation_id": OPERATION}
    asset = {"intent": "asset", "spec": {"assetKind": "material", "name": "Dirt Shoulder",
                                       "shaderGuid": SCENE, "rgba": [0.4, 0.3, 0.2, 1]}}
    assert build_request(**args, payload=asset)["payload"] == asset
    asset["spec"]["propertyPath"] = "m_Color"
    with pytest.raises(RoadAuthoringContractError):
        build_request(**args, payload=asset)
    profile = {"intent": "profile", "spec": {"mode": "variant", "name": "Rural Lane",
        "semanticTags": ["rural"], "oneWay": False,
        "crossSection": {"surfaces": [{"surfaceKey": "carriage", "role": "carriageway", "side": "center",
            "innerOffsetMeters": -1.5, "outerOffsetMeters": 1.5, "heightMeters": 0,
            "materialGuid": SCENE, "closedEnds": True}]},
        "lanes": [{"laneKey": "vehicle_0", "role": "through",
        "modes": 1, "direction": "forward", "widthMeters": 3,
        "speedMetersPerSecond": 15, "costMultiplier": 1,
        "permittedManeuvers": ["straight"], "surfaceKey": "carriage", "centerOffsetMeters": 0}]}}
    assert build_request(**args, payload=profile)["payload"] == profile
    profile["spec"]["lanes"][0]["widthMeters"] = math.nan
    with pytest.raises(RoadAuthoringContractError):
        build_request(**args, payload=profile)
    adopt = {"intent": "adopt", "spec": {"includePrefabInstances": True}}
    assert build_request(**args, scene_guid=SCENE, payload=adopt)["expectedManifestHash"] is None


def _parking_goal():
    point = lambda x, z: {"x": x, "y": 0, "z": z}
    return {"intent": "parking_lot", "spec": {
        "boundary": {"rectangle": {"center": point(0, 0), "size": {"x": 20, "z": 30}, "rotationDeg": 0}},
        "aisles": [{"id": "aisle_a", "points": [point(0, 0), point(0, 20)], "widthMeters": 6,
                    "modeMask": 1, "direction": "both", "speedMetersPerSecond": 5,
                    "costMultiplier": 1}],
        "rows": [{"id": "row_a", "aisleId": "aisle_a", "side": "left", "angleDegrees": 90,
                  "firstOffsetMeters": 0, "count": 3, "stallWidthMeters": 2.5,
                  "stallLengthMeters": 5, "gapMeters": 0, "stallClass": "Standard", "allowedModes": 1}],
        "entrances": [{"id": "entrance_a", "segmentId": "road_a", "aisleId": "aisle_a",
                       "aisleLaneKey": "aisle_lane", "pathLocal": [point(0, 0), point(0, 2)],
                       "inboundRoadLaneKey": "road_lane_in", "inboundRoadPointWorld": point(1, 0),
                       "controlPointLocal": point(0, 1), "modes": 1}],
        "pedestrianRoutes": [], "pedestrianEntrances": [], "zones": [],
        "markingTemplateGuid": SCENE, "surfaceMaterialGuid": SCENE,
    }}


def test_parking_wire_requires_exact_directed_attachment_and_geometry():
    args = {"action": "stage_goal", "mutation": True, "operation_id": OPERATION,
            "scene_guid": SCENE, "expected_manifest_hash": HASH}
    goal = _parking_goal()
    goal["spec"]["entrances"][0]["widthMeters"] = 4.2
    assert build_request(**args, payload=goal)["payload"] == goal
    del goal["spec"]["entrances"][0]["inboundRoadPointWorld"]
    with pytest.raises(RoadAuthoringContractError, match="endpoint"):
        build_request(**args, payload=goal)
    goal = _parking_goal()
    goal["spec"]["rows"][0]["angleDegrees"] = 30
    with pytest.raises(RoadAuthoringContractError, match="angle"):
        build_request(**args, payload=goal)
    goal = _parking_goal()
    goal["spec"]["boundary"]["vertices"] = [{"x": 0, "y": 0, "z": 0}] * 3
    with pytest.raises(RoadAuthoringContractError, match="exactly"):
        build_request(**args, payload=goal)


def test_pedestrian_entrance_and_link_require_exact_routes_and_control():
    args = {"action": "stage_goal", "mutation": True, "operation_id": OPERATION,
            "scene_guid": SCENE, "expected_manifest_hash": HASH}
    point = lambda x, z: {"x": x, "y": 0, "z": z}
    goal = _parking_goal()
    goal["spec"]["pedestrianRoutes"] = [{"id": "walk_a", "points": [point(0, 0), point(0, 10)],
        "widthMeters": 1.5, "linkedStallIds": ["stall_a"]}]
    entrance = {"id": "walk_entry", "segmentId": "road_a", "routeId": "walk_a",
        "routeDistanceMeters": 0, "pathLocal": [point(0, 0), point(0, 2)],
        "inboundRoadLaneKey": "ped_lane", "inboundRoadPointWorld": point(1, 0),
        "control": "Signal", "signalGroupId": "signal_a", "controlPointLocal": point(0, 1)}
    goal["spec"]["pedestrianEntrances"] = [entrance]
    goal["spec"]["pedestrianLinks"] = [{"id": "stall_walk_a", "stallId": "stall_a",
        "routeId": "walk_a", "routeDistanceMeters": 5, "pathLocal": [point(1, 2), point(0, 5)]}]
    assert build_request(**args, payload=goal)["payload"] == goal
    del entrance["inboundRoadPointWorld"]
    with pytest.raises(RoadAuthoringContractError, match="world point"):
        build_request(**args, payload=goal)


def test_create_lot_can_link_new_stalls_by_row_slot_without_guessing_ids():
    args = {"action": "stage_goal", "mutation": True, "operation_id": OPERATION,
            "scene_guid": SCENE, "expected_manifest_hash": HASH}
    point = lambda x, z: {"x": x, "y": 0, "z": z}
    goal = _parking_goal()
    goal["spec"]["pedestrianRoutes"] = [{"id": "walk_a", "points": [point(0, 0), point(0, 10)],
        "widthMeters": 1.5, "linkedStallSlots": [{"rowId": "row_a", "slotIndex": 0}]}]
    goal["spec"]["pedestrianLinks"] = [{"id": "link_a", "rowId": "row_a", "slotIndex": 0,
        "routeId": "walk_a", "routeDistanceMeters": 5,
        "pathLocal": [point(1, 2), point(0, 5)]}]
    assert build_request(**args, payload=goal)["payload"] == goal
    goal["spec"]["pedestrianLinks"][0]["stallId"] = "guessed"
    with pytest.raises(RoadAuthoringContractError, match="exactly"):
        build_request(**args, payload=goal)


def test_atomic_parking_driveway_alias_resolves_only_declared_public_span():
    args = {"action": "stage_goal", "mutation": True, "operation_id": OPERATION,
            "scene_guid": SCENE, "expected_manifest_hash": HASH}
    goal = _parking_goal()
    point = lambda x, z: {"x": x, "y": 0, "z": z}
    goal["spec"]["publicRoadOperations"] = [{"kind": "span", "resultKey": "driveway_a",
        "profileRevisionId": "driveway_profile", "start": point(0, 0), "end": point(5, 0)}]
    entrance = goal["spec"]["entrances"][0]
    del entrance["segmentId"]
    entrance["drivewayResultKey"] = "driveway_a"
    assert build_request(**args, payload=goal)["payload"] == goal
    entrance["drivewayResultKey"] = "unknown"
    with pytest.raises(RoadAuthoringContractError, match="not declared"):
        build_request(**args, payload=goal)
    entrance["drivewayResultKey"] = "driveway_a"
    entrance["segmentId"] = "road_a"
    with pytest.raises(RoadAuthoringContractError, match="exactly"):
        build_request(**args, payload=goal)


def test_accessible_stall_override_is_typed_and_slot_unique():
    args = {"action": "stage_goal", "mutation": True, "operation_id": OPERATION,
            "scene_guid": SCENE, "expected_manifest_hash": HASH}
    goal = _parking_goal()
    row = goal["spec"]["rows"][0]
    row["allowedVehicleClasses"] = 3
    row["overrides"] = [{"slotIndex": 1, "stallClass": "Accessible", "allowedModes": 1,
        "allowedVehicleClasses": 1, "widthMeters": 3.8, "lengthMeters": 5.5,
        "adjacentAccessAisleMeters": 1.5, "markingTemplateGuid": SCENE}]
    assert build_request(**args, payload=goal)["payload"] == goal
    row["overrides"].append(dict(row["overrides"][0]))
    with pytest.raises(RoadAuthoringContractError, match="unique"):
        build_request(**args, payload=goal)


def test_drawn_area_goals_need_scene_but_no_manifest():
    lot = {"intent": "parking_draw", "spec": {
        "outline": {"rectangle": {"center": {"x": 0, "y": 0, "z": 0}, "size": {"x": 40, "z": 30}, "rotationDeg": 15}},
        "settings": {"angleDegrees": 60, "footpath": "grass", "accessiblePairs": 2},
        "connect": [{"suggestion": 0}, {"roadPoint": {"x": 0, "y": 0, "z": -20}, "lotPoint": {"x": 0, "y": 0, "z": -15}}],
    }}
    with pytest.raises(RoadAuthoringContractError, match="scene_guid"):
        build_request(action="stage_goal", mutation=True, operation_id=OPERATION, payload=lot)
    request = build_request(action="stage_goal", mutation=True, operation_id=OPERATION, scene_guid=SCENE, payload=lot)
    assert request["expectedManifestHash"] is None
    plaza = {"intent": "plaza", "spec": {
        "outline": {"vertices": [{"x": 0, "y": 0, "z": 0}, {"x": 20, "y": 0, "z": 0}, {"x": 20, "y": 0, "z": 30}]},
        "surface": "Lawn",
        "areas": [{"surface": "Driveway", "shape": "path", "widthMeters": 3.5,
                   "points": [{"x": 5, "y": 0, "z": 0}, {"x": 5, "y": 0, "z": 15}]}],
    }}
    build_request(action="preview_goal", mutation=True, operation_id=OPERATION, scene_guid=SCENE, payload=plaza)


@pytest.mark.parametrize("intent,spec", [
    # Exactly one of a new outline or an existing target.
    ("parking_draw", {}),
    ("parking_draw", {"lotId": "l1", "outline": {"vertices": [{"x": 0, "y": 0, "z": 0}] * 3}}),
    ("plaza", {"plazaId": "p1", "outline": {"vertices": [{"x": 0, "y": 0, "z": 0}] * 3}}),
    # Outlines are typed and bounded.
    ("parking_draw", {"outline": {"vertices": [{"x": 0, "y": 0, "z": 0}] * 2}}),
    ("parking_draw", {"outline": {"rectangle": {"center": {"x": 0, "y": 0, "z": 0}, "size": {"x": -1, "z": 5}}}}),
    ("plaza", {"outline": {"vertices": [{"x": 0, "y": 0, "z": 0}] * 65}}),
    # Settings and connections are typed.
    ("parking_draw", {"lotId": "l1", "settings": {"angleDegrees": 30}}),
    ("parking_draw", {"lotId": "l1", "settings": {"footpath": "gravel"}}),
    ("parking_draw", {"lotId": "l1", "settings": {"stallWidthMeters": 9}}),
    ("parking_draw", {"lotId": "l1", "connect": [{"suggestion": 0, "lotPoint": {"x": 0, "y": 0, "z": 0}}]}),
    ("parking_draw", {"lotId": "l1", "connect": [{"suggestion": 0}] * 9}),
    ("parking_draw", {"lotId": "l1", "disconnect": [0, 0]}),
    ("parking_draw", {"outline": {"vertices": [{"x": 0, "y": 0, "z": 0}] * 3}, "disconnect": [0]}),
    ("parking_draw", {"lotId": "l1", "delete": True, "connect": [{"suggestion": 0}]}),
    # Plaza areas are typed; a new plaza takes areas, an existing one addAreas.
    ("plaza", {"outline": {"vertices": [{"x": 0, "y": 0, "z": 0}] * 3},
               "areas": [{"surface": "Lawn", "shape": "circle", "points": [{"x": 0, "y": 0, "z": 0}] * 3}]}),
    ("plaza", {"outline": {"vertices": [{"x": 0, "y": 0, "z": 0}] * 3},
               "areas": [{"surface": "Lawn", "shape": "path", "points": [{"x": 0, "y": 0, "z": 0}]}]}),
    ("plaza", {"plazaId": "p1", "areas": [{"surface": "Lawn", "shape": "polygon",
                                           "points": [{"x": 0, "y": 0, "z": 0}] * 3}]}),
    ("plaza", {"plazaId": "p1", "addAreas": [{"surface": "Lawn", "shape": "path", "raiseMeters": -1,
                                              "points": [{"x": 0, "y": 0, "z": 0}] * 2}]}),
    ("plaza", {"plazaId": "p1", "delete": True, "surface": "Tiles"}),
    ("plaza", {"plazaId": "p1", "unknown": 1}),
])
def test_drawn_area_specs_are_exact(intent, spec):
    with pytest.raises(RoadAuthoringContractError):
        build_request(action="stage_goal", mutation=True, operation_id=OPERATION, scene_guid=SCENE,
                      payload={"intent": intent, "spec": spec})


def test_drawn_areas_inspection_is_scene_scoped_and_bounded():
    request = build_request(action="drawn_areas", mutation=False, scene_guid=SCENE,
                            payload={"kind": "plaza", "center": {"x": 0, "y": 0, "z": 0}, "radiusMeters": 50, "limit": 10})
    assert request["action"] == "drawn_areas"
    with pytest.raises(RoadAuthoringContractError, match="scene_guid"):
        build_request(action="drawn_areas", mutation=False, payload={})
    for payload in ({"kind": "roads"}, {"radiusMeters": 5}, {"limit": 101}, {"extra": True}):
        with pytest.raises(RoadAuthoringContractError):
            build_request(action="drawn_areas", mutation=False, scene_guid=SCENE, payload=payload)

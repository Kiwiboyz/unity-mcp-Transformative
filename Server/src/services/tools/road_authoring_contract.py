"""Shared versioned wire validation for the optional Project Storm RoadBuilder bridge."""

from __future__ import annotations

import json
import math
import re
import uuid
from typing import Any

INSPECTION_ACTIONS = frozenset({
    "status", "catalog", "network", "selection", "validate", "spatial_query",
    "profile_candidates", "goal_status", "receipt", "diagnostics",
})
MUTATION_ACTIONS = frozenset({
    "preview_goal", "stage_goal", "commit_goal", "cancel_goal", "restore_goal",
})
GOAL_INTENTS = frozenset({
    "asset", "profile", "road_path", "reprofile", "decoration", "parking_lot",
    "adopt", "bake", "repair_helpers",
})
_HEX32 = re.compile(r"^[a-fA-F0-9]{32}$")
_HEX64 = re.compile(r"^[a-fA-F0-9]{64}$")
MAX_REVISION_HASHES = 64
MAX_REQUEST_BYTES = 262_144
MAX_COLLECTION_ITEMS = 256
MAX_NESTING = 10
MAX_NODES = 4096


class RoadAuthoringContractError(ValueError):
    """The request is invalid before it reaches the Unity editor."""


def _ids(value: Any, *, allow_empty: bool = False) -> None:
    if (not isinstance(value, list) or len(value) > MAX_COLLECTION_ITEMS or
            (not allow_empty and not value) or
            any(not isinstance(item, str) or not 1 <= len(item) <= 128 for item in value) or
            len(value) != len(set(value))):
        raise RoadAuthoringContractError("Exact ID lists must be unique, nonempty and contain at most 256 bounded IDs.")


def _known_fields(spec: dict[str, Any], required: set[str], optional: set[str]) -> None:
    if not required.issubset(spec) or set(spec) - required - optional:
        raise RoadAuthoringContractError("Goal spec has missing or unknown fields.")


def _goal_spec(intent: str, spec: dict[str, Any]) -> None:
    """Validate stable public shapes; project bridge validates domain semantics."""
    if intent == "asset":
        _known_fields(spec, {"assetKind", "name"},
                      {"sourceAssetGuid", "shaderGuid", "textureGuids", "prefabGuids", "rgba"})
        if (spec["assetKind"] not in ("texture_import", "material", "prefab_composition", "marking_template") or
                not isinstance(spec["name"], str) or not 1 <= len(spec["name"]) <= 128):
            raise RoadAuthoringContractError("asset requires a supported assetKind and bounded name.")
        for key in ("sourceAssetGuid", "shaderGuid"):
            if key in spec and (not isinstance(spec[key], str) or not _HEX32.fullmatch(spec[key])):
                raise RoadAuthoringContractError(f"{key} must be an exact Unity asset GUID.")
        for key in ("textureGuids", "prefabGuids"):
            if key in spec:
                _ids(spec[key], allow_empty=True)
                if any(not _HEX32.fullmatch(value) for value in spec[key]):
                    raise RoadAuthoringContractError(f"{key} must contain exact Unity asset GUIDs.")
        if "rgba" in spec and (not isinstance(spec["rgba"], list) or len(spec["rgba"]) != 4 or
                               any(not isinstance(value, (int, float)) or isinstance(value, bool) or
                                   not 0 <= value <= 1 or isinstance(value, float) and not math.isfinite(value)
                                   for value in spec["rgba"])):
            raise RoadAuthoringContractError("rgba must be four finite channels in [0,1].")
    elif intent == "profile":
        _known_fields(spec, {"mode", "name", "semanticTags", "lanes", "crossSection", "oneWay"},
                      {"sourceRoadName", "sourceRevisionId", "exactSegmentIds"})
        if (spec["mode"] not in ("reuse", "new", "variant", "global_update") or
                not isinstance(spec["name"], str) or not 1 <= len(spec["name"]) <= 128):
            raise RoadAuthoringContractError("profile requires a supported mode and bounded name.")
        for key in ("sourceRoadName", "sourceRevisionId"):
            if key in spec and (not isinstance(spec[key], str) or not 1 <= len(spec[key]) <= 128):
                raise RoadAuthoringContractError(f"{key} must be a bounded exact ID.")
        if "exactSegmentIds" in spec:
            _ids(spec["exactSegmentIds"], allow_empty=True)
        if not isinstance(spec["oneWay"], bool):
            raise RoadAuthoringContractError("profile oneWay must be boolean.")
        cross_section = spec["crossSection"]
        if not isinstance(cross_section, dict):
            raise RoadAuthoringContractError("profile crossSection must be a typed object.")
        _known_fields(cross_section, {"surfaces"}, set())
        surfaces = cross_section["surfaces"]
        if not isinstance(surfaces, list) or not 1 <= len(surfaces) <= 64:
            raise RoadAuthoringContractError("profile requires 1–64 physical surfaces.")
        surface_keys: set[str] = set()
        for surface in surfaces:
            if not isinstance(surface, dict):
                raise RoadAuthoringContractError("Physical surfaces must be objects.")
            _known_fields(surface, {"surfaceKey", "role", "side", "innerOffsetMeters",
                                    "outerOffsetMeters", "heightMeters", "materialGuid", "closedEnds"},
                          {"markingTemplateGuid"})
            _id(surface["surfaceKey"])
            if surface["surfaceKey"] in surface_keys:
                raise RoadAuthoringContractError("Physical surfaceKey values must be unique.")
            surface_keys.add(surface["surfaceKey"])
            if (surface["role"] not in ("carriageway", "parking", "cycle", "sidewalk", "shoulder", "marking") or
                    surface["side"] not in ("left", "right", "center")):
                raise RoadAuthoringContractError("Physical surface role or side is unsupported.")
            for key in ("innerOffsetMeters", "outerOffsetMeters", "heightMeters"):
                _number(surface[key], maximum=1000)
            _guid(surface["materialGuid"])
            if "markingTemplateGuid" in surface:
                _guid(surface["markingTemplateGuid"])
            if not isinstance(surface["closedEnds"], bool):
                raise RoadAuthoringContractError("Physical surface closedEnds must be boolean.")
        tags = spec["semanticTags"]
        if (not isinstance(tags, list) or len(tags) > 32 or
                any(not isinstance(tag, str) or not 1 <= len(tag) <= 64 for tag in tags) or
                len(tags) != len(set(tags))):
            raise RoadAuthoringContractError("semanticTags must be up to 32 unique bounded strings.")
        lanes = spec["lanes"]
        if not isinstance(lanes, list) or not 1 <= len(lanes) <= 32:
            raise RoadAuthoringContractError("profile requires 1–32 typed lanes.")
        lane_keys: list[str] = []
        for lane in lanes:
            if not isinstance(lane, dict):
                raise RoadAuthoringContractError("Profile lanes must be objects.")
            _known_fields(lane, {"laneKey", "role", "modes", "direction", "widthMeters",
                                 "speedMetersPerSecond", "costMultiplier", "permittedManeuvers",
                                 "surfaceKey", "centerOffsetMeters"}, set())
            if (any(not isinstance(lane[key], str) or not 1 <= len(lane[key]) <= 64 for key in
                    ("laneKey", "role", "direction")) or
                    not isinstance(lane["modes"], int) or isinstance(lane["modes"], bool) or
                    not 1 <= lane["modes"] <= 15 or
                    lane["role"] not in ("through", "parking", "cycle", "pedestrian", "shoulder") or
                    lane["direction"] not in ("forward", "backward", "both")):
                raise RoadAuthoringContractError("Lane identity, role, direction or modes are invalid.")
            lane_keys.append(lane["laneKey"])
            _id(lane["surfaceKey"])
            if lane["surfaceKey"] not in surface_keys:
                raise RoadAuthoringContractError("Semantic lane must reference an authored physical surfaceKey.")
            _number(lane["centerOffsetMeters"], maximum=1000)
            for key, minimum, maximum in (("widthMeters", 0, 100),
                                          ("speedMetersPerSecond", 0, 1000),
                                          ("costMultiplier", 0, 1000)):
                value = lane[key]
                if not isinstance(value, (int, float)) or isinstance(value, bool) or not minimum < value <= maximum:
                    raise RoadAuthoringContractError(f"Lane {key} is out of range.")
            _ids(lane["permittedManeuvers"], allow_empty=True)
            if any(value not in ("straight", "left", "right", "merge", "roundabout", "crossing", "u_turn")
                   for value in lane["permittedManeuvers"]):
                raise RoadAuthoringContractError("Lane permittedManeuvers contain an unknown value.")
            if lane["role"] == "parking" and any(value in ("straight", "left", "right", "roundabout", "u_turn")
                                                  for value in lane["permittedManeuvers"]):
                raise RoadAuthoringContractError("Parking lanes cannot have through manoeuvres.")
            if lane["role"] == "cycle" and (not lane["modes"] & 2 or lane["modes"] & 1):
                raise RoadAuthoringContractError("Cycle lanes require Bicycle and exclude Car.")
            if lane["role"] == "pedestrian" and (not lane["modes"] & 4 or lane["modes"] & 1):
                raise RoadAuthoringContractError("Pedestrian lanes require Pedestrian and exclude Car.")
        if len(lane_keys) != len(set(lane_keys)):
            raise RoadAuthoringContractError("laneKey values must be unique.")
    elif intent == "adopt":
        _known_fields(spec, {"includePrefabInstances"}, set())
        if not isinstance(spec["includePrefabInstances"], bool):
            raise RoadAuthoringContractError("adopt requires includePrefabInstances boolean.")
    elif intent == "parking_lot":
        _parking_spec(spec)
    elif intent == "road_path":
        _known_fields(spec, {"operations"}, {"profileRevisionId", "exactSegmentIds", "connectionIds"})
        if "profileRevisionId" in spec:
            _id(spec["profileRevisionId"])
        operations = spec["operations"]
        if not isinstance(operations, list) or not 1 <= len(operations) <= 64:
            raise RoadAuthoringContractError("road_path requires 1–64 ordered operations.")
        for operation in operations:
            if not isinstance(operation, dict) or not isinstance(operation.get("kind"), str):
                raise RoadAuthoringContractError("Road operation requires a typed kind.")
            if operation["kind"] == "span":
                _known_fields(operation, {"kind", "start", "end"}, {"profileRevisionId"})
                for key in ("start", "end"):
                    point = operation[key]
                    if not isinstance(point, dict) or not {"x", "y", "z"}.issubset(point) or set(point) - {"x", "y", "z", "anchorId"}:
                        raise RoadAuthoringContractError("Span endpoints require x, y, z and optional anchorId.")
                    _point({axis: point[axis] for axis in ("x", "y", "z")})
                    if "anchorId" in point:
                        _id(point["anchorId"])
                if "profileRevisionId" in operation:
                    _id(operation["profileRevisionId"])
                elif "profileRevisionId" not in spec:
                    raise RoadAuthoringContractError("Span requires a profileRevisionId on the goal or operation.")
            elif operation["kind"] == "roundabout":
                _known_fields(operation, {"kind", "center", "radiusMeters"}, {"profileRevisionId"})
                _point(operation["center"])
                _number(operation["radiusMeters"], positive=True, maximum=1000)
                if "profileRevisionId" in operation:
                    _id(operation["profileRevisionId"])
                elif "profileRevisionId" not in spec:
                    raise RoadAuthoringContractError("Roundabout requires a profileRevisionId on the goal or operation.")
            elif operation["kind"] == "demolish":
                _known_fields(operation, {"kind", "targetId"}, set())
                _id(operation["targetId"])
            else:
                raise RoadAuthoringContractError("Unknown road operation kind.")
        for name in ("exactSegmentIds", "connectionIds"):
            if name in spec:
                _ids(spec[name], allow_empty=True)
    elif intent == "reprofile":
        _known_fields(spec, {"segmentIds", "targetRevisionId", "scope"}, set())
        if not isinstance(spec["scope"], str) or spec["scope"] not in {"exact", "all_known"} or not isinstance(spec["targetRevisionId"], str) or not 1 <= len(spec["targetRevisionId"]) <= 128:
            raise RoadAuthoringContractError("reprofile requires scope exact/all_known and a targetRevisionId.")
        _ids(spec["segmentIds"], allow_empty=spec["scope"] == "all_known")
    elif intent == "decoration":
        _known_fields(spec, {"segmentIds", "splineSpawnerProfileGuid", "rules"}, {"zoneId"})
        _ids(spec["segmentIds"])
        if not isinstance(spec["splineSpawnerProfileGuid"], str) or not _HEX32.fullmatch(spec["splineSpawnerProfileGuid"]):
            raise RoadAuthoringContractError("decoration requires an exact SplineSpawner profile GUID.")
        rules = spec["rules"]
        if not isinstance(rules, dict):
            raise RoadAuthoringContractError("decoration rules must be a typed object.")
        _known_fields(rules, {"biomeId", "seed", "enabledRuleIds", "segmentOverrides"}, set())
        _id(rules["biomeId"])
        if not isinstance(rules["seed"], int) or isinstance(rules["seed"], bool) or abs(rules["seed"]) > 2**31 - 1:
            raise RoadAuthoringContractError("Decoration seed must be a signed 32-bit integer.")
        _ids(rules["enabledRuleIds"])
        if len(rules["enabledRuleIds"]) > 32:
            raise RoadAuthoringContractError("Decoration has at most 32 enabled rules.")
        overrides = rules["segmentOverrides"]
        if not isinstance(overrides, list) or len(overrides) > 64:
            raise RoadAuthoringContractError("Decoration has at most 64 exact rule overrides.")
        override_keys = set()
        for item in overrides:
            if not isinstance(item, dict):
                raise RoadAuthoringContractError("Decoration overrides must be objects.")
            _known_fields(item, {"segmentId", "ruleId"},
                          {"enabled", "side", "densityMultiplier", "lateralOffsetMeters"})
            _id(item["segmentId"]); _id(item["ruleId"])
            if item["segmentId"] not in spec["segmentIds"] or item["ruleId"] not in rules["enabledRuleIds"]:
                raise RoadAuthoringContractError("Decoration override must target a selected segment and enabled rule.")
            key = (item["segmentId"], item["ruleId"])
            if key in override_keys:
                raise RoadAuthoringContractError("Decoration override IDs must be unique.")
            override_keys.add(key)
            if "enabled" in item and not isinstance(item["enabled"], bool):
                raise RoadAuthoringContractError("Decoration enabled override must be boolean.")
            if "side" in item and item["side"] not in ("left", "right", "both"):
                raise RoadAuthoringContractError("Decoration side must be left, right or both.")
            if "densityMultiplier" in item:
                _number(item["densityMultiplier"])
                if not 0 <= item["densityMultiplier"] <= 1:
                    raise RoadAuthoringContractError("Decoration densityMultiplier must be in [0,1].")
            if "lateralOffsetMeters" in item:
                _number(item["lateralOffsetMeters"])
        if "zoneId" in spec and (not isinstance(spec["zoneId"], str) or not 1 <= len(spec["zoneId"]) <= 128):
            raise RoadAuthoringContractError("zoneId must be a bounded exact ID.")
    elif intent == "bake":
        _known_fields(spec, set(), {"affectedSegmentIds"})
        if "affectedSegmentIds" in spec:
            _ids(spec["affectedSegmentIds"], allow_empty=True)
    elif intent == "repair_helpers":
        _known_fields(spec, {"helperGlobalObjectIds"}, set())
        _ids(spec["helperGlobalObjectIds"])


def _point(value: Any) -> None:
    if not isinstance(value, dict) or set(value) != {"x", "y", "z"}:
        raise RoadAuthoringContractError("Spatial points require x, y, z only.")
    if any(not isinstance(value[axis], (int, float)) or isinstance(value[axis], bool) or
           abs(value[axis]) > 10**15 or
           isinstance(value[axis], float) and not math.isfinite(value[axis])
           for axis in ("x", "y", "z")):
        raise RoadAuthoringContractError("Spatial coordinates must be finite bounded numbers.")


def _id(value: Any) -> None:
    if not isinstance(value, str) or not 1 <= len(value) <= 128:
        raise RoadAuthoringContractError("Expected a bounded exact ID.")


def _guid(value: Any) -> None:
    if not isinstance(value, str) or not _HEX32.fullmatch(value):
        raise RoadAuthoringContractError("Expected a 32-character Unity asset GUID.")


def _number(value: Any, *, positive: bool = False, maximum: float = 10**15) -> None:
    if (not isinstance(value, (int, float)) or isinstance(value, bool) or
            abs(value) > maximum or positive and value <= 0 or
            isinstance(value, float) and not math.isfinite(value)):
        raise RoadAuthoringContractError("Expected a finite bounded numeric value.")


def _points(value: Any, minimum: int = 2) -> None:
    if not isinstance(value, list) or not minimum <= len(value) <= MAX_COLLECTION_ITEMS:
        raise RoadAuthoringContractError("Path vertices exceed the bounded count.")
    for point in value:
        _point(point)


def _parking_spec(spec: dict[str, Any]) -> None:
    _known_fields(spec, {"boundary", "aisles", "rows", "entrances", "pedestrianRoutes", "zones",
                         "pedestrianEntrances", "markingTemplateGuid", "surfaceMaterialGuid"},
                  {"lotId", "pedestrianLinks", "publicRoadOperations"})
    if "lotId" in spec:
        _id(spec["lotId"])
    _guid(spec["markingTemplateGuid"])
    _guid(spec["surfaceMaterialGuid"])
    road_operations = spec.get("publicRoadOperations", [])
    if not isinstance(road_operations, list) or len(road_operations) > 16:
        raise RoadAuthoringContractError("publicRoadOperations must contain at most 16 spans.")
    driveway_keys = set()
    for operation in road_operations:
        if not isinstance(operation, dict):
            raise RoadAuthoringContractError("Public road operation must be an object.")
        _known_fields(operation, {"kind", "resultKey", "profileRevisionId", "start", "end"}, set())
        if operation["kind"] != "span":
            raise RoadAuthoringContractError("Parking public road operation must be a span.")
        _id(operation["resultKey"]); _id(operation["profileRevisionId"])
        if operation["resultKey"] in driveway_keys:
            raise RoadAuthoringContractError("Parking driveway resultKey values must be unique.")
        driveway_keys.add(operation["resultKey"])
        for key in ("start", "end"):
            point = operation[key]
            if not isinstance(point, dict) or not {"x", "y", "z"}.issubset(point) or set(point) - {"x", "y", "z", "anchorId"}:
                raise RoadAuthoringContractError("Parking public span endpoints require x, y, z and optional anchorId.")
            _point({axis: point[axis] for axis in ("x", "y", "z")})
            if "anchorId" in point:
                _id(point["anchorId"])
    boundary = spec["boundary"]
    if not isinstance(boundary, dict) or len(boundary) != 1:
        raise RoadAuthoringContractError("Parking boundary requires exactly vertices or rectangle.")
    if "vertices" in boundary:
        _points(boundary["vertices"], 3)
    elif "rectangle" in boundary:
        rectangle = boundary["rectangle"]
        if not isinstance(rectangle, dict):
            raise RoadAuthoringContractError("Parking rectangle must be an object.")
        _known_fields(rectangle, {"center", "size", "rotationDeg"}, set())
        _point(rectangle["center"])
        size = rectangle["size"]
        if not isinstance(size, dict) or set(size) != {"x", "z"}:
            raise RoadAuthoringContractError("Parking rectangle size requires x and z.")
        _number(size["x"], positive=True)
        _number(size["z"], positive=True)
        _number(rectangle["rotationDeg"])
    else:
        raise RoadAuthoringContractError("Parking boundary requires vertices or rectangle.")
    for name, minimum in (("aisles", 1), ("rows", 1), ("entrances", 1),
                          ("pedestrianRoutes", 0), ("pedestrianEntrances", 0),
                          ("pedestrianLinks", 0), ("zones", 0)):
        entries = spec.get(name, [])
        if not isinstance(entries, list) or not minimum <= len(entries) <= MAX_COLLECTION_ITEMS:
            raise RoadAuthoringContractError(f"Parking {name} count is out of range.")
        if any(not isinstance(entry, dict) for entry in entries):
            raise RoadAuthoringContractError(f"Parking {name} entries must be objects.")
    all_ids: list[str] = []
    for aisle in spec["aisles"]:
        _known_fields(aisle, {"id", "points", "widthMeters", "modeMask", "direction",
                              "speedMetersPerSecond", "costMultiplier"}, set())
        _id(aisle["id"]); all_ids.append(aisle["id"])
        _points(aisle["points"])
        for key in ("widthMeters", "speedMetersPerSecond", "costMultiplier"):
            _number(aisle[key], positive=True)
        if not isinstance(aisle["modeMask"], int) or isinstance(aisle["modeMask"], bool) or not 1 <= aisle["modeMask"] <= 15:
            raise RoadAuthoringContractError("Aisle modeMask is invalid.")
        _id(aisle["direction"])
    for row in spec["rows"]:
        _known_fields(row, {"id", "aisleId", "side", "angleDegrees", "firstOffsetMeters", "count",
                            "stallWidthMeters", "stallLengthMeters", "gapMeters", "stallClass", "allowedModes"},
                      {"allowedVehicleClasses", "overrides"})
        _id(row["id"]); all_ids.append(row["id"])
        _id(row["aisleId"])
        if row["side"] not in ("left", "right") or row["angleDegrees"] not in (0, 45, 60, 90):
            raise RoadAuthoringContractError("Parking row side or angle is unsupported.")
        _number(row["firstOffsetMeters"])
        if row["firstOffsetMeters"] < 0:
            raise RoadAuthoringContractError("Parking row firstOffsetMeters must be nonnegative.")
        for key in ("stallWidthMeters", "stallLengthMeters"):
            _number(row[key], positive=True)
        _number(row["gapMeters"])
        if row["gapMeters"] < 0:
            raise RoadAuthoringContractError("Parking row gapMeters must be nonnegative.")
        if not isinstance(row["count"], int) or isinstance(row["count"], bool) or not 1 <= row["count"] <= 256:
            raise RoadAuthoringContractError("Parking row count is out of range.")
        if row["stallClass"] not in ("Standard", "Accessible", "Service"):
            raise RoadAuthoringContractError("Parking row stallClass is unsupported.")
        if not isinstance(row["allowedModes"], int) or isinstance(row["allowedModes"], bool) or not 1 <= row["allowedModes"] <= 15 or row["allowedModes"] & 4:
            raise RoadAuthoringContractError("Parking row allowedModes is invalid.")
        if "allowedVehicleClasses" in row and (not isinstance(row["allowedVehicleClasses"], int) or
                                                isinstance(row["allowedVehicleClasses"], bool) or
                                                not 1 <= row["allowedVehicleClasses"] <= 63):
            raise RoadAuthoringContractError("Parking row allowedVehicleClasses is invalid.")
        if "overrides" in row:
            overrides = row["overrides"]
            if not isinstance(overrides, list) or len(overrides) > row["count"]:
                raise RoadAuthoringContractError("Parking row overrides exceed its count.")
            slots = set()
            for override in overrides:
                if not isinstance(override, dict):
                    raise RoadAuthoringContractError("Parking stall override must be an object.")
                _known_fields(override, {"slotIndex", "stallClass", "allowedModes"},
                              {"allowedVehicleClasses", "widthMeters", "lengthMeters",
                               "adjacentAccessAisleMeters", "markingTemplateGuid"})
                slot = override["slotIndex"]
                if not isinstance(slot, int) or isinstance(slot, bool) or not 0 <= slot < row["count"] or slot in slots:
                    raise RoadAuthoringContractError("Parking stall slotIndex must be unique within the row.")
                slots.add(slot)
                if (override["stallClass"] not in ("Standard", "Accessible", "Service") or
                        not isinstance(override["allowedModes"], int) or isinstance(override["allowedModes"], bool) or
                        not 1 <= override["allowedModes"] <= 15 or override["allowedModes"] & 4):
                    raise RoadAuthoringContractError("Parking stall class or allowedModes is invalid.")
                if "allowedVehicleClasses" in override and (not isinstance(override["allowedVehicleClasses"], int) or
                    isinstance(override["allowedVehicleClasses"], bool) or not 1 <= override["allowedVehicleClasses"] <= 63):
                    raise RoadAuthoringContractError("Parking stall allowedVehicleClasses is invalid.")
                for dimension in ("widthMeters", "lengthMeters"):
                    if dimension in override:
                        _number(override[dimension], positive=True)
                if "adjacentAccessAisleMeters" in override:
                    _number(override["adjacentAccessAisleMeters"])
                    if override["adjacentAccessAisleMeters"] < 0:
                        raise RoadAuthoringContractError("Adjacent access aisle width must be nonnegative.")
                if "markingTemplateGuid" in override:
                    _guid(override["markingTemplateGuid"])
    for entrance in spec["entrances"]:
        _known_fields(entrance, {"id", "aisleId", "aisleLaneKey", "pathLocal", "modes"},
                      {"inboundRoadLaneKey", "outboundRoadLaneKey", "inboundRoadPointWorld",
                       "outboundRoadPointWorld", "roadPointWorld", "inboundControl", "outboundControl",
                       "priority", "signalGroupId", "controlPointLocal", "widthMeters",
                       "segmentId", "drivewayResultKey"})
        _id(entrance["id"]); all_ids.append(entrance["id"])
        if ("segmentId" in entrance) == ("drivewayResultKey" in entrance):
            raise RoadAuthoringContractError("Vehicle entrance requires exactly segmentId or drivewayResultKey.")
        if "segmentId" in entrance:
            _id(entrance["segmentId"])
        else:
            _id(entrance["drivewayResultKey"])
            if entrance["drivewayResultKey"] not in driveway_keys:
                raise RoadAuthoringContractError("Vehicle drivewayResultKey is not declared by publicRoadOperations.")
        for key in ("aisleId", "aisleLaneKey"):
            _id(entrance[key])
        if "inboundRoadLaneKey" not in entrance and "outboundRoadLaneKey" not in entrance:
            raise RoadAuthoringContractError("Entrance requires an inbound or outbound road lane.")
        for key in ("inboundRoadLaneKey", "outboundRoadLaneKey"):
            if key in entrance:
                _id(entrance[key])
        for lane_key, point_key in (("inboundRoadLaneKey", "inboundRoadPointWorld"),
                                    ("outboundRoadLaneKey", "outboundRoadPointWorld")):
            if (lane_key in entrance) != (point_key in entrance):
                raise RoadAuthoringContractError("Each directed entrance lane requires its own road endpoint point.")
            if point_key in entrance:
                _point(entrance[point_key])
        _points(entrance["pathLocal"])
        if "roadPointWorld" in entrance:
            _point(entrance["roadPointWorld"])
        if "widthMeters" in entrance:
            _number(entrance["widthMeters"], positive=True)
        controls = (entrance.get("inboundControl", "Yield"), entrance.get("outboundControl", "Stop"))
        if any(control not in ("Uncontrolled", "Stop", "Yield", "Priority", "Signal") for control in controls):
            raise RoadAuthoringContractError("Parking entrance control kind is unsupported.")
        if "priority" in entrance and (not isinstance(entrance["priority"], int) or
                                        isinstance(entrance["priority"], bool) or
                                        not -100 <= entrance["priority"] <= 100):
            raise RoadAuthoringContractError("Parking entrance priority must be -100..100.")
        if "Signal" in controls:
            if "signalGroupId" not in entrance:
                raise RoadAuthoringContractError("Signal entrance requires signalGroupId.")
            _id(entrance["signalGroupId"])
        elif "signalGroupId" in entrance:
            _id(entrance["signalGroupId"])
        if any(control in ("Stop", "Yield", "Signal") for control in controls):
            if "controlPointLocal" not in entrance:
                raise RoadAuthoringContractError("Controlled entrance requires controlPointLocal.")
        if "controlPointLocal" in entrance:
            _point(entrance["controlPointLocal"])
        if not isinstance(entrance["modes"], int) or isinstance(entrance["modes"], bool) or not 1 <= entrance["modes"] <= 15:
            raise RoadAuthoringContractError("Entrance modes are invalid.")
    row_counts = {row["id"]: row["count"] for row in spec["rows"]}
    for route in spec["pedestrianRoutes"]:
        _known_fields(route, {"id", "points", "widthMeters"},
                      {"linkedStallIds", "linkedStallSlots"})
        _id(route["id"]); all_ids.append(route["id"])
        _points(route["points"])
        _number(route["widthMeters"], positive=True)
        if "linkedStallIds" in route:
            _ids(route["linkedStallIds"], allow_empty=True)
        if "linkedStallSlots" in route:
            slots = route["linkedStallSlots"]
            if not isinstance(slots, list) or len(slots) > MAX_COLLECTION_ITEMS:
                raise RoadAuthoringContractError("linkedStallSlots exceeds the bounded count.")
            seen_slots = set()
            for slot in slots:
                if not isinstance(slot, dict):
                    raise RoadAuthoringContractError("linkedStallSlots entries must be objects.")
                _known_fields(slot, {"rowId", "slotIndex"}, set())
                _id(slot["rowId"])
                if (slot["rowId"] not in row_counts or not isinstance(slot["slotIndex"], int) or
                        isinstance(slot["slotIndex"], bool) or not 0 <= slot["slotIndex"] < row_counts[slot["rowId"]]):
                    raise RoadAuthoringContractError("linkedStallSlots must address an exact row slot.")
                key = (slot["rowId"], slot["slotIndex"])
                if key in seen_slots:
                    raise RoadAuthoringContractError("linkedStallSlots must be unique within the route.")
                seen_slots.add(key)
    route_ids = {route["id"] for route in spec["pedestrianRoutes"]}
    for entrance in spec["pedestrianEntrances"]:
        _known_fields(entrance, {"id", "routeId", "routeDistanceMeters", "pathLocal"},
                      {"inboundRoadLaneKey", "inboundRoadPointWorld", "outboundRoadLaneKey",
                       "outboundRoadPointWorld", "control", "priority", "signalGroupId", "controlPointLocal",
                       "segmentId", "drivewayResultKey"})
        _id(entrance["id"]); all_ids.append(entrance["id"])
        if ("segmentId" in entrance) == ("drivewayResultKey" in entrance):
            raise RoadAuthoringContractError("Pedestrian entrance requires exactly segmentId or drivewayResultKey.")
        if "segmentId" in entrance:
            _id(entrance["segmentId"])
        else:
            _id(entrance["drivewayResultKey"])
            if entrance["drivewayResultKey"] not in driveway_keys:
                raise RoadAuthoringContractError("Pedestrian drivewayResultKey is not declared by publicRoadOperations.")
        _id(entrance["routeId"])
        if entrance["routeId"] not in route_ids:
            raise RoadAuthoringContractError("Pedestrian entrance routeId must exist in this lot.")
        _number(entrance["routeDistanceMeters"])
        if entrance["routeDistanceMeters"] < 0:
            raise RoadAuthoringContractError("Pedestrian routeDistanceMeters must be nonnegative.")
        _points(entrance["pathLocal"])
        if "inboundRoadLaneKey" not in entrance and "outboundRoadLaneKey" not in entrance:
            raise RoadAuthoringContractError("Pedestrian entrance requires an inbound or outbound road lane.")
        for lane_key, point_key in (("inboundRoadLaneKey", "inboundRoadPointWorld"),
                                    ("outboundRoadLaneKey", "outboundRoadPointWorld")):
            if (lane_key in entrance) != (point_key in entrance):
                raise RoadAuthoringContractError("Each pedestrian road lane requires its directed world point.")
            if lane_key in entrance:
                _id(entrance[lane_key]); _point(entrance[point_key])
        control = entrance.get("control", "Uncontrolled")
        if control not in ("Uncontrolled", "Stop", "Yield", "Priority", "Signal"):
            raise RoadAuthoringContractError("Pedestrian control kind is unsupported.")
        if "priority" in entrance and (not isinstance(entrance["priority"], int) or
                                        isinstance(entrance["priority"], bool) or
                                        not -100 <= entrance["priority"] <= 100):
            raise RoadAuthoringContractError("Pedestrian priority must be -100..100.")
        if control == "Signal":
            if "signalGroupId" not in entrance:
                raise RoadAuthoringContractError("Signal pedestrian entrance requires signalGroupId.")
            _id(entrance["signalGroupId"])
        elif "signalGroupId" in entrance:
            _id(entrance["signalGroupId"])
        if control in ("Stop", "Yield", "Signal") and "controlPointLocal" not in entrance:
            raise RoadAuthoringContractError("Controlled pedestrian entrance requires controlPointLocal.")
        if "controlPointLocal" in entrance:
            _point(entrance["controlPointLocal"])
    for link in spec.get("pedestrianLinks", []):
        _known_fields(link, {"id", "routeId", "routeDistanceMeters", "pathLocal"},
                      {"stallId", "rowId", "slotIndex"})
        _id(link["id"]); all_ids.append(link["id"])
        _id(link["routeId"])
        if ("stallId" in link) == ("rowId" in link or "slotIndex" in link):
            raise RoadAuthoringContractError("Pedestrian link requires exactly stallId or rowId+slotIndex.")
        if "stallId" in link:
            _id(link["stallId"])
        else:
            if "rowId" not in link or "slotIndex" not in link:
                raise RoadAuthoringContractError("Pedestrian row-slot link requires rowId and slotIndex.")
            _id(link["rowId"])
            if (link["rowId"] not in row_counts or not isinstance(link["slotIndex"], int) or
                    isinstance(link["slotIndex"], bool) or not 0 <= link["slotIndex"] < row_counts[link["rowId"]]):
                raise RoadAuthoringContractError("Pedestrian link must address an exact row slot.")
        if link["routeId"] not in route_ids:
            raise RoadAuthoringContractError("Pedestrian link routeId must exist in this lot.")
        _number(link["routeDistanceMeters"])
        if link["routeDistanceMeters"] < 0:
            raise RoadAuthoringContractError("Pedestrian link routeDistanceMeters must be nonnegative.")
        _points(link["pathLocal"])
    for zone in spec["zones"]:
        _known_fields(zone, {"id", "kind", "polygon", "prefabGuid", "seed", "spacingMeters"}, set())
        _id(zone["id"]); all_ids.append(zone["id"])
        if zone["kind"] not in ("landscape", "lighting"):
            raise RoadAuthoringContractError("Parking zone kind is unsupported.")
        _points(zone["polygon"], 3)
        _guid(zone["prefabGuid"])
        if not isinstance(zone["seed"], int) or isinstance(zone["seed"], bool) or abs(zone["seed"]) > 2**31 - 1:
            raise RoadAuthoringContractError("Parking zone seed is out of range.")
        _number(zone["spacingMeters"], positive=True)
    if len(all_ids) != len(set(all_ids)):
        raise RoadAuthoringContractError("Parking item IDs must be unique.")
    aisle_ids = {aisle["id"] for aisle in spec["aisles"]}
    if any(row["aisleId"] not in aisle_ids for row in spec["rows"]) or any(
            entrance["aisleId"] not in aisle_ids for entrance in spec["entrances"]):
        raise RoadAuthoringContractError("Rows and entrances must reference an exact aisleId in the lot.")


def _inspection_payload(action: str, payload: dict[str, Any]) -> None:
    if action in {"status", "selection", "goal_status", "receipt"}:
        _known_fields(payload, set(), set())
    elif action == "catalog":
        _known_fields(payload, set(), {"cursor", "limit", "tags", "modes"})
    elif action == "network":
        _known_fields(payload, set(), {"cursor", "limit", "bounds", "include"})
        if "bounds" in payload:
            bounds = payload["bounds"]
            if not isinstance(bounds, dict) or set(bounds) != {"min", "max"}:
                raise RoadAuthoringContractError("network bounds require min and max points.")
            _point(bounds["min"])
            _point(bounds["max"])
            if any(bounds["min"][axis] > bounds["max"][axis] for axis in ("x", "y", "z")):
                raise RoadAuthoringContractError("network bounds min must not exceed max.")
        if "include" in payload and payload["include"] not in ("segments", "junctions", "both"):
            raise RoadAuthoringContractError("network include must be segments, junctions or both.")
    elif action == "validate":
        _known_fields(payload, {"scope"}, {"ids"})
        if payload["scope"] not in ("catalog", "manifest", "graph"):
            raise RoadAuthoringContractError("validate scope must be catalog, manifest or graph.")
        if "ids" in payload:
            _ids(payload["ids"], allow_empty=True)
    elif action == "spatial_query":
        _known_fields(payload, {"center", "radiusMeters"}, {"kind", "modes", "limit"})
        _point(payload["center"])
        radius = payload["radiusMeters"]
        if (not isinstance(radius, (int, float)) or isinstance(radius, bool) or
                not 0 < radius <= 10**6 or
                isinstance(radius, float) and not math.isfinite(radius)):
            raise RoadAuthoringContractError("radiusMeters must be a positive finite value up to 1,000,000.")
        if "kind" in payload and payload["kind"] not in ("anchors", "terrain", "clearance", "junctions"):
            raise RoadAuthoringContractError("spatial_query kind is unsupported.")
    elif action == "profile_candidates":
        _known_fields(payload, set(), {"name", "tags", "lanes", "limit"})
        if "lanes" in payload and (not isinstance(payload["lanes"], list) or len(payload["lanes"]) > 32):
            raise RoadAuthoringContractError("Candidate lanes must contain at most 32 entries.")
    elif action == "diagnostics":
        _known_fields(payload, set(), {"cursor", "limit", "severity"})
        if "severity" in payload and payload["severity"] not in ("error", "warning", "all"):
            raise RoadAuthoringContractError("diagnostics severity is unsupported.")
    for key in ("cursor", "name"):
        if key in payload and (not isinstance(payload[key], str) or not 1 <= len(payload[key]) <= 256):
            raise RoadAuthoringContractError(f"{key} must be a bounded nonempty string.")
    if "tags" in payload:
        tags = payload["tags"]
        if (not isinstance(tags, list) or len(tags) > 32 or
                any(not isinstance(tag, str) or not 1 <= len(tag) <= 64 for tag in tags) or
                len(tags) != len(set(tags))):
            raise RoadAuthoringContractError("tags must be up to 32 unique bounded strings.")
    if "modes" in payload and (not isinstance(payload["modes"], int) or isinstance(payload["modes"], bool) or
                               not 0 <= payload["modes"] <= 65535):
        raise RoadAuthoringContractError("modes must be a bounded mode bitmask.")
    if "limit" in payload:
        max_limit = 3 if action == "profile_candidates" else 100
        if not isinstance(payload["limit"], int) or isinstance(payload["limit"], bool) or not 1 <= payload["limit"] <= max_limit:
            raise RoadAuthoringContractError(f"limit must be 1–{max_limit}.")


def _bounded(value: Any, depth: int, remaining: list[int]) -> None:
    remaining[0] -= 1
    if remaining[0] < 0 or depth > MAX_NESTING:
        raise RoadAuthoringContractError("RoadBuilder request exceeds the structure limit.")
    if isinstance(value, dict):
        if len(value) > MAX_COLLECTION_ITEMS or any(not isinstance(k, str) or len(k) > 128 for k in value):
            raise RoadAuthoringContractError("RoadBuilder object exceeds the key or item limit.")
        for item in value.values():
            _bounded(item, depth + 1, remaining)
    elif isinstance(value, list):
        if len(value) > MAX_COLLECTION_ITEMS:
            raise RoadAuthoringContractError("RoadBuilder array exceeds 256 items.")
        for item in value:
            _bounded(item, depth + 1, remaining)
    elif isinstance(value, int) and not isinstance(value, bool):
        if abs(value) > 10**15:
            raise RoadAuthoringContractError("RoadBuilder numeric value exceeds the safe transport range.")
    elif isinstance(value, float):
        if not math.isfinite(value) or abs(value) > 10**15:
            raise RoadAuthoringContractError("RoadBuilder numeric values must be finite.")
    elif value is not None and not isinstance(value, (str, bool, int, float)):
        raise RoadAuthoringContractError("RoadBuilder payload contains an unsupported value.")
    if isinstance(value, str) and len(value) > 32_768:
        raise RoadAuthoringContractError("RoadBuilder string value is too long.")


def build_request(
    *,
    action: str,
    mutation: bool,
    operation_id: str | None = None,
    scene_guid: str | None = None,
    expected_manifest_hash: str | None = None,
    expected_revision_hashes: list[dict[str, str]] | None = None,
    candidate_hash: str | None = None,
    payload: dict[str, Any] | None = None,
) -> dict[str, Any]:
    allowed = MUTATION_ACTIONS if mutation else INSPECTION_ACTIONS
    if action not in allowed:
        raise RoadAuthoringContractError("Unknown or disallowed RoadBuilder action.")
    if mutation and not operation_id:
        raise RoadAuthoringContractError("Mutation requires a stable UUID operation_id.")
    if operation_id is not None:
        try:
            parsed = uuid.UUID(operation_id)
        except (ValueError, AttributeError, TypeError) as exc:
            raise RoadAuthoringContractError("operation_id must be a UUID.") from exc
        if operation_id != str(parsed):
            raise RoadAuthoringContractError("operation_id must use canonical lowercase UUID form.")
    if scene_guid is not None and (not isinstance(scene_guid, str) or not _HEX32.fullmatch(scene_guid)):
        raise RoadAuthoringContractError("scene_guid must be a 32-character Unity asset GUID.")
    if expected_manifest_hash is not None and (not isinstance(expected_manifest_hash, str) or not _HEX64.fullmatch(expected_manifest_hash)):
        raise RoadAuthoringContractError("expected_manifest_hash must be SHA-256.")
    if candidate_hash is not None and (not isinstance(candidate_hash, str) or not _HEX64.fullmatch(candidate_hash)):
        raise RoadAuthoringContractError("candidate_hash must be SHA-256.")
    if action == "commit_goal" and candidate_hash is None:
        raise RoadAuthoringContractError("commit_goal requires the staged candidate_hash.")
    if expected_revision_hashes is not None:
        if not isinstance(expected_revision_hashes, list) or len(expected_revision_hashes) > MAX_REVISION_HASHES:
            raise RoadAuthoringContractError("expected_revision_hashes exceeds the 64-revision limit.")
        revision_ids: list[str] = []
        for item in expected_revision_hashes:
            if not isinstance(item, dict) or set(item) != {"profileRevisionId", "closureHash"}:
                raise RoadAuthoringContractError("Each revision requires profileRevisionId and closureHash only.")
            revision_id, closure_hash = item["profileRevisionId"], item["closureHash"]
            if not isinstance(revision_id, str) or not 1 <= len(revision_id) <= 128 or not isinstance(closure_hash, str) or not _HEX64.fullmatch(closure_hash):
                raise RoadAuthoringContractError("Revision IDs must be bounded and closureHash must be SHA-256.")
            revision_ids.append(revision_id)
        if revision_ids != sorted(set(revision_ids)):
            raise RoadAuthoringContractError("expected_revision_hashes must be sorted by unique profileRevisionId.")
    if payload is not None and not isinstance(payload, dict):
        raise RoadAuthoringContractError("payload must be an object.")
    if action in {"preview_goal", "stage_goal"}:
        if not payload or set(payload) != {"intent", "spec"} or not isinstance(payload.get("intent"), str) or payload["intent"] not in GOAL_INTENTS or not isinstance(payload.get("spec"), dict):
            raise RoadAuthoringContractError("preview_goal and stage_goal require a known intent and typed spec object.")
        _goal_spec(payload["intent"], payload["spec"])
        if payload["intent"] in {"road_path", "reprofile", "decoration", "parking_lot", "adopt", "bake", "repair_helpers"} and scene_guid is None:
            raise RoadAuthoringContractError("Scene goals require scene_guid from inspection.")
        if payload["intent"] in {"road_path", "reprofile", "decoration", "parking_lot", "bake"} and expected_manifest_hash is None:
            raise RoadAuthoringContractError("Managed scene goals require expected_manifest_hash from inspection.")
    if action in {"network", "selection", "spatial_query"} and scene_guid is None:
        raise RoadAuthoringContractError(f"{action} requires scene_guid.")
    if action in {"goal_status", "receipt"} and operation_id is None:
        raise RoadAuthoringContractError(f"{action} requires operation_id.")
    if not mutation:
        _inspection_payload(action, payload or {})

    request: dict[str, Any] = {
        "schemaVersion": 1,
        "operationId": operation_id,
        "sceneGuid": scene_guid,
        "action": action,
        "expectedManifestHash": expected_manifest_hash,
        "expectedRevisionHashes": expected_revision_hashes or [],
        "candidateHash": candidate_hash,
        "payload": payload or {},
    }
    _bounded(request, 0, [MAX_NODES])
    try:
        encoded = json.dumps(request, allow_nan=False, separators=(",", ":"))
    except (TypeError, ValueError) as exc:
        raise RoadAuthoringContractError("RoadBuilder request is not valid JSON.") from exc
    if len(encoded.encode("utf-8")) > MAX_REQUEST_BYTES:
        raise RoadAuthoringContractError("RoadBuilder request exceeds 256 KiB.")
    return request

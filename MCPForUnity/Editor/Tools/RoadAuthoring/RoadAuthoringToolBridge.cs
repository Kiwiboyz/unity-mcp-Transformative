using System;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools.RoadAuthoring
{
    /// <summary>
    /// Portable, opt-in adapter. Project Storm owns all RoadBuilder and scene logic;
    /// this package only checks the public wire contract and invokes its fixed bridge.
    /// </summary>
    public static class RoadAuthoringToolBridge
    {
        private const string BridgeAssembly = "ProjectStorm.RoadAuthoring.Editor";
        private const string BridgeType = "ProjectStorm.RoadAuthoring.Editor.RoadAuthoringBridge";
        private static readonly string[] InspectionActions =
        {
            "status", "catalog", "network", "selection", "validate", "spatial_query",
            "profile_candidates", "goal_status", "receipt", "diagnostics", "drawn_areas"
        };
        private static readonly string[] MutationActions =
        {
            "preview_goal", "stage_goal", "commit_goal", "cancel_goal", "restore_goal"
        };
        private static readonly string[] GoalIntents =
        {
            "asset", "profile", "road_path", "reprofile", "decoration", "parking_lot",
            "adopt", "bake", "repair_helpers", "parking_draw", "plaza", "farm_field"
        };
        // Areas drawn with the Road Builder's draw tools: scene objects needing no adopted scene or manifest.
        private static readonly string[] DrawnAreaIntents = { "parking_draw", "plaza", "farm_field" };
        private static readonly string[] FarmCrops = { "CornGreen", "CornDry", "WheatGreen", "WheatGolden", "WheatStubble" };
        private static readonly string[] RequestFields =
        {
            "schemaVersion", "operationId", "sceneGuid", "action", "expectedManifestHash",
            "expectedRevisionHashes", "candidateHash", "payload"
        };
        private static readonly string[] ResponseStatuses =
        {
            "ok", "draft", "staged", "previewed", "committed", "canceled", "undone",
            "rejected", "dirty", "conflict", "error"
        };

        public static object Invoke(bool mutation, JObject parameters)
        {
            if (parameters == null)
                return new ErrorResponse("road_invalid_request", "Road authoring requires a request object.");
            if (parameters.Properties().Any(property => !RequestFields.Contains(property.Name, StringComparer.Ordinal)))
                return new ErrorResponse("road_unknown_field", "Unknown RoadBuilder request field.");
            if (parameters["schemaVersion"]?.Type != JTokenType.Integer ||
                parameters["schemaVersion"].ToString(Formatting.None) != "1")
                return new ErrorResponse("road_unsupported_schema", "RoadBuilder schemaVersion 1 is required.");
            if (parameters["action"]?.Type != JTokenType.String)
                return new ErrorResponse("road_unknown_action", "RoadBuilder action must be a string.");
            string action = parameters.Value<string>("action");
            if (string.IsNullOrWhiteSpace(action) ||
                !(mutation ? MutationActions : InspectionActions).Contains(action, StringComparer.Ordinal))
                return new ErrorResponse("road_unknown_action", "Unknown or disallowed RoadBuilder action.");

            if (!OptionalString(parameters, "operationId") || !OptionalString(parameters, "sceneGuid") ||
                !OptionalString(parameters, "expectedManifestHash") || !OptionalString(parameters, "candidateHash"))
                return new ErrorResponse("road_invalid_request", "RoadBuilder IDs and hashes must be strings or null.");
            string operationId = parameters.Value<string>("operationId");
            if (mutation && !IsCanonicalUuid(operationId))
                return new ErrorResponse("road_invalid_operation_id", "Mutation requires a stable UUID operationId.");
            if (operationId != null && !IsCanonicalUuid(operationId))
                return new ErrorResponse("road_invalid_operation_id", "operationId must be a canonical lowercase UUID.");
            string sceneGuid = parameters.Value<string>("sceneGuid");
            if (sceneGuid != null && !IsHex(sceneGuid, 32))
                return new ErrorResponse("road_invalid_scene_id", "sceneGuid must be a 32-character Unity asset GUID.");
            string manifestHash = parameters.Value<string>("expectedManifestHash");
            if (manifestHash != null && !IsHex(manifestHash, 64))
                return new ErrorResponse("road_invalid_hash", "expectedManifestHash must be a SHA-256 hash.");
            string candidateHash = parameters.Value<string>("candidateHash");
            if (candidateHash != null && !IsHex(candidateHash, 64))
                return new ErrorResponse("road_invalid_hash", "candidateHash must be a SHA-256 hash.");
            if (action == "commit_goal" && candidateHash == null)
                return new ErrorResponse("road_candidate_hash_required", "Commit requires the exact staged candidateHash.");
            if ((action == "goal_status" || action == "receipt") && operationId == null)
                return new ErrorResponse("road_operation_id_required", "Goal status and receipt inspection require operationId.");
            if ((action == "network" || action == "selection" || action == "spatial_query" || action == "drawn_areas") &&
                sceneGuid == null)
                return new ErrorResponse("road_scene_id_required", "Scene inspection requires sceneGuid.");
            if (parameters["expectedRevisionHashes"] is JToken hashes && hashes.Type != JTokenType.Null)
            {
                if (hashes is not JArray revisionHashes || revisionHashes.Count > 64)
                    return new ErrorResponse("road_invalid_revision_hashes", "expectedRevisionHashes must contain at most 64 entries.");
                string previous = null;
                foreach (var item in revisionHashes)
                {
                    if (item is not JObject revision || revision.Properties().Count() != 2 ||
                        revision.Properties().Any(property => property.Name != "profileRevisionId" && property.Name != "closureHash") ||
                        revision["profileRevisionId"]?.Type != JTokenType.String || revision["closureHash"]?.Type != JTokenType.String)
                        return new ErrorResponse("road_invalid_revision_hashes", "Each revision requires profileRevisionId and closureHash only.");
                    string revisionId = revision.Value<string>("profileRevisionId");
                    if (string.IsNullOrEmpty(revisionId) || revisionId.Length > 128 || !IsHex(revision.Value<string>("closureHash"), 64) ||
                        previous != null && string.CompareOrdinal(previous, revisionId) >= 0)
                        return new ErrorResponse("road_invalid_revision_hashes", "Revisions must have unique sorted IDs and SHA-256 closure hashes.");
                    previous = revisionId;
                }
            }
            if (parameters["payload"] is JToken payload && payload.Type != JTokenType.Null && payload is not JObject)
                return new ErrorResponse("road_invalid_payload", "payload must be a typed object.");
            if (action == "preview_goal" || action == "stage_goal")
            {
                if (parameters["payload"] is not JObject goal || goal["intent"]?.Type != JTokenType.String ||
                    !GoalIntents.Contains(goal.Value<string>("intent"), StringComparer.Ordinal) || goal["spec"] is not JObject ||
                    !FieldsMatch(goal, new[] { "intent", "spec" }, Array.Empty<string>()))
                    return new ErrorResponse("road_payload_required", "Preview and stage require a known intent and typed spec object.");
                string intent = goal.Value<string>("intent");
                string goalSpecError = ValidateGoalSpec(intent, (JObject)goal["spec"]);
                if (goalSpecError != null)
                    return new ErrorResponse("road_invalid_goal_spec", goalSpecError);
                if (new[] { "road_path", "reprofile", "decoration", "parking_lot", "adopt", "bake", "repair_helpers" }
                    .Concat(DrawnAreaIntents).Contains(intent, StringComparer.Ordinal) && sceneGuid == null)
                    return new ErrorResponse("road_scene_id_required", "Scene goals require sceneGuid from inspection.");
                if (new[] { "road_path", "reprofile", "decoration", "parking_lot", "bake" }
                    .Contains(intent, StringComparer.Ordinal) && manifestHash == null)
                    return new ErrorResponse("road_stale_input", "Managed scene goals require expectedManifestHash from inspection.");
            }
            if (!mutation)
            {
                string inspectionError = ValidateInspectionPayload(action, parameters["payload"] as JObject ?? new JObject());
                if (inspectionError != null)
                    return new ErrorResponse("road_invalid_inspection", inspectionError);
            }
            if (!CheckBounds(parameters, 0, new int[] { 4096 }) ||
                System.Text.Encoding.UTF8.GetByteCount(parameters.ToString(Formatting.None)) > 262144)
                return new ErrorResponse("road_request_too_large", "RoadBuilder request exceeds its bounded structure or 256 KiB limit.");

            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == BridgeAssembly);
                var bridge = assembly?.GetType(BridgeType, false);
                if (bridge == null)
                    return new ErrorResponse("road_bridge_unavailable", "Project Storm RoadAuthoring Editor bridge is unavailable. Install and compile the Project Storm authoring assembly.");
                var version = bridge.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                var method = bridge.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (version == null || version.GetValue(null) is not int number || number != 1 ||
                    method == null || method.ReturnType != typeof(string))
                    return new ErrorResponse("road_bridge_incompatible", "Project Storm RoadAuthoring bridge version or signature is incompatible.");
                var response = method.Invoke(null, new object[] { parameters.ToString(Formatting.None) }) as string;
                if (string.IsNullOrWhiteSpace(response))
                    return new ErrorResponse("road_bridge_invalid_response", "Project Storm RoadAuthoring bridge returned no response.");
                var result = JObject.Parse(response);
                if (result["schemaVersion"]?.Type != JTokenType.Integer ||
                    result["schemaVersion"].ToString(Formatting.None) != "1" ||
                    result["status"]?.Type != JTokenType.String ||
                    !ResponseStatuses.Contains(result.Value<string>("status"), StringComparer.Ordinal))
                    return new ErrorResponse("road_bridge_invalid_response", "Project Storm RoadAuthoring bridge returned an invalid response envelope.");
                return result;
            }
            catch (Exception)
            {
                return new ErrorResponse("road_bridge_failure", "Project Storm RoadAuthoring bridge failed. Inspect Unity diagnostics; no retry is assumed safe until the operation receipt is checked.");
            }
        }

        private static bool IsHex(string value, int length) => value != null && value.Length == length &&
            value.All(character => (character >= '0' && character <= '9') ||
                                   (character >= 'a' && character <= 'f') ||
                                   (character >= 'A' && character <= 'F'));

        private static bool IsCanonicalUuid(string value) =>
            value != null && Guid.TryParseExact(value, "D", out var parsed) &&
            string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

        private static bool OptionalString(JObject parameters, string key) =>
            parameters[key] == null || parameters[key].Type == JTokenType.Null || parameters[key].Type == JTokenType.String;

        private static bool CheckBounds(JToken token, int depth, int[] remaining)
        {
            if (--remaining[0] < 0 || depth > 10) return false;
            if (token is JObject obj)
            {
                if (obj.Count > 256 || obj.Properties().Any(property => property.Name.Length > 128)) return false;
                return obj.Properties().All(property => CheckBounds(property.Value, depth + 1, remaining));
            }
            if (token is JArray array)
            {
                if (array.Count > 256) return false;
                return array.All(item => CheckBounds(item, depth + 1, remaining));
            }
            if (token.Type == JTokenType.String && token.Value<string>().Length > 32768) return false;
            if (token.Type == JTokenType.Float)
            {
                var value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            }
            if (token.Type == JTokenType.Integer)
            {
                if (!decimal.TryParse(token.ToString(Formatting.None),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) ||
                    value < -1000000000000000m || value > 1000000000000000m) return false;
            }
            return true;
        }

        private static bool FieldsMatch(JObject obj, string[] required, string[] optional) =>
            required.All(name => obj[name] != null) &&
            obj.Properties().All(property => required.Contains(property.Name, StringComparer.Ordinal) ||
                                              optional.Contains(property.Name, StringComparer.Ordinal));

        private static bool BoundedId(JToken token) => token?.Type == JTokenType.String &&
            !string.IsNullOrEmpty((string)token) && ((string)token).Length <= 128;

        private static bool IdList(JToken token, bool allowEmpty = false)
        {
            if (token is not JArray array || array.Count > 256 || !allowEmpty && array.Count == 0 ||
                array.Any(item => !BoundedId(item))) return false;
            return array.Select(item => (string)item).Distinct(StringComparer.Ordinal).Count() == array.Count;
        }

        private static bool Coordinate(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                return false;
            try
            {
                double value = token.Value<double>();
                return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 1000000000000000d;
            }
            catch (Exception) { return false; }
        }

        private static bool NumberRange(JToken token, double minimum, double maximum, bool strictMinimum = false)
        {
            if (!Coordinate(token)) return false;
            double value = token.Value<double>();
            return (strictMinimum ? value > minimum : value >= minimum) && value <= maximum;
        }

        private static bool GuidToken(JToken token) => token?.Type == JTokenType.String && IsHex((string)token, 32);

        private static bool Point(JToken token) => token is JObject xyz &&
            FieldsMatch(xyz, new[] { "x", "y", "z" }, Array.Empty<string>()) &&
            Coordinate(xyz["x"]) && Coordinate(xyz["y"]) && Coordinate(xyz["z"]);

        private static bool BoundedText(JToken token, int maximum) => token?.Type == JTokenType.String &&
            !string.IsNullOrEmpty((string)token) && ((string)token).Length <= maximum;

        private static bool IntegerRange(JToken token, int minimum, int maximum) =>
            token?.Type == JTokenType.Integer &&
            long.TryParse(token.ToString(Formatting.None), out var number) &&
            number >= minimum && number <= maximum;

        private static bool Tags(JToken token)
        {
            if (token is not JArray tags || tags.Count > 32 || tags.Any(item => !BoundedText(item, 64)))
                return false;
            return tags.Select(item => (string)item).Distinct(StringComparer.Ordinal).Count() == tags.Count;
        }

        private static string ValidateInspectionPayload(string action, JObject payload)
        {
            if (new[] { "status", "selection", "goal_status", "receipt" }.Contains(action, StringComparer.Ordinal))
                return payload.Count == 0 ? null : "This inspection action accepts no payload fields.";
            string[] optional;
            switch (action)
            {
                case "catalog": optional = new[] { "cursor", "limit", "tags", "modes" }; break;
                case "network": optional = new[] { "cursor", "limit", "bounds", "include" }; break;
                case "validate": optional = new[] { "ids" }; break;
                case "spatial_query": optional = new[] { "kind", "modes", "limit" }; break;
                case "profile_candidates": optional = new[] { "name", "tags", "lanes", "limit" }; break;
                case "diagnostics": optional = new[] { "cursor", "limit", "severity" }; break;
                case "drawn_areas": optional = new[] { "kind", "center", "radiusMeters", "limit" }; break;
                default: return "Unsupported inspection action.";
            }
            string[] required = action == "validate" ? new[] { "scope" } :
                action == "spatial_query" ? new[] { "center", "radiusMeters" } : Array.Empty<string>();
            if (!FieldsMatch(payload, required, optional)) return "Inspection payload has missing or unknown fields.";
            if (payload["cursor"] != null && !BoundedText(payload["cursor"], 256) ||
                payload["name"] != null && !BoundedText(payload["name"], 256) ||
                payload["limit"] != null && !IntegerRange(payload["limit"], 1, action == "profile_candidates" ? 3 : 100) ||
                payload["tags"] != null && !Tags(payload["tags"]) ||
                payload["modes"] != null && !IntegerRange(payload["modes"], 0, 65535))
                return "Inspection filter exceeds its bounded type or range.";
            if (action == "network")
            {
                if (payload["include"] != null &&
                    (payload["include"].Type != JTokenType.String ||
                     !new[] { "segments", "junctions", "both" }.Contains((string)payload["include"], StringComparer.Ordinal)))
                    return "network include must be segments, junctions or both.";
                if (payload["bounds"] != null)
                {
                    if (payload["bounds"] is not JObject bounds ||
                        !FieldsMatch(bounds, new[] { "min", "max" }, Array.Empty<string>()) ||
                        !Point(bounds["min"]) || !Point(bounds["max"]))
                        return "network bounds require finite min and max points.";
                    foreach (string axis in new[] { "x", "y", "z" })
                        if (bounds["min"][axis].Value<double>() > bounds["max"][axis].Value<double>())
                            return "network bounds min must not exceed max.";
                }
            }
            if (action == "validate" &&
                (payload["scope"]?.Type != JTokenType.String ||
                 !new[] { "catalog", "manifest", "graph" }.Contains((string)payload["scope"], StringComparer.Ordinal) ||
                 payload["ids"] != null && !IdList(payload["ids"], true)))
                return "validate requires scope catalog/manifest/graph and optional exact IDs.";
            if (action == "spatial_query")
            {
                if (!Point(payload["center"]) || !Coordinate(payload["radiusMeters"]) ||
                    payload["radiusMeters"].Value<double>() <= 0 || payload["radiusMeters"].Value<double>() > 1000000d ||
                    payload["kind"] != null &&
                    (payload["kind"].Type != JTokenType.String ||
                     !new[] { "anchors", "terrain", "clearance", "junctions" }.Contains((string)payload["kind"], StringComparer.Ordinal)))
                    return "spatial_query requires a finite center, positive radius and known kind.";
            }
            if (action == "profile_candidates" && payload["lanes"] != null &&
                (payload["lanes"] is not JArray lanes || lanes.Count > 32))
                return "profile_candidates lanes must contain at most 32 entries.";
            if (action == "drawn_areas")
            {
                if (payload["kind"] != null &&
                    (payload["kind"].Type != JTokenType.String ||
                     !new[] { "parking_draw", "plaza", "farm_field", "both" }.Contains((string)payload["kind"], StringComparer.Ordinal)))
                    return "drawn_areas kind must be parking_draw, plaza, farm_field or both.";
                if (payload["center"] != null && !Point(payload["center"]))
                    return "drawn_areas center must be a finite point.";
                if (payload["radiusMeters"] != null &&
                    (payload["center"] == null || !NumberRange(payload["radiusMeters"], 0, 1000000d, true)))
                    return "drawn_areas radiusMeters needs center and must be positive.";
            }
            if (action == "diagnostics" && payload["severity"] != null &&
                (payload["severity"].Type != JTokenType.String ||
                 !new[] { "error", "warning", "all" }.Contains((string)payload["severity"], StringComparer.Ordinal)))
                return "diagnostics severity must be error, warning or all.";
            return null;
        }

        private static string ValidateGoalSpec(string intent, JObject spec)
        {
            if (intent == "asset")
            {
                if (!FieldsMatch(spec, new[] { "assetKind", "name" },
                    new[] { "sourceAssetGuid", "shaderGuid", "textureGuids", "prefabGuids", "rgba" }) ||
                    spec["assetKind"]?.Type != JTokenType.String ||
                    !new[] { "texture_import", "material", "prefab_composition", "marking_template" }
                        .Contains((string)spec["assetKind"], StringComparer.Ordinal) ||
                    !BoundedText(spec["name"], 128))
                    return "asset requires a supported assetKind and bounded name.";
                foreach (string key in new[] { "sourceAssetGuid", "shaderGuid" })
                    if (spec[key] != null && !GuidToken(spec[key])) return key + " must be an exact asset GUID.";
                foreach (string key in new[] { "textureGuids", "prefabGuids" })
                    if (spec[key] != null && (spec[key] is not JArray array || array.Count > 256 ||
                        array.Any(item => !GuidToken(item)) ||
                        array.Select(item => (string)item).Distinct(StringComparer.Ordinal).Count() != array.Count))
                        return key + " must contain unique exact asset GUIDs.";
                if (spec["rgba"] != null && (spec["rgba"] is not JArray rgba || rgba.Count != 4 ||
                    rgba.Any(channel => !NumberRange(channel, 0, 1))))
                    return "rgba must contain four finite channels in [0,1].";
            }
            else if (intent == "profile")
            {
                if (!FieldsMatch(spec, new[] { "mode", "name", "semanticTags", "lanes", "crossSection", "oneWay" },
                    new[] { "sourceRoadName", "sourceRevisionId", "exactSegmentIds" }) ||
                    spec["mode"]?.Type != JTokenType.String ||
                    !new[] { "reuse", "new", "variant", "global_update" }
                        .Contains((string)spec["mode"], StringComparer.Ordinal) ||
                    !BoundedText(spec["name"], 128) || !Tags(spec["semanticTags"]) ||
                    spec["oneWay"]?.Type != JTokenType.Boolean ||
                    spec["lanes"] is not JArray lanes || lanes.Count < 1 || lanes.Count > 32)
                    return "profile requires mode, bounded name, semanticTags and 1–32 lanes.";
                foreach (string key in new[] { "sourceRoadName", "sourceRevisionId" })
                    if (spec[key] != null && !BoundedId(spec[key])) return key + " must be a bounded ID.";
                if (spec["exactSegmentIds"] != null && !IdList(spec["exactSegmentIds"], true))
                    return "exactSegmentIds must be unique bounded IDs.";
                if (spec["crossSection"] is not JObject crossSection ||
                    !FieldsMatch(crossSection, new[] { "surfaces" }, Array.Empty<string>()) ||
                    crossSection["surfaces"] is not JArray surfaces || surfaces.Count < 1 || surfaces.Count > 64)
                    return "profile requires 1–64 typed physical cross-section surfaces.";
                var surfaceKeys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var item in surfaces)
                {
                    if (item is not JObject surface || !FieldsMatch(surface,
                        new[] { "surfaceKey", "role", "side", "innerOffsetMeters", "outerOffsetMeters",
                                "heightMeters", "materialGuid", "closedEnds" }, new[] { "markingTemplateGuid" }) ||
                        !BoundedId(surface["surfaceKey"]) || !surfaceKeys.Add((string)surface["surfaceKey"]) ||
                        surface["role"]?.Type != JTokenType.String ||
                        !new[] { "carriageway", "parking", "cycle", "sidewalk", "shoulder", "marking" }
                            .Contains((string)surface["role"], StringComparer.Ordinal) ||
                        surface["side"]?.Type != JTokenType.String ||
                        !new[] { "left", "right", "center" }.Contains((string)surface["side"], StringComparer.Ordinal) ||
                        !NumberRange(surface["innerOffsetMeters"], -1000, 1000) ||
                        !NumberRange(surface["outerOffsetMeters"], -1000, 1000) ||
                        !NumberRange(surface["heightMeters"], -1000, 1000) ||
                        !GuidToken(surface["materialGuid"]) ||
                        surface["closedEnds"]?.Type != JTokenType.Boolean ||
                        surface["markingTemplateGuid"] != null && !GuidToken(surface["markingTemplateGuid"]))
                        return "Physical surface keys, roles, sides, offsets or material GUIDs are invalid.";
                }
                var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var item in lanes)
                {
                    if (item is not JObject lane || !FieldsMatch(lane,
                        new[] { "laneKey", "role", "modes", "direction", "widthMeters",
                                "speedMetersPerSecond", "costMultiplier", "permittedManeuvers",
                                "surfaceKey", "centerOffsetMeters" }, Array.Empty<string>()) ||
                        !BoundedText(lane["laneKey"], 64) || !keys.Add((string)lane["laneKey"]) ||
                        !BoundedText(lane["role"], 64) || !BoundedText(lane["direction"], 64) ||
                        !new[] { "through", "parking", "cycle", "pedestrian", "shoulder" }
                            .Contains((string)lane["role"], StringComparer.Ordinal) ||
                        !new[] { "forward", "backward", "both" }
                            .Contains((string)lane["direction"], StringComparer.Ordinal) ||
                        !IntegerRange(lane["modes"], 1, 15) ||
                        !BoundedId(lane["surfaceKey"]) || !surfaceKeys.Contains((string)lane["surfaceKey"]) ||
                        !NumberRange(lane["centerOffsetMeters"], -1000, 1000) ||
                        !NumberRange(lane["widthMeters"], 0, 100, true) ||
                        !NumberRange(lane["speedMetersPerSecond"], 0, 1000, true) ||
                        !NumberRange(lane["costMultiplier"], 0, 1000, true) ||
                        !IdList(lane["permittedManeuvers"], true))
                        return "Profile lane fields, modes, dimensions or unique laneKey are invalid.";
                    var maneuvers = ((JArray)lane["permittedManeuvers"]).Select(value => (string)value).ToArray();
                    if (maneuvers.Any(value => !new[] { "straight", "left", "right", "merge", "roundabout", "crossing", "u_turn" }
                        .Contains(value, StringComparer.Ordinal)))
                        return "Profile lane has an unknown manoeuvre.";
                    string role = (string)lane["role"];
                    int modes = (int)lane["modes"];
                    if (role == "parking" && maneuvers.Any(value =>
                            value == "straight" || value == "left" || value == "right" ||
                            value == "roundabout" || value == "u_turn") ||
                        role == "cycle" && ((modes & 2) == 0 || (modes & 1) != 0) ||
                        role == "pedestrian" && ((modes & 4) == 0 || (modes & 1) != 0))
                        return "Profile lane access or parking manoeuvres are invalid.";
                }
            }
            else if (intent == "adopt")
            {
                if (!FieldsMatch(spec, new[] { "includePrefabInstances" }, Array.Empty<string>()) ||
                    spec["includePrefabInstances"]?.Type != JTokenType.Boolean)
                    return "adopt requires includePrefabInstances boolean.";
            }
            else if (intent == "parking_lot")
            {
                return ValidateParkingSpec(spec);
            }
            else if (intent == "road_path")
            {
                if (!FieldsMatch(spec, new[] { "operations" },
                    new[] { "profileRevisionId", "exactSegmentIds", "connectionIds" }) ||
                    spec["profileRevisionId"] != null && !BoundedId(spec["profileRevisionId"]) ||
                    spec["operations"] is not JArray operations || operations.Count < 1 || operations.Count > 64)
                    return "road_path requires 1–64 ordered typed operations.";
                foreach (var item in operations)
                {
                    if (item is not JObject operation || operation["kind"]?.Type != JTokenType.String)
                        return "Road operation requires a typed kind.";
                    switch ((string)operation["kind"])
                    {
                        case "span":
                            if (!FieldsMatch(operation, new[] { "kind", "start", "end" }, new[] { "profileRevisionId" }) ||
                                !Waypoint(operation["start"]) || !Waypoint(operation["end"]) ||
                                operation["profileRevisionId"] != null && !BoundedId(operation["profileRevisionId"]) ||
                                operation["profileRevisionId"] == null && spec["profileRevisionId"] == null)
                                return "Span requires exact finite endpoints and a profileRevisionId.";
                            break;
                        case "roundabout":
                            if (!FieldsMatch(operation, new[] { "kind", "center", "radiusMeters" }, new[] { "profileRevisionId" }) ||
                                !Point(operation["center"]) || !NumberRange(operation["radiusMeters"], 0, 1000, true) ||
                                operation["profileRevisionId"] != null && !BoundedId(operation["profileRevisionId"]) ||
                                operation["profileRevisionId"] == null && spec["profileRevisionId"] == null)
                                return "Roundabout requires center, positive radius and a profileRevisionId.";
                            break;
                        case "demolish":
                            if (!FieldsMatch(operation, new[] { "kind", "targetId" }, Array.Empty<string>()) ||
                                !BoundedId(operation["targetId"]))
                                return "Demolish requires an exact targetId.";
                            break;
                        default: return "Unknown road operation kind.";
                    }
                }
                foreach (string key in new[] { "exactSegmentIds", "connectionIds" })
                    if (spec[key] != null && !IdList(spec[key], true)) return key + " must be unique bounded IDs.";
            }
            else if (intent == "reprofile")
            {
                if (!FieldsMatch(spec, new[] { "segmentIds", "targetRevisionId", "scope" }, Array.Empty<string>()) ||
                    !BoundedId(spec["targetRevisionId"]) || spec["scope"]?.Type != JTokenType.String ||
                    ((string)spec["scope"] != "exact" && (string)spec["scope"] != "all_known") ||
                    !IdList(spec["segmentIds"], (string)spec["scope"] == "all_known"))
                    return "reprofile requires exact segmentIds, targetRevisionId and scope exact/all_known.";
            }
            else if (intent == "decoration")
            {
                if (!FieldsMatch(spec, new[] { "segmentIds", "splineSpawnerProfileGuid", "rules" }, new[] { "zoneId" }) ||
                    !IdList(spec["segmentIds"]) || spec["splineSpawnerProfileGuid"]?.Type != JTokenType.String ||
                    !IsHex((string)spec["splineSpawnerProfileGuid"], 32) ||
                    spec["rules"] is not JObject || spec["zoneId"] != null && !BoundedId(spec["zoneId"]))
                    return "decoration requires exact segmentIds, SplineSpawner profile GUID and typed rules.";
                var rules = (JObject)spec["rules"];
                if (!FieldsMatch(rules, new[] { "biomeId", "seed", "enabledRuleIds", "segmentOverrides" }, Array.Empty<string>()) ||
                    !BoundedId(rules["biomeId"]) || !IntegerRange(rules["seed"], int.MinValue, int.MaxValue) ||
                    !IdList(rules["enabledRuleIds"]) || ((JArray)rules["enabledRuleIds"]).Count > 32 ||
                    rules["segmentOverrides"] is not JArray overrides || overrides.Count > 64)
                    return "decoration rules require biomeId, signed seed, at most 32 enabled rules and 64 overrides.";
                var selected = new System.Collections.Generic.HashSet<string>(((JArray)spec["segmentIds"]).Select(item => (string)item), StringComparer.Ordinal);
                var enabled = new System.Collections.Generic.HashSet<string>(((JArray)rules["enabledRuleIds"]).Select(item => (string)item), StringComparer.Ordinal);
                var overrideKeys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var item in overrides)
                {
                    if (item is not JObject entry ||
                        !FieldsMatch(entry, new[] { "segmentId", "ruleId" },
                            new[] { "enabled", "side", "densityMultiplier", "lateralOffsetMeters" }) ||
                        !BoundedId(entry["segmentId"]) || !selected.Contains((string)entry["segmentId"]) ||
                        !BoundedId(entry["ruleId"]) || !enabled.Contains((string)entry["ruleId"]) ||
                        !overrideKeys.Add((string)entry["segmentId"] + "\u0000" + (string)entry["ruleId"]) ||
                        entry["enabled"] != null && entry["enabled"].Type != JTokenType.Boolean ||
                        entry["side"] != null && (entry["side"].Type != JTokenType.String ||
                            !new[] { "left", "right", "both" }.Contains((string)entry["side"], StringComparer.Ordinal)) ||
                        entry["densityMultiplier"] != null && !NumberRange(entry["densityMultiplier"], 0, 1) ||
                        entry["lateralOffsetMeters"] != null && !Coordinate(entry["lateralOffsetMeters"]))
                        return "Decoration override must target a selected segment and enabled rule with bounded fields.";
                }
            }
            else if (intent == "bake")
            {
                if (!FieldsMatch(spec, Array.Empty<string>(), new[] { "affectedSegmentIds" }) ||
                    spec["affectedSegmentIds"] != null && !IdList(spec["affectedSegmentIds"], true))
                    return "bake accepts only optional affectedSegmentIds.";
            }
            else if (intent == "repair_helpers")
            {
                if (!FieldsMatch(spec, new[] { "helperGlobalObjectIds" }, Array.Empty<string>()) ||
                    !IdList(spec["helperGlobalObjectIds"]))
                    return "repair_helpers requires exact helperGlobalObjectIds.";
            }
            else if (intent == "parking_draw" || intent == "plaza")
                return ValidateDrawnAreaSpec(intent, spec);
            else if (intent == "farm_field")
                return ValidateFarmSpec(spec);
            // Asset, profile, adoption and parking semantics are validated by the
            // project bridge after the portable wire shape has been checked here.
            return null;
        }

        // Wire shape of the drawn-area goals (matches the server contract); the project bridge checks meaning
        // (surfaces, lot/plaza IDs, suggestion indices) against the scene.
        private static string ValidateDrawnAreaSpec(string intent, JObject spec)
        {
            bool parking = intent == "parking_draw";
            string target = parking ? "lotId" : "plazaId";
            var optional = parking
                ? new[] { "outline", "lotId", "name", "settings", "snapToRoads", "followRoadSlope", "levelGround",
                          "drivewayRoad", "connect", "disconnect", "delete" }
                : new[] { "outline", "plazaId", "name", "surface", "snapToRoads", "followRoadSlope", "levelGround",
                          "footpathAprons", "areas", "addAreas", "removeAreas", "delete" };
            if (!FieldsMatch(spec, Array.Empty<string>(), optional))
                return intent + " spec has unknown fields.";
            if ((spec["outline"] == null) == (spec[target] == null))
                return intent + " requires exactly one of outline (new) or " + target + " (existing).";
            if (spec["outline"] != null && !Outline(spec["outline"]))
                return "outline requires 3–64 vertices or a rectangle {center, size:{x,z}, rotationDeg?}.";
            if (spec[target] != null && !BoundedId(spec[target]))
                return target + " must be a bounded exact ID.";
            foreach (string name in new[] { "name", "drivewayRoad", "surface" })
                if (spec[name] != null && !BoundedText(spec[name], 128))
                    return name + " must be a bounded nonempty string.";
            foreach (string name in new[] { "snapToRoads", "followRoadSlope", "levelGround", "footpathAprons", "delete" })
                if (spec[name] != null && spec[name].Type != JTokenType.Boolean)
                    return name + " must be boolean.";
            if (spec["delete"] != null && (bool)spec["delete"] &&
                spec.Properties().Any(property => property.Name != target && property.Name != "delete"))
                return "delete cannot be combined with other changes.";
            if (parking)
            {
                if (spec["outline"] != null && (spec["disconnect"] != null || spec["delete"] != null))
                    return "disconnect and delete need lotId.";
                if (spec["settings"] != null)
                {
                    if (spec["settings"] is not JObject settings ||
                        !FieldsMatch(settings, Array.Empty<string>(), new[] { "stallWidthMeters", "stallLengthMeters",
                            "angleDegrees", "aisleWidthMeters", "accessiblePairs", "footpath", "footpathWidthMeters", "hatchEntrances" }) ||
                        settings["stallWidthMeters"] != null && !NumberRange(settings["stallWidthMeters"], 2, 4) ||
                        settings["stallLengthMeters"] != null && !NumberRange(settings["stallLengthMeters"], 4, 8) ||
                        settings["aisleWidthMeters"] != null && !NumberRange(settings["aisleWidthMeters"], 3.5, 9) ||
                        settings["footpathWidthMeters"] != null && !NumberRange(settings["footpathWidthMeters"], 0.5, 10) ||
                        settings["accessiblePairs"] != null && !IntegerRange(settings["accessiblePairs"], 0, 6) ||
                        settings["angleDegrees"] != null && !(IntegerRange(settings["angleDegrees"], 0, 90) &&
                            new[] { 0, 45, 60, 90 }.Contains(settings["angleDegrees"].Value<int>())) ||
                        settings["footpath"] != null && !(settings["footpath"].Type == JTokenType.String &&
                            new[] { "none", "pavement", "asphalt", "grass" }.Contains((string)settings["footpath"], StringComparer.Ordinal)) ||
                        settings["hatchEntrances"] != null && settings["hatchEntrances"].Type != JTokenType.Boolean)
                        return "parking_draw settings are out of range or unknown.";
                }
                if (spec["connect"] != null &&
                    (spec["connect"] is not JArray connect || connect.Count < 1 || connect.Count > 8 ||
                     connect.Any(item => item is not JObject entry ||
                         !(FieldsMatch(entry, new[] { "suggestion" }, Array.Empty<string>()) && IntegerRange(entry["suggestion"], 0, 255) ||
                           FieldsMatch(entry, new[] { "roadPoint", "lotPoint" }, Array.Empty<string>()) &&
                           Point(entry["roadPoint"]) && Point(entry["lotPoint"])))))
                    return "connect must list 1–8 of {suggestion} or {roadPoint, lotPoint}.";
                if (spec["disconnect"] != null &&
                    (spec["disconnect"] is not JArray disconnect || disconnect.Count < 1 || disconnect.Count > 8 ||
                     disconnect.Any(item => !IntegerRange(item, 0, 255)) ||
                     disconnect.Select(item => item.Value<int>()).Distinct().Count() != disconnect.Count))
                    return "disconnect must list 1–8 unique entrance indices.";
            }
            else
            {
                if (spec["outline"] != null && (spec["addAreas"] != null || spec["removeAreas"] != null || spec["delete"] != null))
                    return "addAreas, removeAreas and delete need plazaId (a new plaza takes areas).";
                if (spec["plazaId"] != null && spec["areas"] != null)
                    return "An existing plaza takes addAreas, not areas.";
                foreach (string name in new[] { "areas", "addAreas" })
                    if (spec[name] != null &&
                        (spec[name] is not JArray areas || areas.Count < 1 || areas.Count > 64 || areas.Any(area => !PlazaArea(area))))
                        return name + " must list 1–64 areas {surface, shape: polygon|path, points, widthMeters?, raiseMeters?, name?}.";
                if (spec["removeAreas"] != null && !IdList(spec["removeAreas"]))
                    return "removeAreas must list exact area IDs.";
            }
            return null;
        }

        // Wire shape of the farm_field goal (matches the server contract); the project bridge checks meaning
        // (farm and fence IDs, the layout) against the scene.
        private static string ValidateFarmSpec(JObject spec)
        {
            if (!FieldsMatch(spec, Array.Empty<string>(), new[] { "outline", "farmId", "name", "crop", "seed", "rules", "fences",
                                                                   "addFences", "removeFences", "delete" }))
                return "farm_field spec has unknown fields.";
            if ((spec["outline"] == null) == (spec["farmId"] == null))
                return "farm_field requires exactly one of outline (new) or farmId (existing).";
            if (spec["outline"] != null)
            {
                if (!Outline(spec["outline"]))
                    return "outline requires 3–64 vertices or a rectangle {center, size:{x,z}, rotationDeg?}.";
                if (spec["addFences"] != null || spec["removeFences"] != null || spec["delete"] != null)
                    return "addFences, removeFences and delete need farmId (a new farm takes fences).";
                if (spec["crop"] == null) return "A new farm needs a crop.";
            }
            else
            {
                if (!BoundedId(spec["farmId"])) return "farmId must be a bounded exact ID.";
                if (spec["fences"] != null) return "An existing farm takes addFences, not fences.";
            }
            if (spec["name"] != null && !BoundedText(spec["name"], 128)) return "name must be a bounded nonempty string.";
            if (spec["crop"] != null && !(spec["crop"].Type == JTokenType.String && FarmCrops.Contains((string)spec["crop"], StringComparer.Ordinal)))
                return "crop must be one of " + string.Join(", ", FarmCrops) + ".";
            if (spec["seed"] != null && !IntegerRange(spec["seed"], 0, int.MaxValue)) return "seed must be an integer 0–2147483647.";
            if (spec["delete"] != null && spec["delete"].Type != JTokenType.Boolean) return "delete must be boolean.";
            if (spec["delete"] != null && (bool)spec["delete"] &&
                spec.Properties().Any(property => property.Name != "farmId" && property.Name != "delete"))
                return "delete cannot be combined with other changes.";
            if (spec["rules"] != null)
            {
                if (spec["rules"] is not JObject rules ||
                    !FieldsMatch(rules, Array.Empty<string>(), new[] { "minPlotHectares", "maxPlotHectares", "headland",
                        "headlandFromHectares", "headlandWidth", "trackWidth", "tramlines", "paintTerrain" }) ||
                    rules["minPlotHectares"] != null && !NumberRange(rules["minPlotHectares"], 0.25, 200) ||
                    rules["maxPlotHectares"] != null && !NumberRange(rules["maxPlotHectares"], 0.5, 500) ||
                    rules["headlandFromHectares"] != null && !NumberRange(rules["headlandFromHectares"], 0, 10000) ||
                    rules["headlandWidth"] != null && !NumberRange(rules["headlandWidth"], 2, 20) ||
                    rules["trackWidth"] != null && !NumberRange(rules["trackWidth"], 2, 12) ||
                    new[] { "headland", "tramlines", "paintTerrain" }.Any(name => rules[name] != null && rules[name].Type != JTokenType.Boolean))
                    return "farm_field rules are out of range or unknown.";
                if (rules["minPlotHectares"] != null && rules["maxPlotHectares"] != null &&
                    rules["maxPlotHectares"].Value<double>() < rules["minPlotHectares"].Value<double>())
                    return "rules.maxPlotHectares is below minPlotHectares.";
            }
            foreach (string name in new[] { "fences", "addFences" })
                if (spec[name] != null &&
                    (spec[name] is not JArray fences || fences.Count > 32 || name == "addFences" && fences.Count == 0 ||
                     fences.Any(fence => !FarmFence(fence))))
                    return name + " must list up to 32 fences {aroundFarm: true} or {points, closed?} (addFences at least one).";
            if (spec["removeFences"] != null && !IdList(spec["removeFences"]))
                return "removeFences must list exact fence IDs.";
            return null;
        }

        private static bool FarmFence(JToken token)
        {
            if (token is not JObject fence) return false;
            if (fence["aroundFarm"] != null)
                return FieldsMatch(fence, new[] { "aroundFarm" }, Array.Empty<string>()) &&
                       fence["aroundFarm"].Type == JTokenType.Boolean && (bool)fence["aroundFarm"];
            if (!FieldsMatch(fence, new[] { "points" }, new[] { "closed" }) ||
                fence["closed"] != null && fence["closed"].Type != JTokenType.Boolean) return false;
            bool closed = fence["closed"] != null && (bool)fence["closed"];
            return fence["points"] is JArray points && points.Count >= (closed ? 3 : 2) && points.Count <= 64 && points.All(Point);
        }

        private static bool Outline(JToken token)
        {
            if (token is not JObject outline || outline.Count != 1) return false;
            if (outline["vertices"] != null)
                return outline["vertices"] is JArray vertices && vertices.Count >= 3 && vertices.Count <= 64 && vertices.All(Point);
            return outline["rectangle"] is JObject rectangle &&
                   FieldsMatch(rectangle, new[] { "center", "size" }, new[] { "rotationDeg" }) &&
                   Point(rectangle["center"]) && rectangle["size"] is JObject size &&
                   FieldsMatch(size, new[] { "x", "z" }, Array.Empty<string>()) &&
                   NumberRange(size["x"], 0, 2000, true) && NumberRange(size["z"], 0, 2000, true) &&
                   (rectangle["rotationDeg"] == null || NumberRange(rectangle["rotationDeg"], -3600, 3600));
        }

        private static bool PlazaArea(JToken token)
        {
            if (token is not JObject area ||
                !FieldsMatch(area, new[] { "surface", "shape", "points" }, new[] { "widthMeters", "raiseMeters", "name" }) ||
                !BoundedText(area["surface"], 128) || area["name"] != null && !BoundedText(area["name"], 128) ||
                area["shape"]?.Type != JTokenType.String) return false;
            string shape = (string)area["shape"];
            if (shape != "polygon" && shape != "path") return false;
            return area["points"] is JArray points && points.Count >= (shape == "path" ? 2 : 3) && points.Count <= 64 &&
                   points.All(Point) &&
                   (area["widthMeters"] == null || NumberRange(area["widthMeters"], 0, 20, true)) &&
                   (area["raiseMeters"] == null || NumberRange(area["raiseMeters"], 0, 1));
        }

        private static string ValidateParkingSpec(JObject spec)
        {
            if (!FieldsMatch(spec,
                new[] { "boundary", "aisles", "rows", "entrances", "pedestrianRoutes", "zones",
                        "pedestrianEntrances", "markingTemplateGuid", "surfaceMaterialGuid" },
                new[] { "lotId", "pedestrianLinks", "publicRoadOperations" }) ||
                spec["lotId"] != null && !BoundedId(spec["lotId"]) ||
                !GuidToken(spec["markingTemplateGuid"]) || !GuidToken(spec["surfaceMaterialGuid"]))
                return "parking_lot requires an exact boundary, arrays and marking/surface asset GUIDs.";
            var drivewayKeys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (spec["publicRoadOperations"] != null)
            {
                if (spec["publicRoadOperations"] is not JArray roadOperations || roadOperations.Count > 16)
                    return "parking_lot publicRoadOperations must contain at most 16 spans.";
                foreach (var item in roadOperations)
                {
                    if (item is not JObject operation || !FieldsMatch(operation,
                        new[] { "kind", "resultKey", "profileRevisionId", "start", "end" }, Array.Empty<string>()) ||
                        operation["kind"]?.Type != JTokenType.String || (string)operation["kind"] != "span" ||
                        !BoundedId(operation["resultKey"]) || !drivewayKeys.Add((string)operation["resultKey"]) ||
                        !BoundedId(operation["profileRevisionId"]) ||
                        !Waypoint(operation["start"]) || !Waypoint(operation["end"]))
                        return "Parking public road span requires unique resultKey, profile and exact endpoints.";
                }
            }
            if (spec["boundary"] is not JObject boundary || boundary.Count != 1)
                return "Parking boundary requires exactly vertices or rectangle.";
            if (boundary["vertices"] != null)
            {
                if (!PointList(boundary["vertices"], 3)) return "Parking polygon requires 3–256 finite vertices.";
            }
            else if (boundary["rectangle"] is JObject rectangle)
            {
                if (!FieldsMatch(rectangle, new[] { "center", "size", "rotationDeg" }, Array.Empty<string>()) ||
                    !Point(rectangle["center"]) || rectangle["size"] is not JObject size ||
                    !FieldsMatch(size, new[] { "x", "z" }, Array.Empty<string>()) ||
                    !NumberRange(size["x"], 0, 1000000000000000d, true) ||
                    !NumberRange(size["z"], 0, 1000000000000000d, true) ||
                    !Coordinate(rectangle["rotationDeg"]))
                    return "Parking rectangle requires center, positive x/z size and finite rotationDeg.";
            }
            else return "Parking boundary requires vertices or rectangle.";
            foreach (var pair in new[] { ("aisles", 1), ("rows", 1), ("entrances", 1),
                                         ("pedestrianRoutes", 0), ("pedestrianEntrances", 0),
                                         ("zones", 0) })
                if (spec[pair.Item1] is not JArray items || items.Count < pair.Item2 || items.Count > 256 ||
                    items.Any(item => item is not JObject))
                    return "Parking " + pair.Item1 + " has invalid item count or type.";
            if (spec["pedestrianLinks"] != null &&
                (spec["pedestrianLinks"] is not JArray links || links.Count > 256 ||
                 links.Any(item => item is not JObject)))
                return "Parking pedestrianLinks has invalid item count or type.";

            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var aisleIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var item in (JArray)spec["aisles"])
            {
                var aisle = (JObject)item;
                if (!FieldsMatch(aisle,
                    new[] { "id", "points", "widthMeters", "modeMask", "direction",
                            "speedMetersPerSecond", "costMultiplier" }, Array.Empty<string>()) ||
                    !BoundedId(aisle["id"]) || !ids.Add((string)aisle["id"]) ||
                    !PointList(aisle["points"], 2) ||
                    !NumberRange(aisle["widthMeters"], 0, 1000000000000000d, true) ||
                    !IntegerRange(aisle["modeMask"], 1, 15) || !BoundedId(aisle["direction"]) ||
                    !NumberRange(aisle["speedMetersPerSecond"], 0, 1000000000000000d, true) ||
                    !NumberRange(aisle["costMultiplier"], 0, 1000000000000000d, true))
                    return "Parking aisle fields or values are invalid.";
                aisleIds.Add((string)aisle["id"]);
            }
            foreach (var item in (JArray)spec["rows"])
            {
                var row = (JObject)item;
                if (!FieldsMatch(row,
                    new[] { "id", "aisleId", "side", "angleDegrees", "firstOffsetMeters", "count",
                            "stallWidthMeters", "stallLengthMeters", "gapMeters", "stallClass", "allowedModes" },
                    new[] { "allowedVehicleClasses", "overrides" }) ||
                    !BoundedId(row["id"]) || !ids.Add((string)row["id"]) ||
                    !BoundedId(row["aisleId"]) || !aisleIds.Contains((string)row["aisleId"]) ||
                    row["side"]?.Type != JTokenType.String ||
                    !new[] { "left", "right" }.Contains((string)row["side"], StringComparer.Ordinal) ||
                    row["angleDegrees"]?.Type != JTokenType.Integer ||
                    !new[] { "0", "45", "60", "90" }.Contains(row["angleDegrees"].ToString(Formatting.None), StringComparer.Ordinal) ||
                    !NumberRange(row["firstOffsetMeters"], 0, 1000000000000000d) ||
                    !IntegerRange(row["count"], 1, 256) ||
                    !NumberRange(row["stallWidthMeters"], 0, 1000000000000000d, true) ||
                    !NumberRange(row["stallLengthMeters"], 0, 1000000000000000d, true) ||
                    !NumberRange(row["gapMeters"], 0, 1000000000000000d) ||
                    row["stallClass"]?.Type != JTokenType.String ||
                    !new[] { "Standard", "Accessible", "Service" }
                        .Contains((string)row["stallClass"], StringComparer.Ordinal) ||
                    !IntegerRange(row["allowedModes"], 1, 15) ||
                    ((int)row["allowedModes"] & 4) != 0)
                    return "Parking row fields, dimensions or aisle reference are invalid.";
                if (row["allowedVehicleClasses"] != null && !IntegerRange(row["allowedVehicleClasses"], 1, 63))
                    return "Parking row allowedVehicleClasses is invalid.";
                if (row["overrides"] != null)
                {
                    if (row["overrides"] is not JArray overrides || overrides.Count > (int)row["count"])
                        return "Parking row overrides exceed its count.";
                    var usedSlots = new System.Collections.Generic.HashSet<int>();
                    foreach (var entry in overrides)
                    {
                        if (entry is not JObject stall ||
                            !FieldsMatch(stall, new[] { "slotIndex", "stallClass", "allowedModes" },
                                new[] { "allowedVehicleClasses", "widthMeters", "lengthMeters",
                                        "adjacentAccessAisleMeters", "markingTemplateGuid" }) ||
                            !IntegerRange(stall["slotIndex"], 0, (int)row["count"] - 1) ||
                            !usedSlots.Add((int)stall["slotIndex"]) ||
                            stall["stallClass"]?.Type != JTokenType.String ||
                            !new[] { "Standard", "Accessible", "Service" }
                                .Contains((string)stall["stallClass"], StringComparer.Ordinal) ||
                            !IntegerRange(stall["allowedModes"], 1, 15) ||
                            ((int)stall["allowedModes"] & 4) != 0 ||
                            stall["allowedVehicleClasses"] != null &&
                                !IntegerRange(stall["allowedVehicleClasses"], 1, 63) ||
                            stall["widthMeters"] != null &&
                                !NumberRange(stall["widthMeters"], 0, 1000000000000000d, true) ||
                            stall["lengthMeters"] != null &&
                                !NumberRange(stall["lengthMeters"], 0, 1000000000000000d, true) ||
                            stall["adjacentAccessAisleMeters"] != null &&
                                !NumberRange(stall["adjacentAccessAisleMeters"], 0, 1000000000000000d) ||
                            stall["markingTemplateGuid"] != null && !GuidToken(stall["markingTemplateGuid"]))
                            return "Parking stall override fields or unique slotIndex are invalid.";
                    }
                }
            }
            foreach (var item in (JArray)spec["entrances"])
            {
                var entrance = (JObject)item;
                if (!FieldsMatch(entrance,
                    new[] { "id", "aisleId", "aisleLaneKey", "pathLocal", "modes" },
                    new[] { "inboundRoadLaneKey", "outboundRoadLaneKey", "inboundRoadPointWorld",
                            "outboundRoadPointWorld", "roadPointWorld", "inboundControl", "outboundControl",
                            "priority", "signalGroupId", "controlPointLocal", "widthMeters",
                            "segmentId", "drivewayResultKey" }) ||
                    !BoundedId(entrance["id"]) || !ids.Add((string)entrance["id"]) ||
                    (entrance["segmentId"] != null) == (entrance["drivewayResultKey"] != null) ||
                    entrance["segmentId"] != null && !BoundedId(entrance["segmentId"]) ||
                    entrance["drivewayResultKey"] != null &&
                        (!BoundedId(entrance["drivewayResultKey"]) ||
                         !drivewayKeys.Contains((string)entrance["drivewayResultKey"])) ||
                    !BoundedId(entrance["aisleId"]) ||
                    !aisleIds.Contains((string)entrance["aisleId"]) || !BoundedId(entrance["aisleLaneKey"]) ||
                    entrance["inboundRoadLaneKey"] == null && entrance["outboundRoadLaneKey"] == null ||
                    entrance["inboundRoadLaneKey"] != null && !BoundedId(entrance["inboundRoadLaneKey"]) ||
                    entrance["outboundRoadLaneKey"] != null && !BoundedId(entrance["outboundRoadLaneKey"]) ||
                    (entrance["inboundRoadLaneKey"] != null) != (entrance["inboundRoadPointWorld"] != null) ||
                    (entrance["outboundRoadLaneKey"] != null) != (entrance["outboundRoadPointWorld"] != null) ||
                    entrance["inboundRoadPointWorld"] != null && !Point(entrance["inboundRoadPointWorld"]) ||
                    entrance["outboundRoadPointWorld"] != null && !Point(entrance["outboundRoadPointWorld"]) ||
                    entrance["roadPointWorld"] != null && !Point(entrance["roadPointWorld"]) ||
                    entrance["widthMeters"] != null &&
                        !NumberRange(entrance["widthMeters"], 0, 1000000000000000d, true) ||
                    !PointList(entrance["pathLocal"], 2) ||
                    !IntegerRange(entrance["modes"], 1, 15))
                    return "Parking entrance requires an exact directed road lane and local aisle path.";
                if (entrance["inboundControl"] != null && entrance["inboundControl"].Type != JTokenType.String ||
                    entrance["outboundControl"] != null && entrance["outboundControl"].Type != JTokenType.String)
                    return "Parking entrance control kind must be a string.";
                string inboundControl = entrance["inboundControl"] == null ? "Yield" : (string)entrance["inboundControl"];
                string outboundControl = entrance["outboundControl"] == null ? "Stop" : (string)entrance["outboundControl"];
                string[] controls = { inboundControl, outboundControl };
                string[] allowedControls = { "Uncontrolled", "Stop", "Yield", "Priority", "Signal" };
                if (controls.Any(control => !allowedControls.Contains(control, StringComparer.Ordinal)) ||
                    entrance["priority"] != null && !IntegerRange(entrance["priority"], -100, 100) ||
                    controls.Contains("Signal", StringComparer.Ordinal) && !BoundedId(entrance["signalGroupId"]) ||
                    entrance["signalGroupId"] != null && !BoundedId(entrance["signalGroupId"]) ||
                    controls.Any(control => control == "Stop" || control == "Yield" || control == "Signal") &&
                    !Point(entrance["controlPointLocal"]) ||
                    entrance["controlPointLocal"] != null && !Point(entrance["controlPointLocal"]))
                    return "Parking entrance control, priority or control point is invalid.";
            }
            var rowCounts = ((JArray)spec["rows"]).ToDictionary(
                item => (string)item["id"], item => (int)item["count"], StringComparer.Ordinal);
            foreach (var item in (JArray)spec["pedestrianRoutes"])
            {
                var route = (JObject)item;
                if (!FieldsMatch(route, new[] { "id", "points", "widthMeters" },
                    new[] { "linkedStallIds", "linkedStallSlots" }) ||
                    !BoundedId(route["id"]) || !ids.Add((string)route["id"]) || !PointList(route["points"], 2) ||
                    !NumberRange(route["widthMeters"], 0, 1000000000000000d, true) ||
                    route["linkedStallIds"] != null && !IdList(route["linkedStallIds"], true))
                    return "Parking pedestrian route fields are invalid.";
                if (route["linkedStallSlots"] != null)
                {
                    if (route["linkedStallSlots"] is not JArray slots || slots.Count > 256)
                        return "Parking linkedStallSlots exceeds its bounded count.";
                    var slotKeys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in slots)
                    {
                        if (entry is not JObject slot ||
                            !FieldsMatch(slot, new[] { "rowId", "slotIndex" }, Array.Empty<string>()) ||
                            !BoundedId(slot["rowId"]) ||
                            !rowCounts.TryGetValue((string)slot["rowId"], out var rowCount) ||
                            !IntegerRange(slot["slotIndex"], 0, rowCount - 1) ||
                            !slotKeys.Add((string)slot["rowId"] + "\u0000" + slot["slotIndex"].ToString(Formatting.None)))
                            return "Parking linkedStallSlots must contain unique exact row slots.";
                    }
                }
            }
            var routeIds = new System.Collections.Generic.HashSet<string>(
                ((JArray)spec["pedestrianRoutes"]).Select(item => (string)item["id"]), StringComparer.Ordinal);
            foreach (var item in (JArray)spec["pedestrianEntrances"])
            {
                var entrance = (JObject)item;
                if (!FieldsMatch(entrance,
                    new[] { "id", "routeId", "routeDistanceMeters", "pathLocal" },
                    new[] { "inboundRoadLaneKey", "inboundRoadPointWorld", "outboundRoadLaneKey",
                            "outboundRoadPointWorld", "control", "priority", "signalGroupId", "controlPointLocal",
                            "segmentId", "drivewayResultKey" }) ||
                    !BoundedId(entrance["id"]) || !ids.Add((string)entrance["id"]) ||
                    (entrance["segmentId"] != null) == (entrance["drivewayResultKey"] != null) ||
                    entrance["segmentId"] != null && !BoundedId(entrance["segmentId"]) ||
                    entrance["drivewayResultKey"] != null &&
                        (!BoundedId(entrance["drivewayResultKey"]) ||
                         !drivewayKeys.Contains((string)entrance["drivewayResultKey"])) ||
                    !BoundedId(entrance["routeId"]) ||
                    !routeIds.Contains((string)entrance["routeId"]) ||
                    !NumberRange(entrance["routeDistanceMeters"], 0, 1000000000000000d) ||
                    !PointList(entrance["pathLocal"], 2) ||
                    entrance["inboundRoadLaneKey"] == null && entrance["outboundRoadLaneKey"] == null ||
                    (entrance["inboundRoadLaneKey"] != null) != (entrance["inboundRoadPointWorld"] != null) ||
                    (entrance["outboundRoadLaneKey"] != null) != (entrance["outboundRoadPointWorld"] != null) ||
                    entrance["inboundRoadLaneKey"] != null && !BoundedId(entrance["inboundRoadLaneKey"]) ||
                    entrance["outboundRoadLaneKey"] != null && !BoundedId(entrance["outboundRoadLaneKey"]) ||
                    entrance["inboundRoadPointWorld"] != null && !Point(entrance["inboundRoadPointWorld"]) ||
                    entrance["outboundRoadPointWorld"] != null && !Point(entrance["outboundRoadPointWorld"]))
                    return "Parking pedestrian entrance has invalid route or directed lane attachment.";
                if (entrance["control"] != null && entrance["control"].Type != JTokenType.String)
                    return "Parking pedestrian control kind must be a string.";
                string control = entrance["control"] == null ? "Uncontrolled" : (string)entrance["control"];
                if (!new[] { "Uncontrolled", "Stop", "Yield", "Priority", "Signal" }
                        .Contains(control, StringComparer.Ordinal) ||
                    entrance["priority"] != null && !IntegerRange(entrance["priority"], -100, 100) ||
                    control == "Signal" && !BoundedId(entrance["signalGroupId"]) ||
                    entrance["signalGroupId"] != null && !BoundedId(entrance["signalGroupId"]) ||
                    (control == "Stop" || control == "Yield" || control == "Signal") &&
                        !Point(entrance["controlPointLocal"]) ||
                    entrance["controlPointLocal"] != null && !Point(entrance["controlPointLocal"]))
                    return "Parking pedestrian entrance control is invalid.";
            }
            foreach (var item in (JArray)(spec["pedestrianLinks"] ?? new JArray()))
            {
                var link = (JObject)item;
                if (!FieldsMatch(link,
                    new[] { "id", "routeId", "routeDistanceMeters", "pathLocal" },
                    new[] { "stallId", "rowId", "slotIndex" }) ||
                    !BoundedId(link["id"]) || !ids.Add((string)link["id"]) ||
                    !BoundedId(link["routeId"]) ||
                    (link["stallId"] != null) == (link["rowId"] != null || link["slotIndex"] != null) ||
                    (link["rowId"] != null) != (link["slotIndex"] != null) ||
                    link["stallId"] != null && !BoundedId(link["stallId"]) ||
                    link["rowId"] != null &&
                        (!BoundedId(link["rowId"]) || !rowCounts.TryGetValue((string)link["rowId"], out var linkRowCount) ||
                         !IntegerRange(link["slotIndex"], 0, linkRowCount - 1)) ||
                    !routeIds.Contains((string)link["routeId"]) ||
                    !NumberRange(link["routeDistanceMeters"], 0, 1000000000000000d) ||
                    !PointList(link["pathLocal"], 2))
                    return "Parking pedestrian link fields or route reference are invalid.";
            }
            foreach (var item in (JArray)spec["zones"])
            {
                var zone = (JObject)item;
                if (!FieldsMatch(zone, new[] { "id", "kind", "polygon", "prefabGuid", "seed", "spacingMeters" }, Array.Empty<string>()) ||
                    !BoundedId(zone["id"]) || !ids.Add((string)zone["id"]) ||
                    zone["kind"]?.Type != JTokenType.String ||
                    !new[] { "landscape", "lighting" }.Contains((string)zone["kind"], StringComparer.Ordinal) ||
                    !PointList(zone["polygon"], 3) || !GuidToken(zone["prefabGuid"]) ||
                    !IntegerRange(zone["seed"], int.MinValue, int.MaxValue) ||
                    !NumberRange(zone["spacingMeters"], 0, 1000000000000000d, true))
                    return "Parking zone fields are invalid.";
            }
            return null;
        }

        private static bool PointList(JToken token, int minimum) => token is JArray points &&
            points.Count >= minimum && points.Count <= 256 && points.All(Point);

        private static bool Waypoint(JToken token) => token is JObject point &&
            FieldsMatch(point, new[] { "x", "y", "z" }, new[] { "anchorId" }) &&
            Coordinate(point["x"]) && Coordinate(point["y"]) && Coordinate(point["z"]) &&
            (point["anchorId"] == null || BoundedId(point["anchorId"]));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Graphics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Environment
{
    /// <summary>Approval-gated environment mutation, preview-goal, and legacy-weather migration service.</summary>
    [McpForUnityTool("manage_environment", AutoRegister = false, Group = "vfx", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageEnvironment
    {
        public static object HandleCommand(JObject @params)
        {
            var action = (@params?["action"]?.Value<string>() ?? "apply").ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "apply": return Apply(@params, true);
                    case "repair": return Apply(@params, true);
                    case "begin_goal": return BeginGoal(@params);
                    case "preview_goal": return PreviewGoal(@params);
                    case "commit_goal": return CommitGoal(@params);
                    case "restore_goal": return RestoreGoal(@params);
                    case "cancel_goal": return CancelGoal(@params);
                    case "migrate_legacy_weather": return MigrateLegacyWeather(@params);
                    default: return new ErrorResponse("Unknown environment mutation action: " + action);
                }
            }
            catch (Exception ex) { McpLog.Error("[Environment] Mutation failed: " + ex); return new ErrorResponse("Environment mutation failed: " + ex.Message); }
        }

        internal static object Preflight(JObject parameters)
        {
            var spec = ResolveSpec(parameters, out var error);
            if (spec == null) return new ErrorResponse(error);
            return PreflightSpec(spec);
        }

        internal static object GoalStatus(JObject parameters)
        {
            var goal = LoadGoal(parameters?["goal_id"]?.Value<string>(), out var error);
            return goal == null ? new ErrorResponse(error) : new SuccessResponse("Environment goal status", goal);
        }

        private static object Apply(JObject parameters, bool persistent)
        {
            if (EditorApplication.isPlaying) return new ErrorResponse("Persistent environment edits are blocked in Play Mode. Stop Play Mode before applying or committing changes.");
            var spec = ResolveSpec(parameters, out var error);
            if (spec == null) return new ErrorResponse(error);
            var preflight = PreflightSpec(spec);
            if (preflight is ErrorResponse) return preflight;
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP Environment Apply");
            var operations = ExecuteOperations(spec, out error);
            if (operations == null) { Undo.RevertAllDownToGroup(undoGroup); return new ErrorResponse(error); }
            var result = ApplySpec(spec, persistent, out error, false);
            if (result == null)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                RollbackCreatedAssets(operations);
                return new ErrorResponse("Environment transaction rolled back: " + error);
            }
            if (persistent) AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
            result["operations"] = operations;
            return new SuccessResponse("Environment changes applied", result);
        }

        private static object BeginGoal(JObject parameters)
        {
            var spec = ResolveSpec(parameters, out var error);
            if (spec == null) return new ErrorResponse(error);
            if (spec["operations"] is JArray operations && operations.Count > 0)
                return new ErrorResponse("Goal sessions cannot create assets or components during preview. Apply setup operations first, then begin a goal against their exact discovered identities.");
            var preflight = PreflightSpec(spec);
            if (preflight is ErrorResponse) return preflight;
            var goalId = Guid.NewGuid().ToString("N");
            var goal = new JObject
            {
                ["goal_id"] = goalId,
                ["status"] = "ready",
                ["created_utc"] = DateTime.UtcNow.ToString("o"),
                ["spec"] = spec,
                ["baseline"] = CaptureBaseline(spec),
                ["iteration_policy"] = spec["iteration_policy"]?.DeepClone() ?? new JObject { ["max_iterations"] = 6, ["stop_on_plateau"] = true },
                ["iterations"] = new JArray(),
                ["preview_applied"] = false
            };
            SaveGoal(goal);
            return new SuccessResponse("Environment goal created", new { goalId, status = "ready", note = "Capture a camera screenshot through manage_camera, evaluate it in the calling AI, then send preview_goal with the revised candidate spec." });
        }

        private static object PreviewGoal(JObject parameters)
        {
            var goal = LoadGoal(parameters?["goal_id"]?.Value<string>(), out var error);
            if (goal == null) return new ErrorResponse(error);
            if (EditorApplication.isPlaying && !(parameters?.Value<bool?>("confirm_play_mode") ?? false))
                return new ErrorResponse("Play Mode preview requires confirm_play_mode: true. Start Play Mode separately with manage_editor after approval.");
            var candidate = parameters?["spec"] as JObject ?? goal["spec"] as JObject;
            if (candidate == null) return new ErrorResponse("Goal has no environment spec.");
            if (candidate["operations"] is JArray operations && operations.Count > 0)
                return new ErrorResponse("Goal preview cannot execute setup operations. Apply and rediscover setup first.");
            var preflight = PreflightSpec(candidate);
            if (preflight is ErrorResponse) return preflight;
            var iterations = (JArray)goal["iterations"];
            var maxIterations = (goal["iteration_policy"] as JObject)?.Value<int?>("max_iterations") ?? 6;
            if (iterations.Count >= Math.Max(1, maxIterations))
            {
                goal["status"] = "iteration_limit";
                SaveGoal(goal);
                return new ErrorResponse("Goal reached its configured max_iterations without an accepted result.");
            }
            RestorePreviewUndo(goal);
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP Environment Goal Preview");
            var applied = ApplySpec(candidate, false, out error);
            if (applied == null) return new ErrorResponse(error);
            Undo.CollapseUndoOperations(undoGroup);
            goal["spec"] = candidate;
            goal["preview_applied"] = true;
            goal["preview_undo_group"] = undoGroup;
            goal["status"] = "previewed";
            var assessment = parameters?["assessment"] as JObject;
            var journal = new JObject { ["utc"] = DateTime.UtcNow.ToString("o"), ["change_count"] = ((JArray)candidate["changes"])?.Count ?? 0, ["assessment"] = assessment?.DeepClone() };
            iterations.Add(journal);
            if (assessment?.Value<bool?>("close_enough") ?? false) goal["status"] = "candidate_ready";
            else if (IsPlateau(goal)) goal["status"] = "plateau";
            SaveGoal(goal);
            return new SuccessResponse("Environment goal preview applied", new { goalId = goal["goal_id"], status = goal["status"], preview = applied, next = "Use manage_camera to capture a screenshot. The AI evaluates the image and returns an optional assessment; this MCP service does not run computer vision." });
        }

        private static object CommitGoal(JObject parameters)
        {
            if (EditorApplication.isPlaying) return new ErrorResponse("Goal commits are blocked in Play Mode. Stop Play Mode before committing persistent assets.");
            var goal = LoadGoal(parameters?["goal_id"]?.Value<string>(), out var error);
            if (goal == null) return new ErrorResponse(error);
            var spec = goal["spec"] as JObject;
            var preflight = PreflightSpec(spec);
            if (preflight is ErrorResponse) return preflight;
            RestorePreviewUndo(goal);
            var applied = ApplySpec(spec, true, out error);
            if (applied == null) return new ErrorResponse(error);
            goal["status"] = "committed";
            goal["committed_utc"] = DateTime.UtcNow.ToString("o");
            SaveGoal(goal);
            return new SuccessResponse("Environment goal committed to persistent assets", new { goalId = goal["goal_id"], applied });
        }

        private static object RestoreGoal(JObject parameters)
        {
            if (EditorApplication.isPlaying) return new ErrorResponse("Goal restoration is blocked in Play Mode.");
            var goal = LoadGoal(parameters?["goal_id"]?.Value<string>(), out var error);
            if (goal == null) return new ErrorResponse(error);
            if (goal.Value<bool?>("preview_applied") ?? false)
            {
                RestorePreviewUndo(goal);
                goal["preview_applied"] = false;
                goal["status"] = "restored";
                SaveGoal(goal);
                return new SuccessResponse("Environment goal preview restored", new { goalId = goal["goal_id"], restoredFrom = "undo_staging" });
            }
            var baseline = goal["baseline"] as JObject;
            if (baseline == null) return new ErrorResponse("Goal baseline is missing.");
            var restored = ApplySpec(baseline, true, out error);
            if (restored == null) return new ErrorResponse(error);
            goal["status"] = "restored";
            SaveGoal(goal);
            return new SuccessResponse("Environment goal baseline restored", restored);
        }

        private static object CancelGoal(JObject parameters)
        {
            var goal = LoadGoal(parameters?["goal_id"]?.Value<string>(), out var error);
            if (goal == null) return new ErrorResponse(error);
            RestorePreviewUndo(goal);
            goal["status"] = "cancelled";
            SaveGoal(goal);
            return new SuccessResponse("Environment goal cancelled", new { goalId = goal["goal_id"] });
        }

        private static object MigrateLegacyWeather(JObject parameters)
        {
            const string source = "Assets/WeatherPatterns/AutoGenerated";
            const string destination = "Assets/WeatherPatterns/Authored";
            var assets = AssetDatabase.FindAssets(string.Empty, new[] { source }).Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)).OrderBy(path => path).ToArray();
            var inventory = assets.Select(path => new JObject
            {
                ["source_path"] = path,
                ["destination_path"] = destination + path.Substring(source.Length),
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["dependencies"] = new JArray(AssetDatabase.GetDependencies(path, false))
            }).ToArray();
            var execute = parameters?.Value<bool?>("execute") ?? false;
            if (!execute) return new SuccessResponse("Legacy weather migration dry run", new { source, destination, count = assets.Length, inventory, executeRequired = true, note = "Migration preserves GUIDs by moving assets; it never deletes or regenerates them." });
            if (AssetDatabase.IsValidFolder(destination)) return new ErrorResponse("Migration destination already exists. Review it manually before retrying to avoid overwriting authored assets.");
            var parent = Path.GetDirectoryName(destination)?.Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) return new ErrorResponse("Expected WeatherPatterns parent folder does not exist.");
            var moveError = AssetDatabase.MoveAsset(source, destination);
            if (!string.IsNullOrEmpty(moveError)) return new ErrorResponse("Legacy weather migration failed: " + moveError);
            var verification = inventory.Select(entry =>
            {
                var movedPath = entry.Value<string>("destination_path");
                var guidMatches = entry.Value<string>("guid") == AssetDatabase.AssetPathToGUID(movedPath);
                var dependenciesPresent = (entry["dependencies"] as JArray).All(dependency =>
                {
                    var dependencyPath = dependency.Value<string>();
                    if (dependencyPath.StartsWith(source, StringComparison.Ordinal)) dependencyPath = destination + dependencyPath.Substring(source.Length);
                    return AssetDatabase.LoadMainAssetAtPath(dependencyPath) != null || dependencyPath.StartsWith("Packages/", StringComparison.Ordinal);
                });
                return new { path = movedPath, guidMatches, dependenciesPresent };
            }).ToArray();
            if (verification.Any(item => !item.guidMatches || !item.dependenciesPresent)) return new ErrorResponse("Migration moved assets but post-move validation failed. Use source control and the returned inventory to recover before retiring the generator.");
            AssetDatabase.SaveAssets();
            return new SuccessResponse("Legacy weather assets moved to authored ownership", new { source, destination, count = assets.Length, verification, preservedGuids = true, generatorRetirement = "The generator was not deleted. Remove or disable it only after project-level acceptance." });
        }

        private static object PreflightSpec(JObject spec)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var changes = spec?["changes"] as JArray;
            var operations = spec?["operations"] as JArray;
            if ((changes == null || changes.Count == 0) && (operations == null || operations.Count == 0)) errors.Add("environment_spec must contain at least one change or setup operation.");
            if (EditorApplication.isPlaying) warnings.Add("Play Mode is active. Persistent apply and commit are blocked.");
            var activeManagers = UnityEngine.Resources.FindObjectsOfTypeAll<Component>().Where(component => component != null && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy)
                .Where(component => component.GetType().Name == "WeatherManager" || component.GetType().Name == "StoryDrivenWeatherManager" || component.GetType().Name == "StormDrivenWeatherManager").ToArray();
            if (activeManagers.Length > 1) errors.Add("Multiple active weather/storm managers are authoritative. Inspect authority and target one upstream authored source before applying.");
            if (changes != null)
            {
                foreach (var change in changes.OfType<JObject>())
                {
                    var target = ResolveChangeTarget(change, out var targetError);
                    if (target == null) { errors.Add(targetError); continue; }
                    if (EnvironmentCommon.IsVfxChange(target, change))
                    {
                        if (string.IsNullOrWhiteSpace(change.Value<string>("vfx_property"))) errors.Add("Exposed VFX changes require vfx_property.");
                        continue;
                    }
                    var property = EnvironmentCommon.ResolveProperty(target.GetType(), change);
                    if (string.IsNullOrWhiteSpace(property)) { errors.Add("Each change requires semantic (preferred) or a catalog-discovered serialized property."); continue; }
                    if (target is Component component && GraphicsHelpers.VolumeType != null && GraphicsHelpers.VolumeType.IsAssignableFrom(component.GetType()) && property != "sharedProfile")
                        errors.Add("Volume changes must target the Volume.sharedProfile asset, not the scene Volume component or Volume.profile clone.");
                    var serialized = new SerializedObject(target);
                    if (serialized.FindProperty(property) == null) errors.Add("Property '" + property + "' was not found on " + target.GetType().Name + ".");
                }
            }
            if (operations != null)
                foreach (var operation in operations.OfType<JObject>())
                {
                    var kind = operation.Value<string>("kind");
                    if (kind != "create_volume_profile" && kind != "clone_asset" && kind != "create_asset" && kind != "create_component" && kind != "add_volume_component")
                        errors.Add("Unsupported environment operation '" + kind + "'.");
                    if ((kind == "create_volume_profile" || kind == "clone_asset" || kind == "create_asset") && !EnvironmentCommon.IsAssetPath(operation.Value<string>("asset_path") ?? operation.Value<string>("destination_path")))
                        errors.Add("Operation '" + kind + "' requires an unused safe Assets/ destination path.");
                }
            if (errors.Count > 0) return new ErrorResponse("Environment preflight failed: " + string.Join(" ", errors));
            return new SuccessResponse("Environment preflight passed", new { changeCount = changes?.Count ?? 0, operationCount = operations?.Count ?? 0, warnings, persistentVolumeRule = "Volume.sharedProfile only" });
        }

        private static JObject ResolveSpec(JObject parameters, out string error)
        {
            error = null;
            JObject spec = parameters?["spec"] as JObject;
            if (spec == null && parameters?["spec_path"] != null)
            {
                var path = parameters.Value<string>("spec_path");
                if (!EnvironmentCommon.IsAssetPath(path)) { error = "spec_path must be a safe Assets/ path."; return null; }
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) { error = "spec_path could not be loaded as a TextAsset."; return null; }
                try { spec = JObject.Parse(asset.text); } catch (Exception ex) { error = "spec_path is not valid JSON: " + ex.Message; return null; }
            }
            if (spec == null) { error = "Provide environment_spec as spec or spec_path."; return null; }
            if (spec.Value<int?>("schema_version") != EnvironmentCommon.SpecVersion) { error = "environment_spec.schema_version must be " + EnvironmentCommon.SpecVersion + "."; return null; }
            return (JObject)spec.DeepClone();
        }

        private static JObject CaptureBaseline(JObject spec)
        {
            var baseline = new JObject { ["schema_version"] = EnvironmentCommon.SpecVersion, ["changes"] = new JArray() };
            foreach (var change in (spec["changes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var target = ResolveChangeTarget(change, out _);
                if (target == null) continue;
                if (EnvironmentCommon.IsVfxChange(target, change))
                {
                    if (EnvironmentCommon.ReadVfxParameter(target, change, out var vfxValue, out _))
                        ((JArray)baseline["changes"]).Add(new JObject { ["target"] = EnvironmentCommon.ObjectReference(target), ["semantic"] = change.Value<string>("semantic"), ["vfx_property"] = change.Value<string>("vfx_property"), ["value"] = vfxValue });
                    continue;
                }
                var property = EnvironmentCommon.ResolveProperty(target.GetType(), change);
                if (string.IsNullOrEmpty(property)) continue;
                var serialized = new SerializedObject(target);
                var field = serialized.FindProperty(property);
                if (field == null) continue;
                ((JArray)baseline["changes"]).Add(new JObject { ["target"] = EnvironmentCommon.ObjectReference(target), ["semantic"] = EnvironmentCommon.SemanticPath(target.GetType(), property), ["value"] = EnvironmentCommon.ReadProperty(field) });
            }
            return baseline;
        }

        private static JObject ApplySpec(JObject spec, bool persistent, out string error, bool saveAssets = true)
        {
            error = null;
            var applied = new JArray();
            foreach (var change in (spec["changes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var target = ResolveChangeTarget(change, out error);
                if (target == null) return null;
                if (EnvironmentCommon.IsVfxChange(target, change))
                {
                    Undo.RecordObject(target, "MCP Environment VFX parameter");
                    if (!EnvironmentCommon.WriteVfxParameter(target, change, out var beforeVfx, out error)) return null;
                    if (persistent) EditorUtility.SetDirty(target);
                    applied.Add(new JObject { ["target"] = EnvironmentCommon.ObjectReference(target), ["semantic"] = change.Value<string>("semantic"), ["vfx_property"] = change.Value<string>("vfx_property"), ["before"] = beforeVfx, ["after"] = change["value"] });
                    continue;
                }
                var propertyPath = EnvironmentCommon.ResolveProperty(target.GetType(), change);
                var serialized = new SerializedObject(target);
                var property = serialized.FindProperty(propertyPath);
                if (property == null) { error = "Property '" + propertyPath + "' was not found on " + target.GetType().Name + "."; return null; }
                var expectedHash = change.Value<string>("expected_hash");
                var before = EnvironmentCommon.ReadProperty(property);
                if (!string.IsNullOrWhiteSpace(expectedHash) && !string.Equals(expectedHash, EnvironmentCommon.Hash(before), StringComparison.OrdinalIgnoreCase)) { error = "Conflict detected for '" + propertyPath + "'. Refresh inspection before applying."; return null; }
                Undo.RecordObject(target, "MCP Environment: " + propertyPath);
                if (!EnvironmentCommon.WriteProperty(property, change["value"], out error)) return null;
                serialized.ApplyModifiedProperties();
                if (persistent)
                {
                    EditorUtility.SetDirty(target);
                    if (target is Component component && component.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                }
                applied.Add(new JObject { ["target"] = EnvironmentCommon.ObjectReference(target), ["property"] = propertyPath, ["before"] = before, ["after"] = EnvironmentCommon.ReadProperty(property) });
            }
            if (persistent && saveAssets) AssetDatabase.SaveAssets();
            return new JObject { ["persistent"] = persistent, ["changes"] = applied };
        }

        private static JArray ExecuteOperations(JObject spec, out string error)
        {
            error = null;
            var completed = new JArray();
            foreach (var operation in (spec["operations"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var kind = operation.Value<string>("kind");
                if (kind == "create_volume_profile")
                {
                    var path = operation.Value<string>("asset_path");
                    if (GraphicsHelpers.VolumeProfileType == null) { error = "The Volume package is not installed."; return null; }
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null) { error = "Destination already exists: " + path; return null; }
                    EnsureAssetFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                    var profile = ScriptableObject.CreateInstance(GraphicsHelpers.VolumeProfileType);
                    AssetDatabase.CreateAsset(profile, path);
                    completed.Add(new JObject { ["kind"] = kind, ["target"] = EnvironmentCommon.ObjectReference(profile) });
                }
                else if (kind == "clone_asset")
                {
                    var source = EnvironmentCommon.ResolveObject(operation["source"]);
                    var destination = operation.Value<string>("destination_path");
                    if (source == null || !EnvironmentCommon.IsAssetPath(AssetDatabase.GetAssetPath(source))) { error = "clone_asset requires an exact source asset reference."; return null; }
                    EnsureAssetFolder(Path.GetDirectoryName(destination)?.Replace('\\', '/'));
                    if (AssetDatabase.LoadMainAssetAtPath(destination) != null || !AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), destination)) { error = "Could not clone asset to " + destination; return null; }
                    completed.Add(new JObject { ["kind"] = kind, ["target"] = EnvironmentCommon.ObjectReference(AssetDatabase.LoadMainAssetAtPath(destination)) });
                }
                else if (kind == "create_asset")
                {
                    var type = FindSupportedType(operation.Value<string>("type"));
                    var path = operation.Value<string>("asset_path");
                    if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type)) { error = "create_asset requires a supported ScriptableObject type."; return null; }
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null) { error = "Destination already exists: " + path; return null; }
                    EnsureAssetFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                    var asset = ScriptableObject.CreateInstance(type);
                    AssetDatabase.CreateAsset(asset, path);
                    completed.Add(new JObject { ["kind"] = kind, ["target"] = EnvironmentCommon.ObjectReference(asset) });
                }
                else if (kind == "create_component")
                {
                    var gameObject = EnvironmentCommon.ResolveObject(operation["target"]) as GameObject;
                    var type = FindSupportedType(operation.Value<string>("component"));
                    if (gameObject == null || type == null || !typeof(Component).IsAssignableFrom(type)) { error = "create_component requires an exact GameObject target and supported component."; return null; }
                    var component = Undo.AddComponent(gameObject, type);
                    completed.Add(new JObject { ["kind"] = kind, ["target"] = EnvironmentCommon.ObjectReference(component) });
                }
                else if (kind == "add_volume_component")
                {
                    var profile = EnvironmentCommon.ResolveObject(operation["profile"]);
                    var type = FindSupportedType(operation.Value<string>("component"));
                    var add = profile?.GetType().GetMethod("Add", new[] { typeof(Type), typeof(bool) });
                    if (profile == null || type == null || GraphicsHelpers.VolumeComponentType == null || !GraphicsHelpers.VolumeComponentType.IsAssignableFrom(type) || add == null) { error = "add_volume_component requires a VolumeProfile and an installed VolumeComponent type."; return null; }
                    var component = add.Invoke(profile, new object[] { type, true }) as UnityEngine.Object;
                    Undo.RecordObject(profile, "MCP Environment: Add Volume Component");
                    EditorUtility.SetDirty(profile);
                    completed.Add(new JObject { ["kind"] = kind, ["target"] = EnvironmentCommon.ObjectReference(component) });
                }
            }
            return completed;
        }

        private static void RollbackCreatedAssets(JArray operations)
        {
            foreach (var operation in operations.OfType<JObject>().Reverse())
            {
                var kind = operation.Value<string>("kind");
                if (kind != "create_volume_profile" && kind != "create_asset" && kind != "clone_asset") continue;
                var path = operation["target"]?["asset_path"]?.Value<string>();
                if (EnvironmentCommon.IsAssetPath(path)) AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.SaveAssets();
        }

        private static Type FindSupportedType(string requested) => EnvironmentCommon.GetSupportedTypes().FirstOrDefault(type => type.Name == requested || type.FullName == requested);

        private static void EnsureAssetFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        private static UnityEngine.Object ResolveChangeTarget(JObject change, out string error)
        {
            error = null;
            var reference = change["target"] ?? change["profile"];
            if (reference is JObject objectReference && string.IsNullOrWhiteSpace(objectReference.Value<string>("global_id")) && string.IsNullOrWhiteSpace(objectReference.Value<string>("asset_path")))
            {
                error = "Mutation targets require an exact global_id or asset_path; names are discovery-only.";
                return null;
            }
            var target = EnvironmentCommon.ResolveObject(reference);
            if (target == null) { error = "Each change must include an exact resolvable target global_id or asset_path."; return null; }
            if (target is GameObject gameObject)
            {
                var componentName = change.Value<string>("component");
                if (string.IsNullOrEmpty(componentName)) { error = "GameObject changes require component."; return null; }
                var matches = gameObject.GetComponents<Component>().Where(component => component != null && (component.GetType().Name == componentName || component.GetType().FullName == componentName)).ToArray();
                if (matches.Length != 1) { error = matches.Length == 0 ? "Requested component was not found on the exact GameObject." : "Requested component is ambiguous on the exact GameObject; target its component global_id instead."; return null; }
                target = matches[0];
            }
            if (target is Component component && GraphicsHelpers.VolumeType != null && GraphicsHelpers.VolumeType.IsAssignableFrom(component.GetType()) && change.Value<bool?>("use_shared_profile") == true)
            {
                var sharedProfile = component.GetType().GetField("sharedProfile", BindingFlags.Public | BindingFlags.Instance)?.GetValue(component) as UnityEngine.Object;
                if (sharedProfile == null) { error = "Volume.sharedProfile is not assigned; persistent changes cannot target Volume.profile."; return null; }
                target = sharedProfile;
            }
            if (!EnvironmentCommon.IsSupportedType(target.GetType()) && !(GraphicsHelpers.VolumeProfileType != null && GraphicsHelpers.VolumeProfileType.IsAssignableFrom(target.GetType()))) { error = "Target type '" + target.GetType().FullName + "' is not an allowed environment target."; return null; }
            return target;
        }

        private static void RestorePreviewUndo(JObject goal)
        {
            var group = goal.Value<int?>("preview_undo_group");
            if (!group.HasValue) return;
            try { Undo.RevertAllDownToGroup(group.Value); }
            catch (Exception ex) { McpLog.Warn("[Environment] Preview Undo restoration could not complete: " + ex.Message); }
            goal.Remove("preview_undo_group");
            goal["preview_applied"] = false;
        }

        private static bool IsPlateau(JObject goal)
        {
            var policy = goal["iteration_policy"] as JObject;
            if (!(policy?.Value<bool?>("stop_on_plateau") ?? true)) return false;
            var scores = ((JArray)goal["iterations"]).OfType<JObject>().Select(entry => entry["assessment"]?.Value<float?>("score")).Where(score => score.HasValue).Select(score => score.Value).ToArray();
            if (scores.Length < 3) return false;
            var higherIsBetter = policy?.Value<bool?>("higher_score_is_better") ?? true;
            var newest = scores[scores.Length - 1];
            var previousBest = higherIsBetter ? scores.Take(scores.Length - 1).Max() : scores.Take(scores.Length - 1).Min();
            return higherIsBetter ? newest <= previousBest : newest >= previousBest;
        }

        private static string GoalPath(string goalId) => Path.Combine(EnvironmentCommon.GoalDirectory, goalId + ".json");
        private static void SaveGoal(JObject goal)
        {
            Directory.CreateDirectory(EnvironmentCommon.GoalDirectory);
            File.WriteAllText(GoalPath(goal.Value<string>("goal_id")), goal.ToString(Formatting.Indented));
        }
        private static JObject LoadGoal(string goalId, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(goalId) || goalId.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-')) { error = "A valid goal_id is required."; return null; }
            var path = GoalPath(goalId);
            if (!File.Exists(path)) { error = "Environment goal was not found."; return null; }
            try { return JObject.Parse(File.ReadAllText(path)); } catch (Exception ex) { error = "Environment goal could not be read: " + ex.Message; return null; }
        }
    }
}

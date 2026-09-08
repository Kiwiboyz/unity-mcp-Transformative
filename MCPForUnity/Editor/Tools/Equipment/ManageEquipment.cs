using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Equipment
{
    /// <summary>Approval-gated creation and lifecycle management for Project Storm equipment.</summary>
    [McpForUnityTool("manage_equipment", AutoRegister = false, Group = "equipment", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageEquipment
    {
        public static object HandleCommand(JObject @params)
        {
            string action = (@params?["action"]?.Value<string>() ?? "create").Trim().ToLowerInvariant();
            try
            {
                return action switch
                {
                    "validate" => Preflight(@params),
                    "create" or "compose" => Create(@params),
                    "update" => Update(@params),
                    "thumbnail" => Thumbnail(@params),
                    "deprecate" => Deprecate(@params),
                    "repair" => Repair(@params),
                    "purge" => new ErrorResponse("purge_requires_reference_audit: purge is blocked because player saves have no authoritative cross-save reference index. Deprecate content instead."),
                    _ => new ErrorResponse("Unknown equipment management action: " + action)
                };
            }
            catch (Exception exception)
            {
                McpLog.Error("[Equipment] Mutation failed: " + exception);
                return new ErrorResponse("Equipment mutation failed: " + exception.Message);
            }
        }

        internal static object Preflight(JObject parameters)
        {
            JObject spec = ResolveSpec(parameters, out string error);
            if (spec == null) return new ErrorResponse(error);
            List<string> errors = Validate(spec, parameters);
            return errors.Count == 0
                ? new SuccessResponse("Equipment specification is valid", new { valid = true, spec_version = EquipmentCommon.SpecVersion, normalized_spec = spec })
                : new ErrorResponse("equipment_preflight_failed: " + string.Join(" ", errors), new { valid = false, errors = errors.ToArray() });
        }

        internal static object OperationStatus(JObject parameters)
        {
            string operationId = parameters?.Value<string>("operation_id");
            if (string.IsNullOrWhiteSpace(operationId)) return new ErrorResponse("operation_status requires operation_id.");
            string path = JournalPath(operationId);
            return !File.Exists(path) ? new ErrorResponse("No equipment operation journal exists for '" + operationId + "'.") : new SuccessResponse("Equipment operation status", JObject.Parse(File.ReadAllText(path)));
        }

        private static object Create(JObject parameters)
        {
            if (EditorApplication.isPlaying) return new ErrorResponse("Persistent equipment authoring is blocked in Play Mode.");
            if (parameters?.Value<bool?>("execute") != true) return new ErrorResponse("execution_confirmation_required: create and compose require execute=true after preflight.");
            JObject spec = ResolveSpec(parameters, out string error);
            if (spec == null) return new ErrorResponse(error);
            List<string> errors = Validate(spec, parameters);
            if (errors.Count > 0) return new ErrorResponse("equipment_preflight_failed: " + string.Join(" ", errors));

            string operationId = parameters?.Value<string>("operation_id") ?? Guid.NewGuid().ToString("N");
            string journalPath = JournalPath(operationId);
            if (File.Exists(journalPath))
            {
                JObject prior = JObject.Parse(File.ReadAllText(journalPath));
                if (prior.Value<string>("status") == "complete") return new SuccessResponse("Equipment operation was already completed", prior);
                return new ErrorResponse("operation_incomplete: use repair with this operation_id before retrying.");
            }

            var journal = new JObject
            {
                ["operation_id"] = operationId,
                ["status"] = "started",
                ["created_utc"] = DateTime.UtcNow.ToString("o"),
                ["created_assets"] = new JArray(),
                ["registered_assets"] = new JArray(),
                ["spec"] = spec.DeepClone()
            };
            SaveJournal(journal);

            var created = new List<string>();
            var registrations = new List<UnityEngine.Object>();
            try
            {
                string stableId = spec.Value<string>("stable_id");
                string root = (spec.Value<string>("output_root") ?? EquipmentCommon.DefaultOutputRoot).TrimEnd('/') + "/" + stableId;
                string kind = spec.Value<string>("content_kind");
                EnsureFolder(root + "/Visuals");
                EnsureFolder(root + "/Variants");
                EnsureFolder(root + "/Modules");
                EnsureFolder(root + "/Families");
                EnsureFolder(root + "/Thumbnails");

                if (kind == "performance_part")
                {
                    UnityEngine.Object part = CreatePerformancePart(spec, root, created, journal);
                    RegisterPerformance(parameters, part, registrations, journal);
                }
                else
                {
                    string corePath = root + "/Visuals/" + stableId + "_VisualCore.prefab";
                    CreateVisualCore(spec, corePath, created, journal);
                    if (kind == "family_equipment")
                    {
                        UnityEngine.Object family = CreateAsset("EquipmentFamily", root + "/Families/" + stableId + ".asset", created, journal);
                        SetRequired(family, "familyID", stableId); SetRequired(family, "displayName", spec.Value<string>("display_name"));
                        SetRequired(family, "description", spec.Value<string>("description") ?? string.Empty); SetRequired(family, "basePrice", spec.Value<int?>("price") ?? 0);
                        SetRequired(family, "showInShop", spec.Value<bool?>("show_in_shop") ?? true);

                        var modules = new List<UnityEngine.Object>();
                        foreach (JObject variant in spec["variants"].Children<JObject>())
                        {
                            UnityEngine.Object module = CreateVariant(spec, variant, corePath, root, family, created, journal);
                            modules.Add(module);
                        }
                        CreateThumbnail(family, corePath, root + "/Thumbnails/" + stableId + ".png", created, journal);
                        RegisterEquipment(parameters, family, modules, registrations, journal);
                    }
                    else
                    {
                        JObject variant = spec["variants"]?.Children<JObject>().FirstOrDefault() ?? new JObject { ["mount_type"] = "RoofSmall", ["vehicle_truck_type"] = "All", ["selection_priority"] = 0 };
                        UnityEngine.Object module = CreateVariant(spec, variant, corePath, root, null, created, journal);
                        RegisterEquipment(parameters, null, new[] { module }, registrations, journal);
                    }
                }

                AssetDatabase.SaveAssets();
                journal["status"] = "complete";
                journal["completed_utc"] = DateTime.UtcNow.ToString("o");
                SaveJournal(journal);
                return new SuccessResponse("Equipment authoring completed", journal);
            }
            catch (Exception exception)
            {
                Unregister(parameters, registrations);
                foreach (string path in created.OrderByDescending(path => path.Length).ThenBy(path => path, StringComparer.Ordinal)) if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
                AssetDatabase.SaveAssets();
                journal["status"] = "failed";
                journal["error"] = exception.Message;
                journal["completed_utc"] = DateTime.UtcNow.ToString("o");
                SaveJournal(journal);
                return new ErrorResponse("equipment_transaction_failed: " + exception.Message, journal);
            }
        }

        private static object Update(JObject parameters)
        {
            if (parameters?.Value<bool?>("execute") != true) return new ErrorResponse("execution_confirmation_required: update requires execute=true after review.");
            UnityEngine.Object target = EquipmentCommon.ResolveAsset(parameters?["target"]);
            if (target == null) return new ErrorResponse("update requires an exact target asset.");
            string path = AssetDatabase.GetAssetPath(target);
            string expected = parameters?.Value<string>("expected_revision");
            string actual = AssetDatabase.GetAssetDependencyHash(path).ToString();
            string policy = (parameters?.Value<string>("conflict_policy") ?? "fail").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(expected)) return new ErrorResponse("update requires expected_revision from describe.");
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                if (policy == "prefer_project") return new SuccessResponse("Target revision changed; prefer_project preserved it", new { asset_path = path, revision = actual, updated = false });
                if (policy != "prefer_spec") return new ErrorResponse("revision_conflict: target changed after inspection.");
            }
            JObject spec = ResolveSpec(parameters, out string error);
            if (spec == null) return new ErrorResponse(error);
            foreach ((string property, JToken value) in FieldsForUpdate(target.GetType().Name, spec)) if (!EquipmentCommon.SetProperty(target, property, value, out error)) return new ErrorResponse(error);
            AssetDatabase.SaveAssets();
            return new SuccessResponse("Equipment asset updated", EquipmentCommon.DescribeAsset(target));
        }

        private static object Deprecate(JObject parameters)
        {
            if (parameters?.Value<bool?>("execute") != true) return new ErrorResponse("execution_confirmation_required: deprecate requires execute=true after review.");
            UnityEngine.Object target = EquipmentCommon.ResolveAsset(parameters?["target"]);
            if (target == null) return new ErrorResponse("deprecate requires an exact target asset.");
            string shopVisibilityProperty = target.GetType().Name == "EquipmentFamily" ? "showInShop" : "ShowInShop";
            if (!EquipmentCommon.SetProperty(target, shopVisibilityProperty, false, out string error)) return new ErrorResponse(error);
            AssetDatabase.SaveAssets();
            return new SuccessResponse("Equipment content deprecated from the shop while preserving references", EquipmentCommon.DescribeAsset(target));
        }

        private static object Thumbnail(JObject parameters)
        {
            if (parameters?.Value<bool?>("execute") != true) return new ErrorResponse("execution_confirmation_required: thumbnail requires execute=true after review.");
            JObject target = parameters?["target"] as JObject ?? parameters;
            UnityEngine.Object family = EquipmentCommon.ResolveAsset(target?["family_asset_path"] ?? target?["family"]);
            GameObject visual = EquipmentCommon.ResolveAsset(target?["visual_target_asset_path"] ?? target?["visual_target"]) as GameObject;
            string output = target?.Value<string>("output_asset_path");
            if (family == null || visual == null || !EquipmentCommon.IsSafeAssetPath(output)) return new ErrorResponse("thumbnail requires exact family_asset_path, visual_target_asset_path, and traversal-free Assets-relative output_asset_path.");
            var created = new List<string>(); var journal = new JObject { ["created_assets"] = new JArray() };
            CreateThumbnail(family, AssetDatabase.GetAssetPath(visual), output, created, journal);
            AssetDatabase.SaveAssets();
            return new SuccessResponse("Equipment family thumbnail generated", new { family_asset_path = AssetDatabase.GetAssetPath(family), output_asset_path = output });
        }

        private static object Repair(JObject parameters)
        {
            if (parameters?.Value<bool?>("execute") != true) return new ErrorResponse("execution_confirmation_required: repair requires execute=true after review.");
            string operationId = parameters?.Value<string>("operation_id");
            if (string.IsNullOrWhiteSpace(operationId) || !File.Exists(JournalPath(operationId))) return new ErrorResponse("repair requires an existing operation_id.");
            JObject journal = JObject.Parse(File.ReadAllText(JournalPath(operationId)));
            if (journal.Value<string>("status") == "complete") return new SuccessResponse("Completed operation needs no repair", journal);
            foreach (string path in journal["created_assets"]?.Values<string>().OrderByDescending(path => path.Length).ThenBy(path => path, StringComparer.Ordinal) ?? Enumerable.Empty<string>()) if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.SaveAssets(); journal["status"] = "repaired"; journal["repaired_utc"] = DateTime.UtcNow.ToString("o"); SaveJournal(journal);
            return new SuccessResponse("Incomplete generated assets were removed", journal);
        }

        private static JObject ResolveSpec(JObject parameters, out string error)
        {
            error = null;
            JObject inline = parameters?["spec"] as JObject;
            string path = parameters?.Value<string>("spec_path");
            if (inline != null && !string.IsNullOrWhiteSpace(path)) { error = "Provide exactly one of spec or spec_path."; return null; }
            if (inline != null) return inline;
            if (string.IsNullOrWhiteSpace(path)) { error = "A versioned equipment spec is required."; return null; }
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) { error = "spec_path must resolve to an Assets-relative JSON TextAsset."; return null; }
            try { return JObject.Parse(asset.text)["spec"] as JObject ?? JObject.Parse(asset.text); }
            catch (Exception exception) { error = "spec_path JSON could not be parsed: " + exception.Message; return null; }
        }

        private static List<string> Validate(JObject spec, JObject parameters)
        {
            var errors = new List<string>();
            if (spec.Value<int?>("schema_version") != EquipmentCommon.SpecVersion) errors.Add("schema_version must be " + EquipmentCommon.SpecVersion + ".");
            string kind = spec.Value<string>("content_kind");
            if (kind is not ("family_equipment" or "legacy_equipment" or "handheld_equipment" or "performance_part")) errors.Add("content_kind is unsupported.");
            if (!EquipmentCommon.IsSafeId(spec.Value<string>("stable_id"))) errors.Add("stable_id may contain only letters, numbers, hyphens, and underscores.");
            if (string.IsNullOrWhiteSpace(spec.Value<string>("display_name"))) errors.Add("display_name is required.");
            if ((spec.Value<int?>("price") ?? 0) < 0) errors.Add("price cannot be negative.");
            string outputRoot = spec.Value<string>("output_root") ?? EquipmentCommon.DefaultOutputRoot;
            if (!EquipmentCommon.IsSafeAssetPath(outputRoot + "/validation.asset")) errors.Add("output_root must be a traversal-free Assets-relative path.");
            JObject target = parameters?["target"] as JObject;
            string registryPath = target?.Value<string>("registry_prefab_path") ?? target?.Value<string>("registryPrefabPath");
            if (string.IsNullOrWhiteSpace(registryPath) || AssetDatabase.LoadAssetAtPath<GameObject>(registryPath) == null) errors.Add("target.registry_prefab_path must identify an exact registry prefab.");
            else ValidateRegistry(registryPath, kind == "performance_part" ? "SaveManager" : "EquipmentDatabase", errors);
            if (kind != "performance_part") ValidateEquipment(spec, kind, errors);
            else ValidatePerformance(spec, errors);
            return errors;
        }

        private static void ValidateEquipment(JObject spec, string kind, ICollection<string> errors)
        {
            if (!EquipmentCommon.IsInstalled("EquipmentModule") || !EquipmentCommon.IsInstalled("Equipment")) errors.Add("Project Storm equipment types were not detected. Install this package into a compatible Project Storm project.");
            string template = spec.Value<string>("gameplay_template") ?? "mountable";
            string component = template switch { "mountable" => "MountableEquipment", "doppler_radar" => "EQDopplerRadar", "cargo" => "EQCargo", "handheld" => "HandHeldEquipment", _ => null };
            if (component == null || !EquipmentCommon.IsInstalled(component)) errors.Add("Unsupported or unavailable gameplay_template '" + template + "'.");
            if (kind == "family_equipment" && (!EquipmentCommon.IsInstalled("EquipmentFamily") || spec["variants"] is not JArray variants || variants.Count == 0)) errors.Add("family_equipment requires EquipmentFamily and at least one variant.");
            foreach (JObject variant in spec["variants"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (string.IsNullOrWhiteSpace(variant.Value<string>("mount_type"))) errors.Add("Every variant requires mount_type.");
                if (string.IsNullOrWhiteSpace(variant.Value<string>("vehicle_truck_type"))) errors.Add("Every variant requires vehicle_truck_type.");
            }
            JObject composition = spec["composition"] as JObject ?? new JObject();
            bool hasImported = !string.IsNullOrWhiteSpace(spec.Value<string>("source_asset_path"));
            if (hasImported && (!spec.Value<string>("source_asset_path").Replace('\\', '/').StartsWith("Assets/Generated/Imported/", StringComparison.Ordinal) || AssetDatabase.LoadAssetAtPath<GameObject>(spec.Value<string>("source_asset_path")) == null)) errors.Add("source_asset_path must resolve to an imported prefab under Assets/Generated/Imported.");
            foreach (JObject prop in composition["props"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()) if (!EquipmentCommon.IsApprovedProp(prop.Value<string>("asset_path")) || AssetDatabase.LoadAssetAtPath<GameObject>(prop.Value<string>("asset_path")) == null) errors.Add("Every composition prop must be an optimized prefab under " + EquipmentCommon.PropsRoot + ".");
            foreach (JObject primitive in composition["primitives"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (!new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" }.Contains(primitive.Value<string>("primitive_type"), StringComparer.OrdinalIgnoreCase)) errors.Add("Primitive type is not approved.");
                if (!EquipmentCommon.IsApprovedMaterial(primitive.Value<string>("material_asset_path"))) errors.Add("Every primitive requires an existing material under Assets/.");
            }
            if (!hasImported && !(composition["props"]?.HasValues ?? false) && !(composition["primitives"]?.HasValues ?? false)) errors.Add("Equipment composition requires an approved Prop, primitive, or imported source_asset_path.");
        }

        private static void ValidatePerformance(JObject spec, ICollection<string> errors)
        {
            if (!EquipmentCommon.IsInstalled("PerformanceObject")) errors.Add("PerformanceObject was not detected in the project.");
            JObject performance = spec["performance"] as JObject;
            if (performance == null || string.IsNullOrWhiteSpace(performance.Value<string>("performance_type")) || string.IsNullOrWhiteSpace(performance.Value<string>("vehicle_truck_type"))) errors.Add("performance_part requires performance.performance_type and performance.vehicle_truck_type.");
        }

        private static void ValidateRegistry(string path, string typeName, ICollection<string> errors)
        {
            GameObject contents = null;
            try { contents = PrefabUtility.LoadPrefabContents(path); if (EquipmentCommon.FindComponent(contents, typeName) == null) errors.Add("Configured registry prefab has no " + typeName + " component."); }
            finally { if (contents != null) PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static UnityEngine.Object CreateAsset(string typeName, string path, ICollection<string> created, JObject journal)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) throw new InvalidOperationException("Asset already exists: " + path);
            Type type = EquipmentCommon.FindType(typeName) ?? throw new InvalidOperationException(typeName + " was not detected.");
            var asset = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(asset, path); Track(created, journal, path); return asset;
        }

        private static void CreateVisualCore(JObject spec, string path, ICollection<string> created, JObject journal)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) throw new InvalidOperationException("Asset already exists: " + path);
            GameObject root = new GameObject(spec.Value<string>("stable_id") + " Visual Core");
            try
            {
                string imported = spec.Value<string>("source_asset_path");
                if (!string.IsNullOrWhiteSpace(imported)) AddVisualInstance(root.transform, AssetDatabase.LoadAssetAtPath<GameObject>(imported), Vector3.zero, Vector3.zero, Vector3.one, "Imported Source");
                JObject composition = spec["composition"] as JObject ?? new JObject();
                foreach (JObject prop in composition["props"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()) AddVisualInstance(root.transform, AssetDatabase.LoadAssetAtPath<GameObject>(prop.Value<string>("asset_path")), ReadVector(prop["local_position"]), ReadVector(prop["local_euler_angles"]), ReadVector(prop["local_scale"], Vector3.one), prop.Value<string>("name"));
                foreach (JObject primitive in composition["primitives"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()) AddPrimitive(root.transform, primitive);
                PrefabUtility.SaveAsPrefabAsset(root, path); Track(created, journal, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static UnityEngine.Object CreateVariant(JObject spec, JObject variant, string corePath, string root, UnityEngine.Object family, ICollection<string> created, JObject journal)
        {
            string suffix = family == null ? "default" : variant.Value<string>("mount_type").ToLowerInvariant();
            string id = spec.Value<string>("stable_id") + "_" + suffix;
            string prefabPath = root + "/Variants/" + id + ".prefab";
            string modulePath = root + "/Modules/" + id + ".asset";
            GameObject wrapper = new GameObject(id);
            try
            {
                string template = spec.Value<string>("gameplay_template") ?? "mountable";
                Type runtimeType = EquipmentCommon.FindType(template switch { "doppler_radar" => "EQDopplerRadar", "cargo" => "EQCargo", "handheld" => "HandHeldEquipment", _ => "MountableEquipment" }) ?? throw new InvalidOperationException("Runtime equipment type is unavailable.");
                Component runtime = wrapper.AddComponent(runtimeType);
                SetRequired(runtime, "equipmentID", id); SetRequired(runtime, "displayName", spec.Value<string>("display_name")); SetRequired(runtime, "description", spec.Value<string>("description") ?? string.Empty);
                AddVisualInstance(wrapper.transform, AssetDatabase.LoadAssetAtPath<GameObject>(corePath), ReadVector(variant["local_position"]), ReadVector(variant["local_euler_angles"]), ReadVector(variant["local_scale"], Vector3.one), "Visual Core");
                PrefabUtility.SaveAsPrefabAsset(wrapper, prefabPath); Track(created, journal, prefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(wrapper); }
            UnityEngine.Object module = CreateAsset("EquipmentModule", modulePath, created, journal);
            SetRequired(module, "equipmentID", id); SetRequired(module, "prefab", new JObject { ["asset_path"] = prefabPath }); SetRequired(module, "BasePrice", family == null ? spec.Value<int?>("price") ?? 0 : 0); SetRequired(module, "ShowInShop", family == null && (spec.Value<bool?>("show_in_shop") ?? true)); SetRequired(module, "Stackable", false); SetRequired(module, "MaxStack", 1); SetRequired(module, "variantSelectionPriority", variant.Value<int?>("selection_priority") ?? 0); SetRequired(module, "compatibleMount", variant.Value<string>("mount_type")); SetRequired(module, "compatibleVehicleTruckType", variant.Value<string>("vehicle_truck_type"));
            if (family != null) SetRequired(module, "family", new JObject { ["asset_path"] = AssetDatabase.GetAssetPath(family) });
            return module;
        }

        private static UnityEngine.Object CreatePerformancePart(JObject spec, string root, ICollection<string> created, JObject journal)
        {
            string path = root + "/" + spec.Value<string>("stable_id") + ".asset";
            UnityEngine.Object part = CreateAsset("PerformanceObject", path, created, journal);
            JObject data = spec["performance"] as JObject;
            SetRequired(part, "ID", spec.Value<string>("stable_id")); SetRequired(part, "displayName", spec.Value<string>("display_name")); SetRequired(part, "description", spec.Value<string>("description") ?? string.Empty); SetRequired(part, "price", spec.Value<int?>("price") ?? 0); SetRequired(part, "ShowInShop", spec.Value<bool?>("show_in_shop") ?? true); SetRequired(part, "type", data.Value<string>("performance_type")); SetRequired(part, "vehicleTruckType", data.Value<string>("vehicle_truck_type"));
            foreach (string field in new[] { "TorqueModifier", "AccelerationModifier", "SpeedModifier", "FuelCapacityModifier", "FuelConsumptionModifier", "EnergyGenerationModifier", "EnergyStorageModifier", "WeightModifier" }) if (data[field] != null) SetRequired(part, field, data[field]);
            return part;
        }

        private static void RegisterEquipment(JObject parameters, UnityEngine.Object family, IEnumerable<UnityEngine.Object> modules, ICollection<UnityEngine.Object> registrations, JObject journal)
        {
            WithRegistry(parameters, "EquipmentDatabase", (contents, registry) =>
            {
                foreach (UnityEngine.Object module in modules) { AppendRequired(registry, "AllEquipment", module); registrations.Add(module); ((JArray)journal["registered_assets"]).Add(AssetDatabase.GetAssetPath(module)); }
                if (family != null) { AppendRequired(registry, "AllEquipmentFamilies", family); registrations.Add(family); ((JArray)journal["registered_assets"]).Add(AssetDatabase.GetAssetPath(family)); }
            });
        }

        private static void RegisterPerformance(JObject parameters, UnityEngine.Object part, ICollection<UnityEngine.Object> registrations, JObject journal)
        {
            WithRegistry(parameters, "SaveManager", (contents, registry) => { AppendRequired(registry, "allPerformanceParts", part); registrations.Add(part); ((JArray)journal["registered_assets"]).Add(AssetDatabase.GetAssetPath(part)); });
        }

        private static void Unregister(JObject parameters, IEnumerable<UnityEngine.Object> registrations)
        {
            try
            {
                WithRegistry(parameters, "EquipmentDatabase", (contents, registry) => { foreach (UnityEngine.Object value in registrations) { EquipmentCommon.RemoveReference(registry, "AllEquipment", value); EquipmentCommon.RemoveReference(registry, "AllEquipmentFamilies", value); } });
                WithRegistry(parameters, "SaveManager", (contents, registry) => { foreach (UnityEngine.Object value in registrations) EquipmentCommon.RemoveReference(registry, "allPerformanceParts", value); });
            }
            catch { /* rollback is best effort; original error remains authoritative */ }
        }

        private static void WithRegistry(JObject parameters, string componentType, Action<GameObject, Component> action)
        {
            JObject target = parameters?["target"] as JObject ?? throw new InvalidOperationException("A target.registry_prefab_path is required.");
            string path = target.Value<string>("registry_prefab_path") ?? target.Value<string>("registryPrefabPath") ?? throw new InvalidOperationException("A target.registry_prefab_path is required.");
            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(path);
                Component registry = EquipmentCommon.FindComponent(contents, componentType) ?? throw new InvalidOperationException("Registry prefab has no " + componentType + " component.");
                action(contents, registry);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { if (contents != null) PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void CreateThumbnail(UnityEngine.Object family, string visualPath, string outputPath, ICollection<string> created, JObject journal)
        {
            GameObject visual = AssetDatabase.LoadAssetAtPath<GameObject>(visualPath) ?? throw new InvalidOperationException("Visual prefab could not be loaded for thumbnail generation.");
            if (!EquipmentCommon.IsSafeAssetPath(outputPath)) throw new InvalidOperationException("Thumbnail output path must be a traversal-free Assets-relative PNG path.");
            GameObject instance = null; PreviewRenderUtility preview = null; RenderTexture renderTexture = null; Texture2D texture = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(visual);
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) throw new InvalidOperationException("Visual prefab has no renderers for thumbnail generation.");
                Bounds bounds = renderers[0].bounds; foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                preview = new PreviewRenderUtility(); preview.camera.clearFlags = CameraClearFlags.Color; preview.camera.backgroundColor = new Color(0f, 0f, 0f, 0f); preview.AddSingleGO(instance);
                float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.1f); preview.camera.orthographic = true; preview.camera.orthographicSize = size * 0.75f; preview.camera.transform.position = bounds.center + new Vector3(0f, 0f, -size * 3f); preview.camera.transform.LookAt(bounds.center); preview.lights[0].intensity = 1.2f; preview.lights[0].transform.rotation = Quaternion.Euler(35f, -30f, 0f);
                renderTexture = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32); preview.camera.targetTexture = renderTexture; preview.camera.Render(); RenderTexture.active = renderTexture; texture = new Texture2D(512, 512, TextureFormat.RGBA32, false); texture.ReadPixels(new Rect(0, 0, 512, 512), 0, 0); texture.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(EquipmentCommon.AbsoluteAssetPath(outputPath)) ?? throw new InvalidOperationException("Thumbnail destination is invalid.")); File.WriteAllBytes(EquipmentCommon.AbsoluteAssetPath(outputPath), texture.EncodeToPNG()); AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);
                if (AssetImporter.GetAtPath(outputPath) is not TextureImporter importer) throw new InvalidOperationException("Thumbnail PNG did not import as a texture.");
                importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single; importer.mipmapEnabled = false; importer.alphaIsTransparency = true; importer.SaveAndReimport();
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(outputPath) ?? throw new InvalidOperationException("Thumbnail PNG did not expose a Sprite."); SetRequired(family, "icon", new JObject { ["asset_path"] = outputPath }); Track(created, journal, outputPath);
            }
            finally { RenderTexture.active = null; if (texture != null) UnityEngine.Object.DestroyImmediate(texture); if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.DestroyImmediate(renderTexture); } if (preview != null) preview.Cleanup(); if (instance != null) UnityEngine.Object.DestroyImmediate(instance); }
        }

        private static void AddVisualInstance(Transform parent, GameObject source, Vector3 position, Vector3 rotation, Vector3 scale, string name)
        {
            if (source == null) throw new InvalidOperationException("Composition source could not be loaded.");
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source); instance.name = string.IsNullOrWhiteSpace(name) ? source.name : name; instance.transform.SetParent(parent, false); instance.transform.localPosition = position; instance.transform.localRotation = Quaternion.Euler(rotation); instance.transform.localScale = scale;
            foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(behaviour);
        }

        private static void AddPrimitive(Transform parent, JObject primitive)
        {
            if (!Enum.TryParse(primitive.Value<string>("primitive_type"), true, out PrimitiveType type)) throw new InvalidOperationException("Unsupported primitive type.");
            GameObject instance = GameObject.CreatePrimitive(type); instance.name = primitive.Value<string>("name") ?? type.ToString(); instance.transform.SetParent(parent, false); instance.transform.localPosition = ReadVector(primitive["local_position"]); instance.transform.localRotation = Quaternion.Euler(ReadVector(primitive["local_euler_angles"])); instance.transform.localScale = ReadVector(primitive["local_scale"], Vector3.one);
            if (primitive.Value<bool?>("collider") == false) UnityEngine.Object.DestroyImmediate(instance.GetComponent<Collider>());
            Material material = AssetDatabase.LoadAssetAtPath<Material>(primitive.Value<string>("material_asset_path")); foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true)) renderer.sharedMaterial = material;
        }

        private static IEnumerable<(string property, JToken value)> FieldsForUpdate(string typeName, JObject spec)
        {
            if (typeName == "EquipmentFamily") return new[] { ("displayName", spec["display_name"]), ("description", spec["description"]), ("basePrice", spec["price"]), ("showInShop", spec["show_in_shop"]) }.Where(pair => pair.Item2 != null);
            if (typeName == "EquipmentModule") return new[] { ("BasePrice", spec["price"]), ("ShowInShop", spec["show_in_shop"]) }.Where(pair => pair.Item2 != null);
            return new[] { ("displayName", spec["display_name"]), ("description", spec["description"]), ("price", spec["price"]), ("ShowInShop", spec["show_in_shop"]) }.Where(pair => pair.Item2 != null);
        }

        private static void SetRequired(UnityEngine.Object target, string property, object value)
        {
            JToken token = value as JToken ?? JToken.FromObject(value);
            if (!EquipmentCommon.SetProperty(target, property, token, out string error)) throw new InvalidOperationException(error);
        }

        private static void AppendRequired(UnityEngine.Object target, string property, UnityEngine.Object value)
        {
            if (!EquipmentCommon.AppendReference(target, property, value, out string error)) throw new InvalidOperationException(error);
        }

        private static void Track(ICollection<string> created, JObject journal, string path) { created.Add(path); ((JArray)journal["created_assets"]).Add(path); SaveJournal(journal); }
        private static Vector3 ReadVector(JToken token, Vector3 fallback = default)
        {
            if (token is JArray array && array.Count >= 3) return new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
            if (token is JObject vector) return new Vector3(vector.Value<float?>("x") ?? fallback.x, vector.Value<float?>("y") ?? fallback.y, vector.Value<float?>("z") ?? fallback.z);
            return fallback;
        }
        private static string JournalPath(string operationId) => Path.Combine(EquipmentCommon.JournalDirectory, operationId + ".json");
        private static void SaveJournal(JObject journal) { Directory.CreateDirectory(EquipmentCommon.JournalDirectory); File.WriteAllText(JournalPath(journal.Value<string>("operation_id")), journal.ToString(Formatting.Indented)); }
        private static void EnsureFolder(string folder) { string parent = "Assets"; foreach (string segment in folder.Split('/').Skip(1)) { string next = parent + "/" + segment; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(parent, segment); parent = next; } }
    }
}

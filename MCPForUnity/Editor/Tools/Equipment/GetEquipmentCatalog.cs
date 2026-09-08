using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Equipment
{
    /// <summary>Read-only Project Storm equipment, performance-part, Prop, and registry discovery.</summary>
    [McpForUnityTool("get_equipment_catalog", AutoRegister = false, Group = "equipment", Capability = ToolCapability.Inspection)]
    public static class GetEquipmentCatalog
    {
        public static object HandleCommand(JObject @params)
        {
            string action = (@params?["action"]?.Value<string>() ?? "status").Trim().ToLowerInvariant();
            try
            {
                return action switch
                {
                    "status" => new SuccessResponse("Project Storm equipment tooling status", EquipmentCommon.Status()),
                    "catalog" or "search" => new SuccessResponse("Approved equipment Prop catalog", Catalog(@params)),
                    "describe" or "inspect" => Describe(@params),
                    "resolve" => Resolve(@params),
                    "propose_composition" => ProposeComposition(@params),
                    "preflight" => ManageEquipment.Preflight(@params),
                    "registry_candidates" => new SuccessResponse("Explicit equipment registry candidates", RegistryCandidates()),
                    "operation_status" => ManageEquipment.OperationStatus(@params),
                    "coverage" => new SuccessResponse("Equipment tooling coverage", Coverage()),
                    "refresh_cache" => RefreshCache(),
                    _ => new ErrorResponse("Unknown equipment catalog action: " + action)
                };
            }
            catch (Exception exception)
            {
                McpLog.Error("[Equipment] Catalog request failed: " + exception);
                return new ErrorResponse("Equipment catalog request failed: " + exception.Message);
            }
        }

        private static object Catalog(JObject parameters)
        {
            int page = Math.Max(1, parameters?.Value<int?>("page") ?? 1);
            int pageSize = Mathf.Clamp(parameters?.Value<int?>("page_size") ?? parameters?.Value<int?>("pageSize") ?? 50, 1, 200);
            string query = parameters?.Value<string>("query");
            List<GameObject> props = EquipmentCommon.DiscoverProps(query).ToList();
            return new
            {
                page,
                page_size = pageSize,
                prop_total = props.Count,
                props = props.Skip((page - 1) * pageSize).Take(pageSize).Select(EquipmentCommon.DescribePrefab).ToArray(),
                families = DiscoverAssets("EquipmentFamily").Select(EquipmentCommon.DescribeAsset).ToArray(),
                modules = DiscoverAssets("EquipmentModule").Select(EquipmentCommon.DescribeAsset).ToArray(),
                performance_parts = DiscoverAssets("PerformanceObject").Select(EquipmentCommon.DescribeAsset).ToArray(),
                allowed_primitives = new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" },
                next_page = page * pageSize < props.Count ? page + 1 : (int?)null,
                excluded_source_roots = new[] { "Assets/Cargo/Meshes" }
            };
        }

        private static object Describe(JObject parameters)
        {
            UnityEngine.Object asset = EquipmentCommon.ResolveAsset(parameters?["target"] ?? parameters?["asset_path"] ?? parameters?["assetPath"]);
            if (asset == null) return new ErrorResponse("Provide an exact asset_path, asset GUID reference, or resolvable target.");
            if (asset is GameObject prefab) return new SuccessResponse("Equipment prefab description", EquipmentCommon.DescribePrefab(prefab));
            return new SuccessResponse("Equipment asset description", EquipmentCommon.DescribeAsset(asset));
        }

        private static object Resolve(JObject parameters)
        {
            string query = parameters?.Value<string>("query");
            if (string.IsNullOrWhiteSpace(query)) return new ErrorResponse("resolve requires a Prop query.");
            return new SuccessResponse("Equipment Prop candidates", new
            {
                query,
                candidates = EquipmentCommon.DiscoverProps(query).Take(50).Select(EquipmentCommon.DescribePrefab).ToArray(),
                exact_identity_required_for_mutation = true
            });
        }

        private static object ProposeComposition(JObject parameters)
        {
            object preflight = ManageEquipment.Preflight(parameters);
            if (preflight is ErrorResponse) return preflight;
            return new SuccessResponse("Equipment composition is valid for authoring", new
            {
                preflight,
                next = "Use manage_equipment action=create or compose with execute=true after Project Automation approval."
            });
        }

        private static object RegistryCandidates()
        {
            var candidates = new List<object>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                GameObject contents = null;
                try
                {
                    contents = PrefabUtility.LoadPrefabContents(path);
                    Component database = EquipmentCommon.FindComponent(contents, "EquipmentDatabase");
                    Component saveManager = EquipmentCommon.FindComponent(contents, "SaveManager");
                    if (database == null && saveManager == null) continue;
                    candidates.Add(new { asset_path = path, asset_guid = guid, has_equipment_database = database != null, has_save_manager = saveManager != null, selection_required = true });
                }
                finally
                {
                    if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            return new { candidates = candidates.OrderBy(candidate => candidate.ToString(), StringComparer.Ordinal).ToArray(), policy = "No registry is selected implicitly; manage_equipment requires target.registry_prefab_path." };
        }

        private static object Coverage() => new
        {
            content_kinds = new[] { "family_equipment", "legacy_equipment", "handheld_equipment", "performance_part" },
            read_actions = new[] { "status", "catalog", "search", "describe", "inspect", "resolve", "propose_composition", "preflight", "registry_candidates", "operation_status", "coverage", "refresh_cache" },
            mutation_actions = new[] { "create", "compose", "update", "thumbnail", "validate", "repair", "deprecate", "purge" },
            source_policy = new { props_root = EquipmentCommon.PropsRoot, imported_model_root = "Assets/Generated/Imported", excluded = "Assets/Cargo/Meshes" },
            game_assembly_reference = false
        };

        private static object RefreshCache()
        {
            EquipmentCommon.InvalidateCache();
            return new SuccessResponse("Equipment reflection cache cleared without mutating project assets", EquipmentCommon.Status());
        }

        private static IEnumerable<UnityEngine.Object> DiscoverAssets(string typeName)
        {
            Type type = EquipmentCommon.FindType(typeName);
            if (type == null) return Enumerable.Empty<UnityEngine.Object>();
            return AssetDatabase.FindAssets("t:" + typeName).Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadMainAssetAtPath).Where(asset => asset != null && type.IsInstanceOfType(asset))
                .OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal).ToArray();
        }
    }
}

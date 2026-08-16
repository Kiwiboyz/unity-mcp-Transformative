using System;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.ModernUI
{
    /// <summary>Read-only discovery, validation, and contextual-style inspection for Modern UI Pack.</summary>
    [McpForUnityTool("get_modern_ui_catalog", AutoRegister = false, Group = "ui", Capability = ToolCapability.Inspection)]
    public static class GetModernUICatalog
    {
        public static object HandleCommand(JObject @params)
        {
            var action = (@params?["action"]?.Value<string>() ?? "status").ToLowerInvariant();
            var packagePath = ModernUICommon.ResolvePackagePath(@params?["package_path"]?.Value<string>());
            try
            {
                return action switch
                {
                    "status" => new SuccessResponse("Modern UI Pack status", ModernUICommon.BuildStatus(packagePath)),
                    "catalog" => new SuccessResponse("Modern UI Pack catalog", ModernUICommon.BuildCatalog(
                        packagePath, @params?["family"]?.Value<string>(), @params?.Value<bool?>("include_examples") ?? false,
                        @params?.Value<int?>("page") ?? 1, @params?.Value<int?>("page_size") ?? 50)),
                    "describe" => Describe(@params, packagePath),
                    "inspect" => Inspect(@params),
                    "resolve_style_source" => ResolveStyleSource(@params),
                    "preflight" => ManageModernUI.Preflight(@params, packagePath),
                    "coverage" => Coverage(packagePath),
                    "refresh_cache" => RefreshCache(),
                    _ => new ErrorResponse("Unknown Modern UI catalog action: " + action)
                };
            }
            catch (Exception ex)
            {
                McpLog.Error("[ModernUI] Catalog request failed: " + ex.Message);
                return new ErrorResponse("Modern UI catalog request failed: " + ex.Message);
            }
        }

        private static object Describe(JObject @params, string packagePath)
        {
            var requested = @params?["component"]?.Value<string>();
            var type = ModernUICommon.FindComponentType(requested) ?? ModernUICommon.GetModernUITypes().FirstOrDefault(candidate => candidate.Name == requested || candidate.FullName == requested);
            return type == null ? new ErrorResponse("A valid Modern UI component name is required.") : new SuccessResponse("Modern UI component schema", ModernUICommon.DescribeType(type));
        }

        private static object Inspect(JObject @params)
        {
            var target = ResolveTarget(@params?["target"]);
            if (target == null) return new ErrorResponse("Target could not be resolved. Provide an exact GlobalObjectId, hierarchy path, or GameObject name.");
            var components = target.GetComponents<Component>().Where(component => component != null && component.GetType().Namespace == ModernUICommon.NamespacePrefix)
                .Select(component => new
                {
                    type = component.GetType().FullName,
                    globalId = ModernUICommon.GlobalId(component),
                    fields = component.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(field => !field.IsNotSerialized).ToDictionary(field => field.Name, field => field.GetValue(component)?.ToString())
                }).ToArray();
            return new SuccessResponse("Modern UI target inspection", new
            {
                name = target.name,
                path = ModernUIObjectMatch.GetHierarchyPath(target.transform),
                globalId = ModernUICommon.GlobalId(target),
                components
            });
        }

        private static object ResolveStyleSource(JObject @params)
        {
            var query = @params?["name"]?.Value<string>() ?? @params?["target"]?.Value<string>();
            var expected = @params?["component"]?.Value<string>();
            var near = ResolveTarget(@params?["near"]);
            var selected = ModernUIObjectResolver.ResolveClear(query, expected, near, out var candidates);
            return new SuccessResponse(selected == null ? "Style source is ambiguous or missing" : "Style source resolved", new JObject
            {
                ["requested"] = query,
                ["resolved"] = selected == null ? JValue.CreateNull() : JObject.FromObject(selected.ToResponse()),
                ["candidates"] = new JArray(candidates.Select(candidate => JObject.FromObject(candidate.ToResponse()))),
                ["requiresConfirmation"] = selected == null
            });
        }

        private static object Coverage(string packagePath)
        {
            var rows = ModernUICommon.GetModernUIComponentTypes().Select(type => new
            {
                component = type.Name,
                category = ModernUICommon.GetCategory(type),
                support = ModernUICommon.IsProductionType(type) ? "supported" : "example_only",
                discovery = true,
                creation = true,
                configuration = "generic_serialized",
                references = "assets_and_global_ids",
                collections = "generic_serialized_arrays",
                events = "validated_unityevents",
                refreshMethods = ModernUIAdapterRegistry.GetRefreshMethods(type).ToArray(),
                refresh = ModernUIAdapterRegistry.GetRefreshMethods(type).Any() ? "allowlisted" : "not_required",
                validation = "serialized_schema_and_preflight",
                testRequirement = "synthetic_fixture_and_real_package_acceptance"
            }).ToArray();
            return new SuccessResponse("Modern UI V1 coverage", new
            {
                version = ModernUICommon.GetVersion(packagePath), rows,
                complete = rows.Where(row => row.support == "supported").All(row => row.discovery && row.creation && row.configuration == "generic_serialized"),
                realPackageAcceptanceRequired = true
            });
        }

        private static object RefreshCache()
        {
            ModernUICommon.InvalidateCache();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return new SuccessResponse("Modern UI discovery cache refreshed", new { refreshed = true });
        }

        internal static GameObject ResolveTarget(JToken target)
        {
            if (target == null || target.Type == JTokenType.Null) return null;
            if (target.Type == JTokenType.Object)
            {
                var globalId = target.Value<string>("global_id");
                var byId = ModernUICommon.ResolveGlobalObject(globalId);
                if (byId != null) return byId;
                target = target["path"] ?? target["name"];
            }
            var text = target.Value<string>();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var exact = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(go => go != null && go.scene.IsValid() && ModernUIObjectMatch.GetHierarchyPath(go.transform) == text);
            if (exact != null) return exact;
            return UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(go => go != null && go.scene.IsValid() && go.name == text);
        }
    }
}

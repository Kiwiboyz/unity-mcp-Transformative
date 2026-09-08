using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Graphics;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Environment
{
    [McpForUnityTool("get_environment_catalog", AutoRegister = false, Group = "vfx", Capability = ToolCapability.Inspection)]
    public static class GetEnvironmentCatalog
    {
        public static object HandleCommand(JObject @params)
        {
            var action = (@params?["action"]?.Value<string>() ?? "status").ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "status": return new SuccessResponse("Environment tooling status", Status());
                    case "catalog": return new SuccessResponse("Environment catalog", Catalog(@params));
                    case "describe": return Describe(@params);
                    case "inspect_context": return InspectContext(@params);
                    case "inspect_authority": return new SuccessResponse("Environment authority", Authority());
                    case "inspect_profile": return InspectProfile(@params);
                    case "inspect_preset": return InspectPreset(@params);
                    case "preflight": return ManageEnvironment.Preflight(@params);
                    case "coverage": return new SuccessResponse("Environment coverage", Coverage());
                    case "resolve_target": return ResolveTarget(@params);
                    case "goal_status": return ManageEnvironment.GoalStatus(@params);
                    case "refresh_cache": EnvironmentCommon.InvalidateCache(); return new SuccessResponse("Environment cache refreshed", Status());
                    default: return new ErrorResponse("Unknown environment catalog action: " + action);
                }
            }
            catch (Exception ex) { McpLog.Error("[Environment] Catalog request failed: " + ex.Message); return new ErrorResponse("Environment catalog request failed: " + ex.Message); }
        }

        private static object Status() => new
        {
            specVersion = EnvironmentCommon.SpecVersion,
            volumeSystemInstalled = GraphicsHelpers.HasVolumeSystem,
            hdrpActive = GraphicsHelpers.HasHDRP,
            supportedTypeCount = EnvironmentCommon.GetSupportedTypes().Count,
            expanseInstalled = EnvironmentCommon.GetSupportedTypes().Any(type => type.Name == "ExpanseSettings"),
            persistentVolumeRule = "Persistent volume changes target Volume.sharedProfile assets. Volume.profile clones are inspection-only.",
            tornadoCloudVolumeSupported = false,
            goalStorage = EnvironmentCommon.GoalDirectory
        };

        private static object Catalog(JObject parameters)
        {
            var family = parameters?["family"]?.Value<string>();
            var page = Math.Max(1, parameters?.Value<int?>("page") ?? 1);
            var pageSize = Mathf.Clamp(parameters?.Value<int?>("page_size") ?? 50, 1, 200);
            var types = EnvironmentCommon.GetSupportedTypes().Where(type => string.IsNullOrWhiteSpace(family) || EnvironmentCommon.Family(type) == family).ToList();
            var assets = DiscoverAuthoringAssets(family);
            var total = Math.Max(types.Count, assets.Count);
            return new
            {
                page, pageSize, componentTotal = types.Count, assetTotal = assets.Count,
                components = types.Skip((page - 1) * pageSize).Take(pageSize).Select(EnvironmentCommon.Describe).ToArray(),
                assets = assets.Skip((page - 1) * pageSize).Take(pageSize).Select(asset => new { target = EnvironmentCommon.ObjectReference(asset), family = EnvironmentCommon.Family(asset.GetType()), fields = EnvironmentCommon.ReadSerialized(asset, 1) }).ToArray(),
                nextPage = page * pageSize < total ? page + 1 : (int?)null
            };
        }

        private static List<UnityEngine.Object> DiscoverAuthoringAssets(string family)
        {
            var result = new List<UnityEngine.Object>();
            foreach (var guid in AssetDatabase.FindAssets(string.Empty))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null) continue;
                var isProfile = GraphicsHelpers.VolumeProfileType != null && GraphicsHelpers.VolumeProfileType.IsAssignableFrom(asset.GetType());
                if ((!isProfile && !EnvironmentCommon.IsSupportedType(asset.GetType())) || (!string.IsNullOrWhiteSpace(family) && EnvironmentCommon.Family(asset.GetType()) != family)) continue;
                result.Add(asset);
            }
            return result.OrderBy(asset => AssetDatabase.GetAssetPath(asset), StringComparer.Ordinal).ToList();
        }

        private static object Describe(JObject parameters)
        {
            var requested = parameters?["component"]?.Value<string>();
            var type = EnvironmentCommon.GetSupportedTypes().FirstOrDefault(candidate => candidate.Name == requested || candidate.FullName == requested);
            return type == null ? new ErrorResponse("A supported environment component type is required.") : new SuccessResponse("Environment type schema", EnvironmentCommon.Describe(type));
        }

        private static object ResolveTarget(JObject parameters)
        {
            var requested = parameters?["target"];
            if (requested is JObject exact && (!string.IsNullOrEmpty(exact.Value<string>("global_id")) || !string.IsNullOrEmpty(exact.Value<string>("asset_path"))))
            {
                var target = EnvironmentCommon.ResolveObject(exact);
                return target == null ? new ErrorResponse("Exact target could not be resolved.") : new SuccessResponse("Environment target resolved", new { target = EnvironmentCommon.ObjectReference(target), fields = EnvironmentCommon.ReadSerialized(target) });
            }
            var query = parameters?["query"]?.Value<string>() ?? requested?.Value<string>();
            if (string.IsNullOrWhiteSpace(query)) return new ErrorResponse("Provide an exact target or a read-only discovery query.");
            var candidates = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Object>().Where(item => item != null && EnvironmentCommon.IsSupportedType(item.GetType()) && item.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(50).Select(EnvironmentCommon.ObjectReference).ToList();
            foreach (var guid in AssetDatabase.FindAssets(query))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null && EnvironmentCommon.IsSupportedType(asset.GetType()) && candidates.All(candidate => candidate.Value<string>("asset_path") != AssetDatabase.GetAssetPath(asset))) candidates.Add(EnvironmentCommon.ObjectReference(asset));
                if (candidates.Count >= 50) break;
            }
            return new SuccessResponse("Environment target candidates", new { query, candidates, exactIdentityRequiredForMutation = true });
        }

        private static object InspectContext(JObject parameters)
        {
            var requested = parameters?["context"] ?? parameters?["target"];
            var target = EnvironmentCommon.ResolveObject(requested);
            if (target == null) return new ErrorResponse("A context or target reference is required.");
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (gameObject == null) return new ErrorResponse("Environment context must resolve to a GameObject or Component.");
            var volumes = GraphicsHelpers.VolumeType == null ? Array.Empty<object>() : gameObject.GetComponents(GraphicsHelpers.VolumeType).Cast<Component>().Select(volume => new
            {
                target = EnvironmentCommon.ObjectReference(volume),
                sharedProfile = EnvironmentCommon.ObjectReference(EnvironmentCommon.SharedProfile(volume)),
                profileComponents = EnvironmentCommon.ProfileComponents(EnvironmentCommon.SharedProfile(volume)).Select(EnvironmentCommon.ObjectReference).ToArray(),
                persistentRule = "Only sharedProfile and its listed component subassets are durable authored targets."
            }).Cast<object>().ToArray();
            return new SuccessResponse("Environment context", new { target = EnvironmentCommon.ObjectReference(gameObject), scene = gameObject.scene.path, components = gameObject.GetComponents<Component>().Where(component => component != null && EnvironmentCommon.IsSupportedType(component.GetType())).Select(component => new { target = EnvironmentCommon.ObjectReference(component), fields = EnvironmentCommon.ReadSerialized(component) }).ToArray(), volumes });
        }

        private static object InspectProfile(JObject parameters)
        {
            var profile = EnvironmentCommon.ResolveObject(parameters?["asset_path"] != null ? new JObject { ["asset_path"] = parameters["asset_path"] } : parameters?["target"]);
            if (profile == null) return new ErrorResponse("A profile asset reference is required.");
            var components = EnvironmentCommon.ProfileComponents(profile).Select(item => new { target = EnvironmentCommon.ObjectReference(item), fields = EnvironmentCommon.ReadSerialized(item) }).Cast<object>().ToList();
            return new SuccessResponse("Persistent profile inspection", new { profile = EnvironmentCommon.ObjectReference(profile), components, warning = "Mutations must target this asset or a Volume.sharedProfile reference, never Volume.profile." });
        }

        private static object InspectPreset(JObject parameters)
        {
            var preset = EnvironmentCommon.ResolveObject(parameters?["asset_path"] != null ? new JObject { ["asset_path"] = parameters["asset_path"] } : parameters?["target"]);
            return preset == null ? new ErrorResponse("A preset asset reference is required.") : new SuccessResponse("Environment preset inspection", new { preset = EnvironmentCommon.ObjectReference(preset), fields = EnvironmentCommon.ReadSerialized(preset, 3) });
        }

        private static object Authority()
        {
            var writers = UnityEngine.Resources.FindObjectsOfTypeAll<Component>().Where(component => component != null && EnvironmentCommon.IsSupportedType(component.GetType()))
                .Select(component => new { target = EnvironmentCommon.ObjectReference(component), typeName = component.GetType().Name, family = EnvironmentCommon.Family(component.GetType()), active = component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy }).ToArray();
            var edges = UnityEngine.Resources.FindObjectsOfTypeAll<Component>().Where(component => component != null && EnvironmentCommon.IsSupportedType(component.GetType()))
                .SelectMany(component => EnvironmentCommon.GetFields(component.GetType())
                    .SelectMany(field => EnvironmentCommon.ReferencedObjects(component, field).Select(target => new { from = EnvironmentCommon.ObjectReference(component), field = field.Name, to = EnvironmentCommon.ObjectReference(target) }))).ToArray();
            var activeWeatherManagers = writers.Where(writer => writer.active && (writer.typeName == "WeatherManager" || writer.typeName == "StoryDrivenWeatherManager" || writer.typeName == "StormDrivenWeatherManager")).ToArray();
            return new { writers, edges, activeWeatherManagers, conflict = activeWeatherManagers.Length > 1 ? "multiple_authoritative_managers" : null, policy = "Object-reference edges identify authored upstream candidates. If an active Creative or weather manager points at a downstream target, commit to the upstream authoring object; direct downstream changes are preview-only." };
        }

        private static object Coverage() => new { supported = EnvironmentCommon.GetSupportedTypes().Select(type => new { type = type.FullName, family = EnvironmentCommon.Family(type), adapter = EnvironmentCommon.IsVisualEffect(type) ? "exposed_vfx" : "semantic_serialized" }).ToArray(), excluded = new[] { "TornadoCloudVolume" } };
    }
}

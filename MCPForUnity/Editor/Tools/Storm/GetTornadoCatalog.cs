using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
namespace MCPForUnity.Editor.Tools.Storm
{
    [McpForUnityTool("get_tornado_catalog", AutoRegister = false, Group = "vfx", Capability = ToolCapability.Inspection)]
    public static class GetTornadoCatalog
    {
        public static object HandleCommand(JObject parameters) => StormDesignerToolBridge.Invoke("tornado", false, parameters);
    }
}

namespace MCPForUnity.Editor.Tools.Storm
{
    /// <summary>Portable package adapter. Only the versioned, fixed Project Storm Editor endpoint may be invoked.</summary>
    public static class StormDesignerToolBridge
    {
        public static object Invoke(string domain, bool mutation, JObject parameters)
        {
            string action = (string)parameters?["action"] ?? "status";
            string[] allowed = mutation ? new[] { "preview_goal", "commit_goal", "restore_goal", "control", "laboratory", "capture", "profile_start", "profile_stop" }
                : new[] { "status", "catalog", "describe", "validate", "preflight", "coverage", "instances", "inspect", "validation_impact", "artifact", "sample", "sample_batch", "laboratory", "cost", "goal_status" };
            if (System.Array.IndexOf(allowed, action) < 0) return new ErrorResponse("Unknown semantic storm action: " + action);
            if (domain != "tornado" && domain != "storm_wind") return new ErrorResponse("Unknown storm domain.");
            try {
                System.Type bridge = null;
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies()) {
                    if (assembly.GetName().Name != "ProjectStorm.Tornado.Editor") continue;
                    bridge = assembly.GetType("ProjectStorm.Tornado.Editor.StormDesignerBridge", false); break;
                }
                if (bridge == null) return new ErrorResponse("Project Storm Stage9 Editor bridge is not installed or compiled. No changes were made.");
                var version = bridge.GetField("Version", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (version == null || (int)version.GetValue(null) != 1) return new ErrorResponse("Unsupported Project Storm authoring bridge version; expected1.");
                var method = bridge.GetMethod("Dispatch", new[] { typeof(string), typeof(bool), typeof(string) });
                if (method == null || method.ReturnType != typeof(string)) return new ErrorResponse("Project Storm authoring bridge signature mismatch.");
                string response = (string)method.Invoke(null, new object[] { domain, mutation, (parameters ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None) });
                return JObject.Parse(response);
            } catch (System.Exception e) { return new ErrorResponse("Storm authoring request failed: " + e.GetBaseException().Message); }
        }
    }
}

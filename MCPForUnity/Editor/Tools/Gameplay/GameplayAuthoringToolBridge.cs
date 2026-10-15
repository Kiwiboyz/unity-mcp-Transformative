using System;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Tools.Gameplay
{
    /// <summary>
    /// Portable adapter for Project Storm gameplay authoring (Storm Story graphs, Recipes, Mission Designer preview and
    /// simulation). It calls the project's fixed, versioned MissionDesignerBridge by reflection, so this package needs no
    /// compile reference to game code, and every request runs the same authoring services as the Mission Designer window.
    /// Only allow-listed semantic operations pass; the read tool can never reach a mutation.
    /// </summary>
    public static class GameplayAuthoringToolBridge
    {
        public const int BridgeVersion = 1;
        private static readonly string[] StoryReads = { "schema", "list", "read", "validate", "bindings", "references" };
        private static readonly string[] StoryMutations = { "create", "duplicate", "edit", "save", "publish", "anchor.add", "route.add", "route.set", "scene.adopt" };
        private static readonly string[] RecipeReads = { "schema", "read", "validate" };
        private static readonly string[] RecipeMutations = { "create", "duplicate", "save", "publish", "create-catalog" };
        private static readonly string[] DesignerReads = { "visual.query", "preview.bindings", "recipe.summary", "simulation.status", "story.live" };
        private static readonly string[] DesignerMutations = { "visual.set", "visual.focus", "simulation.configure", "simulation.start", "simulation.stop", "simulation.restart", "simulation.pause", "simulation.step", "story.open", "story.test" };

        /// <summary>The operation a request carries: "op" for story and recipe, "action" for mission_designer.</summary>
        public static string Operation(string domain, JObject request) => domain == "mission_designer" ? (string)request?["action"] ?? "" : (string)request?["op"] ?? "";
        public static bool Allowed(string domain, bool mutation, string operation)
        {
            string[] list = domain == "story" ? (mutation ? StoryMutations : StoryReads)
                : domain == "recipe" ? (mutation ? RecipeMutations : RecipeReads)
                : domain == "mission_designer" ? (mutation ? DesignerMutations : DesignerReads) : null;
            return list != null && Array.IndexOf(list, operation ?? "") >= 0;
        }

        public static object Invoke(bool mutation, JObject parameters)
        {
            string domain = (string)parameters?["domain"] ?? "";
            if (domain != "story" && domain != "recipe" && domain != "mission_designer") return new ErrorResponse("Unknown gameplay authoring domain. Use story, recipe or mission_designer.");
            var request = parameters?["request"] as JObject ?? new JObject();
            string operation = Operation(domain, request);
            if (!Allowed(domain, mutation, operation))
                return new ErrorResponse((mutation ? "manage" : "get") + "_gameplay_authoring does not allow " + domain + " operation '" + operation + "'." + (mutation ? " Reads use get_gameplay_authoring." : " Changes use manage_gameplay_authoring."));
            try {
                Type bridge = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                    if (assembly.GetName().Name != "Assembly-CSharp-Editor") continue;
                    bridge = assembly.GetType("ProjectStorm.MissionDesigner.MissionDesignerBridge", false); break;
                }
                if (bridge == null) return new ErrorResponse("Project Storm Mission Designer bridge is not installed or compiled. No changes were made.");
                var version = bridge.GetField("Version", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (version == null || (int)version.GetValue(null) != BridgeVersion) return new ErrorResponse("Unsupported Mission Designer bridge version; expected " + BridgeVersion + ".");
                var method = bridge.GetMethod("Dispatch", new[] { typeof(string), typeof(bool), typeof(string) });
                if (method == null || method.ReturnType != typeof(string)) return new ErrorResponse("Mission Designer bridge signature mismatch.");
                string response = (string)method.Invoke(null, new object[] { domain, mutation, request.ToString(Newtonsoft.Json.Formatting.None) });
                return JObject.Parse(response);
            } catch (Exception e) { return new ErrorResponse("Gameplay authoring request failed: " + e.GetBaseException().Message); }
        }
    }
}

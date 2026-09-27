using System;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Asks the optional Project Storm authoring assembly whether a command may run
    /// while an exact road stage exists. This check is deliberately independent of
    /// Python tool visibility and the normal Project Automation approval.
    /// </summary>
    internal static class RoadAuthoringDispatchFence
    {
        private const string BridgeAssembly = "ProjectStorm.RoadAuthoring.Editor";
        private const string BridgeType = "ProjectStorm.RoadAuthoring.Editor.RoadAuthoringBridge";

        internal sealed class Decision
        {
            public bool Allowed { get; private set; }
            public string Code { get; private set; }
            public string Message { get; private set; }
            public string ActiveOperationId { get; private set; }

            public static Decision Allow() => new Decision { Allowed = true };
            public static Decision Deny(string code, string message, string activeOperationId = null) =>
                new Decision { Allowed = false, Code = code, Message = message, ActiveOperationId = activeOperationId };
        }

        internal static Decision Check(string command, JObject parameters, ToolMetadata tool, bool registeredResource)
        {
            // A missing optional project assembly must not disable unrelated MCP tools.
            // Road authoring handlers separately report that their bridge is unavailable.
            Assembly assembly;
            try
            {
                assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == BridgeAssembly);
            }
            catch (Exception)
            {
                return Decision.Deny("road_stage_fence_unavailable", "Road authoring stage state could not be checked.");
            }
            if (assembly == null)
                return Decision.Allow();

            try
            {
                var request = BuildRequest(command, parameters, tool, registeredResource);
                return CheckBridge(assembly.GetType(BridgeType, false), request);
            }
            catch (Exception)
            {
                // A present but broken project bridge is never a bypass.
                return Decision.Deny("road_stage_fence_unavailable", "Road authoring stage state could not be checked.");
            }
        }

        internal static JObject BuildRequest(string command, JObject parameters, ToolMetadata tool, bool registeredResource)
        {
            // Inspection is derived from registered Unity metadata, never from
            // request arguments. Unknown commands fail closed during a stage.
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["command"] = command,
                ["action"] = (string)parameters?["action"],
                ["operationId"] = (string)(parameters?["operationId"] ?? parameters?["operation_id"]),
                ["candidateHash"] = (string)(parameters?["candidateHash"] ?? parameters?["candidate_hash"]),
                ["inspection"] = registeredResource || tool?.Capability == ToolCapability.Inspection,
                ["batchContainer"] = string.Equals(command, "batch_execute", StringComparison.Ordinal)
            };
        }

        // Separating reflection evaluation makes malformed-provider and stateful
        // stage decisions directly testable without requiring Project Storm code
        // inside the public package's standalone Unity test project.
        internal static Decision CheckBridge(Type bridge, JObject request)
        {
            try
            {
                var version = bridge?.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                var method = bridge?.GetMethod("CheckMcpDispatch", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (version == null || version.GetValue(null) is not int number || number != 1 ||
                    method == null || method.ReturnType != typeof(string))
                    return Decision.Deny("road_stage_fence_unavailable", "Road authoring stage fence version or signature is incompatible.");
                var raw = method.Invoke(null, new object[] { request.ToString(Formatting.None) }) as string;
                if (string.IsNullOrWhiteSpace(raw))
                    return Decision.Deny("road_stage_fence_unavailable", "Road authoring stage fence returned no decision.");
                var response = JObject.Parse(raw);
                if (response["allowed"]?.Type != JTokenType.Boolean)
                    return Decision.Deny("road_stage_fence_unavailable", "Road authoring stage fence returned an invalid decision.");
                if (response.Value<bool>("allowed"))
                    return Decision.Allow();
                return Decision.Deny(
                    response.Value<string>("code") ?? "road_stage_active",
                    response.Value<string>("message") ?? "An exact RoadBuilder stage is active. Commit or cancel that goal before another mutation.",
                    response.Value<string>("activeOperationId"));
            }
            catch (Exception)
            {
                return Decision.Deny("road_stage_fence_unavailable", "Road authoring stage state could not be checked.");
            }
        }
    }
}

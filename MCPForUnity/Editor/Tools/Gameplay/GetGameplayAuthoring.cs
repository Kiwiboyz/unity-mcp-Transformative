using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Tools.Gameplay
{
    [McpForUnityTool("get_gameplay_authoring", AutoRegister = false, Group = "gameplay_authoring", Capability = ToolCapability.Inspection)]
    public static class GetGameplayAuthoring
    {
        public static object HandleCommand(JObject parameters) => GameplayAuthoringToolBridge.Invoke(false, parameters);
    }
}

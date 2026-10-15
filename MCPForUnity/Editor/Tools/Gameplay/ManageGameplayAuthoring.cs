using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Tools.Gameplay
{
    [McpForUnityTool("manage_gameplay_authoring", AutoRegister = false, Group = "gameplay_authoring", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageGameplayAuthoring
    {
        public static object HandleCommand(JObject parameters) => GameplayAuthoringToolBridge.Invoke(true, parameters);
    }
}

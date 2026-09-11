using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
namespace MCPForUnity.Editor.Tools.Storm
{
    [McpForUnityTool("manage_tornado", AutoRegister = false, Group = "vfx", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageTornado
    {
        public static object HandleCommand(JObject parameters) => StormDesignerToolBridge.Invoke("tornado", true, parameters);
    }
}

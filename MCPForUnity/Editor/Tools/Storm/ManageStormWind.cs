using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
namespace MCPForUnity.Editor.Tools.Storm
{
    [McpForUnityTool("manage_storm_wind", AutoRegister = false, Group = "vfx", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageStormWind
    {
        public static object HandleCommand(JObject parameters) => StormDesignerToolBridge.Invoke("storm_wind", true, parameters);
    }
}

using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
namespace MCPForUnity.Editor.Tools.Storm
{
    [McpForUnityTool("get_storm_wind_catalog", AutoRegister = false, Group = "vfx", Capability = ToolCapability.Inspection)]
    public static class GetStormWindCatalog
    {
        public static object HandleCommand(JObject parameters) => StormDesignerToolBridge.Invoke("storm_wind", false, parameters);
    }
}

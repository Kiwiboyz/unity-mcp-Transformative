using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools.RoadAuthoring
{
    [McpForUnityTool("get_road_authoring", AutoRegister = false, Group = "road_authoring", Capability = ToolCapability.Inspection)]
    public static class GetRoadAuthoring
    {
        public static object HandleCommand(JObject parameters) => RoadAuthoringToolBridge.Invoke(false, parameters);
    }
}

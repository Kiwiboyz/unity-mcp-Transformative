using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools.RoadAuthoring
{
    [McpForUnityTool("manage_road_authoring", AutoRegister = false, Group = "road_authoring", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageRoadAuthoring
    {
        public static object HandleCommand(JObject parameters) => RoadAuthoringToolBridge.Invoke(true, parameters);
    }
}

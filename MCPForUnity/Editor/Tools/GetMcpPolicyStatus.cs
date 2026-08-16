using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>Returns non-sensitive MCP authorization state for an agent.</summary>
    [McpForUnityTool("get_mcp_policy_status", Capability = ToolCapability.Inspection)]
    public static class GetMcpPolicyStatus
    {
        public static object HandleCommand(JObject @params)
        {
            return new SuccessResponse("MCP policy status retrieved.", McpAuthorizationService.GetSafeStatus());
        }
    }
}

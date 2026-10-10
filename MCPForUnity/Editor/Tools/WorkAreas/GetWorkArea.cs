using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>Reads the work areas drawn in the Scene view with the MCP Work Area tool (read-only).</summary>
    [McpForUnityTool("get_work_area", AutoRegister = false, Group = "core", Capability = ToolCapability.Inspection)]
    public static class GetWorkArea
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            string action = (p.Get("action") ?? "list").Trim().ToLowerInvariant();
            if (action != "list" && action != "get")
                return new ErrorResponse("Unknown action '" + action + "'. Use 'list' or 'get'.");
            float? waterLevel = p.GetFloat("water_level");
            if (waterLevel.HasValue && (float.IsNaN(waterLevel.Value) || float.IsInfinity(waterLevel.Value)))
                return new ErrorResponse("water_level must be a finite number.");

            WorkAreaStore.Refresh();
            var store = WorkAreaStore.Instance;
            string key = p.Get("area");
            var areas = store.Areas.ToList();
            if (action == "get")
            {
                var area = store.Find(string.IsNullOrWhiteSpace(key) ? "active" : key);
                if (area == null)
                    return new ErrorResponse(store.Areas.Count == 0
                        ? "No work areas. Ask Jordan to draw one (Window > Transformative MCP for Project Storm > Work Area Tool)."
                        : "No work area matches '" + (key ?? "active") + "'. Known: " +
                          string.Join(", ", store.Areas.Select(item => item.name + " [" + item.id + "]")) + ".");
                areas = new System.Collections.Generic.List<WorkArea> { area };
            }

            var reports = new JArray(areas.Select(area => WorkAreaReport.Build(area, area.id == store.ActiveId, waterLevel)));
            string message = store.Areas.Count == 0
                ? "No work areas have been drawn."
                : areas.Count == 1 && action == "get"
                    ? (string)reports[0]["summary"]
                    : store.Areas.Count + " work area(s)" + (store.Active != null ? ", active: " + store.Active.name : "") + ".";
            return new SuccessResponse(message, new JObject
            {
                ["units"] = WorkAreaReport.Units,
                ["store"] = WorkAreaStore.FilePath,
                ["active_id"] = store.ActiveId,
                ["count"] = store.Areas.Count,
                ["areas"] = reports
            });
        }
    }
}

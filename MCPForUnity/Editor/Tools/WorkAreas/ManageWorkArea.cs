using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>
    /// Lets an agent propose or adjust a work area for Jordan to refine by hand. Only the per-user UserSettings file
    /// changes (never a scene or asset), and every change is a normal Unity Undo step.
    /// </summary>
    [McpForUnityTool("manage_work_area", AutoRegister = false, Group = "core", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageWorkArea
    {
        private const float CoordinateLimit = 1000000f;

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            string action = (p.Get("action") ?? "").Trim().ToLowerInvariant();
            WorkAreaStore.Refresh();
            var store = WorkAreaStore.Instance;
            object result;
            switch (action)
            {
                case "create": result = Create(store, p); break;
                case "update": result = Update(store, p); break;
                case "delete": result = Delete(store, p); break;
                case "set_active": result = SetActive(store, p); break;
                case "clear": result = Clear(store); break;
                default:
                    return new ErrorResponse("Unknown action '" + action + "'. Use create, update, delete, set_active or clear.");
            }
            SceneView.RepaintAll();
            return result;
        }

        private static object Create(WorkAreaStore store, ToolParams p)
        {
            if (store.Areas.Count >= WorkAreaStore.MaxAreas)
                return new ErrorResponse("At most " + WorkAreaStore.MaxAreas + " work areas. Delete one first.");
            var area = new WorkArea { source = "mcp", name = p.Get("name") };
            string kind = (p.Get("kind") ?? (p.Has("vertices") ? WorkArea.Polygon : WorkArea.Rect)).Trim().ToLowerInvariant();
            string error = kind == WorkArea.Rect ? ShapeRect(area, p, true) : kind == WorkArea.Polygon ? ShapePolygon(area, p, true)
                : "kind must be 'rect' or 'polygon'.";
            error = error ?? ApplyHeight(area, p);
            if (error != null) return new ErrorResponse(error);
            WorkAreaGround.UpdateGround(area);
            store.Record("MCP Create Work Area");
            store.Add(area, p.GetBool("set_active", true));
            store.Touch(area);
            return new SuccessResponse("Created work area '" + area.name + "'.",
                new JObject { ["area"] = WorkAreaReport.Build(area, area.id == store.ActiveId) });
        }

        private static object Update(WorkAreaStore store, ToolParams p)
        {
            var area = store.Find(p.Get("area"));
            if (area == null) return NotFound(store, p.Get("area"));
            var edited = JsonUtility.FromJson<WorkArea>(JsonUtility.ToJson(area));
            string error = null;
            if (p.Has("vertices")) error = ShapePolygon(edited, p, true);
            else if (p.Has("center") || p.Has("size") || p.Has("yaw") || (p.Get("kind") ?? "") == WorkArea.Rect)
            {
                if (!edited.IsRect && (!p.Has("center") || !p.Has("size")))
                    error = "Turning a polygon into a rect needs both center and size.";
                else error = ShapeRect(edited, p, false);
            }
            else if ((p.Get("kind") ?? "") == WorkArea.Polygon) edited.kind = WorkArea.Polygon;
            error = error ?? ApplyHeight(edited, p);
            if (error != null) return new ErrorResponse(error);
            store.Record("MCP Update Work Area");
            if (p.Has("name")) area.name = store.UniqueName(p.Get("name"), area);
            area.kind = edited.kind;
            area.vertices = edited.vertices;
            area.rectCentre = edited.rectCentre;
            area.rectSize = edited.rectSize;
            area.rectYaw = edited.rectYaw;
            area.manualHeight = edited.manualHeight;
            area.heightMin = edited.heightMin;
            area.heightMax = edited.heightMax;
            area.heightMargin = edited.heightMargin;
            WorkAreaGround.UpdateGround(area);
            if (p.GetBool("set_active")) store.SetActive(area);
            store.Touch(area);
            return new SuccessResponse("Updated work area '" + area.name + "'.",
                new JObject { ["area"] = WorkAreaReport.Build(area, area.id == store.ActiveId) });
        }

        private static object Delete(WorkAreaStore store, ToolParams p)
        {
            var area = store.Find(p.Get("area"));
            if (area == null) return NotFound(store, p.Get("area"));
            store.Record("MCP Delete Work Area");
            store.Remove(area);
            store.Touch();
            return new SuccessResponse("Deleted work area '" + area.name + "'.",
                new JObject { ["deleted_id"] = area.id, ["count"] = store.Areas.Count, ["active_id"] = store.ActiveId });
        }

        private static object SetActive(WorkAreaStore store, ToolParams p)
        {
            var area = store.Find(p.Get("area"));
            if (area == null) return NotFound(store, p.Get("area"));
            store.Record("MCP Set Active Work Area");
            store.SetActive(area);
            store.Touch();
            return new SuccessResponse("Active work area is now '" + area.name + "'.", new JObject { ["active_id"] = area.id });
        }

        private static object Clear(WorkAreaStore store)
        {
            int count = store.Areas.Count;
            store.Record("MCP Clear Work Areas");
            store.RemoveAll();
            store.Touch();
            return new SuccessResponse("Removed " + count + " work area(s).", new JObject { ["deleted"] = count });
        }

        private static object NotFound(WorkAreaStore store, string key) =>
            new ErrorResponse(string.IsNullOrWhiteSpace(key)
                ? "area is required (id, name or 'active')."
                : "No work area matches '" + key + "'. Known: " +
                  (store.Areas.Count == 0 ? "none" : string.Join(", ", store.Areas.Select(item => item.name + " [" + item.id + "]"))) + ".");

        // ------------------------------------------------------------------ parsing

        private static string ShapePolygon(WorkArea area, ToolParams p, bool required)
        {
            if (!(p.GetRaw("vertices") is JArray raw)) return required ? "vertices must be a list of [x, z] or [x, y, z] points." : null;
            if (raw.Count < 3 || raw.Count > WorkAreaStore.MaxVertices)
                return "A polygon needs 3 to " + WorkAreaStore.MaxVertices + " vertices.";
            var points = new List<Vector3>(raw.Count);
            foreach (var token in raw)
            {
                if (!TryPoint(token, out var point, out bool hasY)) return "Each vertex must be [x, z], [x, y, z] or {x, y?, z} with finite numbers.";
                points.Add(hasY ? point : WorkAreaGround.Snap(point));
            }
            if (!WorkAreaGeometry.IsSimple(points)) return "The polygon's sides cross or repeat a corner. List the corners in order around the outline.";
            if (WorkAreaGeometry.Area(points) < 0.01f) return "The polygon has no area.";
            area.kind = WorkArea.Polygon;
            area.vertices = points;
            return null;
        }

        private static string ShapeRect(WorkArea area, ToolParams p, bool creating)
        {
            var centre = area.rectCentre;
            var size = area.rectSize;
            float yaw = p.GetFloat("yaw") ?? area.rectYaw;
            if (p.Has("center"))
            {
                if (!TryPoint(p.GetRaw("center"), out centre, out _)) return "center must be [x, z] or [x, y, z].";
            }
            else if (creating) return "A rect needs center ([x, z]) and size ([width, length] in metres).";
            if (p.Has("size"))
            {
                if (!(p.GetRaw("size") is JArray raw) || raw.Count != 2 || !Finite(raw[0], out float width) || !Finite(raw[1], out float length))
                    return "size must be [width, length] in metres.";
                size = new Vector2(width, length);
            }
            else if (creating) return "A rect needs center ([x, z]) and size ([width, length] in metres).";
            if (size.x < 0.1f || size.y < 0.1f || size.x > 100000f || size.y > 100000f) return "Each side of a rect must be 0.1 to 100000 m.";
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return "yaw must be a finite number of degrees.";
            WorkAreaGround.ApplyRect(area, centre, size, yaw);
            return null;
        }

        private static string ApplyHeight(WorkArea area, ToolParams p)
        {
            var range = p.GetRaw("y_range");
            if (range != null && range.Type != JTokenType.Null)
            {
                if (range.Type == JTokenType.String && string.Equals((string)range, "auto", System.StringComparison.OrdinalIgnoreCase))
                    area.manualHeight = false;
                else if (range is JArray pair && pair.Count == 2 && Finite(pair[0], out float min) && Finite(pair[1], out float max) && max > min)
                {
                    area.manualHeight = true;
                    area.heightMin = min;
                    area.heightMax = max;
                }
                else return "y_range must be [min_y, max_y] with max above min, or \"auto\".";
            }
            if (p.Has("height_margin"))
            {
                float margin = p.GetFloat("height_margin") ?? -1f;
                if (margin < 0f || margin > 1000f) return "height_margin must be 0 to 1000 m.";
                area.heightMargin = margin;
            }
            return null;
        }

        private static bool TryPoint(JToken token, out Vector3 point, out bool hasY)
        {
            point = default;
            hasY = false;
            float x, y = 0f, z;
            if (token is JArray array)
            {
                if (array.Count == 2 && Finite(array[0], out x) && Finite(array[1], out z)) { point = new Vector3(x, 0f, z); return true; }
                if (array.Count == 3 && Finite(array[0], out x) && Finite(array[1], out y) && Finite(array[2], out z))
                {
                    point = new Vector3(x, y, z);
                    hasY = true;
                    return true;
                }
                return false;
            }
            if (token is JObject obj && Finite(obj["x"], out x) && Finite(obj["z"], out z))
            {
                hasY = obj["y"] != null && obj["y"].Type != JTokenType.Null;
                if (hasY && !Finite(obj["y"], out y)) return false;
                point = new Vector3(x, y, z);
                return true;
            }
            return false;
        }

        private static bool Finite(JToken token, out float value)
        {
            value = 0f;
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)) return false;
            double number = token.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number) || System.Math.Abs(number) > CoordinateLimit) return false;
            value = (float)number;
            return true;
        }
    }
}

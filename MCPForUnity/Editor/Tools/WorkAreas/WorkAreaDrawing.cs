using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>
    /// Scene-view drawing of work areas: translucent fill, outline, faint volume and a name label. Drawn by the tool while
    /// it is active; with "Keep showing areas" on, also on Scene-view repaints while it isn't (subscribed only then).
    /// </summary>
    [InitializeOnLoad]
    public static class WorkAreaDrawing
    {
        private const string KeepVisibleKey = "MCPForUnity.WorkAreas.KeepVisible";
        private static readonly Color ActiveColour = new Color(1f, 0.85f, 0.1f);
        private static readonly Color OtherColour = new Color(0.25f, 0.85f, 1f);
        private static readonly Dictionary<string, KeyValuePair<int, List<int>>> Fills = new Dictionary<string, KeyValuePair<int, List<int>>>();
        private static GUIStyle labelStyle;
        private static bool keepVisible;

        static WorkAreaDrawing()
        {
            // One settings read per domain reload; nothing is hooked unless the toggle is on.
            keepVisible = EditorUserSettings.GetConfigValue(KeepVisibleKey) == "1";
            if (keepVisible) SceneView.duringSceneGui += DrawWhileToolIsOff;
        }

        public static bool KeepVisible
        {
            get => keepVisible;
            set
            {
                if (value == keepVisible) return;
                keepVisible = value;
                EditorUserSettings.SetConfigValue(KeepVisibleKey, value ? "1" : "0");
                SceneView.duringSceneGui -= DrawWhileToolIsOff;
                if (value) SceneView.duringSceneGui += DrawWhileToolIsOff;
                SceneView.RepaintAll();
            }
        }

        private static void DrawWhileToolIsOff(SceneView view)
        {
            if (Event.current.type != EventType.Repaint || WorkAreaTool.IsActive) return;
            var store = WorkAreaStore.Instance;
            if (store.Areas.Count > 0) DrawAll(store, null);
        }

        public static void DrawAll(WorkAreaStore store, string selectedId)
        {
            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;
            foreach (var area in store.Areas)
            {
                if (area.vertices.Count < 3) continue;
                bool active = area.id == store.ActiveId;
                bool selected = area.id == selectedId;
                var colour = active ? ActiveColour : OtherColour;
                var outline = area.vertices;
                var closed = outline.Concat(new[] { outline[0] }).ToArray();

                Handles.color = new Color(colour.r, colour.g, colour.b, selected ? 0.2f : 0.12f);
                var triangles = Fill(area);
                for (int i = 0; i + 2 < triangles.Count; i += 3)
                    Handles.DrawAAConvexPolygon(outline[triangles[i]], outline[triangles[i + 1]], outline[triangles[i + 2]]);

                Handles.color = colour;
                Handles.DrawAAPolyLine(selected ? 5f : 3f, closed);

                // The volume agents work inside: posts at the corners and a faint top outline.
                float bottom = area.VolumeMin, top = area.VolumeMax;
                Handles.color = new Color(colour.r, colour.g, colour.b, 0.3f);
                foreach (var corner in outline)
                    Handles.DrawLine(new Vector3(corner.x, bottom, corner.z), new Vector3(corner.x, top, corner.z));
                Handles.DrawAAPolyLine(1.5f, closed.Select(point => new Vector3(point.x, top, point.z)).ToArray());

                var centre = WorkAreaGeometry.Centroid(outline);
                centre.y = outline.Max(point => point.y) + 1f;
                Handles.Label(centre, string.Format("{0}{1}\n{2:N0} m²", area.name, active ? "  (active)" : "",
                    WorkAreaGeometry.Area(outline)), LabelStyle(colour));
            }
            Handles.zTest = previousZTest;
        }

        public static void DrawHandles(WorkArea area, List<(Vector3 position, int grip)> grips, int hovered)
        {
            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;
            for (int i = 0; i < grips.Count; i++)
            {
                var (position, grip) = grips[i];
                float size = HandleUtility.GetHandleSize(position);
                bool hot = i == hovered;
                switch (grip)
                {
                    case 0: // corner
                        Handles.color = hot ? Color.white : ActiveColour;
                        Handles.DotHandleCap(0, position, Quaternion.identity, size * (hot ? 0.08f : 0.06f), EventType.Repaint);
                        break;
                    case 1: // mid-side
                        Handles.color = hot ? Color.white : new Color(1f, 0.92f, 0.4f, 0.6f);
                        Handles.DotHandleCap(0, position, Quaternion.identity, size * (hot ? 0.06f : 0.04f), EventType.Repaint);
                        break;
                    case 2: // move
                        Handles.color = hot ? Color.white : new Color(1f, 1f, 1f, 0.75f);
                        Handles.RectangleHandleCap(0, position, Quaternion.LookRotation(Vector3.up), size * 0.12f, EventType.Repaint);
                        break;
                    case 3: // turn
                        Handles.color = hot ? Color.white : new Color(1f, 0.6f, 0.2f, 0.9f);
                        Handles.CircleHandleCap(0, position, Quaternion.LookRotation(Vector3.up), size * 0.08f, EventType.Repaint);
                        Handles.DrawLine(position, area.rectCentre);
                        break;
                }
            }
            Handles.zTest = previousZTest;
        }

        public static void Preview(List<Vector3> points, bool closed, string label)
        {
            if (points.Count == 0) return;
            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;
            Handles.color = ActiveColour;
            var line = closed && points.Count > 2 ? points.Concat(new[] { points[0] }).ToArray() : points.ToArray();
            if (line.Length > 1) Handles.DrawAAPolyLine(4f, line);
            foreach (var point in points)
                Handles.DotHandleCap(0, point, Quaternion.identity, HandleUtility.GetHandleSize(point) * 0.04f, EventType.Repaint);
            if (!string.IsNullOrEmpty(label) && points.Count > 1)
            {
                var centre = WorkAreaGeometry.Centroid(points);
                centre.y = points.Max(point => point.y) + 1f;
                Handles.Label(centre, label, LabelStyle(ActiveColour));
            }
            Handles.zTest = previousZTest;
        }

        public static void CloseMarker(Vector3 first, bool near)
        {
            Handles.color = near ? Color.white : ActiveColour;
            Handles.DrawWireDisc(first, Vector3.up, HandleUtility.GetHandleSize(first) * (near ? 0.12f : 0.09f));
        }

        public static void CursorHint(Vector2 mouse, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Handles.BeginGUI();
            var style = LabelStyle(Color.white);
            var size = style.CalcSize(new GUIContent(text));
            GUI.Label(new Rect(mouse.x + 16f, mouse.y + 12f, size.x, size.y), text, style);
            Handles.EndGUI();
        }

        // Triangulated once per outline, not per repaint.
        private static List<int> Fill(WorkArea area)
        {
            int hash = area.vertices.Count;
            foreach (var point in area.vertices) hash = hash * 31 + point.GetHashCode();
            if (Fills.TryGetValue(area.id, out var cached) && cached.Key == hash) return cached.Value;
            var triangles = WorkAreaGeometry.Triangulate(area.vertices);
            Fills[area.id] = new KeyValuePair<int, List<int>>(hash, triangles);
            return triangles;
        }

        private static GUIStyle LabelStyle(Color colour)
        {
            if (labelStyle == null)
                labelStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 11, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(6, 6, 3, 3), richText = false
                };
            labelStyle.normal.textColor = colour;
            return labelStyle;
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>
    /// Scene-view tool for drawing work areas: footprints AI agents read back over MCP (get_work_area) so they know
    /// exactly where to work. Rectangle: drag ([ and ] turn it 15°). Polygon: click the corners, then click the first
    /// corner or press Enter; Backspace removes the last corner; Esc cancels. Editor-only and idle unless selected:
    /// nothing runs or draws while another tool is active (unless "Keep showing areas" is on).
    /// </summary>
    [EditorTool("MCP Work Area")]
    public sealed class WorkAreaTool : EditorTool
    {
        public enum Shape { Rectangle, Polygon }

        private enum State { Idle, DrawingRect, DrawingPolygon, Dragging }

        private enum Grip { Corner, Middle, Centre, Turn }

        private struct Handle
        {
            public Grip grip;
            public int index;
            public Vector3 position;
        }

        private const string ShapePrefsKey = "MCPForUnity.WorkAreas.Shape";
        private const float PickPixels = 12f;
        private static readonly int ControlHint = "MCPWorkAreaTool".GetHashCode();

        internal static string SelectedId;
        internal static string Message;
        private static GUIContent icon;

        private State state;
        private Vector3 mousePoint;
        private bool mouseOnGround;
        private Vector3 rectStart, rectEnd;
        private float drawYaw;
        private readonly List<Vector3> polygon = new List<Vector3>();
        // Drag of a handle on the selected area: what was grabbed, and the area as it was when grabbed.
        private Handle grabbed;
        private int hovered = -1;
        private Vector3 grabPoint;
        private List<Vector3> grabVertices;
        private Vector3 grabCentre;
        private Vector2 grabSize;
        private float grabYaw;

        public static bool IsActive => ToolManager.activeToolType == typeof(WorkAreaTool);

        internal static Shape CurrentShape
        {
            get => (Shape)EditorPrefs.GetInt(ShapePrefsKey, 0);
            set => EditorPrefs.SetInt(ShapePrefsKey, (int)value);
        }

        internal static WorkAreaTool Current { get; private set; }

        public override GUIContent toolbarIcon =>
            icon ?? (icon = new GUIContent(EditorGUIUtility.IconContent("RectTool").image,
                "MCP Work Area: draw a footprint (rectangle or polygon) that AI agents read with get_work_area."));

        /// <summary>Menu entry: turns the tool on (focusing a Scene view) or back off.</summary>
        public static void Toggle()
        {
            if (IsActive)
            {
                UnityEditor.Tools.current = Tool.Move;
                return;
            }
            var view = SceneView.lastActiveSceneView;
            if (view == null) view = EditorWindow.GetWindow<SceneView>();
            view.Focus();
            ToolManager.SetActiveTool<WorkAreaTool>();
        }

        public override void OnActivated()
        {
            Current = this;
            WorkAreaStore.Refresh();
            Message = null;
            SceneView.RepaintAll();
        }

        public override void OnWillBeDeactivated()
        {
            CancelDrawing();
            WorkAreaStore.Instance.Flush();
            if (Current == this) Current = null;
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ Scene view

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView)) return;
            var e = Event.current;
#if !UNITY_2022_1_OR_NEWER
            // No transient overlays before 2022.1: draw the panel in a corner of the Scene view.
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(8f, 8f, 290f, window.position.height - 40f), GUI.skin.box);
            WorkAreaPanel.Draw();
            GUILayout.EndArea();
            Handles.EndGUI();
#endif
            var store = WorkAreaStore.Instance;
            int control = GUIUtility.GetControlID(ControlHint, FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
            if (state != State.Dragging) store.Flush();

            if (e.isMouse || e.type == EventType.Repaint)
                mouseOnGround = WorkAreaGround.TryPick(HandleUtility.GUIPointToWorldRay(e.mousePosition), out mousePoint);

            var selected = store.Areas.FirstOrDefault(area => area.id == SelectedId);
            if (selected == null && state == State.Dragging) state = State.Idle;
            var handles = selected != null && (state == State.Idle || state == State.Dragging) ? GripsFor(selected) : null;

            if (e.type == EventType.Repaint)
            {
                WorkAreaDrawing.DrawAll(store, SelectedId);
                if (handles != null) WorkAreaDrawing.DrawHandles(selected, handles.Select(h => (h.position, (int)h.grip)).ToList(),
                    state == State.Dragging ? -1 : hovered);
                DrawPreview();
                WorkAreaDrawing.CursorHint(e.mousePosition, CursorText());
                return;
            }
            if (e.alt) return;

            switch (e.GetTypeForControl(control))
            {
                case EventType.MouseMove:
                {
                    int now = handles != null ? Nearest(handles, e.mousePosition) : -1;
                    if (now != hovered || state == State.DrawingPolygon) SceneView.RepaintAll();
                    hovered = now;
                    break;
                }
                case EventType.MouseDown when e.button == 0:
                    OnMouseDown(e, control, store, selected, handles);
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == control:
                    if (state == State.DrawingRect && mouseOnGround) rectEnd = mousePoint;
                    else if (state == State.Dragging && mouseOnGround && selected != null) DragTo(store, selected, e);
                    SceneView.RepaintAll();
                    e.Use();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == control:
                    GUIUtility.hotControl = 0;
                    if (state == State.DrawingRect) FinishRect(store);
                    else if (state == State.Dragging) FinishDrag(store, selected);
                    e.Use();
                    break;
                case EventType.KeyDown:
                    OnKeyDown(e, store, selected);
                    break;
            }
        }

        private void OnMouseDown(Event e, int control, WorkAreaStore store, WorkArea selected, List<Handle> handles)
        {
            if (state == State.DrawingPolygon)
            {
                if (polygon.Count >= 3 && ScreenDistance(polygon[0], e.mousePosition) < PickPixels) FinishPolygon(store);
                else if (mouseOnGround && polygon.Count < WorkAreaStore.MaxVertices) polygon.Add(mousePoint);
                e.Use();
                SceneView.RepaintAll();
                return;
            }
            if (state != State.Idle) return;

            int index = handles != null ? Nearest(handles, e.mousePosition) : -1;
            if (index >= 0)
            {
                var handle = handles[index];
                if (handle.grip == Grip.Corner && e.control)
                {
                    if (!selected.IsRect && selected.vertices.Count > 3)
                    {
                        var fewer = new List<Vector3>(selected.vertices);
                        fewer.RemoveAt(handle.index);
                        if (WorkAreaGeometry.IsSimple(fewer))
                        {
                            store.Record("Remove Work Area Corner");
                            selected.vertices = fewer;
                            WorkAreaGround.UpdateGround(selected);
                            store.Touch(selected);
                        }
                    }
                    e.Use();
                    return;
                }
                if (handle.grip == Grip.Middle && !selected.IsRect)
                {
                    // A mid-side dot on a polygon adds a corner there, then drags it.
                    store.Record("Add Work Area Corner");
                    selected.vertices.Insert(handle.index + 1, handle.position);
                    store.Touch(selected);
                    handle = new Handle { grip = Grip.Corner, index = handle.index + 1, position = handle.position };
                }
                grabbed = handle;
                grabPoint = mouseOnGround ? mousePoint : handle.position;
                grabVertices = new List<Vector3>(selected.vertices);
                grabCentre = selected.rectCentre;
                grabSize = selected.rectSize;
                grabYaw = selected.rectYaw;
                state = State.Dragging;
                GUIUtility.hotControl = control;
                e.Use();
                return;
            }

            if (!mouseOnGround) return;
            var clicked = store.Areas.Where(area => area.id != SelectedId && WorkAreaGeometry.Contains(area.vertices, mousePoint))
                .OrderBy(area => WorkAreaGeometry.Area(area.vertices)).FirstOrDefault();
            if (clicked != null)
            {
                Select(clicked);
                e.Use();
                return;
            }
            Message = null;
            if (CurrentShape == Shape.Rectangle)
            {
                state = State.DrawingRect;
                rectStart = rectEnd = mousePoint;
                GUIUtility.hotControl = control;
            }
            else
            {
                state = State.DrawingPolygon;
                polygon.Clear();
                polygon.Add(mousePoint);
            }
            e.Use();
            SceneView.RepaintAll();
        }

        private void OnKeyDown(Event e, WorkAreaStore store, WorkArea selected)
        {
            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    if (state == State.DrawingPolygon || state == State.DrawingRect) CancelDrawing();
                    else if (state == State.Idle && SelectedId != null) SelectedId = null;
                    else return;
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (state != State.DrawingPolygon) return;
                    FinishPolygon(store);
                    break;
                case KeyCode.Backspace:
                    if (state != State.DrawingPolygon) return;
                    if (polygon.Count > 0) polygon.RemoveAt(polygon.Count - 1);
                    if (polygon.Count == 0) state = State.Idle;
                    break;
                case KeyCode.LeftBracket:
                case KeyCode.RightBracket:
                {
                    float step = e.keyCode == KeyCode.LeftBracket ? -15f : 15f;
                    if (state == State.DrawingRect) drawYaw += step;
                    else if (state == State.Idle && selected != null && selected.IsRect)
                    {
                        store.Record("Turn Work Area");
                        WorkAreaGround.ApplyRect(selected, selected.rectCentre, selected.rectSize, selected.rectYaw + step);
                        WorkAreaGround.UpdateGround(selected);
                        store.Touch(selected);
                    }
                    else return;
                    break;
                }
                default:
                    return;
            }
            WorkAreaPanel.Repaint();
            SceneView.RepaintAll();
            e.Use();
        }

        internal void CancelDrawing()
        {
            if (state != State.Idle && GUIUtility.hotControl != 0) GUIUtility.hotControl = 0;
            state = State.Idle;
            polygon.Clear();
        }

        internal static void Select(WorkArea area)
        {
            SelectedId = area?.id;
            Message = null;
            WorkAreaPanel.Repaint();
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ drawing new areas

        private List<Vector3> DrawnRect(out Vector3 centre, out Vector2 size)
        {
            var rotation = Quaternion.Euler(0f, drawYaw, 0f);
            var right = rotation * Vector3.right;
            var forward = rotation * Vector3.forward;
            var delta = rectEnd - rectStart;
            float x = Vector3.Dot(delta, right), z = Vector3.Dot(delta, forward);
            centre = rectStart + right * (x * 0.5f) + forward * (z * 0.5f);
            size = new Vector2(Mathf.Abs(x), Mathf.Abs(z));
            return WorkAreaGeometry.RectangleCorners(centre, size, drawYaw).ToList();
        }

        private void FinishRect(WorkAreaStore store)
        {
            DrawnRect(out var centre, out var size);
            state = State.Idle;
            if (size.x < 0.5f || size.y < 0.5f) return; // a click, not a drag
            var area = new WorkArea { source = "drawn" };
            WorkAreaGround.ApplyRect(area, centre, size, drawYaw);
            Create(store, area);
        }

        private void FinishPolygon(WorkAreaStore store)
        {
            var outline = new List<Vector3>(polygon);
            if (outline.Count < 3)
            {
                Message = "A polygon needs at least 3 corners.";
                return;
            }
            if (!WorkAreaGeometry.IsSimple(outline))
            {
                Message = "The sides cross. Backspace removes the last corner.";
                return;
            }
            state = State.Idle;
            polygon.Clear();
            Create(store, new WorkArea { source = "drawn", kind = WorkArea.Polygon, vertices = outline });
        }

        private static void Create(WorkAreaStore store, WorkArea area)
        {
            if (store.Areas.Count >= WorkAreaStore.MaxAreas)
            {
                Message = "At most " + WorkAreaStore.MaxAreas + " areas: delete one first.";
                return;
            }
            WorkAreaGround.UpdateGround(area);
            store.Record("Draw Work Area");
            store.Add(area, true);
            store.Touch(area);
            Message = null;
            Select(area);
        }

        private void DrawPreview()
        {
            if (state == State.DrawingRect)
            {
                var corners = DrawnRect(out _, out var size);
                WorkAreaDrawing.Preview(corners, true, string.Format("{0:0.0} × {1:0.0} m", size.x, size.y));
            }
            else if (state == State.DrawingPolygon)
            {
                var points = new List<Vector3>(polygon);
                if (mouseOnGround) points.Add(mousePoint);
                WorkAreaDrawing.Preview(points, false,
                    points.Count >= 3 ? string.Format("{0:0} m²", WorkAreaGeometry.Area(points)) : null);
                if (polygon.Count >= 3)
                    WorkAreaDrawing.CloseMarker(polygon[0], ScreenDistance(polygon[0], Event.current.mousePosition) < PickPixels);
            }
        }

        // ------------------------------------------------------------------ editing the selected area

        private static List<Handle> GripsFor(WorkArea area)
        {
            var list = new List<Handle>();
            var points = area.vertices;
            for (int i = 0; i < points.Count; i++)
            {
                list.Add(new Handle { grip = Grip.Corner, index = i, position = points[i] });
                list.Add(new Handle { grip = Grip.Middle, index = i, position = (points[i] + points[(i + 1) % points.Count]) * 0.5f });
            }
            var centre = WorkAreaGeometry.Centroid(points);
            list.Add(new Handle { grip = Grip.Centre, position = centre });
            if (area.IsRect)
            {
                var forward = Quaternion.Euler(0f, area.rectYaw, 0f) * Vector3.forward;
                var edge = area.rectCentre + forward * (area.rectSize.y * 0.5f);
                list.Add(new Handle { grip = Grip.Turn, position = edge + forward * (HandleUtility.GetHandleSize(edge) * 0.6f) });
            }
            return list;
        }

        private static int Nearest(List<Handle> handles, Vector2 mouse)
        {
            int best = -1;
            float bestDistance = PickPixels;
            for (int i = 0; i < handles.Count; i++)
            {
                float distance = ScreenDistance(handles[i].position, mouse);
                // Corners win over the mid-side and centre dots when they overlap on a small area.
                if (handles[i].grip != Grip.Corner) distance += 2f;
                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }
            return best;
        }

        private void DragTo(WorkAreaStore store, WorkArea area, Event e)
        {
            var delta = WorkAreaGeometry.Flat(mousePoint - grabPoint);
            switch (grabbed.grip)
            {
                case Grip.Centre:
                    store.Record("Move Work Area");
                    if (area.IsRect) WorkAreaGround.ApplyRect(area, grabCentre + delta, grabSize, grabYaw);
                    else area.vertices = grabVertices.Select(point => WorkAreaGround.Snap(point + delta)).ToList();
                    break;
                case Grip.Turn:
                {
                    var direction = WorkAreaGeometry.Flat(mousePoint - grabCentre);
                    if (direction.sqrMagnitude < 0.01f) return;
                    float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                    if (e.control) yaw = Mathf.Round(yaw / 15f) * 15f;
                    store.Record("Turn Work Area");
                    WorkAreaGround.ApplyRect(area, grabCentre, grabSize, yaw);
                    break;
                }
                case Grip.Corner when area.IsRect:
                {
                    // The opposite corner stays put.
                    var rotation = Quaternion.Euler(0f, grabYaw, 0f);
                    var right = rotation * Vector3.right;
                    var forward = rotation * Vector3.forward;
                    var opposite = grabVertices[(grabbed.index + 2) % 4];
                    var span = WorkAreaGeometry.Flat(mousePoint - opposite);
                    float x = Vector3.Dot(span, right), z = Vector3.Dot(span, forward);
                    if (Mathf.Abs(x) < 0.1f || Mathf.Abs(z) < 0.1f) return;
                    store.Record("Resize Work Area");
                    WorkAreaGround.ApplyRect(area, opposite + right * (x * 0.5f) + forward * (z * 0.5f), new Vector2(Mathf.Abs(x), Mathf.Abs(z)), grabYaw);
                    break;
                }
                case Grip.Middle when area.IsRect:
                {
                    // Corners run -x-z, +x-z, +x+z, -x+z: side 0 faces -z, 1 faces +x, 2 faces +z, 3 faces -x.
                    var rotation = Quaternion.Euler(0f, grabYaw, 0f);
                    bool across = grabbed.index % 2 == 1;
                    var outward = (across ? rotation * Vector3.right : rotation * Vector3.forward) *
                                  (grabbed.index == 1 || grabbed.index == 2 ? 1f : -1f);
                    float half = (across ? grabSize.x : grabSize.y) * 0.5f;
                    float reach = Vector3.Dot(WorkAreaGeometry.Flat(mousePoint - grabCentre), outward);
                    float length = reach + half;
                    if (length < 0.1f) return;
                    var size = across ? new Vector2(length, grabSize.y) : new Vector2(grabSize.x, length);
                    store.Record("Resize Work Area");
                    WorkAreaGround.ApplyRect(area, grabCentre + outward * ((reach - half) * 0.5f), size, grabYaw);
                    break;
                }
                case Grip.Corner:
                {
                    var moved = new List<Vector3>(area.vertices) { [grabbed.index] = mousePoint };
                    if (!WorkAreaGeometry.IsSimple(moved)) return;
                    store.Record("Move Work Area Corner");
                    area.vertices = moved;
                    break;
                }
                default:
                    return;
            }
            store.Touch(area);
            WorkAreaPanel.Repaint();
        }

        private void FinishDrag(WorkAreaStore store, WorkArea area)
        {
            state = State.Idle;
            grabVertices = null;
            if (area == null) return;
            store.Record("Reshape Work Area");
            WorkAreaGround.UpdateGround(area);
            store.Touch(area);
            WorkAreaPanel.Repaint();
        }

        // ------------------------------------------------------------------ hints

        internal static string Hint()
        {
            var tool = Current;
            if (tool != null && tool.state == State.DrawingPolygon)
                return "Click each corner. Click the first corner or press Enter to finish. Backspace removes the last corner, Esc cancels.";
            if (tool != null && tool.state == State.DrawingRect) return "Drag to size it. [ and ] turn it 15°. Esc cancels.";
            string draw = CurrentShape == Shape.Rectangle
                ? "Drag on the ground to draw a rectangle."
                : "Click on the ground to start a polygon.";
            return SelectedId == null
                ? draw + " Click an area to edit it."
                : "Drag yellow corners to reshape, the middle dots to " +
                  (WorkAreaStore.Instance.Areas.FirstOrDefault(a => a.id == SelectedId)?.IsRect == true
                      ? "move a side, the square to move it, the ring to turn it ([ ] = 15°)."
                      : "add a corner (Ctrl+click a corner removes it), the square to move it.") + " Esc deselects. " + draw;
        }

        private string CursorText()
        {
            switch (state)
            {
                case State.DrawingPolygon:
                    return polygon.Count >= 3 ? "Enter / click first corner to finish" : "Click the next corner";
                case State.DrawingRect:
                    return null;
                default:
                    return mouseOnGround ? null : "Point at the ground";
            }
        }

        private static float ScreenDistance(Vector3 world, Vector2 mouse) => Vector2.Distance(HandleUtility.WorldToGUIPoint(world), mouse);
    }
}

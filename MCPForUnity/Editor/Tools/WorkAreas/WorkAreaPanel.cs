using System.Linq;
using UnityEditor;
using UnityEngine;
#if UNITY_2022_1_OR_NEWER
using UnityEditor.Overlays;
#endif

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>The Work Area tool's panel: shape, area list (active, rename, delete) and the selected area's settings.</summary>
    public static class WorkAreaPanel
    {
        private static readonly GUIContent[] ShapeNames =
        {
            new GUIContent("Rectangle", "Drag on the ground. [ and ] turn it 15°."),
            new GUIContent("Polygon", "Click each corner, then click the first corner again or press Enter.")
        };
        private static Vector2 scroll;

        internal static void Repaint()
        {
#if UNITY_2022_1_OR_NEWER
            WorkAreaOverlay.RepaintAll();
#endif
            SceneView.RepaintAll();
        }

        public static void Draw()
        {
            var store = WorkAreaStore.Instance;
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70f;
            try { DrawContents(store); }
            finally { EditorGUIUtility.labelWidth = labelWidth; }
        }

        private static void DrawContents(WorkAreaStore store)
        {
            using (new GUILayout.VerticalScope(GUILayout.Width(280f)))
            {
                var shape = (WorkAreaTool.Shape)GUILayout.Toolbar((int)WorkAreaTool.CurrentShape, ShapeNames);
                if (shape != WorkAreaTool.CurrentShape)
                {
                    WorkAreaTool.CurrentShape = shape;
                    WorkAreaTool.Current?.CancelDrawing();
                }
                GUILayout.Label(WorkAreaTool.Hint(), EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(WorkAreaTool.Message)) EditorGUILayout.HelpBox(WorkAreaTool.Message, MessageType.Warning);

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(new GUIContent("Areas (" + store.Areas.Count + ")",
                    "The radio button marks the active area: the one agents target by default."), EditorStyles.boldLabel);
                if (store.Areas.Count == 0) GUILayout.Label("None yet: draw one in the Scene view.", EditorStyles.miniLabel);
                bool scrolling = store.Areas.Count > 6;
                if (scrolling) scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(150f));
                foreach (var area in store.Areas.ToList()) AreaRow(store, area);
                if (scrolling) EditorGUILayout.EndScrollView();

                var selected = store.Areas.FirstOrDefault(area => area.id == WorkAreaTool.SelectedId);
                if (selected != null) Details(store, selected);

                EditorGUILayout.Space(4f);
                WorkAreaDrawing.KeepVisible = EditorGUILayout.ToggleLeft(new GUIContent("Keep showing areas when the tool is off",
                    "Off: nothing is drawn or hooked while another tool is active. On: areas are drawn on Scene-view repaints."),
                    WorkAreaDrawing.KeepVisible);
                using (new EditorGUI.DisabledScope(store.Areas.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("Clear all…", "Delete every work area (Ctrl+Z brings them back).")) &&
                        EditorUtility.DisplayDialog("Clear work areas", "Delete all " + store.Areas.Count + " work areas?", "Delete", "Cancel"))
                    {
                        store.Record("Clear Work Areas");
                        store.RemoveAll();
                        store.Touch();
                        WorkAreaTool.Select(null);
                    }
                }
                GUILayout.Label(new GUIContent("Saved in " + WorkAreaStore.FilePath + " (per user, not submitted).",
                    "Delete the file to remove every area."), EditorStyles.wordWrappedMiniLabel);
            }
        }

        private static void AreaRow(WorkAreaStore store, WorkArea area)
        {
            bool selected = area.id == WorkAreaTool.SelectedId;
            using (new GUILayout.HorizontalScope())
            {
                bool active = area.id == store.ActiveId;
                if (GUILayout.Toggle(active, new GUIContent("", "Active: the area agents target by default."), EditorStyles.radioButton,
                        GUILayout.Width(16f)) && !active)
                {
                    store.Record("Set Active Work Area");
                    store.SetActive(area);
                    store.Touch();
                }
                if (GUILayout.Button(new GUIContent(area.name, "Select to edit"), selected ? EditorStyles.boldLabel : EditorStyles.label))
                    WorkAreaTool.Select(selected ? null : area);
                GUILayout.FlexibleSpace();
                GUILayout.Label(string.Format("{0:N0} m²", WorkAreaGeometry.Area(area.vertices)), EditorStyles.miniLabel);
                if (GUILayout.Button(new GUIContent("✕", "Delete this area (Ctrl+Z brings it back)."), EditorStyles.miniButton, GUILayout.Width(22f)))
                {
                    store.Record("Delete Work Area");
                    store.Remove(area);
                    store.Touch();
                    if (selected) WorkAreaTool.Select(null);
                }
            }
        }

        private static void Details(WorkAreaStore store, WorkArea area)
        {
            EditorGUILayout.Space(4f);
            using (new GUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                string name = EditorGUILayout.DelayedTextField("Name", area.name);
                if (EditorGUI.EndChangeCheck() && !string.IsNullOrWhiteSpace(name))
                {
                    store.Record("Rename Work Area");
                    area.name = store.UniqueName(name, area);
                    store.Touch(area);
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(area.IsRect
                        ? string.Format("Rectangle {0:0.0} × {1:0.0} m, {2:0}°", area.rectSize.x, area.rectSize.y, area.rectYaw)
                        : "Polygon, " + area.vertices.Count + " corners", EditorStyles.miniLabel);
                    if (area.IsRect && GUILayout.Button(new GUIContent("To polygon", "Edit the corners freely."), EditorStyles.miniButton))
                    {
                        store.Record("Work Area To Polygon");
                        area.kind = WorkArea.Polygon;
                        store.Touch(area);
                    }
                }
                GUILayout.Label(string.Format("{0:N1} m², perimeter {1:N1} m", WorkAreaGeometry.Area(area.vertices),
                    WorkAreaGeometry.Perimeter(area.vertices)), EditorStyles.miniLabel);

                EditorGUI.BeginChangeCheck();
                bool manual = EditorGUILayout.ToggleLeft(new GUIContent("Set the height range by hand",
                    "Off: from the ground under the area, plus/minus the margin."), area.manualHeight);
                float min = area.heightMin, max = area.heightMax, margin = area.heightMargin;
                if (manual)
                {
                    if (!area.manualHeight) { min = area.VolumeMin; max = area.VolumeMax; }
                    min = EditorGUILayout.FloatField("Bottom y", min);
                    max = EditorGUILayout.FloatField("Top y", max);
                }
                else
                {
                    margin = Mathf.Clamp(EditorGUILayout.FloatField(new GUIContent("Margin (m)",
                        string.Format("Ground under the area: y {0:0.0} to {1:0.0}.", area.groundMin, area.groundMax)), margin), 0f, 1000f);
                    GUILayout.Label(string.Format("Volume y {0:0.0} to {1:0.0}", area.VolumeMin, area.VolumeMax), EditorStyles.miniLabel);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    store.Record("Work Area Height");
                    area.manualHeight = manual;
                    area.heightMin = Mathf.Min(min, max - 0.1f);
                    area.heightMax = max;
                    area.heightMargin = margin;
                    store.Touch(area);
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Frame", "Frame the area in the Scene view.")))
                    {
                        var points = area.vertices;
                        var bounds = new Bounds(points[0], Vector3.zero);
                        foreach (var point in points) bounds.Encapsulate(point);
                        bounds.Encapsulate(new Vector3(bounds.center.x, area.VolumeMax, bounds.center.z));
                        SceneView.lastActiveSceneView?.Frame(bounds, false);
                    }
                    if (GUILayout.Button(new GUIContent("Copy JSON", "Copy what get_work_area returns for this area.")))
                        EditorGUIUtility.systemCopyBuffer = WorkAreaReport.Build(area, area.id == store.ActiveId).ToString();
                }
            }
        }
    }

#if UNITY_2022_1_OR_NEWER
    /// <summary>Scene-view overlay that appears only while the Work Area tool is active.</summary>
    [Overlay(typeof(SceneView), "mcp-work-area", "MCP Work Area", true)]
    internal sealed class WorkAreaOverlay : IMGUIOverlay, ITransientOverlay
    {
        private static readonly System.Collections.Generic.List<WorkAreaOverlay> Open = new System.Collections.Generic.List<WorkAreaOverlay>();

        public bool visible => WorkAreaTool.IsActive;

        public override void OnCreated() => Open.Add(this);

        public override void OnWillBeDestroyed() => Open.Remove(this);

        public override void OnGUI() => WorkAreaPanel.Draw();

        internal static void RepaintAll()
        {
            foreach (var overlay in Open) overlay.containerWindow?.Repaint();
        }
    }
#endif
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>A footprint drawn in the Scene view (or proposed by an agent): world XZ outline, treated as a vertical volume.</summary>
    [Serializable]
    public sealed class WorkArea
    {
        public const string Rect = "rect";
        public const string Polygon = "polygon";

        public string id;
        public string name;
        public string kind = Polygon;
        /// <summary>World-space corners as drawn; y is the snapped surface height.</summary>
        public List<Vector3> vertices = new List<Vector3>();
        // Rectangles keep their frame so they stay rectangular while edited.
        public Vector3 rectCentre;
        public Vector2 rectSize;
        public float rectYaw;
        // Volume: automatic (ground under the area +/- margin) unless manual.
        public bool manualHeight;
        public float heightMin, heightMax;
        public float heightMargin = 5f;
        // Ground height range under the area at its last reshape (display only; reads re-sample).
        public float groundMin, groundMax;
        public string scene;
        public string source;
        public string createdUtc, modifiedUtc;

        public bool IsRect => kind == Rect;

        public float VolumeMin => manualHeight ? heightMin : groundMin - heightMargin;
        public float VolumeMax => manualHeight ? heightMax : groundMax + heightMargin;
    }

    /// <summary>
    /// Work areas live in one small per-user file, UserSettings/MCPForUnity/WorkAreas.json: never in a scene or an asset,
    /// so drawing never dirties Northern Valley or its tile scenes, and nothing is submitted to version control.
    /// Deleting the file (or "Clear all") removes every area. The in-memory copy is a hidden ScriptableObject so edits
    /// go through Unity's Undo; nothing is loaded until the tool or an MCP call first needs it.
    /// </summary>
    public sealed class WorkAreaStore : ScriptableObject
    {
        public const int MaxAreas = 64;
        public const int MaxVertices = 256;
        public const int MaxNameLength = 64;
        public const string DefaultFilePath = "UserSettings/MCPForUnity/WorkAreas.json";

        [Serializable]
        private sealed class FileData
        {
            public int version = 1;
            public string activeId;
            public List<WorkArea> areas = new List<WorkArea>();
        }

        [SerializeField] private List<WorkArea> areas = new List<WorkArea>();
        [SerializeField] private string activeId;
        [SerializeField] private int revision;
        [SerializeField] private bool unsaved;
        [SerializeField] private long fileStamp;

        private static WorkAreaStore instance;
        private static int savedRevision = -1;

        /// <summary>Project-relative (or absolute) file the areas are kept in. Tests point this elsewhere.</summary>
        internal static string FilePath = DefaultFilePath;

        /// <summary>Raised after any change (drawn, MCP, undo, file reload) so Scene views can repaint.</summary>
        public static event Action Changed;

        public static WorkAreaStore Instance
        {
            get
            {
                if (instance == null) Load();
                return instance;
            }
        }

        public IReadOnlyList<WorkArea> Areas => areas;
        public int Revision => revision;
        public string ActiveId => activeId;
        public WorkArea Active => areas.FirstOrDefault(area => area.id == activeId);

        // ------------------------------------------------------------------ lookup

        /// <summary>Finds an area by id, by name (case-insensitive) or by "active".</summary>
        public WorkArea Find(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = key.Trim();
            if (string.Equals(key, "active", StringComparison.OrdinalIgnoreCase)) return Active;
            return areas.FirstOrDefault(area => area.id == key) ??
                   areas.FirstOrDefault(area => string.Equals(area.name, key, StringComparison.OrdinalIgnoreCase));
        }

        public string UniqueName(string wanted, WorkArea except = null)
        {
            wanted = string.IsNullOrWhiteSpace(wanted) ? null : wanted.Trim();
            if (wanted != null && wanted.Length > MaxNameLength) wanted = wanted.Substring(0, MaxNameLength);
            if (wanted == null)
            {
                int number = areas.Count + 1;
                while (Taken("Area " + number, except)) number++;
                return "Area " + number;
            }
            if (!Taken(wanted, except)) return wanted;
            for (int suffix = 2; ; suffix++)
                if (!Taken(wanted + " (" + suffix + ")", except)) return wanted + " (" + suffix + ")";
        }

        private bool Taken(string name, WorkArea except) =>
            areas.Any(area => area != except && string.Equals(area.name, name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ changes (callers record Undo first)

        public void Record(string undoName) => Undo.RecordObject(this, undoName);

        public WorkArea Add(WorkArea area, bool makeActive)
        {
            area.id = "wa_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            area.name = UniqueName(area.name);
            area.createdUtc = area.modifiedUtc = Now();
            if (string.IsNullOrEmpty(area.scene)) area.scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            areas.Add(area);
            if (makeActive || activeId == null) activeId = area.id;
            return area;
        }

        public void Remove(WorkArea area)
        {
            areas.Remove(area);
            if (activeId == area.id) activeId = areas.Count > 0 ? areas[areas.Count - 1].id : null;
        }

        public void RemoveAll()
        {
            areas.Clear();
            activeId = null;
        }

        public void SetActive(WorkArea area) => activeId = area?.id;

        /// <summary>Stamps a change. Saves now unless a Scene-view drag is in progress (then on release).</summary>
        public void Touch(WorkArea area = null)
        {
            if (area != null) area.modifiedUtc = Now();
            revision++;
            if (GUIUtility.hotControl != 0) unsaved = true;
            else Save();
            Changed?.Invoke();
        }

        /// <summary>Writes a change held back during a drag.</summary>
        public void Flush()
        {
            if (unsaved && GUIUtility.hotControl == 0) Save();
        }

        // ------------------------------------------------------------------ file

        /// <summary>Picks up the file being edited or deleted outside Unity. Cheap: one file-time check.</summary>
        public static void Refresh()
        {
            if (instance == null) { Load(); return; }
            if (instance.unsaved || Stamp() == instance.fileStamp) return;
            Undo.ClearUndo(instance);
            instance.Read();
            Changed?.Invoke();
        }

        internal static void ResetForTests()
        {
            if (instance != null)
            {
                Undo.ClearUndo(instance);
                DestroyImmediate(instance);
            }
            instance = null;
            savedRevision = -1;
        }

        private static void Load()
        {
            // A domain reload keeps the hidden object (and any edit not yet written); only the static is lost.
            instance = UnityEngine.Resources.FindObjectsOfTypeAll<WorkAreaStore>().FirstOrDefault();
            if (instance == null)
            {
                instance = CreateInstance<WorkAreaStore>();
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.Read();
            }
            else if (instance.unsaved) instance.Save();
            else if (Stamp() != instance.fileStamp) instance.Read();
            savedRevision = instance.revision;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private static void OnUndoRedo()
        {
            if (instance == null || instance.revision == savedRevision) return;
            instance.Save();
            Changed?.Invoke();
        }

        private void Read()
        {
            areas = new List<WorkArea>();
            activeId = null;
            string path = FullPath();
            if (File.Exists(path))
            {
                try
                {
                    var data = JsonUtility.FromJson<FileData>(File.ReadAllText(path));
                    if (data?.areas != null)
                    {
                        areas = data.areas.Where(area => area != null && !string.IsNullOrEmpty(area.id) && area.vertices != null &&
                                                         area.vertices.Count >= 3).Take(MaxAreas).ToList();
                        activeId = areas.Any(area => area.id == data.activeId) ? data.activeId : areas.FirstOrDefault()?.id;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("MCP work areas: could not read " + FilePath + " (" + exception.Message + "). Starting empty.");
                }
            }
            revision++;
            savedRevision = revision;
            unsaved = false;
            fileStamp = Stamp();
        }

        private void Save()
        {
            string path = FullPath();
            try
            {
                if (areas.Count == 0)
                {
                    // Nothing to keep: leave no file behind.
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonUtility.ToJson(new FileData { activeId = activeId, areas = areas }, true));
                }
                unsaved = false;
                savedRevision = revision;
                fileStamp = Stamp();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("MCP work areas: could not write " + FilePath + " (" + exception.Message + ").");
            }
        }

        private static string FullPath() =>
            Path.IsPathRooted(FilePath) ? FilePath : Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", FilePath);

        private static long Stamp()
        {
            string path = FullPath();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks ^ new FileInfo(path).Length : 0;
        }

        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}

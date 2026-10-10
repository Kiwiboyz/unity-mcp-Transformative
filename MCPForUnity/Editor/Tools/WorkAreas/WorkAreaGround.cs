using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Runtime.Helpers;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>Ground heights, terrain tiles and water under work areas. Only runs when asked (no caching scans).</summary>
    public static class WorkAreaGround
    {
        private const float RayTop = 20000f;

        /// <summary>A terrain tile: a loaded Terrain, or a Gaia terrain scene that isn't loaded.</summary>
        public struct Tile
        {
            public string name;
            public string scenePath;
            public bool loaded;
            public Rect worldRect;
        }

        // ------------------------------------------------------------------ heights

        /// <summary>Surface height at (x, z): the loaded terrain there, else the highest collider, else false.</summary>
        public static bool TrySample(float x, float z, out float y)
        {
            var terrain = TerrainAt(x, z);
            if (terrain != null)
            {
                y = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.GetPosition().y;
                return true;
            }
            if (UnityEngine.Physics.Raycast(new Vector3(x, RayTop, z), Vector3.down, out var hit, RayTop * 2f,
                    UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                y = hit.point.y;
                return true;
            }
            y = 0f;
            return false;
        }

        public static Terrain TerrainAt(float x, float z)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                if (TerrainRect(terrain).Contains(new Vector2(x, z))) return terrain;
            }
            return null;
        }

        public static Rect TerrainRect(Terrain terrain)
        {
            var position = terrain.GetPosition();
            var size = terrain.terrainData.size;
            return new Rect(position.x, position.z, size.x, size.z);
        }

        /// <summary>Snaps a point's height to the surface below it (kept as given when there is none).</summary>
        public static Vector3 Snap(Vector3 point) => TrySample(point.x, point.z, out float y) ? new Vector3(point.x, y, point.z) : point;

        /// <summary>Surface point under a Scene-view mouse ray: colliders (terrain included), else the y = 0 plane.</summary>
        public static bool TryPick(Ray ray, out Vector3 point)
        {
            if (UnityEngine.Physics.Raycast(ray, out var hit, 50000f, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                return true;
            }
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }
            point = default;
            return false;
        }

        /// <summary>Ground heights over the inside of an outline on a grid of at most about <paramref name="maxSamples"/>.</summary>
        public static List<float> SampleInside(IList<Vector3> outline, int maxSamples, out int attempted, out float spacing)
        {
            var heights = new List<float>();
            attempted = 0;
            float area = WorkAreaGeometry.Area(outline);
            spacing = Mathf.Max(0.5f, Mathf.Sqrt(Mathf.Max(area, 0.01f) / Mathf.Max(1, maxSamples)));
            float minX = outline.Min(p => p.x), maxX = outline.Max(p => p.x);
            float minZ = outline.Min(p => p.z), maxZ = outline.Max(p => p.z);
            for (float x = minX + spacing * 0.5f; x < maxX; x += spacing)
            for (float z = minZ + spacing * 0.5f; z < maxZ; z += spacing)
            {
                var point = new Vector3(x, 0f, z);
                if (!WorkAreaGeometry.Contains(outline, point)) continue;
                attempted++;
                if (TrySample(x, z, out float y)) heights.Add(y);
            }
            foreach (var corner in outline)
            {
                attempted++;
                if (TrySample(corner.x, corner.z, out float y)) heights.Add(y);
            }
            return heights;
        }

        // ------------------------------------------------------------------ shapes

        /// <summary>Makes a rectangle from its frame, corners snapped to the ground.</summary>
        public static void ApplyRect(WorkArea area, Vector3 centre, Vector2 size, float yaw)
        {
            area.kind = WorkArea.Rect;
            area.rectYaw = Mathf.Repeat(yaw, 360f);
            area.rectSize = new Vector2(Mathf.Abs(size.x), Mathf.Abs(size.y));
            area.rectCentre = Snap(new Vector3(centre.x, centre.y, centre.z));
            area.vertices = WorkAreaGeometry.RectangleCorners(area.rectCentre, area.rectSize, area.rectYaw).Select(Snap).ToList();
        }

        /// <summary>Finds a rectangle's frame from four corners in drawing order (first side = width).</summary>
        public static void FrameFromCorners(IList<Vector3> corners, out Vector3 centre, out Vector2 size, out float yaw)
        {
            var side = WorkAreaGeometry.Flat(corners[1] - corners[0]);
            var other = WorkAreaGeometry.Flat(corners[3] - corners[0]);
            yaw = side.sqrMagnitude > 1e-6f ? Mathf.Atan2(-side.z, side.x) * Mathf.Rad2Deg : 0f;
            centre = (corners[0] + corners[2]) * 0.5f;
            size = new Vector2(side.magnitude, other.magnitude);
        }

        /// <summary>Refreshes the cached ground range used to draw the default volume.</summary>
        public static void UpdateGround(WorkArea area, int maxSamples = 400)
        {
            var heights = SampleInside(area.vertices, maxSamples, out _, out _);
            if (heights.Count == 0) heights = area.vertices.Select(vertex => vertex.y).ToList();
            area.groundMin = heights.Min();
            area.groundMax = heights.Max();
        }

        // ------------------------------------------------------------------ tiles and water

        /// <summary>Loaded terrains plus the Gaia terrain scenes that aren't loaded (from the Gaia terrain loader, if present).</summary>
        public static List<Tile> Tiles()
        {
            var tiles = new List<Tile>();
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                tiles.Add(new Tile
                {
                    name = terrain.name, scenePath = terrain.gameObject.scene.path, loaded = true, worldRect = TerrainRect(terrain)
                });
            }
            var loadedScenes = new HashSet<string>(tiles.Select(tile => tile.scenePath));
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) loadedScenes.Add(scene.path);
            }
            foreach (var tile in GaiaTileScenes())
                if (!loadedScenes.Contains(tile.scenePath)) tiles.Add(tile);
            return tiles;
        }

        private static Type gaiaLoaderType;
        private static bool gaiaLooked;

        private static IEnumerable<Tile> GaiaTileScenes()
        {
            if (!gaiaLooked)
            {
                gaiaLooked = true;
                gaiaLoaderType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("Gaia.TerrainLoaderManager", false))
                    .FirstOrDefault(type => type != null);
            }
            if (gaiaLoaderType == null) yield break;
            var storageField = gaiaLoaderType.GetField("m_terrainSceneStorage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (storageField == null) yield break;
            var seen = new HashSet<string>();
            foreach (var manager in UnityFindObjectsCompat.FindAll(gaiaLoaderType, true))
            {
                IList scenes = null;
                try
                {
                    var storage = storageField.GetValue(manager);
                    scenes = storage?.GetType().GetField("m_terrainScenes")?.GetValue(storage) as IList;
                }
                catch (Exception) { }
                if (scenes == null) continue;
                foreach (var item in scenes)
                {
                    if (item == null) continue;
                    var type = item.GetType();
                    var path = type.GetField("m_scenePath")?.GetValue(item) as string;
                    var bounds = type.GetField("m_bounds")?.GetValue(item);
                    if (string.IsNullOrEmpty(path) || bounds == null || !seen.Add(path)) continue;
                    var centre = Vector(bounds, "center");
                    var extents = Vector(bounds, "extents");
                    yield return new Tile
                    {
                        name = System.IO.Path.GetFileNameWithoutExtension(path), scenePath = path, loaded = false,
                        worldRect = Rect.MinMaxRect(centre.x - extents.x, centre.z - extents.z, centre.x + extents.x, centre.z + extents.z)
                    };
                }
            }
        }

        private static Vector3 Vector(object owner, string member)
        {
            var type = owner.GetType();
            var value = type.GetProperty(member)?.GetValue(owner) ?? type.GetField(member)?.GetValue(owner);
            if (value is Vector3 vector) return vector;
            if (value == null) return Vector3.zero;
            // Gaia's BoundsDouble stores Vector3Double (x, y, z doubles).
            var valueType = value.GetType();
            float Read(string axis) => Convert.ToSingle(valueType.GetField(axis)?.GetValue(value) ?? valueType.GetProperty(axis)?.GetValue(value) ?? 0f);
            return new Vector3(Read("x"), Read("y"), Read("z"));
        }

        /// <summary>The scene's water level: Gaia's "Water Surface" object if there is one.</summary>
        public static bool TryWaterLevel(out float level, out string source)
        {
            var water = GameObject.Find("Water Surface");
            if (water != null)
            {
                level = water.transform.position.y;
                source = "Water Surface (" + water.scene.name + ")";
                return true;
            }
            level = 0f;
            source = null;
            return false;
        }
    }
}

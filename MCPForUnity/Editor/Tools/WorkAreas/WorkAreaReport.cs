using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>What an agent reads about a work area: exact world coordinates plus the ground, tiles and water under it.</summary>
    public static class WorkAreaReport
    {
        public const string Units = "Unity world space, metres, y up. Vertices are [x, y, z]; y is the surface height where each corner was snapped.";
        private static readonly Regex TileGrid = new Regex(@"^Terrain_(\d+)_(\d+)", RegexOptions.CultureInvariant);

        public static JObject Build(WorkArea area, bool active, float? waterLevel = null, int maxSamples = 2500)
        {
            var outline = area.vertices;
            float signedArea = WorkAreaGeometry.SignedArea(outline);
            float areaM2 = Mathf.Abs(signedArea);

            var heights = WorkAreaGround.SampleInside(outline, maxSamples, out int attempted, out float spacing);
            float groundMin = heights.Count > 0 ? heights.Min() : outline.Min(p => p.y);
            float groundMax = heights.Count > 0 ? heights.Max() : outline.Max(p => p.y);
            float yMin = area.manualHeight ? area.heightMin : groundMin - area.heightMargin;
            float yMax = area.manualHeight ? area.heightMax : groundMax + area.heightMargin;

            var centroid = WorkAreaGeometry.Centroid(outline);
            if (WorkAreaGround.TrySample(centroid.x, centroid.z, out float centroidY)) centroid.y = centroidY;

            var result = new JObject
            {
                ["id"] = area.id,
                ["name"] = area.name,
                ["kind"] = area.kind,
                ["active"] = active,
                ["vertex_count"] = outline.Count,
                ["vertices"] = new JArray(outline.Select(Point)),
                ["winding"] = signedArea >= 0 ? "counter-clockwise seen from above" : "clockwise seen from above",
                ["centroid"] = Point(centroid),
                ["bounds"] = new JObject
                {
                    ["min"] = Point(new Vector3(outline.Min(p => p.x), yMin, outline.Min(p => p.z))),
                    ["max"] = Point(new Vector3(outline.Max(p => p.x), yMax, outline.Max(p => p.z))),
                    ["size"] = Point(new Vector3(outline.Max(p => p.x) - outline.Min(p => p.x), yMax - yMin,
                        outline.Max(p => p.z) - outline.Min(p => p.z)))
                },
                ["area_m2"] = Round(areaM2, 2),
                ["perimeter_m"] = Round(WorkAreaGeometry.Perimeter(outline), 2),
                ["height_range"] = new JObject
                {
                    ["min_y"] = Round(yMin),
                    ["max_y"] = Round(yMax),
                    ["mode"] = area.manualHeight ? "manual" : "auto",
                    ["margin_m"] = area.manualHeight ? null : (JToken)Round(area.heightMargin, 2)
                },
                ["ground"] = new JObject
                {
                    ["min_y"] = Round(groundMin),
                    ["max_y"] = Round(groundMax),
                    ["mean_y"] = heights.Count > 0 ? (JToken)Round(heights.Average()) : null,
                    ["samples"] = heights.Count,
                    ["sampled_fraction"] = attempted > 0 ? Round((float)heights.Count / attempted, 3) : 0,
                    ["spacing_m"] = Round(spacing, 2)
                }
            };
            if (area.IsRect)
                result["rect"] = new JObject
                {
                    ["centre"] = Point(area.rectCentre),
                    ["width_m"] = Round(area.rectSize.x),
                    ["length_m"] = Round(area.rectSize.y),
                    ["yaw_deg"] = Round(area.rectYaw, 2)
                };
            result["terrain_tiles"] = Tiles(outline);
            result["water"] = Water(outline, waterLevel, areaM2);
            result["scene"] = area.scene;
            result["source"] = area.source;
            result["created_utc"] = area.createdUtc;
            result["modified_utc"] = area.modifiedUtc;
            result["summary"] = Summary(area, areaM2, centroid, result);
            return result;
        }

        private static JArray Tiles(IList<Vector3> outline)
        {
            var tiles = new JArray();
            foreach (var tile in WorkAreaGround.Tiles())
            {
                float overlap = WorkAreaGeometry.OverlapArea(outline, tile.worldRect);
                if (overlap <= 0.0001f) continue;
                var entry = new JObject
                {
                    ["name"] = tile.name,
                    ["scene"] = tile.scenePath,
                    ["loaded"] = tile.loaded,
                    ["overlap_m2"] = Round(overlap, 2),
                    ["x_range"] = new JArray(Round(tile.worldRect.xMin), Round(tile.worldRect.xMax)),
                    ["z_range"] = new JArray(Round(tile.worldRect.yMin), Round(tile.worldRect.yMax))
                };
                var grid = TileGrid.Match(tile.name ?? "");
                if (grid.Success)
                    entry["grid"] = new JArray(int.Parse(grid.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(grid.Groups[2].Value, CultureInfo.InvariantCulture));
                tiles.Add(entry);
            }
            return tiles;
        }

        private static JToken Water(IList<Vector3> outline, float? requested, float areaM2)
        {
            float level;
            string source;
            if (requested.HasValue) { level = requested.Value; source = "water_level parameter"; }
            else if (!WorkAreaGround.TryWaterLevel(out level, out source)) return null;
            var heights = WorkAreaGround.SampleInside(outline, 1500, out _, out _);
            if (heights.Count == 0)
                return new JObject { ["level_y"] = Round(level), ["source"] = source, ["covers_water"] = null };
            float below = (float)heights.Count(height => height < level) / heights.Count;
            return new JObject
            {
                ["level_y"] = Round(level),
                ["source"] = source,
                ["covers_water"] = below > 0f,
                ["fraction_under_water"] = Round(below, 3),
                ["approx_m2_under_water"] = Round(below * areaM2, 1)
            };
        }

        private static string Summary(WorkArea area, float areaM2, Vector3 centroid, JObject report)
        {
            string text = string.Format(CultureInfo.InvariantCulture, "{0}: {1}, {2} corners, {3:0.#} m², centroid ({4:0.##}, {5:0.##}, {6:0.##})",
                area.name, area.IsRect ? "rectangle" : "polygon", area.vertices.Count, areaM2, centroid.x, centroid.y, centroid.z);
            var tiles = report["terrain_tiles"] as JArray;
            if (tiles != null && tiles.Count > 0)
                text += ", on " + string.Join(", ", tiles.Select(tile => (string)tile["name"] + ((bool)tile["loaded"] ? "" : " (not loaded)")));
            if (report["water"] is JObject water && water["fraction_under_water"] != null)
                text += string.Format(CultureInfo.InvariantCulture, ", {0:0}% under water (y {1})",
                    (float)water["fraction_under_water"] * 100f, (float)water["level_y"]);
            return text + ".";
        }

        public static JArray Point(Vector3 point) => new JArray(Round(point.x), Round(point.y), Round(point.z));

        private static double Round(float value, int digits = 3) => Math.Round(value, digits);
    }
}

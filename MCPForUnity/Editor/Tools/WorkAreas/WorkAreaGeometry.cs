using System.Collections.Generic;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.WorkAreas
{
    /// <summary>Footprint maths for work areas, seen from above (world XZ).</summary>
    public static class WorkAreaGeometry
    {
        /// <summary>Shoelace area in XZ: positive when the corners run counter-clockwise seen from above.</summary>
        public static float SignedArea(IList<Vector3> points)
        {
            double sum = 0;
            for (int i = 0, count = points.Count; i < count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % count];
                sum += (double)a.x * b.z - (double)b.x * a.z;
            }
            // Seen from above (the Scene view's top view) x runs right and z up the screen, an ordinary 2D frame.
            return (float)(sum * 0.5);
        }

        public static float Area(IList<Vector3> points) => Mathf.Abs(SignedArea(points));

        public static float Perimeter(IList<Vector3> points)
        {
            float length = 0f;
            for (int i = 0, count = points.Count; i < count; i++)
                length += Flat(points[(i + 1) % count] - points[i]).magnitude;
            return length;
        }

        /// <summary>Area-weighted centroid in XZ (vertex average for degenerate outlines); y is the mean vertex height.</summary>
        public static Vector3 Centroid(IList<Vector3> points)
        {
            int count = points.Count;
            if (count == 0) return Vector3.zero;
            double area = 0, cx = 0, cz = 0, y = 0;
            for (int i = 0; i < count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % count];
                double cross = (double)a.x * b.z - (double)b.x * a.z;
                area += cross;
                cx += (a.x + b.x) * cross;
                cz += (a.z + b.z) * cross;
                y += a.y;
            }
            y /= count;
            if (System.Math.Abs(area) < 1e-6)
            {
                double ax = 0, az = 0;
                foreach (var point in points) { ax += point.x; az += point.z; }
                return new Vector3((float)(ax / count), (float)y, (float)(az / count));
            }
            return new Vector3((float)(cx / (3 * area)), (float)y, (float)(cz / (3 * area)));
        }

        public static bool Contains(IList<Vector3> points, Vector3 point)
        {
            bool inside = false;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var a = points[i];
                var b = points[j];
                if ((a.z > point.z) != (b.z > point.z) &&
                    point.x < (b.x - a.x) * (point.z - a.z) / (b.z - a.z) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>True when no two sides cross or touch (other than neighbours at their shared corner).</summary>
        public static bool IsSimple(IList<Vector3> points)
        {
            int count = points.Count;
            if (count < 3) return false;
            for (int i = 0; i < count; i++)
            {
                var a = Flat2(points[i]);
                var b = Flat2(points[(i + 1) % count]);
                if ((b - a).sqrMagnitude < 1e-8f) return false;
                for (int j = i + 1; j < count; j++)
                {
                    if (j == i + 1 || (i == 0 && j == count - 1)) continue;
                    if (SegmentsTouch(a, b, Flat2(points[j]), Flat2(points[(j + 1) % count]))) return false;
                }
            }
            return true;
        }

        /// <summary>Area (m²) of the outline that lies inside an axis-aligned XZ rectangle.</summary>
        public static float OverlapArea(IList<Vector3> points, Rect rect)
        {
            var polygon = new List<Vector2>(points.Count);
            foreach (var point in points) polygon.Add(Flat2(point));
            polygon = Clip(polygon, p => p.x >= rect.xMin, (p, q) => Cross(p, q, rect.xMin, true));
            polygon = Clip(polygon, p => p.x <= rect.xMax, (p, q) => Cross(p, q, rect.xMax, true));
            polygon = Clip(polygon, p => p.y >= rect.yMin, (p, q) => Cross(p, q, rect.yMin, false));
            polygon = Clip(polygon, p => p.y <= rect.yMax, (p, q) => Cross(p, q, rect.yMax, false));
            double sum = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                sum += (double)a.x * b.y - (double)b.x * a.y;
            }
            return (float)System.Math.Abs(sum * 0.5);
        }

        /// <summary>Ear-clipping triangulation of a simple outline (indices into <paramref name="points"/>).</summary>
        public static List<int> Triangulate(IList<Vector3> points)
        {
            var triangles = new List<int>();
            int count = points.Count;
            if (count < 3) return triangles;
            var remaining = new List<int>(count);
            for (int i = 0; i < count; i++) remaining.Add(i);
            float winding = Mathf.Sign(SignedArea(points));
            int guard = count * count;
            while (remaining.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int previous = remaining[(i + remaining.Count - 1) % remaining.Count];
                    int current = remaining[i];
                    int next = remaining[(i + 1) % remaining.Count];
                    var a = points[previous];
                    var b = points[current];
                    var c = points[next];
                    if (Mathf.Sign(SignedArea(new[] { a, b, c })) != winding) continue;
                    bool empty = true;
                    foreach (int other in remaining)
                    {
                        if (other == previous || other == current || other == next) continue;
                        if (Contains(new[] { a, b, c }, points[other])) { empty = false; break; }
                    }
                    if (!empty) continue;
                    triangles.Add(previous);
                    triangles.Add(current);
                    triangles.Add(next);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (remaining.Count == 3) triangles.AddRange(remaining);
            return triangles;
        }

        /// <summary>Corners of a rectangle turned <paramref name="yawDegrees"/> about +y: -x-z, +x-z, +x+z, -x+z in its own frame.</summary>
        public static Vector3[] RectangleCorners(Vector3 centre, Vector2 size, float yawDegrees)
        {
            var rotation = Quaternion.Euler(0f, yawDegrees, 0f);
            var right = rotation * Vector3.right * (size.x * 0.5f);
            var forward = rotation * Vector3.forward * (size.y * 0.5f);
            return new[] { centre - right - forward, centre + right - forward, centre + right + forward, centre - right + forward };
        }

        public static Vector3 Flat(Vector3 value) => new Vector3(value.x, 0f, value.z);

        private static Vector2 Flat2(Vector3 value) => new Vector2(value.x, value.z);

        private static List<Vector2> Clip(List<Vector2> polygon, System.Func<Vector2, bool> inside,
            System.Func<Vector2, Vector2, Vector2> cross)
        {
            var result = new List<Vector2>(polygon.Count + 4);
            for (int i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i];
                var previous = polygon[(i + polygon.Count - 1) % polygon.Count];
                bool currentIn = inside(current), previousIn = inside(previous);
                if (currentIn)
                {
                    if (!previousIn) result.Add(cross(previous, current));
                    result.Add(current);
                }
                else if (previousIn) result.Add(cross(previous, current));
            }
            return result;
        }

        private static Vector2 Cross(Vector2 p, Vector2 q, float value, bool alongX)
        {
            float t = alongX ? (value - p.x) / (q.x - p.x) : (value - p.y) / (q.y - p.y);
            return Vector2.LerpUnclamped(p, q, t);
        }

        private static bool SegmentsTouch(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Orientation(c, d, a), d2 = Orientation(c, d, b), d3 = Orientation(a, b, c), d4 = Orientation(a, b, d);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            return (d1 == 0 && OnSegment(c, d, a)) || (d2 == 0 && OnSegment(c, d, b)) ||
                   (d3 == 0 && OnSegment(a, b, c)) || (d4 == 0 && OnSegment(a, b, d));
        }

        private static float Orientation(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        private static bool OnSegment(Vector2 a, Vector2 b, Vector2 p) =>
            p.x >= Mathf.Min(a.x, b.x) && p.x <= Mathf.Max(a.x, b.x) && p.y >= Mathf.Min(a.y, b.y) && p.y <= Mathf.Max(a.y, b.y);
    }
}

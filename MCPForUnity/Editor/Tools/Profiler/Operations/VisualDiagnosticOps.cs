using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MCPForUnity.Editor.Tools.Profiler
{
    [InitializeOnLoad]
    internal static class VisualDiagnosticOps
    {
        static Camera camera;
        static Vector3 oldPosition;
        static Quaternion oldRotation;
        static float oldFov;
        static bool active;
        static int count, interval, nextFrame, captureFrame;
        static string id, pending, result;
        static double deadline;
        static JArray frames, replay;
        static JObject settings, pendingRow;
        static VisualDiagnosticOps()
        {
            EditorApplication.update += Tick;
            RenderPipelineManager.beginCameraRendering += (context, c) => { if (active && c == camera && replay != null) ApplyPose(); };
            RenderPipelineManager.endCameraRendering += (context, c) => ObserveRender(c);
            Camera.onPostRender += ObserveRender;
            AssemblyReloadEvents.beforeAssemblyReload += () => { if (active) Finish("domain_reload"); };
            EditorApplication.quitting += () => { if (active) Finish("editor_quit"); };
        }
        internal static JObject Start(JObject p)
        {
            if (active) throw new InvalidOperationException("A visual sequence is active.");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) throw new InvalidOperationException("Capture visual sequences in unpaused Play Mode.");
            if (!Camera.main) throw new InvalidOperationException("A tagged MainCamera is required; screenshot captures the composed Game View.");
            count = DiagnosticCommon.Int(p, "count", 12, 1, 120);
            interval = DiagnosticCommon.Int(p, "interval_frames", 30, 2, 600);
            var baseline = p["baseline"] == null ? null : DiagnosticCommon.Read((string)p["baseline"]);
            replay = baseline == null ? null : (JArray)baseline["frames"];
            if (baseline != null)
            {
                if (replay == null || replay.Count < 1 || replay.Count > 120) throw new ArgumentException("Baseline requires 1..120 captured frames.");
                count = replay.Count;
                interval = DiagnosticCommon.Int(baseline, "interval_frames", 30, 2, 600);
            }
            camera = Camera.main; oldPosition = camera.transform.position; oldRotation = camera.transform.rotation; oldFov = camera.fieldOfView;
            settings = DiagnosticSceneOps.Settings(); frames = new JArray(); pending = null;
            id = "visual_" + Guid.NewGuid().ToString("N") + ".json";
            Directory.CreateDirectory(DiagnosticCommon.Root);
            nextFrame = Time.frameCount + 2; active = true; deadline = EditorApplication.timeSinceStartup + 600;
            ApplyPose();
            return Status();
        }
        static void ObserveRender(Camera rendered)
        {
            if (!active || rendered != camera || pendingRow == null || pending == null || (bool?)pendingRow["render_observed"] == true) return;
            pendingRow["render_observed"] = true;
            pendingRow["frame"] = Time.frameCount;
            pendingRow["simulation_time"] = Time.timeAsDouble;
            pendingRow["position"] = JToken.FromObject(camera.transform.position);
            pendingRow["rotation"] = JToken.FromObject(camera.transform.rotation);
            pendingRow["fov"] = camera.fieldOfView;
            pendingRow["weather"] = Weather();
        }
        static void ApplyPose()
        {
            if (replay == null) return;
            var row = replay[frames.Count];
            camera.transform.SetPositionAndRotation(row["position"].ToObject<Vector3>(), row["rotation"].ToObject<Quaternion>());
            camera.fieldOfView = (float)row["fov"];
        }
        static void Tick()
        {
            if (!active) return;
            try
            {
                if (!camera || !EditorApplication.isPlaying || EditorApplication.isPaused) { Finish("playback_stopped_or_camera_lost"); return; }
                if (EditorApplication.timeSinceStartup > deadline) { Finish("timeout"); return; }
                if (pending != null)
                {
                    if (!File.Exists(DiagnosticCommon.Artifact(pending)) || Time.frameCount <= captureFrame) return;
                    frames.Add(pendingRow); pending = null;
                    if (frames.Count >= count) { Finish("complete"); return; }
                    ApplyPose(); nextFrame = Time.frameCount + interval;
                }
                if (Time.frameCount < nextFrame) return;
                pending = Path.GetFileNameWithoutExtension(id) + "_" + frames.Count.ToString("D4") + ".png";
                pendingRow = new JObject { ["image"] = pending, ["frame"] = Time.frameCount, ["simulation_time"] = Time.timeAsDouble,
                    ["position"] = JToken.FromObject(camera.transform.position), ["rotation"] = JToken.FromObject(camera.transform.rotation),
                    ["fov"] = camera.fieldOfView, ["weather"] = Weather(), ["render_observed"] = false };
                captureFrame = Time.frameCount;
                ScreenCapture.CaptureScreenshot(DiagnosticCommon.Artifact(pending));
            }
            catch (Exception ex) { Finish("capture_error: " + ex.GetBaseException().Message); }
        }
        static JArray Weather() => new JArray(UnityEngine.Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(m => m && m.gameObject.scene.IsValid() && m.GetType().Name == "WeatherManager").Select(m => new JObject {
            ["path"] = DiagnosticCommon.Hierarchy(m.transform), ["weather_seconds"] = DiagnosticCommon.Member(m, "WeatherSeconds") is double d ? new JValue(d) : JValue.CreateNull(),
            ["configuration"] = DiagnosticSceneOps.Serialized(m) }));
        internal static JObject Finish(string reason = "cancelled")
        {
            if (!active) return Status();
            active = false;
            if (camera && replay != null) { camera.transform.SetPositionAndRotation(oldPosition, oldRotation); camera.fieldOfView = oldFov; }
            File.WriteAllText(DiagnosticCommon.Artifact(id), new JObject { ["schema"] = 1, ["reason"] = reason, ["settings"] = settings,
                ["interval_frames"] = interval, ["frames"] = frames,
                ["note"] = "Game View PNG sequence. Replay sets camera poses; reset the same scene, weather seed, inputs and simulation clock before the second run. Dynamic simulation is not rewound. Screenshots complete at end of frame." }.ToString());
            result = id; return Status();
        }
        internal static JObject Status() => new JObject { ["active"] = active, ["artifact"] = active ? id : result, ["captured"] = frames?.Count ?? 0 };
        internal static JObject Compare(JObject p)
        {
            var a = DiagnosticCommon.Read((string)p["baseline"]); var b = DiagnosticCommon.Read((string)p["candidate"]);
            var left = (JArray)a["frames"]; var right = (JArray)b["frames"];
            var settingsMatch = DiagnosticSceneOps.CompareSettings((JObject)a["settings"], (JObject)b["settings"]);
            var rows = new JArray();
            int n = Math.Min(left.Count, right.Count);
            int offset = DiagnosticCommon.Int(p, "offset", 0, 0, int.MaxValue);
            int end = (int)Math.Min(n, (long)offset + DiagnosticCommon.Int(p, "limit", 12, 1, 120));
            for (int i = offset; i < end; i++)
            {
                bool matched = (bool?)left[i]["render_observed"] == true && (bool?)right[i]["render_observed"] == true
                    && JToken.DeepEquals(left[i]["simulation_time"], right[i]["simulation_time"])
                    && JToken.DeepEquals(left[i]["position"], right[i]["position"]) && JToken.DeepEquals(left[i]["rotation"], right[i]["rotation"]) && JToken.DeepEquals(left[i]["fov"], right[i]["fov"])
                    && JToken.DeepEquals(left[i]["weather"], right[i]["weather"]);
                var row = new JObject { ["index"] = i, ["capture_context_matched"] = matched, ["before"] = left[i]["image"], ["after"] = right[i]["image"] };
                if ((!matched || !(bool)settingsMatch["matched"]) && (bool?)p["allow_mismatch"] != true) { row["comparison"] = DiagnosticCommon.Unavailable("Camera, simulation clock, weather or settings differ. Reset/replay, or use allow_mismatch=true for explicitly non-matched visual review."); rows.Add(row); continue; }
                var ta = new Texture2D(2, 2); var tb = new Texture2D(2, 2); Texture2D comparison = null;
                try
                {
                    if (!ta.LoadImage(File.ReadAllBytes(DiagnosticCommon.Artifact((string)left[i]["image"]))) || !tb.LoadImage(File.ReadAllBytes(DiagnosticCommon.Artifact((string)right[i]["image"])))) throw new InvalidDataException("Invalid captured PNG.");
                    if (ta.width != tb.width || ta.height != tb.height) throw new InvalidDataException("Capture resolutions differ.");
                    if (ta.width > 4096 || ta.height > 4096) throw new InvalidOperationException("Comparison supports images up to 4096x4096.");
                    var pa = ta.GetPixels32(); var pb = tb.GetPixels32(); var delta = new Color32[pa.Length]; double error = 0;
                    for (int x = 0; x < pa.Length; x++) { int r = Math.Abs(pa[x].r - pb[x].r), g = Math.Abs(pa[x].g - pb[x].g), bl = Math.Abs(pa[x].b - pb[x].b); error += r + g + bl; delta[x] = new Color32((byte)r, (byte)g, (byte)bl, 255); }
                    comparison = new Texture2D(ta.width, ta.height, TextureFormat.RGBA32, false); comparison.SetPixels32(delta); comparison.Apply();
                    string diff = "visual_diff_" + Guid.NewGuid().ToString("N") + ".png";
                    File.WriteAllBytes(DiagnosticCommon.Artifact(diff), comparison.EncodeToPNG());
                    row["absolute_difference_image"] = diff; row["mean_absolute_rgb_error"] = error / (pa.Length * 3.0 * 255);
                }
                finally { UnityEngine.Object.DestroyImmediate(ta); UnityEngine.Object.DestroyImmediate(tb); if (comparison) UnityEngine.Object.DestroyImmediate(comparison); }
                rows.Add(row);
            }
            return new JObject { ["settings_comparison"] = settingsMatch, ["frame_count_matched"] = left.Count == right.Count, ["frames"] = rows,
                ["artifact_directory"] = DiagnosticCommon.Root, ["review"] = "Review before/after PNG sequences alongside difference images; temporal AA, particles and stochastic weather can produce legitimate differences." };
        }
    }
}

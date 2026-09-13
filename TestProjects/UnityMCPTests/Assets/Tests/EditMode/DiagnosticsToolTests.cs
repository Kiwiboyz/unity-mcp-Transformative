using System;
using System.Collections;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Profiler;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor
{
    public class DiagnosticsToolTests
    {
        [Test] public void Summary_CountsEveryBudgetMissAndKeepsZeroUnavailable()
        {
            var frames = new JArray(new[] { 10.0, 12, 20, 40 }.Select(ms => new JObject { ["cpu_frame_ms"] = ms }));
            var result = BenchmarkOps.Summary(frames);
            Assert.That((int)result["misses_90_fps"], Is.EqualTo(3));
            Assert.That((int)result["misses_60_fps"], Is.EqualTo(2));
            Assert.That((double)result["p99_ms"], Is.EqualTo(40));
            Assert.That((string)BenchmarkOps.Summary(new JArray())["status"], Is.EqualTo("unavailable"));
        }
        [Test] public void SettingsComparison_IsOrderIndependentAndFlagsChanges()
        {
            Assert.That((bool)DiagnosticSceneOps.CompareSettings(JObject.Parse("{'a':1,'b':2}"), JObject.Parse("{'b':2,'a':1}"))["matched"], Is.True);
            Assert.That((string)DiagnosticSceneOps.CompareSettings(JObject.Parse("{'vsync':1}"), JObject.Parse("{'vsync':0}"))["changed_fields"][0], Is.EqualTo("vsync"));
        }
        [TestCase("../escape.json")] [TestCase("C:/escape.json")] [TestCase("..")] [TestCase("file:evil")]
        public void ArtifactsRejectEscapes(string input) { Assert.Throws<ArgumentException>(() => DiagnosticCommon.Artifact(input)); }
        [Test] public void IntegerValidationRejectsCoercion()
        {
            Assert.Throws<ArgumentException>(() => DiagnosticCommon.Int(JObject.Parse("{'limit':true}"), "limit", 1, 1, 500));
        }
        [Test] public void HandlerRejectsUnknownAndInvalidMemoryPaths()
        {
            Assert.That(ManageDiagnostics.HandleCommand(JObject.Parse("{'action':'bogus'}")), Is.TypeOf<ErrorResponse>());
            Assert.Throws<ArgumentException>(() => SnapshotAnalysis.ValidatePath("C:/outside.snap"));
            Assert.Throws<ArgumentException>(() => SnapshotAnalysis.ValidatePath("//server/share/a.snap"));
        }
        [Test] public void RendererAuditFindsFixture()
        {
            var root = new GameObject("DiagnosticsAuditFixture");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.parent = root.transform;
            try
            {
                var rows = (JArray)DiagnosticSceneOps.RendererAudit(new JObject { ["limit"] = 500 })["families"];
                Assert.That(rows.Any(r => (string)r["family"] == root.name && (int)r["submesh_slots_all_lods"] == 1), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [Test] public void StableAssetReferencesAndMissingGpuData()
        {
            var t = new Texture2D(4, 4) { name = "DiagnosticsTexture" };
            try { Assert.That(DiagnosticSceneOps.Serialized(t).ToString(), Does.Not.Contain("instanceID")); }
            finally { UnityEngine.Object.DestroyImmediate(t); }
            Assert.That((string)BenchmarkOps.GpuPasses(-1, 10)["status"], Is.EqualTo("unavailable"));
        }
        [UnityTest] public IEnumerator CapturedSnapshotHasObjectsArraysAndReferenceGraph()
        {
            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Unity.MemoryProfiler.Editor"))
                Assert.Ignore("Install Memory Profiler 1.1 in the test project to run the real capture adapter test.");
            string path = Path.Combine(Application.dataPath, "../Library/diagnostics-reader-test.snap");
            var texture = new Texture2D(64, 64) { name = "DiagnosticsOwnedTexture" };
            byte[] heldArray = new byte[12345];
            bool done = false, success = false;
            try
            {
                Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot(path, (p, ok) => { done = true; success = ok; },
                    Unity.Profiling.Memory.CaptureFlags.ManagedObjects | Unity.Profiling.Memory.CaptureFlags.NativeObjects);
                double deadline = EditorApplication.timeSinceStartup + 120;
                while (!done && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.That(done && success, Is.True, "Snapshot capture failed or timed out.");
                using (var snapshot = new SnapshotAnalysis(path, true))
                {
                    var rows = snapshot.Objects();
                    var row = rows.FirstOrDefault(r => (string)r["name"] == texture.name && (string)r["kind"] == "native");
                    Assert.That(row, Is.Not.Null);
                    Assert.That((long)row["tracked_bytes"], Is.GreaterThan(0));
                    Assert.That(rows.Any(r => (string)r["kind"] == "managed" && ((string)r["type"]).Contains("Byte[]")), Is.True);
                    Assert.That(snapshot.References("native", (long)row["index"], 3, 32)["paths_to_owners"], Is.Not.Null);
                    Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.References("native", long.MaxValue, 2, 4));
                }
                var diff = SnapshotAnalysis.Compare(path, path, new JObject { ["allow_large_snapshot"] = true, ["limit"] = 500 });
                Assert.That(((JArray)diff["groups"]).All(r => (long)r["delta_bytes"] == 0), Is.True);
                GC.KeepAlive(heldArray);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); if (File.Exists(path)) File.Delete(path); }
        }
        [Test] public void VisualComparisonExportsIdenticalDifferenceAndFlagsSettingsMismatch()
        {
            Directory.CreateDirectory(DiagnosticCommon.Root);
            string image = "test_" + Guid.NewGuid().ToString("N") + ".png";
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            string a = null, b = null, diff = null;
            try
            {
                texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white }); texture.Apply();
                File.WriteAllBytes(DiagnosticCommon.Artifact(image), texture.EncodeToPNG());
                var frame = new JObject { ["image"] = image, ["position"] = new JObject(), ["rotation"] = new JObject(),
                    ["weather"] = new JArray(), ["simulation_time"] = 0, ["fov"] = 60, ["render_observed"] = true };
                a = DiagnosticCommon.Save("test_visual", new JObject { ["settings"] = new JObject { ["vsync"] = 0 }, ["frames"] = new JArray(frame) });
                var result = VisualDiagnosticOps.Compare(new JObject { ["baseline"] = a, ["candidate"] = a });
                Assert.That((double)result["frames"][0]["mean_absolute_rgb_error"], Is.EqualTo(0));
                diff = (string)result["frames"][0]["absolute_difference_image"];
                Assert.That(File.Exists(DiagnosticCommon.Artifact(diff)), Is.True);
                b = DiagnosticCommon.Save("test_visual", new JObject { ["settings"] = new JObject { ["vsync"] = 1 }, ["frames"] = new JArray(frame) });
                result = VisualDiagnosticOps.Compare(new JObject { ["baseline"] = a, ["candidate"] = b });
                Assert.That((bool)result["settings_comparison"]["matched"], Is.False);
                Assert.That((string)result["frames"][0]["comparison"]["status"], Is.EqualTo("unavailable"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                foreach (var file in new[] { image, a, b, diff }.Where(f => f != null)) File.Delete(DiagnosticCommon.Artifact(file));
            }
        }
        [UnityTest] public IEnumerator BenchmarkRecordsAndRestoresProfiler()
        {
            yield return new EnterPlayMode();
            bool enabled = UnityEngine.Profiling.Profiler.enabled;
            string artifact = null;
            try
            {
                BenchmarkOps.Start(new JObject { ["duration_seconds"] = 1 });
                double deadline = EditorApplication.timeSinceStartup + 20;
                while ((bool)BenchmarkOps.Status()["active"] && EditorApplication.timeSinceStartup < deadline) yield return null;
                var state = BenchmarkOps.Status(); artifact = (string)state["artifact"];
                Assert.That((bool)state["active"], Is.False);
                Assert.That((int)state["frames_recorded"], Is.GreaterThan(0));
                Assert.That(UnityEngine.Profiling.Profiler.enabled, Is.EqualTo(enabled));
                Assert.That(File.Exists(DiagnosticCommon.Artifact(artifact)), Is.True);
                var report = DiagnosticCommon.Read(artifact);
                Assert.That((double)report["summary"]["sampled_duration_ms"], Is.GreaterThan(700), "Summary must cover the entire one-second run, not just the 300-frame history tail.");
                Assert.That((int)report["summary"]["missing_frames"], Is.EqualTo(0));
            }
            finally { BenchmarkOps.Stop(); }
            yield return new ExitPlayMode();
        }
    }
}

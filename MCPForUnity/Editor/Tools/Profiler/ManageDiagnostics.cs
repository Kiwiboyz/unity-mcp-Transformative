using System;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditorInternal;

namespace MCPForUnity.Editor.Tools.Profiler
{
    [McpForUnityTool("manage_diagnostics", AutoRegister = false, Group = "profiling", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageDiagnostics
    {
        public static object HandleCommand(JObject parameters)
        {
            try
            {
                if (parameters == null) throw new ArgumentException("Parameters are required.");
                string action = (string)parameters["action"];
                var p = parameters["options"] as JObject ?? new JObject();
                JObject data;
                switch (action)
                {
                    case "capabilities": data = new JObject { ["artifact_directory"] = DiagnosticCommon.Root, ["memory_reader"] = "Memory Profiler 1.1 optional reflection adapter",
                        ["benchmark"] = "Editor Play Mode, 1..600 seconds, complete game-frame intervals and 60/90 FPS misses plus raw CPU/GPU profiler export",
                        ["visual"] = "Game View PNG sequences with optional camera-pose replay; reset simulation separately; comparison flags context mismatches" }; break;
                    case "benchmark_start": data = BenchmarkOps.Start(p); break;
                    case "benchmark_status": data = BenchmarkOps.Status(); break;
                    case "benchmark_stop": data = BenchmarkOps.Stop(); break;
                    case "benchmark_analyze": data = BenchmarkOps.Analyze(p); break;
                    case "benchmark_report":
                        data = DiagnosticCommon.Read((string)p["artifact"]);
                        data["frames"] = DiagnosticCommon.Page((JArray)data["frames"], p); data.Remove("hitch_context");
                        data["profiler_frames"] = DiagnosticCommon.Page((JArray)data["profiler_frames"], p);
                        data.Remove("settings"); data.Remove("settings_at_end"); break;
                    case "benchmark_compare":
                        var a = DiagnosticCommon.Read((string)p["baseline"]); var b = DiagnosticCommon.Read((string)p["candidate"]);
                        data = new JObject { ["settings"] = DiagnosticSceneOps.CompareSettings((JObject)a["settings"], (JObject)b["settings"]), ["before"] = a["summary"], ["after"] = b["summary"],
                            ["before_end_settings"] = a["settings_changed_during_run"], ["after_end_settings"] = b["settings_changed_during_run"] }; break;
                    case "frame_breakdown":
                        int frame = DiagnosticCommon.Int(p, "frame", ProfilerDriver.lastFrameIndex, 0, int.MaxValue);
                        if (p["artifact"] != null)
                        {
                            var report = DiagnosticCommon.Read((string)p["artifact"]);
                            data = ((JArray)report["hitch_context"]).OfType<JObject>().FirstOrDefault(f => (int)f["frame"] == frame) ?? DiagnosticCommon.Unavailable("Frame is not in retained hitch context; load the raw recording in Unity Profiler.");
                        }
                        else data = BenchmarkOps.Frame(frame, DiagnosticCommon.Int(p, "limit", 100, 1, 500));
                        break;
                    case "settings_capture":
                        data = DiagnosticSceneOps.Settings();
                        data = new JObject { ["artifact"] = DiagnosticCommon.Save("settings", data), ["artifact_directory"] = DiagnosticCommon.Root,
                            ["quality"] = data["quality"], ["render_pipeline"] = data["render_pipeline"]?["name"],
                            ["camera_count"] = ((JArray)data["cameras"]).Count, ["loaded_terrain_count"] = ((JArray)data["terrain_tiles"]).Count,
                            ["unsaved_scene_warning"] = data["unsaved_scene_warning"],
                            ["settings"] = (bool?)p["include_details"] == true ? data : null }; break;
                    case "settings_compare": data = DiagnosticSceneOps.CompareSettings(DiagnosticCommon.Read((string)p["baseline"]), DiagnosticCommon.Read((string)p["candidate"])); break;
                    case "storm_diagnostics": data = DiagnosticSceneOps.Storm(); break;
                    case "renderer_audit": data = DiagnosticSceneOps.RendererAudit(p); break;
                    case "memory_compare": data = SnapshotAnalysis.Compare((string)p["snapshot_a"], (string)p["snapshot_b"], p); break;
                    case "memory_objects":
                        using (var snapshot = new SnapshotAnalysis((string)p["snapshot_path"], (bool?)p["allow_large_snapshot"] == true))
                        {
                            var rows = snapshot.Objects();
                            data = new JObject { ["object_count"] = rows.Count, ["objects"] = DiagnosticCommon.Page(rows.OrderByDescending(r => (long)r["tracked_bytes"]), p) };
                        }
                        break;
                    case "memory_references":
                        using (var snapshot = new SnapshotAnalysis((string)p["snapshot_path"], (bool?)p["allow_large_snapshot"] == true)) data = snapshot.References((string)p["kind"], DiagnosticCommon.Int(p, "index", 0, 0, int.MaxValue), DiagnosticCommon.Int(p, "depth", 4, 1, 10), DiagnosticCommon.Int(p, "limit", 64, 1, 500));
                        break;
                    case "visual_start": data = VisualDiagnosticOps.Start(p); break;
                    case "visual_status": data = VisualDiagnosticOps.Status(); break;
                    case "visual_stop": data = VisualDiagnosticOps.Finish(); break;
                    case "visual_compare": data = VisualDiagnosticOps.Compare(p); break;
                    default: throw new ArgumentException("Unknown diagnostic action: " + action);
                }
                return new SuccessResponse("Diagnostic action completed.", data);
            }
            catch (Exception ex) { return new ErrorResponse("Diagnostic action failed: " + ex.GetBaseException().Message); }
        }
    }
}

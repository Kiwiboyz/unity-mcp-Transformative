using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.LowLevel;
using UProfiler = UnityEngine.Profiling.Profiler;

namespace MCPForUnity.Editor.Tools.Profiler
{
    [InitializeOnLoad]
    internal static class BenchmarkOps
    {
        static bool active, previousEnabled, previousEditor, previousCpu, previousGpu;
        static string previousLog, rawId, id;
        static double until;
        static int lastFrame, retainThrough;
        static JObject settings;
        static readonly JArray timings = new JArray();
        static readonly JArray profilerTimings = new JArray();
        struct Interval { internal int frame, missing; internal double ms; }
        sealed class BenchmarkFrameSampler { }
        static readonly List<Interval> intervals = new List<Interval>(10000);
        static long lastSampleTicks;
        static int lastGameFrame;
        static readonly Dictionary<int, JObject> retained = new Dictionary<int, JObject>();
        static string lastResult;
        const int MaxRetainedFrames = 128;
        static BenchmarkOps()
        {
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += () => { if (active) Stop("domain_reload"); };
            EditorApplication.quitting += () => { if (active) Stop("editor_quit"); };
        }
        internal static JObject Start(JObject p)
        {
            if (active) throw new InvalidOperationException("A benchmark is already running.");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) throw new InvalidOperationException("Start a benchmark while the game is playing and unpaused.");
            if (UProfiler.enableBinaryLog) throw new InvalidOperationException("Another binary profiler recording is active; stop it first.");
            if (ProfilerDriver.GetConnectionIdentifier(ProfilerDriver.connectedProfiler) != "Editor") throw new InvalidOperationException("Select the local Editor profiler target first; settings capture cannot describe a remote player.");
            int seconds = DiagnosticCommon.Int(p, "duration_seconds", 60, 1, 600);
            settings = DiagnosticSceneOps.Settings();
            timings.Clear(); profilerTimings.Clear(); intervals.Clear(); retained.Clear();
            Directory.CreateDirectory(DiagnosticCommon.Root);
            id = "benchmark_" + Guid.NewGuid().ToString("N") + ".json";
            rawId = Path.ChangeExtension(id, ".raw");
            previousEnabled = UProfiler.enabled; previousLog = UProfiler.logFile; previousEditor = ProfilerDriver.profileEditor;
            previousCpu = UProfiler.GetAreaEnabled(ProfilerArea.CPU); previousGpu = UProfiler.GetAreaEnabled(ProfilerArea.GPU);
            lastFrame = ProfilerDriver.lastFrameIndex; retainThrough = -1;
            until = EditorApplication.timeSinceStartup + seconds;
            active = true;
            ProfilerDriver.profileEditor = false;
            UProfiler.SetAreaEnabled(ProfilerArea.CPU, true);
            UProfiler.SetAreaEnabled(ProfilerArea.GPU, true);
            UProfiler.logFile = DiagnosticCommon.Artifact(rawId);
            UProfiler.enableBinaryLog = true;
            UProfiler.enabled = true;
            lastSampleTicks = 0; lastGameFrame = -1;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!InstallSampler(ref loop)) { Stop("sampler_install_failed"); throw new InvalidOperationException("Player loop has no PostLateUpdate stage."); }
            PlayerLoop.SetPlayerLoop(loop);
            return Status();
        }
        static bool InstallSampler(ref PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
            {
                var list = loop.subSystemList?.ToList() ?? new List<PlayerLoopSystem>();
                list.Add(new PlayerLoopSystem { type = typeof(BenchmarkFrameSampler), updateDelegate = SampleFrame });
                loop.subSystemList = list.ToArray(); return true;
            }
            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; i++) if (InstallSampler(ref loop.subSystemList[i])) return true;
            return false;
        }
        static void RemoveSampler(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return;
            loop.subSystemList = loop.subSystemList.Where(s => s.type != typeof(BenchmarkFrameSampler)).ToArray();
            for (int i = 0; i < loop.subSystemList.Length; i++) RemoveSampler(ref loop.subSystemList[i]);
        }
        static void SampleFrame()
        {
            if (!active || Time.frameCount == lastGameFrame) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastSampleTicks != 0) intervals.Add(new Interval { frame = Time.frameCount,
                missing = Math.Max(0, Time.frameCount - lastGameFrame - 1),
                ms = (now - lastSampleTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency });
            lastSampleTicks = now; lastGameFrame = Time.frameCount;
        }
        static void Update()
        {
            if (!active) return;
            try
            {
                if (!EditorApplication.isPlaying || EditorApplication.isPaused) Stop("playback_stopped_or_paused");
                else if (EditorApplication.timeSinceStartup >= until) Stop("duration_complete");
            }
            catch (Exception ex) { Stop("capture_error: " + ex.GetBaseException().Message); }
        }
        static void ReadRecordedFrames()
        {
                int newest = ProfilerDriver.lastFrameIndex;
                int first = Math.Max(lastFrame + 1, ProfilerDriver.firstFrameIndex);
                for (int f = first; f <= newest; f++)
                {
                    using var view = ProfilerDriver.GetRawFrameDataView(f, 0);
                    if (!view.valid) continue;
                    double ms = view.frameTimeMs;
                    if (double.IsNaN(ms) || ms <= 0) continue;
                    bool hitch = ms > 1000.0 / 90;
                    profilerTimings.Add(new JObject { ["profiler_frame"] = f, ["cpu_frame_ms"] = ms,
                        ["gpu_frame_ms"] = view.frameGpuTimeMs > 0 ? new JValue(view.frameGpuTimeMs) : JValue.CreateNull(),
                        ["miss_90"] = hitch, ["miss_60"] = ms > 1000.0 / 60,
                        ["missing_frames_before"] = Math.Max(0, f - lastFrame - 1) });
                    if (hitch) retainThrough = f + 3;
                    if (hitch || f <= retainThrough)
                        for (int neighbor = Math.Max(ProfilerDriver.firstFrameIndex, f - 3); neighbor <= f; neighbor++)
                            if (!retained.ContainsKey(neighbor) && retained.Count < MaxRetainedFrames) retained[neighbor] = Frame(neighbor, 20);
                    lastFrame = f;
                }
        }
        internal static JObject Frame(int frame, int limit)
        {
            var threads = new JArray();
            for (int thread = 0; thread < 128; thread++)
            {
                using var view = ProfilerDriver.GetRawFrameDataView(frame, thread);
                if (!view.valid) break;
                var samples = new JArray();
                var stack = new Stack<(int index, int end)>();
                int count = Math.Min(view.sampleCount, 100000);
                for (int i = 0; i < count; i++)
                {
                    while (stack.Count > 0 && stack.Peek().end < i) stack.Pop();
                    int parent = stack.Count > 0 ? stack.Peek().index : -1;
                    samples.Add(new JObject { ["sample"] = i, ["parent"] = parent, ["name"] = view.GetSampleName(i),
                        ["inclusive_ms"] = view.GetSampleTimeMs(i), ["start_ms"] = view.GetSampleStartTimeMs(i), ["depth"] = stack.Count });
                    int children = view.GetSampleChildrenCountRecursive(i);
                    if (children > 0) stack.Push((i, i + children));
                }
                var childCosts = samples.Where(s => (int)s["parent"] >= 0).GroupBy(s => (int)s["parent"]).ToDictionary(g => g.Key, g => g.Sum(s => (double)s["inclusive_ms"]));
                foreach (var s in samples) { childCosts.TryGetValue((int)s["sample"], out var cost); s["self_ms"] = Math.Max(0, (double)s["inclusive_ms"] - cost); }
                threads.Add(new JObject { ["thread"] = view.threadName, ["frame_ms"] = view.frameTimeMs,
                    ["fixed_step_samples"] = samples.Count(s => (string)s["name"] == "FixedUpdate"),
                    ["samples"] = new JArray(samples.OrderByDescending(s => (double)s["self_ms"]).Take(limit)),
                    ["sample_count"] = view.sampleCount, ["truncated"] = view.sampleCount > limit,
                    ["gpu_frame_ms"] = view.frameGpuTimeMs > 0 ? new JValue(view.frameGpuTimeMs) : JValue.CreateNull() });
            }
            return new JObject { ["frame"] = frame, ["threads"] = threads,
                ["status"] = threads.Count > 0 ? "available" : "unavailable",
                ["gpu_passes"] = GpuPasses(frame, limit),
                ["interpretation"] = "CPU inclusive samples overlap; rank by self_ms. Wait markers are CPU waits, not GPU durations. Raw .raw recording retains full profiler samples." };
        }
        internal static JObject GpuPasses(int frame, int limit)
        {
            // GPU timings are separate hierarchy columns, never CPU marker durations or draw counts.
            try
            {
                var stateType = typeof(ProfilerDriver).Assembly.GetType("UnityEditorInternal.GpuProfilingStatisticsAvailabilityStates", true);
                var method = typeof(ProfilerDriver).GetMethod("GetGpuStatisticsAvailabilityState", DiagnosticCommon.Flags);
                int state = Convert.ToInt32(method.Invoke(null, new object[] { frame }));
                int gathered = Convert.ToInt32(Enum.Parse(stateType, "Gathered"));
                if ((state & gathered) == 0) return DiagnosticCommon.Unavailable("GPU samples not gathered: " + Enum.ToObject(stateType, state));
                int total = (int)typeof(HierarchyFrameDataView).GetField("columnTotalGpuTime", DiagnosticCommon.Flags).GetValue(null);
                int self = (int)typeof(HierarchyFrameDataView).GetField("columnSelfGpuTime", DiagnosticCommon.Flags).GetValue(null);
                using var view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0, HierarchyFrameDataView.ViewModes.Default, total, false);
                if (!view.valid) return DiagnosticCommon.Unavailable("No GPU hierarchy in this captured frame.");
                var queue = new Queue<int>(); var children = new List<int>(); var rows = new JArray();
                queue.Enqueue(view.GetRootItemID()); int visited = 0;
                while (queue.Count > 0 && visited++ < 100000)
                {
                    int item = queue.Dequeue(); double ms = view.GetItemColumnDataAsDouble(item, total);
                    if (ms > 0) rows.Add(new JObject { ["name"] = view.GetItemName(item), ["inclusive_gpu_ms"] = ms, ["self_gpu_ms"] = view.GetItemColumnDataAsDouble(item, self) });
                    children.Clear(); view.GetItemChildren(item, children); foreach (int child in children) queue.Enqueue(child);
                }
                return new JObject { ["status"] = "available", ["passes"] = new JArray(rows.OrderByDescending(r => (double)r["self_gpu_ms"]).Take(limit)),
                    ["truncated"] = rows.Count > limit || queue.Count > 0, ["basis"] = "Unity GPU profiler hierarchy; nested inclusive timings overlap." };
            }
            catch (Exception ex) { return DiagnosticCommon.Unavailable("GPU adapter unavailable: " + ex.GetBaseException().Message); }
        }
        internal static JObject Summary(JArray frames)
        {
            var sorted = frames.Select(f => (double)(f["frame_ms"] ?? f["cpu_frame_ms"])).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return DiagnosticCommon.Unavailable("No completed CPU frames were recorded.");
            double Percentile(double q) => sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1)];
            return new JObject { ["frames"] = sorted.Length, ["p50_ms"] = Percentile(.5), ["p95_ms"] = Percentile(.95), ["p99_ms"] = Percentile(.99),
                ["worst_ms"] = sorted.LastOrDefault(), ["misses_90_fps"] = sorted.Count(v => v > 1000.0 / 90), ["misses_60_fps"] = sorted.Count(v => v > 1000.0 / 60),
                ["sampled_duration_ms"] = sorted.Sum(), ["missing_frames"] = frames.Sum(f => (int?)f["missing_frames_before"] ?? 0),
                ["basis"] = frames[0]["frame_ms"] != null ? "Monotonic intervals between game PostLateUpdate samples, including waits/editor stalls; first partial interval excluded. Not presented FPS or CPU self time." : "Recorded CPU frame durations including waits; editor overhead applies." };
        }
        internal static JObject Stop(string reason = "requested")
        {
            if (!active) return Status();
            active = false;
            var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveSampler(ref loop); PlayerLoop.SetPlayerLoop(loop);
            UProfiler.enableBinaryLog = false; UProfiler.enabled = false;
            foreach (var sample in intervals) timings.Add(new JObject { ["game_frame"] = sample.frame, ["frame_ms"] = sample.ms,
                ["miss_90"] = sample.ms > 1000.0 / 90, ["miss_60"] = sample.ms > 1000.0 / 60, ["missing_frames_before"] = sample.missing });
            // Binary logging need not populate the live history. Analyze only after recording,
            // avoiding per-frame JSON/stack analysis overhead in the benchmark itself.
            try
            {
                lastFrame = ProfilerDriver.lastFrameIndex;
                if (File.Exists(DiagnosticCommon.Artifact(rawId)))
                {
                    if (new FileInfo(DiagnosticCommon.Artifact(rawId)).Length > 512L * 1024 * 1024)
                        throw new InvalidOperationException("Raw recording exceeds 512 MiB; use benchmark_analyze with allow_large_recording=true after budgeting RAM. Raw timing data is preserved.");
                    ProfilerDriver.LoadProfile(DiagnosticCommon.Artifact(rawId), true);
                    ReadRecordedFrames();
                }
            }
            catch (Exception ex) { reason += "; analysis_error: " + ex.GetBaseException().Message; }
            finally
            {
                UProfiler.logFile = previousLog; UProfiler.enabled = previousEnabled; ProfilerDriver.profileEditor = previousEditor;
                UProfiler.SetAreaEnabled(ProfilerArea.CPU, previousCpu); UProfiler.SetAreaEnabled(ProfilerArea.GPU, previousGpu);
            }
            var finalSettings = DiagnosticSceneOps.Settings();
            var report = new JObject { ["schema"] = 1, ["reason"] = reason, ["settings"] = settings,
                ["settings_at_end"] = finalSettings, ["settings_changed_during_run"] = DiagnosticSceneOps.CompareSettings(settings, finalSettings),
                ["summary"] = Summary(timings), ["frames"] = timings.DeepClone(), ["profiler_frames"] = profilerTimings.DeepClone(),
                ["profiler_context_note"] = "Profiler history can retain only a tail of the raw recording. game_frame IDs are not profiler_frame IDs; do not equate them. Full raw samples remain on disk.",
                ["hitch_context"] = new JArray(retained.OrderBy(k => k.Key).Select(k => k.Value)),
                ["hitch_context_limit_reached"] = retained.Count >= MaxRetainedFrames, ["raw_profiler_file"] = rawId,
                ["storm_at_end"] = DiagnosticSceneOps.Storm() };
            File.WriteAllText(DiagnosticCommon.Artifact(id), report.ToString(Newtonsoft.Json.Formatting.None));
            lastResult = id;
            return new JObject { ["active"] = false, ["artifact"] = id, ["raw_profiler_file"] = rawId, ["summary"] = report["summary"] };
        }
        internal static JObject Analyze(JObject p)
        {
            if (active || UProfiler.enableBinaryLog) throw new InvalidOperationException("Stop recording before analyzing a saved benchmark.");
            string artifact = (string)p["artifact"]; var report = DiagnosticCommon.Read(artifact);
            string raw = DiagnosticCommon.Artifact((string)report["raw_profiler_file"]);
            if (new FileInfo(raw).Length > 512L * 1024 * 1024 && (bool?)p["allow_large_recording"] != true)
                throw new InvalidOperationException("Set allow_large_recording=true only after budgeting RAM for this recording.");
            bool enabled = UProfiler.enabled; UProfiler.enabled = false;
            try
            {
                profilerTimings.Clear(); retained.Clear(); retainThrough = -1; lastFrame = ProfilerDriver.lastFrameIndex;
                ProfilerDriver.LoadProfile(raw, true); ReadRecordedFrames();
                report["profiler_frames"] = profilerTimings.DeepClone();
                report["hitch_context"] = new JArray(retained.OrderBy(k => k.Key).Select(k => k.Value));
                report["hitch_context_limit_reached"] = retained.Count >= MaxRetainedFrames;
                report["analysis_completed"] = true;
                File.WriteAllText(DiagnosticCommon.Artifact(artifact), report.ToString(Newtonsoft.Json.Formatting.None));
                return new JObject { ["artifact"] = artifact, ["summary"] = report["summary"] };
            }
            finally { UProfiler.enabled = enabled; }
        }
        internal static JObject Status() => new JObject { ["active"] = active, ["artifact"] = active ? id : lastResult,
            ["frames_recorded"] = active ? intervals.Count : timings.Count, ["analysis"] = active ? "Full-run frame intervals and binary recording active; profiler stacks analyzed after stop." : "Finished",
            ["remaining_seconds"] = active ? Math.Max(0, until - EditorApplication.timeSinceStartup) : 0 };
    }
}

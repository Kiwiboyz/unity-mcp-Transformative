using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools.Profiler
{
    // Optional package adapter: no compile-time dependency on Memory Profiler internals.
    // Missing/changed reader contracts fail explicitly instead of comparing file sizes.
    internal sealed class SnapshotAnalysis : IDisposable
    {
        readonly object reader;
        readonly object snapshot;
        readonly Type dataType;
        internal SnapshotAnalysis(string path, bool allowLarge = false)
        {
            path = ValidatePath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Snapshot not found", path);
            if (!allowLarge && new FileInfo(path).Length > 512L * 1024 * 1024)
                throw new InvalidOperationException("Snapshot exceeds 512 MiB. Parsing may stall Unity and require several times the file size in RAM. Use manage_diagnostics with allow_large_snapshot=true after budgeting memory.");
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.MemoryProfiler.Editor")
                ?? Assembly.Load("Unity.MemoryProfiler.Editor");
            reader = Activator.CreateInstance(assembly.GetType("Unity.MemoryProfiler.Editor.Format.QueriedSnapshot.FileReader", true), true);
            try
            {
                var error = reader.GetType().GetMethod("Open").Invoke(reader, new object[] { path });
                if (error.ToString() != "Success") throw new InvalidDataException("Snapshot reader: " + error);
                snapshot = Activator.CreateInstance(assembly.GetType("Unity.MemoryProfiler.Editor.CachedSnapshot", true), new[] { reader });
                var processor = (IEnumerator)snapshot.GetType().GetMethod("PostProcess").Invoke(snapshot, null);
                try { while (processor.MoveNext()) { } } finally { (processor as IDisposable)?.Dispose(); }
                dataType = assembly.GetType("Unity.MemoryProfiler.Editor.ObjectData", true);
            }
            catch { Dispose(); throw; }
        }
        internal static string ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\") || path.StartsWith("//")) throw new ArgumentException("A local project .snap path is required.");
            path = Path.GetFullPath(path);
            var roots = new[] { Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..")), Path.GetFullPath(UnityEngine.Application.temporaryCachePath) };
            if (!path.EndsWith(".snap", StringComparison.OrdinalIgnoreCase) || !roots.Any(r => path.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Snapshot must be a .snap inside this project or its Unity temporary cache.");
            for (var entry = new FileInfo(path) as FileSystemInfo; entry != null; entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
                if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Snapshot paths cannot traverse links or junctions.");
            return path;
        }
        static object M(object o, string n) => DiagnosticCommon.Member(o, n) ?? throw new NotSupportedException("Memory Profiler adapter missing member " + n);
        static readonly Dictionary<Type, Func<object, long, object>> indexReaders = new Dictionary<Type, Func<object, long, object>>();
        static object[] Values(object array)
        {
            // Memory Profiler 1.1.9's DynamicArray enumerator omits the final element.
            // Read its checked ref-return indexer; reflection Invoke cannot box a ref return.
            Type type = array.GetType();
            if (!indexReaders.TryGetValue(type, out var get))
            {
                var accessor = type.GetProperty("Item").GetMethod;
                var element = accessor.ReturnType.GetElementType();
                if (element == null || !type.IsValueType) throw new NotSupportedException("Unknown snapshot array indexer contract.");
                var method = new DynamicMethod("ReadSnapshotArrayItem", typeof(object), new[] { typeof(object), typeof(long) }, typeof(SnapshotAnalysis).Module, true);
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox, type); il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, accessor); il.Emit(OpCodes.Ldobj, element); il.Emit(OpCodes.Box, element); il.Emit(OpCodes.Ret);
                get = (Func<object, long, object>)method.CreateDelegate(typeof(Func<object, long, object>)); indexReaders[type] = get;
            }
            int count = checked(Convert.ToInt32(M(array, "Count"))); var result = new object[count];
            for (int i = 0; i < count; i++) result[i] = get(array, i);
            return result;
        }
        internal JArray Objects()
        {
            var rows = new JArray();
            var natives = M(snapshot, "NativeObjects");
            var names = (string[])M(natives, "ObjectName");
            var sizes = Values(M(natives, "Size"));
            var types = Values(M(natives, "NativeTypeArrayIndex"));
            var typeNames = (string[])M(M(snapshot, "NativeTypes"), "TypeName");
            for (int i = 0; i < names.Length; i++) rows.Add(new JObject {
                ["kind"] = "native", ["index"] = i, ["name"] = names[i], ["type"] = typeNames[Convert.ToInt32(types[i])],
                ["tracked_bytes"] = Convert.ToInt64(sizes[i]), ["size_basis"] = "snapshot native tracked allocation; not measured physical residency" });
            var managed = Values(M(M(snapshot, "CrawledData"), "m_ManagedObjects"));
            var managedTypes = (string[])M(M(snapshot, "TypeDescriptions"), "TypeDescriptionName");
            for (int i = 0; i < managed.Length; i++)
            {
                int type = Convert.ToInt32(M(managed[i], "ITypeDescription"));
                if (type < 0) continue;
                rows.Add(new JObject { ["kind"] = "managed", ["index"] = i, ["name"] = managedTypes[type], ["type"] = managedTypes[type],
                    ["tracked_bytes"] = Convert.ToInt64(M(managed[i], "Size")), ["size_basis"] = "crawled managed object size including arrays; not heap reserved bytes" });
            }
            return rows;
        }
        internal JObject References(string kind, long index, int depth, int limit)
        {
            if (kind != "native" && kind != "managed") throw new ArgumentException("kind must be native or managed.");
            long objectCount = kind == "native" ? ((string[])M(M(snapshot, "NativeObjects"), "ObjectName")).LongLength : Convert.ToInt64(M(M(M(snapshot, "CrawledData"), "m_ManagedObjects"), "Count"));
            if (index < 0 || index >= objectCount) throw new ArgumentOutOfRangeException(nameof(index), "Object index is outside this snapshot.");
            var make = dataType.GetMethod(kind == "native" ? "FromNativeObjectIndex" : "FromManagedObjectIndex");
            var root = make.Invoke(null, new[] { snapshot, (object)index });
            var queue = new Queue<(object obj, JArray chain)>();
            var seen = new HashSet<string>();
            var result = new JArray();
            queue.Enqueue((root, new JArray()));
            int visited = 0; bool edgesTruncated = false;
            while (queue.Count > 0 && visited++ < limit)
            {
                var item = queue.Dequeue();
                var source = dataType.GetMethod("GetSourceLink").Invoke(item.obj, new[] { snapshot });
                var key = M(source, "Id") + ":" + M(source, "Index");
                var chain = (JArray)item.chain.DeepClone();
                chain.Add(new JObject { ["source"] = key, ["name"] = (string)dataType.GetMethod("GenerateObjectName").Invoke(item.obj, new[] { snapshot }) });
                if (!seen.Add(key)) { result.Add(new JObject { ["chain"] = chain, ["termination"] = "cycle_or_shared_reference" }); continue; }
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(dataType));
                var method = dataType.GetMethod("GetAllReferencingObjects");
                var args = new object[] { snapshot, list, null, Enum.ToObject(method.GetParameters()[3].ParameterType, 0) };
                method.Invoke(item.obj, args);
                list = (IList)args[1];
                if (list.Count == 0 || chain.Count >= depth) result.Add(new JObject { ["chain"] = chain, ["termination"] = list.Count == 0 ? "no_incoming_reference_in_capture" : "depth_limit" });
                else
                {
                    int space = Math.Max(0, limit - queue.Count);
                    edgesTruncated |= list.Count > space;
                    foreach (var owner in list.Cast<object>().Take(space)) queue.Enqueue((owner, chain));
                }
            }
            return new JObject { ["paths_to_owners"] = result, ["truncated"] = queue.Count > 0 || edgesTruncated,
                ["interpretation"] = "Incoming references in captured graph; a leaf is not proof of a GC root. Cycles and depth limits are reported." };
        }
        internal static JObject Compare(string a, string b, JObject p)
        {
            JArray left, right;
            using (var snapshotA = new SnapshotAnalysis(a, (bool?)p["allow_large_snapshot"] == true)) left = snapshotA.Objects();
            using (var snapshotB = new SnapshotAnalysis(b, (bool?)p["allow_large_snapshot"] == true)) right = snapshotB.Objects();
            string Key(JToken r) => r["kind"] + "|" + r["type"] + "|" + r["name"];
            var groupsA = left.GroupBy(Key).ToDictionary(g => g.Key, g => new { bytes = g.Sum(r => (long)r["tracked_bytes"]), count = g.Count() });
            var groupsB = right.GroupBy(Key).ToDictionary(g => g.Key, g => new { bytes = g.Sum(r => (long)r["tracked_bytes"]), count = g.Count() });
            var rows = new JArray(groupsA.Keys.Union(groupsB.Keys).Select(key => {
                groupsA.TryGetValue(key, out var x); groupsB.TryGetValue(key, out var y);
                return new JObject { ["group"] = key, ["before_bytes"] = x?.bytes ?? 0, ["after_bytes"] = y?.bytes ?? 0,
                    ["delta_bytes"] = (y?.bytes ?? 0) - (x?.bytes ?? 0), ["count_delta"] = (y?.count ?? 0) - (x?.count ?? 0) };
            }).OrderByDescending(r => (long)r["delta_bytes"]));
            return new JObject { ["comparison"] = "object_type_and_name_groups", ["group_count"] = rows.Count,
                ["groups"] = DiagnosticCommon.Page(rows, p), ["artifact"] = DiagnosticCommon.Save("memory_diff", new JObject { ["groups"] = rows }),
                ["note"] = "Groups aggregate duplicate names. Instance IDs/addresses are not used for cross-session identity. Native and managed tracked sizes do not establish physical/GPU residency." };
        }
        public void Dispose() { (snapshot as IDisposable)?.Dispose(); (reader as IDisposable)?.Dispose(); }
    }
}

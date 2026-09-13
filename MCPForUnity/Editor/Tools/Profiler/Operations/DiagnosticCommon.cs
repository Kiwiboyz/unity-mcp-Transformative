using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Profiler
{
    internal static class DiagnosticCommon
    {
        internal static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        internal static JObject Unavailable(string reason) => new JObject { ["status"] = "unavailable", ["value"] = null, ["reason"] = reason };
        internal static object Member(object value, string name)
        {
            if (value == null) return null;
            var type = value as Type ?? value.GetType();
            var field = type.GetField(name, Flags);
            if (field != null) return field.GetValue(value is Type ? null : value);
            var prop = type.GetProperty(name, Flags);
            return prop != null && prop.GetIndexParameters().Length == 0 ? prop.GetValue(value is Type ? null : value) : null;
        }
        internal static int Int(JObject p, string key, int fallback, int min, int max)
        {
            if (p[key] != null && p[key].Type != JTokenType.Integer) throw new ArgumentException(key + " must be an integer.");
            int result = p[key] == null ? fallback : p[key].Value<int>();
            if (result < min || result > max) throw new ArgumentException($"{key} must be between {min} and {max}.");
            return result;
        }
        internal static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/McpDiagnostics"));
        internal static string Artifact(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.'))
                throw new ArgumentException("Artifact must be a filename returned by this tool.");
            var path = Path.GetFullPath(Path.Combine(Root, id));
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid artifact path.");
            return path;
        }
        internal static string Save(string prefix, JToken value)
        {
            Directory.CreateDirectory(Root);
            string id = prefix + "_" + Guid.NewGuid().ToString("N") + ".json";
            File.WriteAllText(Artifact(id), value.ToString(Formatting.None));
            return id;
        }
        internal static JObject Read(string id) => JObject.Parse(File.ReadAllText(Artifact(id)));
        internal static JArray Page(IEnumerable<JToken> values, JObject p)
        {
            int offset = Int(p, "offset", 0, 0, int.MaxValue);
            int limit = Int(p, "limit", 50, 1, 500);
            return new JArray(values.Skip(offset).Take(limit));
        }
        internal static string Hierarchy(Transform t) => t.parent ? Hierarchy(t.parent) + "/" + t.name : t.name;
        internal static JObject Identity(UnityEngine.Object obj) => obj == null ? null : new JObject {
            ["name"] = obj.name, ["type"] = obj.GetType().FullName,
            ["path"] = AssetDatabase.GetAssetPath(obj), ["instance_id"] = obj.GetInstanceID()
        };
    }
}

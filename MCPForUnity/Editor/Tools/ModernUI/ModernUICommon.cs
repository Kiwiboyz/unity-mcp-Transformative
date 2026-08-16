using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;

namespace MCPForUnity.Editor.Tools.ModernUI
{
    internal static class ModernUICommon
    {
        internal const string NamespacePrefix = "Michsky.MUIP";
        internal const string DefaultPackagePath = "Assets/Modern UI Pack";
        internal const string ManifestLabel = "McpForUnityModernUIManifest";
        internal const string SpecVersion = "1";

        private static List<Type> cachedModernUITypes;
        private static readonly Dictionary<Type, string> ScriptPaths = new();
        private static readonly Dictionary<string, List<string>> PrefabIndexes = new(StringComparer.Ordinal);

        private static readonly string[] ExpectedTypes =
        {
            "ButtonManager", "CustomDropdown", "DropdownMultiSelect", "CustomInputField",
            "ModalWindowManager", "WindowManager", "ListView", "NotificationManager",
            "ProgressBar", "PBFilled", "SliderManager", "RadialSlider", "RangeSlider",
            "SwitchManager", "CustomToggle", "HorizontalSelector", "ContextMenuManager",
            "TooltipManager", "IconManager", "AnimatedIconHandler", "PieChart", "UIManager"
        };

        internal static string ResolvePackagePath(string requestedPath = null)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                var sanitized = AssetPathUtility.SanitizeAssetPath(requestedPath);
                if (sanitized != null && AssetDatabase.IsValidFolder(sanitized)) return sanitized;
            }
            if (AssetDatabase.IsValidFolder(DefaultPackagePath)) return DefaultPackagePath;
            foreach (var guid in AssetDatabase.FindAssets("Read Me t:TextAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.IndexOf("Modern UI Pack", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(directory) && AssetDatabase.IsValidFolder(directory)) return directory;
            }
            return null;
        }

        internal static string GetVersion(string packagePath)
        {
            if (string.IsNullOrEmpty(packagePath)) return null;
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(packagePath + "/Read Me.txt");
            if (asset == null) return null;
            var firstLine = asset.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(firstLine)) return null;
            var marker = firstLine.IndexOf("v", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 ? firstLine.Substring(marker + 1).Trim() : firstLine.Trim();
        }

        internal static List<Type> GetModernUIComponentTypes()
        {
            return GetModernUITypes().Where(type => typeof(Component).IsAssignableFrom(type)).ToList();
        }

        internal static List<Type> GetModernUITypes()
        {
            if (cachedModernUITypes == null)
                cachedModernUITypes = TypeCache.GetTypesDerivedFrom<UnityEngine.Object>()
                    .Where(type => type != null && !type.IsAbstract && string.Equals(type.Namespace, NamespacePrefix, StringComparison.Ordinal))
                    .OrderBy(type => type.Name, StringComparer.Ordinal).ToList();
            return new List<Type>(cachedModernUITypes);
        }

        internal static void InvalidateCache()
        {
            cachedModernUITypes = null;
            ScriptPaths.Clear();
            PrefabIndexes.Clear();
        }

        internal static Type FindComponentType(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;
            return GetModernUIComponentTypes().FirstOrDefault(type =>
                string.Equals(type.FullName, requested, StringComparison.Ordinal) || string.Equals(type.Name, requested, StringComparison.Ordinal));
        }

        internal static bool IsProductionType(Type type)
        {
            var script = GetScriptPath(type);
            return string.IsNullOrEmpty(script) || script.IndexOf("/Demo/", StringComparison.OrdinalIgnoreCase) < 0;
        }

        internal static string GetScriptPath(Type type)
        {
            if (type == null) return null;
            if (ScriptPaths.TryGetValue(type, out var cached)) return cached;
            foreach (var guid in AssetDatabase.FindAssets(type.Name + " t:Script"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script != null && script.GetClass() == type) { ScriptPaths[type] = path; return path; }
            }
            ScriptPaths[type] = null;
            return null;
        }

        internal static object BuildStatus(string packagePath)
        {
            var types = GetModernUITypes();
            var names = new HashSet<string>(types.Select(type => type.Name), StringComparer.Ordinal);
            var missing = ExpectedTypes.Where(expected => !names.Contains(expected)).ToArray();
            return new
            {
                packagePath,
                installed = !string.IsNullOrEmpty(packagePath),
                version = GetVersion(packagePath),
                expectedVersion = "5.5.16",
                componentCount = GetModernUIComponentTypes().Count,
                discoveredTypeCount = types.Count,
                fingerprintMatches = missing.Length == 0,
                missingExpectedTypes = missing,
                mutationSupported = !string.IsNullOrEmpty(packagePath) && missing.Length == 0
            };
        }

        internal static object BuildCatalog(string packagePath, string family, bool includeExamples, int page, int pageSize)
        {
            var types = GetModernUIComponentTypes()
                .Where(type => string.IsNullOrEmpty(family) || type.Name.IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(type => includeExamples || IsProductionType(type)).ToList();
            var prefabs = DiscoverPrefabs(packagePath, family, includeExamples);
            page = Math.Max(1, page);
            pageSize = Mathf.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);
            return new
            {
                version = GetVersion(packagePath), page, pageSize,
                componentTotal = types.Count, prefabTotal = prefabs.Count,
                components = types.Skip((page - 1) * pageSize).Take(pageSize).Select(DescribeType).ToArray(),
                prefabs = prefabs.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
                nextPage = page * pageSize < Math.Max(types.Count, prefabs.Count) ? page + 1 : (int?)null
            };
        }

        internal static object DescribeType(Type type)
        {
            return new
            {
                name = type.Name,
                fullName = type.FullName,
                category = GetCategory(type),
                support = IsProductionType(type) ? "supported" : "example_only",
                scriptPath = GetScriptPath(type),
                fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(field => !field.IsNotSerialized)
                    .Select(field => new { name = field.Name, type = GetFriendlyTypeName(field.FieldType), writable = !field.IsInitOnly }).ToArray(),
                refreshMethods = ModernUIAdapterRegistry.GetRefreshMethods(type).ToArray(),
                eventFields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(field => typeof(UnityEventBase).IsAssignableFrom(field.FieldType)).Select(field => field.Name).ToArray()
            };
        }

        internal static string GetCategory(Type type)
        {
            var path = GetScriptPath(type);
            var index = path?.IndexOf("/Scripts/", StringComparison.OrdinalIgnoreCase) ?? -1;
            if (index < 0) return "Other";
            var relative = path.Substring(index + "/Scripts/".Length);
            var slash = relative.IndexOf('/');
            return slash > 0 ? relative.Substring(0, slash) : "Other";
        }

        internal static List<object> DiscoverPrefabs(string packagePath, string family, bool includeExamples)
        {
            var root = packagePath + "/Prefabs";
            if (string.IsNullOrEmpty(packagePath) || !AssetDatabase.IsValidFolder(root)) return new List<object>();
            if (!PrefabIndexes.TryGetValue(root, out var allPrefabs))
            {
                allPrefabs = AssetDatabase.FindAssets("t:Prefab", new[] { root }).Select(AssetDatabase.GUIDToAssetPath)
                    .OrderBy(path => path, StringComparer.Ordinal).ToList();
                PrefabIndexes[root] = allPrefabs;
            }
            return allPrefabs
                .Where(path => string.IsNullOrEmpty(family) || path.IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(path => includeExamples || path.IndexOf("/Demo/", StringComparison.OrdinalIgnoreCase) < 0)
                .Select(path => (object)new
                {
                    path,
                    category = PrefabCategory(root, path),
                    support = path.IndexOf("/Demo/", StringComparison.OrdinalIgnoreCase) >= 0 ? "example_only" : "supported",
                    componentTypes = AssetDatabase.LoadAssetAtPath<GameObject>(path)?.GetComponentsInChildren<Component>(true)
                        .Where(component => component != null && component.GetType().Namespace == NamespacePrefix)
                        .Select(component => component.GetType().Name).Distinct().OrderBy(name => name).ToArray() ?? Array.Empty<string>()
                }).ToList();
        }

        private static string PrefabCategory(string root, string path)
        {
            var relative = path.Substring(Math.Min(path.Length, root.Length)).TrimStart('/');
            var slash = relative.IndexOf('/');
            return slash > 0 ? relative.Substring(0, slash) : "Other";
        }

        internal static string GetFriendlyTypeName(Type type)
        {
            if (type == null) return "unknown";
            if (!type.IsGenericType) return type.Name;
            return type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(",", type.GetGenericArguments().Select(GetFriendlyTypeName)) + ">";
        }

        internal static string GetAbsoluteAssetPath(string assetPath)
        {
            assetPath = AssetPathUtility.SanitizeAssetPath(assetPath);
            if (assetPath == null || !AssetPathUtility.IsValidAssetPath(assetPath)) return null;
            var root = Directory.GetParent(Application.dataPath)?.FullName;
            return Path.Combine(root ?? string.Empty, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        internal static string GetManifestPath(string ownerPath)
        {
            var directory = Path.GetDirectoryName(ownerPath)?.Replace('\\', '/');
            return directory + "/" + Path.GetFileNameWithoutExtension(ownerPath) + ".modernui.json";
        }

        internal static string GlobalId(UnityEngine.Object target) => target == null ? null : GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();

        internal static GameObject ResolveGlobalObject(string globalId)
        {
            return string.IsNullOrWhiteSpace(globalId) || !GlobalObjectId.TryParse(globalId, out var parsed)
                ? null : GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as GameObject;
        }

        internal static string StableHash(string value)
        {
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Select(item => item.ToString("x2")));
        }
    }

    internal sealed class ModernUIAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(path => path != null &&
                    (path.StartsWith(ModernUICommon.DefaultPackagePath, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))))
                ModernUICommon.InvalidateCache();
        }
    }

    internal static class ModernUIAdapterRegistry
    {
        private static readonly Dictionary<string, string[]> RefreshMethods = new(StringComparer.Ordinal)
        {
            ["ButtonManager"] = new[] { "UpdateUI" }, ["CustomDropdown"] = new[] { "SetupDropdown", "UpdateItemLayout" },
            ["DropdownMultiSelect"] = new[] { "SetupDropdown", "UpdateItemLayout" }, ["HorizontalSelector"] = new[] { "SetupSelector", "UpdateUI", "UpdateIndicators", "UpdateContentLayout" },
            ["WindowManager"] = new[] { "InitializeWindows" }, ["ListView"] = new[] { "InitializeItems" },
            ["ModalWindowManager"] = new[] { "UpdateUI" }, ["NotificationManager"] = new[] { "UpdateUI" },
            ["ProgressBar"] = new[] { "UpdateUI", "InitializeEvents" }, ["PBFilled"] = new[] { "UpdateUI" },
            ["SliderManager"] = new[] { "UpdateUI" }, ["RadialSlider"] = new[] { "UpdateUI" },
            ["SwitchManager"] = new[] { "UpdateUI" }, ["IconManager"] = new[] { "UpdateElement" }, ["PieChart"] = new[] { "UpdateIndicators" }
        };

        internal static IEnumerable<string> GetRefreshMethods(Type type) => type != null && RefreshMethods.TryGetValue(type.Name, out var methods) ? methods : Array.Empty<string>();

        internal static List<string> Refresh(Component component)
        {
            var called = new List<string>();
            if (component == null) return called;
            foreach (var name in GetRefreshMethods(component.GetType()))
            {
                var method = component.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method == null) continue;
                try { method.Invoke(component, null); called.Add(name); }
                catch (Exception ex) { McpLog.Warn("[ModernUI] Safe refresh '" + component.GetType().Name + "." + name + "' failed: " + ex.Message); }
            }
            return called;
        }
    }

    internal sealed class ModernUIManifest
    {
        public string specVersion = ModernUICommon.SpecVersion;
        public string ownerAssetGuid;
        public string ownerPath;
        public string styleSourceGlobalId;
        public string styleSourcePath;
        public string styleSourceRequestedName;
        public string styleSourceActualName;
        public List<ModernUIManifestNode> nodes = new();

        internal static ModernUIManifest Load(string ownerPath)
        {
            var absolute = ModernUICommon.GetAbsoluteAssetPath(ModernUICommon.GetManifestPath(ownerPath));
            if (string.IsNullOrEmpty(absolute) || !File.Exists(absolute)) return null;
            try { return JsonConvert.DeserializeObject<ModernUIManifest>(File.ReadAllText(absolute)); }
            catch (Exception ex) { McpLog.Warn("[ModernUI] Could not read manifest: " + ex.Message); return null; }
        }

        internal void Save(string ownerPath)
        {
            var manifestPath = ModernUICommon.GetManifestPath(ownerPath);
            var absolute = ModernUICommon.GetAbsoluteAssetPath(manifestPath);
            if (string.IsNullOrEmpty(absolute)) throw new InvalidOperationException("Manifest path must be under Assets.");
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            this.ownerPath = AssetPathUtility.SanitizeAssetPath(ownerPath);
            ownerAssetGuid = AssetDatabase.AssetPathToGUID(this.ownerPath);
            File.WriteAllText(absolute, JsonConvert.SerializeObject(this, Formatting.Indented));
            AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceUpdate);
            var manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(manifestPath);
            if (manifestAsset != null) AssetDatabase.SetLabels(manifestAsset, new[] { ModernUICommon.ManifestLabel });
        }

        internal ModernUIManifestNode FindNode(string id) => nodes?.FirstOrDefault(node => node.id == id);
    }

    internal sealed class ModernUIManifestNode
    {
        public string id;
        public string globalId;
        public string hierarchyPath;
        public string componentType;
        public string baselineHash;
        public string specHash;
        public bool adopted;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Equipment
{
    /// <summary>
    /// Reflection-only integration boundary for Project Storm equipment. This code deliberately
    /// ships in the MCP package and never references the game's assembly at compile time.
    /// </summary>
    internal static class EquipmentCommon
    {
        internal const int SpecVersion = 1;
        internal const string PropsRoot = "Assets/Prefabs/Props";
        internal const string DefaultOutputRoot = "Assets/Generated/McpEquipment";
        internal const string JournalDirectory = "Library/MCPForUnity/EquipmentOperations";
        private static readonly string[] KnownTypeNames =
        {
            "EquipmentFamily", "EquipmentModule", "EquipmentDatabase", "Equipment",
            "MountableEquipment", "HandHeldEquipment", "EQDopplerRadar", "EQCargo",
            "PerformanceObject", "SaveManager", "SlotMountPoint", "VehicleController"
        };

        private static Dictionary<string, Type> types;

        internal static void InvalidateCache() => types = null;

        internal static Type FindType(string shortName)
        {
            if (types == null)
            {
                types = TypeCache.GetTypesDerivedFrom<UnityEngine.Object>()
                    .Where(type => type != null && !type.IsAbstract && KnownTypeNames.Contains(type.Name, StringComparer.Ordinal))
                    .GroupBy(type => type.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.OrderBy(type => type.FullName, StringComparer.Ordinal).First(), StringComparer.Ordinal);
            }

            types.TryGetValue(shortName, out Type result);
            return result;
        }

        internal static bool IsInstalled(string typeName) => FindType(typeName) != null;

        internal static JObject Status()
        {
            var detected = new JObject();
            foreach (string typeName in KnownTypeNames) detected[typeName] = IsInstalled(typeName);
            return new JObject
            {
                ["spec_version"] = SpecVersion,
                ["props_root"] = PropsRoot,
                ["equipment_system_detected"] = IsInstalled("EquipmentFamily") && IsInstalled("EquipmentModule") && IsInstalled("EquipmentDatabase"),
                ["detected_types"] = detected,
                ["integration"] = "reflection_and_serialized_properties",
                ["project_files_required"] = false
            };
        }

        internal static IEnumerable<GameObject> DiscoverProps(string query)
        {
            if (!AssetDatabase.IsValidFolder(PropsRoot)) return Enumerable.Empty<GameObject>();
            return AssetDatabase.FindAssets("t:Prefab", new[] { PropsRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => string.IsNullOrWhiteSpace(query) || path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .Where(prefab => prefab != null)
                .OrderBy(prefab => AssetDatabase.GetAssetPath(prefab), StringComparer.Ordinal)
                .ToArray();
        }

        internal static JObject DescribePrefab(GameObject prefab)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(path);
                Renderer[] renderers = contents.GetComponentsInChildren<Renderer>(true);
                Collider[] colliders = contents.GetComponentsInChildren<Collider>(true);
                Bounds? bounds = BoundsOf(renderers);
                return new JObject
                {
                    ["asset_path"] = path,
                    ["asset_guid"] = AssetDatabase.AssetPathToGUID(path),
                    ["name"] = prefab.name,
                    ["approved_for_composition"] = IsApprovedProp(path),
                    ["bounds"] = bounds.HasValue ? new JObject
                    {
                        ["center"] = JObject.FromObject(bounds.Value.center),
                        ["size"] = JObject.FromObject(bounds.Value.size)
                    } : JValue.CreateNull(),
                    ["renderer_count"] = renderers.Length,
                    ["collider_count"] = colliders.Length,
                    ["material_paths"] = new JArray(renderers.SelectMany(renderer => renderer.sharedMaterials)
                        .Where(material => material != null).Distinct().Select(AssetDatabase.GetAssetPath).Where(value => !string.IsNullOrEmpty(value))),
                    ["dependencies"] = new JArray(AssetDatabase.GetDependencies(path, true).Where(dependency => dependency.StartsWith("Assets/", StringComparison.Ordinal)).OrderBy(dependency => dependency, StringComparer.Ordinal)),
                    ["anchors"] = new JArray(contents.GetComponentsInChildren<Transform>(true)
                        .Where(transform => IsAnchorName(transform.name))
                        .Select(transform => new JObject
                        {
                            ["name"] = transform.name,
                            ["path"] = TransformPath(transform),
                            ["local_position"] = JObject.FromObject(transform.localPosition),
                            ["local_rotation"] = JObject.FromObject(transform.localEulerAngles)
                        }))
                };
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        internal static JObject DescribeAsset(UnityEngine.Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            var fields = new JObject();
            if (asset != null)
            {
                var serialized = new SerializedObject(asset);
                foreach (SerializedProperty property in SerializedProperties(serialized))
                {
                    if (property.propertyPath == "m_Script") continue;
                    fields[property.propertyPath] = DescribeProperty(property);
                }
            }

            return new JObject
            {
                ["name"] = asset?.name,
                ["type"] = asset?.GetType().FullName,
                ["asset_path"] = string.IsNullOrEmpty(path) ? null : path,
                ["asset_guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                ["revision"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.GetAssetDependencyHash(path).ToString(),
                ["fields"] = fields
            };
        }

        internal static UnityEngine.Object ResolveAsset(JToken target)
        {
            if (target == null) return null;
            if (target.Type == JTokenType.String)
            {
                string value = target.Value<string>();
                UnityEngine.Object pathAsset = AssetDatabase.LoadMainAssetAtPath(value);
                if (pathAsset != null) return pathAsset;
                foreach (string guid in AssetDatabase.FindAssets(value))
                {
                    UnityEngine.Object candidate = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                    if (candidate != null) return candidate;
                }
                return null;
            }

            if (target is not JObject reference) return null;
            string path = reference.Value<string>("asset_path") ?? reference.Value<string>("assetPath");
            if (!string.IsNullOrWhiteSpace(path)) return AssetDatabase.LoadMainAssetAtPath(path);
            string globalId = reference.Value<string>("global_id") ?? reference.Value<string>("globalId");
            return GlobalObjectId.TryParse(globalId, out GlobalObjectId parsed) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) : null;
        }

        internal static Component FindComponent(GameObject root, string typeName)
        {
            Type type = FindType(typeName);
            return root == null || type == null ? null : root.GetComponentsInChildren(type, true).OfType<Component>().FirstOrDefault();
        }

        internal static bool IsApprovedProp(string path) => !string.IsNullOrWhiteSpace(path) && path.Replace('\\', '/').StartsWith(PropsRoot + "/", StringComparison.Ordinal) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        internal static bool IsApprovedMaterial(string path) => !string.IsNullOrWhiteSpace(path) && path.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal) && AssetDatabase.LoadAssetAtPath<Material>(path) != null;

        internal static bool IsSafeAssetPath(string path) => !string.IsNullOrWhiteSpace(path) && path.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal) && !path.Replace('\\', '/').Split('/').Contains("..");

        internal static bool IsSafeId(string value) => !string.IsNullOrWhiteSpace(value) && value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-');

        internal static bool SetProperty(UnityEngine.Object target, string propertyPath, JToken value, out string error)
        {
            error = null;
            if (target == null) { error = "Target is missing."; return false; }
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyPath);
            if (property == null) { error = "Serialized property '" + propertyPath + "' was not found on " + target.GetType().Name + "."; return false; }
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.String: property.stringValue = value?.Value<string>() ?? string.Empty; break;
                    case SerializedPropertyType.Integer: property.intValue = value?.Value<int>() ?? 0; break;
                    case SerializedPropertyType.Boolean: property.boolValue = value?.Value<bool>() ?? false; break;
                    case SerializedPropertyType.Float: property.floatValue = value?.Value<float>() ?? 0f; break;
                    case SerializedPropertyType.Enum:
                        string enumName = value?.Value<string>();
                        int index = Array.IndexOf(property.enumNames, enumName);
                        if (index < 0) { error = "'" + enumName + "' is not a valid value for " + propertyPath + "."; return false; }
                        property.enumValueIndex = index;
                        break;
                    case SerializedPropertyType.ObjectReference:
                        property.objectReferenceValue = ResolveAsset(value);
                        if (value != null && value.Type != JTokenType.Null && property.objectReferenceValue == null) { error = "Object reference for '" + propertyPath + "' could not be resolved."; return false; }
                        break;
                    case SerializedPropertyType.Vector3: property.vector3Value = value.ToObject<Vector3>(); break;
                    default: error = "Property '" + propertyPath + "' has unsupported type " + property.propertyType + "."; return false;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(target);
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        internal static bool AppendReference(UnityEngine.Object target, string propertyPath, UnityEngine.Object value, out string error)
        {
            error = null;
            var serialized = new SerializedObject(target);
            SerializedProperty array = serialized.FindProperty(propertyPath);
            if (array == null || !array.isArray) { error = "Reference list '" + propertyPath + "' is unavailable on " + target.GetType().Name + "."; return false; }
            for (int index = 0; index < array.arraySize; index++) if (array.GetArrayElementAtIndex(index).objectReferenceValue == value) return true;
            array.InsertArrayElementAtIndex(array.arraySize);
            array.GetArrayElementAtIndex(array.arraySize - 1).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
            return true;
        }

        internal static bool RemoveReference(UnityEngine.Object target, string propertyPath, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty array = serialized.FindProperty(propertyPath);
            if (array == null || !array.isArray) return false;
            for (int index = array.arraySize - 1; index >= 0; index--)
            {
                if (array.GetArrayElementAtIndex(index).objectReferenceValue == value) array.DeleteArrayElementAtIndex(index);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
            return true;
        }

        internal static IEnumerable<SerializedProperty> SerializedProperties(SerializedObject serialized)
        {
            SerializedProperty iterator = serialized.GetIterator();
            if (!iterator.NextVisible(true)) yield break;
            do { yield return iterator.Copy(); } while (iterator.NextVisible(false));
        }

        internal static string AbsoluteAssetPath(string assetPath) => Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty, assetPath).Replace('\\', '/');

        private static Bounds? BoundsOf(IEnumerable<Renderer> renderers)
        {
            Bounds bounds = default;
            bool found = false;
            foreach (Renderer renderer in renderers.Where(renderer => renderer != null))
            {
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return found ? bounds : null;
        }

        private static bool IsAnchorName(string name) => !string.IsNullOrWhiteSpace(name) && (name.IndexOf("anchor", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("socket", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("mount", StringComparison.OrdinalIgnoreCase) >= 0);
        private static string TransformPath(Transform transform) => transform.parent == null ? transform.name : TransformPath(transform.parent) + "/" + transform.name;

        private static JToken DescribeProperty(SerializedProperty property)
        {
            return property.propertyType switch
            {
                SerializedPropertyType.String => property.stringValue,
                SerializedPropertyType.Integer => property.intValue,
                SerializedPropertyType.Boolean => property.boolValue,
                SerializedPropertyType.Float => property.floatValue,
                SerializedPropertyType.Enum => property.enumValueIndex >= 0 && property.enumValueIndex < property.enumNames.Length ? property.enumNames[property.enumValueIndex] : property.enumValueIndex,
                SerializedPropertyType.ObjectReference => property.objectReferenceValue == null ? JValue.CreateNull() : new JObject { ["name"] = property.objectReferenceValue.name, ["asset_path"] = AssetDatabase.GetAssetPath(property.objectReferenceValue) },
                SerializedPropertyType.Vector3 => JObject.FromObject(property.vector3Value),
                _ => property.propertyType.ToString()
            };
        }
    }
}

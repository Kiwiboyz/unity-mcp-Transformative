using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.ModernUI
{
    internal sealed class ModernUIObjectMatch
    {
        internal GameObject gameObject;
        internal float score;
        internal string reason;

        internal object ToResponse() => new
        {
            name = gameObject != null ? gameObject.name : null,
            path = gameObject != null ? GetHierarchyPath(gameObject.transform) : null,
            globalId = ModernUICommon.GlobalId(gameObject),
            score = Math.Round(score, 3),
            reason,
            components = gameObject == null ? Array.Empty<string>() : gameObject.GetComponents<Component>()
                .Where(component => component != null).Select(component => component.GetType().Name).ToArray()
        };

        internal static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return null;
            var names = new Stack<string>();
            while (transform != null) { names.Push(transform.name); transform = transform.parent; }
            return string.Join("/", names);
        }
    }

    internal static class ModernUIObjectResolver
    {
        internal static List<ModernUIObjectMatch> Find(string query, string expectedComponent = null, GameObject near = null, bool includeInactive = true)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<ModernUIObjectMatch>();
            var expected = ModernUICommon.FindComponentType(expectedComponent);
            var candidates = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => go != null && go.scene.IsValid() && (includeInactive || go.activeInHierarchy))
                .Where(go => !EditorUtility.IsPersistent(go))
                .Select(go => Score(go, query, expected, near))
                .Where(match => match.score > 0.42f)
                .OrderByDescending(match => match.score)
                .ThenBy(match => ModernUIObjectMatch.GetHierarchyPath(match.gameObject.transform), StringComparer.Ordinal)
                .Take(3).ToList();
            return candidates;
        }

        internal static ModernUIObjectMatch ResolveClear(string query, string expectedComponent, GameObject near, out List<ModernUIObjectMatch> candidates)
        {
            candidates = Find(query, expectedComponent, near);
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1 && candidates[0].score >= 0.72f) return candidates[0];
            if (candidates.Count > 1 && candidates[0].score >= 0.72f && candidates[0].score - candidates[1].score >= 0.12f) return candidates[0];
            return null;
        }

        private static ModernUIObjectMatch Score(GameObject candidate, string query, Type expected, GameObject near)
        {
            var normalizedQuery = Normalize(query);
            var normalizedName = Normalize(candidate.name);
            var exact = normalizedName == normalizedQuery;
            var distance = Levenshtein(normalizedQuery, normalizedName);
            var nameScore = exact ? 1f : Mathf.Clamp01(1f - (float)distance / Math.Max(normalizedQuery.Length, normalizedName.Length));
            if (normalizedName.Contains(normalizedQuery) || normalizedQuery.Contains(normalizedName)) nameScore = Math.Max(nameScore, 0.82f);
            var score = nameScore * 0.72f;
            var reasons = new List<string> { exact ? "exact normalized name" : "name similarity" };
            if (expected != null && candidate.GetComponent(expected) != null) { score += 0.18f; reasons.Add("matching component"); }
            if (near != null && candidate.scene == near.scene) { score += 0.04f; reasons.Add("same scene"); }
            if (near != null && candidate.transform.parent == near.transform.parent) { score += 0.06f; reasons.Add("same container"); }
            if (candidate.activeInHierarchy) score += 0.01f;
            return new ModernUIObjectMatch { gameObject = candidate, score = Mathf.Clamp01(score), reason = string.Join(", ", reasons) };
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return cleaned;
        }

        private static int Levenshtein(string left, string right)
        {
            var matrix = new int[left.Length + 1, right.Length + 1];
            for (var i = 0; i <= left.Length; i++) matrix[i, 0] = i;
            for (var j = 0; j <= right.Length; j++) matrix[0, j] = j;
            for (var i = 1; i <= left.Length; i++)
                for (var j = 1; j <= right.Length; j++)
                    matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            return matrix[left.Length, right.Length];
        }
    }

    internal static class ModernUISerialization
    {
        private static readonly HashSet<string> ForbiddenRoots = new(StringComparer.Ordinal) { "m_Script", "m_GameObject", "m_Enabled" };

        internal static List<string> ApplyProperties(UnityEngine.Object component, JObject values)
        {
            var errors = new List<string>();
            if (component == null || values == null) return errors;
            var serialized = new SerializedObject(component);
            foreach (var property in values.Properties())
            {
                if (ForbiddenRoots.Contains(property.Name) || property.Name.StartsWith("m_PersistentCalls", StringComparison.Ordinal))
                {
                    errors.Add("Property '" + property.Name + "' is not writable through Modern UI automation.");
                    continue;
                }
                var target = serialized.FindProperty(property.Name);
                if (target == null) { errors.Add("Unknown serialized property '" + property.Name + "' on " + component.GetType().Name + "."); continue; }
                if (!TrySet(target, property.Value, out var error)) errors.Add(property.Name + ": " + error);
            }
            if (errors.Count == 0) serialized.ApplyModifiedPropertiesWithoutUndo();
            return errors;
        }

        internal static bool TrySet(SerializedProperty property, JToken value, out string error)
        {
            error = null;
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: property.intValue = value.Value<int>(); return true;
                    case SerializedPropertyType.Boolean: property.boolValue = value.Value<bool>(); return true;
                    case SerializedPropertyType.Float: property.floatValue = value.Value<float>(); return true;
                    case SerializedPropertyType.String: property.stringValue = value.Value<string>() ?? string.Empty; return true;
                    case SerializedPropertyType.Enum:
                        if (value.Type == JTokenType.String)
                        {
                            var index = Array.FindIndex(property.enumNames, item => string.Equals(item, value.Value<string>(), StringComparison.OrdinalIgnoreCase));
                            if (index < 0) { error = "Unknown enum value '" + value.Value<string>() + "'."; return false; }
                            property.enumValueIndex = index;
                        }
                        else property.enumValueIndex = value.Value<int>();
                        return true;
                    case SerializedPropertyType.Color: property.colorValue = ParseColor(value); return true;
                    case SerializedPropertyType.Vector2: property.vector2Value = ParseVector2(value); return true;
                    case SerializedPropertyType.Vector3: property.vector3Value = ParseVector3(value); return true;
                    case SerializedPropertyType.Vector4: property.vector4Value = ParseVector4(value); return true;
                    case SerializedPropertyType.Rect: property.rectValue = ParseRect(value); return true;
                    case SerializedPropertyType.ObjectReference:
                        property.objectReferenceValue = ResolveObject(value);
                        if (property.objectReferenceValue == null && value.Type != JTokenType.Null) { error = "Object reference could not be resolved."; return false; }
                        return true;
                    case SerializedPropertyType.Generic:
                        if (property.isArray && value is JArray array)
                        {
                            property.arraySize = array.Count;
                            for (var i = 0; i < array.Count; i++) if (!TrySet(property.GetArrayElementAtIndex(i), array[i], out error)) return false;
                            return true;
                        }
                        if (value is JObject obj)
                        {
                            foreach (var child in obj.Properties())
                            {
                                var childProperty = property.FindPropertyRelative(child.Name);
                                if (childProperty == null) { error = "Unknown nested property '" + child.Name + "'."; return false; }
                                if (!TrySet(childProperty, child.Value, out error)) return false;
                            }
                            return true;
                        }
                        error = "Expected an object or array."; return false;
                    default: error = "Unsupported serialized type '" + property.propertyType + "'."; return false;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal static void ApplyLayout(RectTransform target, JObject layout, List<string> errors)
        {
            if (target == null || layout == null) return;
            try
            {
                if (layout["anchor_min"] != null) target.anchorMin = ParseVector2(layout["anchor_min"]);
                if (layout["anchor_max"] != null) target.anchorMax = ParseVector2(layout["anchor_max"]);
                if (layout["pivot"] != null) target.pivot = ParseVector2(layout["pivot"]);
                if (layout["offset_min"] != null) target.offsetMin = ParseVector2(layout["offset_min"]);
                if (layout["offset_max"] != null) target.offsetMax = ParseVector2(layout["offset_max"]);
                if (layout["size_delta"] != null) target.sizeDelta = ParseVector2(layout["size_delta"]);
                if (layout["anchored_position"] != null) target.anchoredPosition = ParseVector2(layout["anchored_position"]);
                if (layout["sibling_index"] != null) target.SetSiblingIndex(Math.Max(0, layout.Value<int>("sibling_index")));
            }
            catch (Exception ex) { errors.Add("layout: " + ex.Message); }
        }

        internal static void CopyStyle(Component target, Component donor, JObject inherit, List<string> errors)
        {
            if (target == null || donor == null || target.GetType() != donor.GetType()) { errors.Add("Style donor must have the same Modern UI component type."); return; }
            var targetObject = new SerializedObject(target);
            var donorObject = new SerializedObject(donor);
            var iterator = donorObject.GetIterator();
            var copyAll = inherit == null || inherit.Value<bool?>("all") != false;
            var copied = false;
            while (iterator.NextVisible(true))
            {
                if (iterator.propertyPath == "m_Script" || iterator.propertyPath.StartsWith("m_PersistentCalls", StringComparison.Ordinal)) continue;
                if (!IsPresentationProperty(iterator.propertyPath, copyAll, inherit)) continue;
                var destination = targetObject.FindProperty(iterator.propertyPath);
                if (destination == null || destination.propertyType != iterator.propertyType) continue;
                targetObject.CopyFromSerializedProperty(iterator); copied = true;
            }
            if (copied) targetObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool IsPresentationProperty(string path, bool copyAll, JObject inherit)
        {
            if (path.IndexOf("on", StringComparison.OrdinalIgnoreCase) == 0 || path.IndexOf("event", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (copyAll) return path.IndexOf("item", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("data", StringComparison.OrdinalIgnoreCase) < 0;
            var normalized = path.ToLowerInvariant();
            return (inherit.Value<bool?>("colors") == true && normalized.Contains("color")) ||
                (inherit.Value<bool?>("fonts") == true && (normalized.Contains("font") || normalized.Contains("textsize"))) ||
                (inherit.Value<bool?>("animation") == true && (normalized.Contains("transition") || normalized.Contains("animation"))) ||
                (inherit.Value<bool?>("content") == true && normalized.Contains("custom"));
        }

        private static UnityEngine.Object ResolveObject(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            var path = value.Type == JTokenType.String ? value.Value<string>() : value.Value<string>("asset_path");
            if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            var globalId = value.Type == JTokenType.Object ? value.Value<string>("global_id") : null;
            if (!string.IsNullOrWhiteSpace(globalId) && GlobalObjectId.TryParse(globalId, out var parsed)) return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed);
            return null;
        }

        private static Vector2 ParseVector2(JToken token) => new(token.Value<float>("x"), token.Value<float>("y"));
        private static Vector3 ParseVector3(JToken token) => new(token.Value<float>("x"), token.Value<float>("y"), token.Value<float>("z"));
        private static Vector4 ParseVector4(JToken token) => new(token.Value<float>("x"), token.Value<float>("y"), token.Value<float>("z"), token.Value<float>("w"));
        private static Rect ParseRect(JToken token) => new(token.Value<float>("x"), token.Value<float>("y"), token.Value<float>("width"), token.Value<float>("height"));
        private static Color ParseColor(JToken token)
        {
            if (token.Type == JTokenType.String && ColorUtility.TryParseHtmlString(token.Value<string>(), out var color)) return color;
            return new Color(token.Value<float>("r"), token.Value<float>("g"), token.Value<float>("b"), token.Value<float?>("a") ?? 1f);
        }
    }
}

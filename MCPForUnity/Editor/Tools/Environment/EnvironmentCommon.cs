using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Tools.Graphics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Environment
{
    /// <summary>Reflection-only boundary for HDRP, Expanse, and project weather integrations.</summary>
    internal static class EnvironmentCommon
    {
        internal const int SpecVersion = 1;
        internal const string GoalDirectory = "Library/MCPForUnity/EnvironmentGoals";

        private static readonly HashSet<string> KnownTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ExpanseSettings", "GlobalSettings", "AtmosphereLayer", "CelestialBody", "FogParticleSystem",
            "SkyboxLayerManager", "LightControl", "CreativeAtmosphere", "CreativeCloudVolume", "CreativeFog",
            "CreativeSun", "CreativeMoon", "ProceduralCloudVolume", "ProceduralCloudPlane", "TextureCloudPlane",
            "SkyboxLayer", "DateTimeController", "CloudLayerInterpolator", "TextureInterpolator",
            "ProceduralNoiseGenerator", "CloudPainter", "CloudPresetMapper", "LensFlareAttenuator",
            "WeatherManager", "StoryDrivenWeatherManager", "StormDrivenWeatherManager", "StormFront",
            "StormCenter", "LightningEffectController", "TornadoWeatherBinding", "SurroundingPlayerParticleController",
            "RaindropEffect", "ScreenRaindropEffectController", "Wiper", "WeatherState", "WeatherPatternPreset", "StormVisualProfile",
            "WeatherFeatureDefinition", "ShieldPatchFeatureDefinition", "RingBandFeatureDefinition",
            "LineBandFeatureDefinition", "VortexCoreFeatureDefinition", "StormOverlayBinder", "TimeController"
        };

        private static List<Type> cachedTypes;
        private static readonly Dictionary<string, Dictionary<string, string>> SemanticControls = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["CreativeAtmosphere"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["daytime_color"] = "m_daytimeColor", ["sunset_color"] = "m_sunsetColor", ["ozone"] = "m_ozone",
                ["thickness"] = "m_thickness", ["smogginess"] = "m_smogginess", ["smog_saturation"] = "m_smogSaturation"
            },
            ["CreativeCloudVolume"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["coverage"] = "m_coverage", ["density"] = "m_density", ["shadowing"] = "m_shadowing",
                ["ambient"] = "m_ambient", ["multiple_scattering"] = "m_multipleScattering", ["silver_lining"] = "m_silverLining",
                ["silver_lining_spread"] = "m_silverLiningSpread", ["raininess"] = "m_raininess", ["swirl"] = "m_swirl", ["wind"] = "m_wind", ["quality"] = "m_quality"
            },
            ["CreativeFog"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["color"] = "m_color", ["visibility_distance"] = "m_visibilityDistance", ["radius"] = "m_radius",
                ["thickness"] = "m_thickness", ["smog"] = "m_smog", ["glare"] = "m_glare", ["receive_density_particles"] = "m_receiveDensityParticles"
            },
            ["CreativeSun"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["size"] = "m_size", ["light_brightness"] = "m_lightBrightness", ["light_tint"] = "m_lightTint",
                ["disc_brightness"] = "m_discBrightness", ["disc_tint"] = "m_discTint"
            }
        };

        internal static List<Type> GetSupportedTypes()
        {
            if (cachedTypes == null)
            {
                cachedTypes = TypeCache.GetTypesDerivedFrom<UnityEngine.Object>()
                    .Where(type => type != null && !type.IsAbstract && IsSupportedType(type))
                    .OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
            }
            return new List<Type>(cachedTypes);
        }

        internal static void InvalidateCache() => cachedTypes = null;

        internal static bool IsSupportedType(Type type)
        {
            if (type == null) return false;
            if (type.Name == "TornadoCloudVolume") return false;
            if (IsVisualEffect(type)) return true;
            if (KnownTypeNames.Contains(type.Name)) return true;
            if (type.FullName != null && type.FullName.IndexOf("Expanse", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (typeof(Component).IsAssignableFrom(type) || typeof(ScriptableObject).IsAssignableFrom(type))) return true;
            return GraphicsHelpers.VolumeComponentType != null && GraphicsHelpers.VolumeComponentType.IsAssignableFrom(type) &&
                   type.Namespace != null && type.Namespace.StartsWith("UnityEngine.Rendering.HighDefinition", StringComparison.Ordinal);
        }

        internal static bool IsVisualEffect(Type type) => type?.FullName == "UnityEngine.VFX.VisualEffect";

        internal static bool IsVfxChange(UnityEngine.Object target, JObject change) => target != null && IsVisualEffect(target.GetType()) && (change?.Value<string>("semantic") ?? string.Empty).StartsWith("exposed_", StringComparison.Ordinal);

        internal static bool WriteVfxParameter(UnityEngine.Object target, JObject change, out JToken before, out string error)
        {
            before = null;
            error = null;
            var property = change.Value<string>("vfx_property");
            var semantic = change.Value<string>("semantic");
            if (string.IsNullOrWhiteSpace(property)) { error = "Exposed VFX changes require vfx_property."; return false; }
            var suffix = semantic.Substring("exposed_".Length);
            var typeName = suffix switch { "float" => "Single", "int" => "Int32", "bool" => "Boolean", "vector2" => "Vector2", "vector3" => "Vector3", "vector4" => "Vector4" , _ => null };
            if (typeName == null) { error = "Unsupported exposed VFX semantic '" + semantic + "'."; return false; }
            var parameterType = typeName == "Single" ? typeof(float) : typeName == "Int32" ? typeof(int) : typeName == "Boolean" ? typeof(bool) : typeName == "Vector2" ? typeof(Vector2) : typeName == "Vector3" ? typeof(Vector3) : typeof(Vector4);
            var has = target.GetType().GetMethod("Has" + suffix.Substring(0, 1).ToUpperInvariant() + suffix.Substring(1), new[] { typeof(string) });
            var get = target.GetType().GetMethod("Get" + suffix.Substring(0, 1).ToUpperInvariant() + suffix.Substring(1), new[] { typeof(string) });
            var set = target.GetType().GetMethod("Set" + suffix.Substring(0, 1).ToUpperInvariant() + suffix.Substring(1), new[] { typeof(string), parameterType });
            if (has == null || get == null || set == null || !(has.Invoke(target, new object[] { property }) is bool exists) || !exists) { error = "VisualEffect does not expose a compatible parameter named '" + property + "'."; return false; }
            before = JToken.FromObject(get.Invoke(target, new object[] { property }));
            var value = change["value"]?.ToObject(parameterType);
            set.Invoke(target, new[] { (object)property, value });
            return true;
        }

        internal static bool ReadVfxParameter(UnityEngine.Object target, JObject change, out JToken value, out string error)
        {
            value = null;
            error = null;
            var property = change.Value<string>("vfx_property");
            var semantic = change.Value<string>("semantic") ?? string.Empty;
            if (!IsVfxChange(target, change) || string.IsNullOrWhiteSpace(property)) { error = "A supported exposed VFX semantic and vfx_property are required."; return false; }
            var suffix = semantic.Substring("exposed_".Length);
            var methodName = "Get" + suffix.Substring(0, 1).ToUpperInvariant() + suffix.Substring(1);
            var hasName = "Has" + suffix.Substring(0, 1).ToUpperInvariant() + suffix.Substring(1);
            var has = target.GetType().GetMethod(hasName, new[] { typeof(string) });
            var get = target.GetType().GetMethod(methodName, new[] { typeof(string) });
            if (has == null || get == null || !(has.Invoke(target, new object[] { property }) is bool exists) || !exists) { error = "VisualEffect does not expose a compatible parameter named '" + property + "'."; return false; }
            value = JToken.FromObject(get.Invoke(target, new object[] { property }));
            return true;
        }

        internal static string Family(Type type)
        {
            var name = type?.Name ?? string.Empty;
            if (IsVisualEffect(type)) return "vfx";
            if (GraphicsHelpers.VolumeProfileType != null && GraphicsHelpers.VolumeProfileType.IsAssignableFrom(type)) return "hdrp";
            if (name.IndexOf("Storm", StringComparison.OrdinalIgnoreCase) >= 0) return "storm";
            if (name.IndexOf("Weather", StringComparison.OrdinalIgnoreCase) >= 0) return "weather";
            if (name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Lightning", StringComparison.OrdinalIgnoreCase) >= 0) return "vfx";
            if (name.IndexOf("Expanse", StringComparison.OrdinalIgnoreCase) >= 0 || KnownTypeNames.Contains(name) && !name.StartsWith("Weather") && !name.StartsWith("Storm")) return "expanse";
            if (GraphicsHelpers.VolumeComponentType != null && GraphicsHelpers.VolumeComponentType.IsAssignableFrom(type)) return "hdrp";
            return "environment";
        }

        internal static JObject ObjectReference(UnityEngine.Object value)
        {
            if (value == null) return null;
            var path = AssetDatabase.GetAssetPath(value);
            return new JObject
            {
                ["name"] = value.name,
                ["type"] = value.GetType().FullName,
                ["global_id"] = GlobalObjectId.GetGlobalObjectIdSlow(value).ToString(),
                ["asset_path"] = string.IsNullOrEmpty(path) ? null : path
            };
        }

        internal static UnityEngine.Object SharedProfile(Component volume)
        {
            if (volume == null || GraphicsHelpers.VolumeType == null || !GraphicsHelpers.VolumeType.IsAssignableFrom(volume.GetType())) return null;
            var field = volume.GetType().GetField("sharedProfile", BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(volume) as UnityEngine.Object;
        }

        internal static IEnumerable<UnityEngine.Object> ProfileComponents(UnityEngine.Object profile)
        {
            if (profile == null) return Enumerable.Empty<UnityEngine.Object>();
            var property = profile.GetType().GetProperty("components", BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(profile) is System.Collections.IEnumerable values)
                return values.Cast<object>().OfType<UnityEngine.Object>().ToArray();
            var assetPath = AssetDatabase.GetAssetPath(profile);
            return string.IsNullOrWhiteSpace(assetPath) ? Enumerable.Empty<UnityEngine.Object>() : AssetDatabase.LoadAllAssetsAtPath(assetPath).Where(item => item != profile && IsSupportedType(item.GetType())).ToArray();
        }

        internal static IEnumerable<UnityEngine.Object> ReferencedObjects(UnityEngine.Object owner, FieldInfo field)
        {
            if (owner == null || field == null) return Enumerable.Empty<UnityEngine.Object>();
            var value = field.GetValue(owner);
            if (value is UnityEngine.Object direct) return direct == null ? Enumerable.Empty<UnityEngine.Object>() : new[] { direct };
            if (value is System.Collections.IEnumerable sequence && !(value is string)) return sequence.Cast<object>().OfType<UnityEngine.Object>().Where(item => item != null).ToArray();
            return Enumerable.Empty<UnityEngine.Object>();
        }

        internal static UnityEngine.Object ResolveObject(JToken reference, Type expected = null)
        {
            if (reference == null || reference.Type == JTokenType.Null) return null;
            if (reference.Type == JTokenType.String) reference = new JObject { ["asset_path"] = reference.Value<string>() };
            if (!(reference is JObject obj)) return null;
            var globalId = obj.Value<string>("global_id");
            if (!string.IsNullOrWhiteSpace(globalId) && GlobalObjectId.TryParse(globalId, out var parsed))
            {
                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed);
                if (resolved != null && (expected == null || expected.IsAssignableFrom(resolved.GetType()))) return resolved;
            }
            var assetPath = obj.Value<string>("asset_path");
            if (IsAssetPath(assetPath))
            {
                var resolved = expected == null ? AssetDatabase.LoadMainAssetAtPath(assetPath) : AssetDatabase.LoadAssetAtPath(assetPath, expected);
                if (resolved != null) return resolved;
            }
            var name = obj.Value<string>("name");
            var requestedType = obj.Value<string>("type");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var candidate = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                    .FirstOrDefault(item => item != null && item.name == name &&
                        (expected == null || expected.IsAssignableFrom(item.GetType())) &&
                        (string.IsNullOrEmpty(requestedType) || item.GetType().FullName == requestedType));
                if (candidate != null) return candidate;
            }
            return null;
        }

        internal static bool IsAssetPath(string path) => !string.IsNullOrWhiteSpace(path) && path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && path.IndexOf("..", StringComparison.Ordinal) < 0;

        internal static JObject Describe(Type type)
        {
            return new JObject
            {
                ["name"] = type.Name,
                ["full_name"] = type.FullName,
                ["family"] = Family(type),
                ["kind"] = typeof(Component).IsAssignableFrom(type) ? "component" : "asset",
                ["script_path"] = ScriptPath(type),
                ["semantic_controls"] = SemanticControlsJson(type),
                ["fields"] = new JArray(GetFields(type).Select(field => DescribeField(field, 2)))
            };
        }

        private static JObject DescribeField(FieldInfo field, int remainingDepth)
        {
            var result = new JObject { ["name"] = field.Name, ["semantic"] = SemanticName(field.Name), ["type"] = FriendlyName(field.FieldType), ["writable"] = !field.IsInitOnly };
            var nested = field.FieldType;
            if (remainingDepth > 0 && !typeof(UnityEngine.Object).IsAssignableFrom(nested) && !nested.IsPrimitive && !nested.IsEnum && nested != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(nested))
                result["fields"] = new JArray(GetFields(nested).Select(child => DescribeField(child, remainingDepth - 1)));
            return result;
        }

        internal static IReadOnlyDictionary<string, string> GetSemanticControls(Type type)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (type == null) return result;
            foreach (var field in GetFields(type)) result[SemanticName(field.Name)] = field.Name;
            if (SemanticControls.TryGetValue(type.Name, out var controls))
                foreach (var pair in controls) result[pair.Key] = pair.Value;
            return result;
        }

        private static JObject SemanticControlsJson(Type type)
        {
            var result = new JObject();
            foreach (var pair in GetSemanticControls(type)) result[pair.Key] = pair.Value;
            return result;
        }

        internal static string ResolveProperty(Type type, JObject change)
        {
            var semantic = change?.Value<string>("semantic");
            if (string.IsNullOrWhiteSpace(semantic)) return null;
            if (GetSemanticControls(type).TryGetValue(semantic, out var property)) return property;
            var segments = semantic.Split('.');
            if (segments.Length < 2 || !GetSemanticControls(type).TryGetValue(segments[0], out property)) return null;
            var currentType = GetFields(type).FirstOrDefault(field => field.Name == property)?.FieldType;
            for (var index = 1; index < segments.Length; index++)
            {
                var field = currentType == null ? null : GetFields(currentType).FirstOrDefault(candidate => SemanticName(candidate.Name) == segments[index]);
                if (field == null) return null;
                property += "." + field.Name;
                currentType = field.FieldType;
            }
            return property;
        }

        internal static string SemanticName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return fieldName;
            var source = fieldName.StartsWith("m_", StringComparison.Ordinal) ? fieldName.Substring(2) : fieldName;
            var builder = new StringBuilder();
            for (var index = 0; index < source.Length; index++)
            {
                var current = source[index];
                if (char.IsUpper(current) && index > 0 && (char.IsLower(source[index - 1]) || char.IsDigit(source[index - 1]))) builder.Append('_');
                builder.Append(char.ToLowerInvariant(current));
            }
            return builder.ToString();
        }

        internal static string SemanticPath(Type type, string propertyPath)
        {
            if (type == null || string.IsNullOrWhiteSpace(propertyPath)) return null;
            var currentType = type;
            var semantic = new List<string>();
            foreach (var segment in propertyPath.Split('.'))
            {
                var field = GetFields(currentType).FirstOrDefault(candidate => candidate.Name == segment);
                if (field == null) return null;
                semantic.Add(SemanticName(field.Name));
                currentType = field.FieldType;
            }
            return string.Join(".", semantic);
        }

        internal static IEnumerable<FieldInfo> GetFields(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsNotSerialized && !field.IsInitOnly && (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null))
            .OrderBy(field => field.Name, StringComparer.Ordinal);

        internal static string ScriptPath(Type type)
        {
            foreach (var guid in AssetDatabase.FindAssets(type.Name + " t:Script"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script != null && script.GetClass() == type) return path;
            }
            return null;
        }

        internal static JObject ReadSerialized(UnityEngine.Object target, int maxDepth = 2)
        {
            var result = new JObject();
            if (target == null) return result;
            var serialized = new SerializedObject(target);
            var iterator = serialized.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.depth > maxDepth || iterator.propertyPath == "m_Script") continue;
                result[iterator.propertyPath] = ReadProperty(iterator);
            }
            return result;
        }

        internal static JToken ReadProperty(SerializedProperty property)
        {
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                var array = new JArray();
                for (var index = 0; index < property.arraySize; index++) array.Add(ReadProperty(property.GetArrayElementAtIndex(index)));
                return array;
            }
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue;
                case SerializedPropertyType.Boolean: return property.boolValue;
                case SerializedPropertyType.Float: return property.floatValue;
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Color: return JObject.FromObject(property.colorValue);
                case SerializedPropertyType.ObjectReference: return ObjectReference(property.objectReferenceValue);
                case SerializedPropertyType.Enum: return property.enumNames.Length > property.enumValueIndex ? property.enumNames[property.enumValueIndex] : property.enumValueIndex;
                case SerializedPropertyType.Vector2: return JObject.FromObject(property.vector2Value);
                case SerializedPropertyType.Vector3: return JObject.FromObject(property.vector3Value);
                case SerializedPropertyType.Vector4: return JObject.FromObject(property.vector4Value);
                case SerializedPropertyType.Vector2Int: return JObject.FromObject(property.vector2IntValue);
                case SerializedPropertyType.Vector3Int: return JObject.FromObject(property.vector3IntValue);
                case SerializedPropertyType.Quaternion: return JObject.FromObject(property.quaternionValue);
                case SerializedPropertyType.Rect: return JObject.FromObject(property.rectValue);
                case SerializedPropertyType.Bounds: return JObject.FromObject(property.boundsValue);
                case SerializedPropertyType.AnimationCurve: return new JObject { ["keys"] = new JArray(property.animationCurveValue?.keys.Select(key => JObject.FromObject(key)) ?? Enumerable.Empty<JObject>()) };
                case SerializedPropertyType.Generic:
                    var fields = new JObject();
                    var copy = property.Copy();
                    var end = copy.GetEndProperty();
                    var enterChildren = true;
                    while (copy.NextVisible(enterChildren) && !SerializedProperty.EqualContents(copy, end))
                    {
                        enterChildren = false;
                        if (copy.depth == property.depth + 1) fields[copy.name] = ReadProperty(copy);
                    }
                    return fields;
                default: return property.propertyType.ToString();
            }
        }

        internal static bool WriteProperty(SerializedProperty property, JToken value, out string error)
        {
            error = null;
            try
            {
                if (property.isArray && property.propertyType != SerializedPropertyType.String)
                {
                    if (!(value is JArray values)) { error = "Array properties require a JSON array value."; return false; }
                    property.arraySize = values.Count;
                    for (var index = 0; index < values.Count; index++) if (!WriteProperty(property.GetArrayElementAtIndex(index), values[index], out error)) return false;
                    return true;
                }
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: property.intValue = value.Value<int>(); break;
                    case SerializedPropertyType.Boolean: property.boolValue = value.Value<bool>(); break;
                    case SerializedPropertyType.Float: property.floatValue = value.Value<float>(); break;
                    case SerializedPropertyType.String: property.stringValue = value.Value<string>() ?? string.Empty; break;
                    case SerializedPropertyType.ObjectReference: property.objectReferenceValue = ResolveObject(value); break;
                    case SerializedPropertyType.Enum:
                        var text = value.Type == JTokenType.String ? value.Value<string>() : null;
                        var index = text == null ? value.Value<int>() : Array.FindIndex(property.enumNames, item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase));
                        if (index < 0) { error = "Unknown enum value."; return false; } property.enumValueIndex = index; break;
                    case SerializedPropertyType.Vector2: property.vector2Value = value.ToObject<Vector2>(); break;
                    case SerializedPropertyType.Vector3: property.vector3Value = value.ToObject<Vector3>(); break;
                    case SerializedPropertyType.Vector4: property.vector4Value = value.ToObject<Vector4>(); break;
                    case SerializedPropertyType.Vector2Int: property.vector2IntValue = value.ToObject<Vector2Int>(); break;
                    case SerializedPropertyType.Vector3Int: property.vector3IntValue = value.ToObject<Vector3Int>(); break;
                    case SerializedPropertyType.Quaternion: property.quaternionValue = value.ToObject<Quaternion>(); break;
                    case SerializedPropertyType.Color: property.colorValue = value.ToObject<Color>(); break;
                    case SerializedPropertyType.AnimationCurve:
                        var keys = (value["keys"] as JArray)?.Select(token => token.ToObject<Keyframe>()).ToArray();
                        if (keys == null) { error = "AnimationCurve values require a keys array."; return false; }
                        property.animationCurveValue = new AnimationCurve(keys); break;
                    case SerializedPropertyType.Generic:
                        if (!(value is JObject fields)) { error = "Structured properties require a JSON object value."; return false; }
                        foreach (var field in fields.Properties())
                        {
                            var child = property.FindPropertyRelative(field.Name);
                            if (child == null) { error = "Unknown structured field '" + field.Name + "'."; return false; }
                            if (!WriteProperty(child, field.Value, out error)) return false;
                        }
                        break;
                    default: error = "Property type '" + property.propertyType + "' is not supported by the generic environment adapter."; return false;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal static string Hash(JToken value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value?.ToString(Formatting.None) ?? "null"));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        internal static string FriendlyName(Type type) => type == null ? "unknown" : type.IsGenericType ? type.Name.Split('`')[0] + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">" : type.Name;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.UI;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.ModernUI
{
    /// <summary>Creates, configures, repairs, and removes manifest-owned Modern UI Pack hierarchies.</summary>
    [McpForUnityTool("manage_modern_ui", AutoRegister = false, Group = "ui", Capability = ToolCapability.ProjectAutomation)]
    public static class ManageModernUI
    {
        public static object HandleCommand(JObject @params)
        {
            var action = (@params?["action"]?.Value<string>() ?? "apply").ToLowerInvariant();
            var packagePath = ModernUICommon.ResolvePackagePath(@params?["package_path"]?.Value<string>());
            try
            {
                return action switch
                {
                    "apply" => Apply(@params, packagePath),
                    "repair" => Repair(@params, packagePath),
                    "create_theme_copy" => CreateThemeCopy(@params, packagePath),
                    "remove_managed" => RemoveManaged(@params),
                    _ => new ErrorResponse("Unknown Modern UI management action: " + action)
                };
            }
            catch (Exception ex)
            {
                McpLog.Error("[ModernUI] Mutation failed: " + ex);
                return new ErrorResponse("Modern UI mutation failed: " + ex.Message);
            }
        }

        internal static object Preflight(JObject @params, string packagePath)
        {
            if (!TryReadSpec(@params, out var spec, out var error)) return new ErrorResponse(error);
            var errors = ValidateSpec(spec, packagePath);
            var normalized = new JObject(spec) { ["spec_version"] = ModernUICommon.SpecVersion };
            return new SuccessResponse(errors.Count == 0 ? "Modern UI specification is valid" : "Modern UI specification has errors", new
            {
                valid = errors.Count == 0,
                errors,
                normalizedSpec = normalized,
                package = ModernUICommon.BuildStatus(packagePath)
            });
        }

        private static object Apply(JObject @params, string packagePath)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return new ErrorResponse("Unity is compiling or reloading. Wait for the editor to become idle before mutating Modern UI.", new { code = "editor_busy" });
            if (!TryReadSpec(@params, out var spec, out var readError)) return new ErrorResponse(readError);
            var errors = ValidateSpec(spec, packagePath);
            if (errors.Count > 0) return new ErrorResponse("Modern UI specification failed preflight.", new { code = "preflight_failed", errors });

            var mode = (spec["mode"]?.Value<string>() ?? @params?["mode"]?.Value<string>() ?? "create").ToLowerInvariant();
            if (mode != "create" && mode != "merge" && mode != "replace_managed") return new ErrorResponse("mode must be create, merge, or replace_managed.");
            var targetToken = spec["target"] ?? @params?["target"];
            var prefabAssetPath = ResolvePrefabAssetPath(targetToken);
            GameObject prefabContents = null;
            var target = GetModernUICatalog.ResolveTarget(targetToken);
            if (target == null && !string.IsNullOrEmpty(prefabAssetPath))
            {
                prefabContents = PrefabUtility.LoadPrefabContents(prefabAssetPath);
                target = prefabContents;
            }
            var createdRoot = false;
            if (target == null)
            {
                target = CreateScreenRoot(spec);
                createdRoot = true;
            }

            var ownerPath = !string.IsNullOrEmpty(prefabAssetPath) ? prefabAssetPath : ResolveOwnerPath(spec, target);
            var manifest = !string.IsNullOrEmpty(ownerPath) ? ModernUIManifest.Load(ownerPath) : null;
            if (manifest == null) manifest = new ModernUIManifest();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Modern UI specification");
            var results = new List<object>();
            var mutationErrors = new List<string>();
            try
            {
                var conflictPolicy = (spec["conflict_policy"]?.Value<string>() ?? @params?["conflict_policy"]?.Value<string>() ?? "fail_on_conflict").ToLowerInvariant();
                if (conflictPolicy != "fail_on_conflict" && conflictPolicy != "prefer_spec" && conflictPolicy != "prefer_project")
                    return new ErrorResponse("conflict_policy must be fail_on_conflict, prefer_spec, or prefer_project.");
                foreach (var node in spec["nodes"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var result = ApplyNode(node, target.transform, manifest, mode, conflictPolicy, mutationErrors);
                    if (result != null) results.Add(result);
                }
                if (mode == "replace_managed") RemoveStaleManagedNodes(manifest, spec["nodes"] as JArray, results);
                if (mutationErrors.Count > 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    return new ErrorResponse("Modern UI apply rolled back.", new { code = "rolled_back", errors = mutationErrors });
                }
                if (!string.IsNullOrEmpty(prefabAssetPath)) PrefabUtility.SaveAsPrefabAsset(target, prefabAssetPath);
                if (!string.IsNullOrEmpty(ownerPath)) manifest.Save(ownerPath);
                EditorUtility.SetDirty(target);
                if (string.IsNullOrEmpty(prefabAssetPath) && target.scene.IsValid()) EditorSceneManager.MarkSceneDirty(target.scene);
                return new SuccessResponse("Modern UI specification applied", new
                {
                    status = "complete", root = ModernUICommon.GlobalId(target), createdRoot, ownerPath, prefabAssetPath,
                    nodes = results, warnings = string.IsNullOrEmpty(ownerPath) ? new[] { "The target has no saved scene or prefab asset path, so no ownership manifest was written." } : Array.Empty<string>()
                });
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
                if (prefabContents != null) PrefabUtility.UnloadPrefabContents(prefabContents);
            }
        }

        private static object Repair(JObject @params, string packagePath)
        {
            if (@params?["spec"] != null || @params?["spec_path"] != null)
            {
                var copied = new JObject(@params) { ["action"] = "apply", ["mode"] = "merge" };
                return Apply(copied, packagePath);
            }
            var ownerPath = AssetPathUtility.SanitizeAssetPath(@params?["owner_path"]?.Value<string>());
            if (string.IsNullOrEmpty(ownerPath) || !AssetPathUtility.IsValidAssetPath(ownerPath)) return new ErrorResponse("repair requires an Assets-relative owner_path.");
            var manifest = ModernUIManifest.Load(ownerPath);
            if (manifest == null) return new ErrorResponse("No Modern UI ownership manifest exists for the requested owner.");
            var missing = manifest.nodes.Where(node => ModernUICommon.ResolveGlobalObject(node.globalId) == null).Select(node => node.id).ToArray();
            return new SuccessResponse(missing.Length == 0 ? "Modern UI manifest is healthy" : "Modern UI repair requires the original specification", new
            {
                ownerPath, missingNodeIds = missing, repaired = false,
                guidance = missing.Length == 0 ? "No repair is required." : "Call apply with the original specification and mode='merge' to restore only missing managed nodes."
            });
        }

        private static object CreateThemeCopy(JObject @params, string packagePath)
        {
            var source = @params?["source_path"]?.Value<string>() ?? packagePath + "/Resources/MUIP Manager.asset";
            var destination = AssetPathUtility.SanitizeAssetPath(@params?["destination_path"]?.Value<string>() ?? "Assets/ScriptableObjects/UI/Project Storm MUIP Manager.asset");
            if (!AssetPathUtility.IsValidAssetPath(destination) || !destination.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return new ErrorResponse("Theme destination must be an Assets-relative .asset path.");
            if (!AssetDatabase.LoadAssetAtPath<ScriptableObject>(source)) return new ErrorResponse("Modern UI manager source asset was not found: " + source);
            if (Path.GetFileNameWithoutExtension(destination).Equals("MUIP Manager", StringComparison.OrdinalIgnoreCase)) return new ErrorResponse("Theme copies must use a unique name and cannot be named 'MUIP Manager'.");
            Directory.CreateDirectory(Path.GetDirectoryName(ModernUICommon.GetAbsoluteAssetPath(destination)));
            destination = AssetDatabase.GenerateUniqueAssetPath(destination);
            if (!AssetDatabase.CopyAsset(source, destination)) return new ErrorResponse("Unity could not copy the Modern UI manager asset.");
            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);
            var copy = AssetDatabase.LoadAssetAtPath<ScriptableObject>(destination);
            var propertyErrors = ModernUISerialization.ApplyProperties(copy, @params?["properties"] as JObject);
            if (propertyErrors.Count > 0)
            {
                AssetDatabase.DeleteAsset(destination);
                return new ErrorResponse("Theme copy properties could not be applied.", new { errors = propertyErrors });
            }
            EditorUtility.SetDirty(copy);
            AssetDatabase.SaveAssets();
            return new SuccessResponse("Created Project Storm-owned Modern UI theme copy", new { source, destination });
        }

        private static object RemoveManaged(JObject @params)
        {
            var ownerPath = AssetPathUtility.SanitizeAssetPath(@params?["owner_path"]?.Value<string>());
            var ids = @params?["node_ids"]?.Values<string>().Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
            var manifest = string.IsNullOrEmpty(ownerPath) ? null : ModernUIManifest.Load(ownerPath);
            if (manifest == null) return new ErrorResponse("remove_managed requires an owner_path with an existing manifest.");
            var selected = manifest.nodes.Where(node => ids.Count == 0 || ids.Contains(node.id)).ToList();
            var group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Remove managed Modern UI nodes");
            foreach (var node in selected)
            {
                var target = ModernUICommon.ResolveGlobalObject(node.globalId);
                if (target != null) Undo.DestroyObjectImmediate(target);
                manifest.nodes.Remove(node);
            }
            manifest.Save(ownerPath);
            Undo.CollapseUndoOperations(group);
            return new SuccessResponse("Removed managed Modern UI nodes", new { removedNodeIds = selected.Select(node => node.id).ToArray() });
        }

        private static object ApplyNode(JObject node, Transform parent, ModernUIManifest manifest, string mode, string conflictPolicy, List<string> errors)
        {
            var id = node["id"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(id)) { errors.Add("Every Modern UI node requires a stable id."); return null; }
            var manifestNode = manifest.FindNode(id);
            var target = mode != "create" ? ModernUICommon.ResolveGlobalObject(manifestNode?.globalId) : null;
            var adopted = string.Equals(node["ownership"]?.Value<string>(), "adopt", StringComparison.OrdinalIgnoreCase);
            if (target == null && adopted) target = GetModernUICatalog.ResolveTarget(node["target"]);
            var created = target == null;
            var requestedHash = HashNode(node);
            var hasConflict = !created && mode == "merge" && manifestNode != null && !string.IsNullOrEmpty(manifestNode.baselineHash) &&
                HashCurrentState(target) != manifestNode.baselineHash && manifestNode.specHash != requestedHash;
            if (hasConflict && conflictPolicy == "fail_on_conflict")
            {
                errors.Add("Managed node '" + id + "' has developer changes that conflict with the requested specification. Use prefer_project or prefer_spec explicitly.");
                return null;
            }
            if (hasConflict && conflictPolicy == "prefer_project")
            {
                manifestNode.baselineHash = HashCurrentState(target);
                manifestNode.specHash = requestedHash;
                return new { id, name = target.name, globalId = manifestNode.globalId, componentType = manifestNode.componentType, adopted = manifestNode.adopted, preservedProjectChanges = true };
            }
            if (target == null)
            {
                target = InstantiateNode(node, parent, errors);
                if (target == null) return null;
            }
            if (created) Undo.RegisterCreatedObjectUndo(target, "Create Modern UI node");
            else Undo.RecordObject(target, "Configure Modern UI node");
            target.name = node["name"]?.Value<string>() ?? target.name;
            ApplyNodeComponents(target, node, errors);
            if (target.transform is RectTransform rectTransform) ModernUISerialization.ApplyLayout(rectTransform, node["layout"] as JObject, errors);
            ApplyStyle(target, node["style"] as JObject, errors, manifest);
            foreach (var child in node["children"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) ApplyNode(child, target.transform, manifest, mode, conflictPolicy, errors);
            if (errors.Count > 0) return null;
            var componentType = target.GetComponents<Component>().FirstOrDefault(component => component != null && component.GetType().Namespace == ModernUICommon.NamespacePrefix)?.GetType().FullName;
            if (manifestNode == null) { manifestNode = new ModernUIManifestNode { id = id }; manifest.nodes.Add(manifestNode); }
            manifestNode.globalId = ModernUICommon.GlobalId(target);
            manifestNode.hierarchyPath = ModernUIObjectMatch.GetHierarchyPath(target.transform);
            manifestNode.componentType = componentType;
            manifestNode.adopted = adopted;
            manifestNode.specHash = requestedHash;
            manifestNode.baselineHash = HashCurrentState(target);
            return new { id, name = target.name, globalId = manifestNode.globalId, componentType, adopted, refresh = RefreshAll(target) };
        }

        private static GameObject InstantiateNode(JObject node, Transform parent, List<string> errors)
        {
            var prefabPath = node["prefab_path"]?.Value<string>() ?? node["prefab"]?.Value<string>();
            GameObject target = null;
            if (!string.IsNullOrWhiteSpace(prefabPath))
            {
                if (!prefabPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) { errors.Add("Prefab path must be under Assets: " + prefabPath); return null; }
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) { errors.Add("Modern UI prefab was not found: " + prefabPath); return null; }
                target = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
            }
            else target = new GameObject(node["name"]?.Value<string>() ?? "Modern UI Node", typeof(RectTransform));
            if (target != null && target.transform.parent != parent) target.transform.SetParent(parent, false);
            return target;
        }

        private static void ApplyNodeComponents(GameObject target, JObject node, List<string> errors)
        {
            var componentSpecs = new List<JObject>();
            if (node["component"] != null) componentSpecs.Add(new JObject { ["type"] = node["component"], ["properties"] = node["properties"] ?? new JObject(), ["events"] = node["events"] ?? new JArray() });
            componentSpecs.AddRange(node["components"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>());
            foreach (var spec in componentSpecs)
            {
                var type = ModernUICommon.FindComponentType(spec["type"]?.Value<string>());
                if (type == null) { errors.Add("Unknown Modern UI component: " + spec["type"]); continue; }
                var component = target.GetComponent(type) ?? Undo.AddComponent(target, type);
                Undo.RecordObject(component, "Configure Modern UI component");
                errors.AddRange(ModernUISerialization.ApplyProperties(component, spec["properties"] as JObject));
                BindEvents(component, spec["events"] as JArray, errors);
            }
        }

        private static void ApplyStyle(GameObject target, JObject style, List<string> errors, ModernUIManifest manifest)
        {
            if (style == null || string.Equals(style["mode"]?.Value<string>(), "explicit", StringComparison.OrdinalIgnoreCase)) return;
            var sourceName = style["source"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(sourceName) && string.Equals(style["mode"]?.Value<string>(), "theme", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var targetComponent in target.GetComponents<Component>().Where(component => component != null && component.GetType().Namespace == ModernUICommon.NamespacePrefix))
            {
                var selected = string.IsNullOrWhiteSpace(sourceName) ? FindNearbyDonor(target, targetComponent.GetType()) : ModernUIObjectResolver.ResolveClear(sourceName, targetComponent.GetType().Name, target, out var candidates);
                if (selected == null) { errors.Add("Style source '" + sourceName + "' is ambiguous or unavailable for " + targetComponent.GetType().Name + "."); continue; }
                var donor = selected.gameObject.GetComponent(targetComponent.GetType());
                ModernUISerialization.CopyStyle(targetComponent, donor, style["inherit"] as JObject, errors);
                manifest.styleSourceGlobalId = ModernUICommon.GlobalId(selected.gameObject);
                manifest.styleSourcePath = ModernUIObjectMatch.GetHierarchyPath(selected.gameObject.transform);
                manifest.styleSourceRequestedName = sourceName;
                manifest.styleSourceActualName = selected.gameObject.name;
            }
        }

        private static void BindEvents(Component source, JArray events, List<string> errors)
        {
            if (events == null) return;
            foreach (var binding in events.OfType<JObject>())
            {
                var eventName = binding["event"]?.Value<string>();
                var targetObject = GetModernUICatalog.ResolveTarget(binding["target"]);
                var methodName = binding["method"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(eventName) || targetObject == null || string.IsNullOrWhiteSpace(methodName)) { errors.Add("Event binding requires event, exact target, and method."); continue; }
                var serialized = new SerializedObject(source);
                var eventProperty = serialized.FindProperty(eventName);
                if (eventProperty == null || eventProperty.type.IndexOf("UnityEvent", StringComparison.OrdinalIgnoreCase) < 0) { errors.Add("'" + eventName + "' is not a UnityEvent on " + source.GetType().Name + "."); continue; }
                if (!TryGetEventArgumentTypes(source, eventName, out var argumentTypes)) { errors.Add("'" + eventName + "' is not a supported UnityEvent field."); continue; }
                var targetComponent = ResolveEventTargetComponent(targetObject, binding["target_component"]?.Value<string>(), methodName, argumentTypes, out var method);
                if (targetComponent == null || method == null) { errors.Add("No safe compatible public method '" + methodName + "' exists on the event target."); continue; }
                if (HasPersistentBinding(eventProperty, targetComponent, method)) continue;
                AddPersistentBinding(serialized, eventProperty, targetComponent, method);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static bool TryGetEventArgumentTypes(Component source, string eventName, out Type[] argumentTypes)
        {
            argumentTypes = null;
            var field = source.GetType().GetField(eventName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || !typeof(UnityEventBase).IsAssignableFrom(field.FieldType)) return false;
            argumentTypes = field.FieldType.IsGenericType ? field.FieldType.GetGenericArguments() : Type.EmptyTypes;
            return argumentTypes.Length <= 1;
        }

        private static Component ResolveEventTargetComponent(GameObject target, string typeName, string methodName, Type[] argumentTypes, out MethodInfo selectedMethod)
        {
            selectedMethod = null;
            var components = string.IsNullOrWhiteSpace(typeName) ? target.GetComponents<Component>() : target.GetComponents<Component>().Where(component => component != null && (component.GetType().Name == typeName || component.GetType().FullName == typeName));
            foreach (var component in components.Where(component => component != null))
            {
                var method = component.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(candidate => candidate.Name == methodName && IsSafeEventMethod(candidate) && IsEventCompatible(candidate, argumentTypes));
                if (method == null) continue;
                selectedMethod = method;
                return component;
            }
            return null;
        }

        private static bool IsSafeEventMethod(MethodInfo method)
        {
            if (method == null || method.IsStatic || method.IsGenericMethod || method.IsSpecialName || method.ReturnType != typeof(void)) return false;
            var ns = method.DeclaringType?.Namespace ?? string.Empty;
            return !ns.StartsWith("UnityEditor", StringComparison.Ordinal) && !ns.StartsWith("System", StringComparison.Ordinal) &&
                !ns.StartsWith("MCPForUnity.", StringComparison.Ordinal) && !method.DeclaringType.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), true);
        }

        private static bool IsEventCompatible(MethodInfo method, Type[] argumentTypes)
        {
            var parameters = method.GetParameters();
            return parameters.Length == argumentTypes.Length && parameters.Zip(argumentTypes, (parameter, argument) => parameter.ParameterType.IsAssignableFrom(argument)).All(item => item);
        }

        private static bool HasPersistentBinding(SerializedProperty eventProperty, Component target, MethodInfo method)
        {
            var calls = eventProperty.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (calls == null) return false;
            for (var index = 0; index < calls.arraySize; index++)
            {
                var call = calls.GetArrayElementAtIndex(index);
                if (call.FindPropertyRelative("m_Target")?.objectReferenceValue == target && call.FindPropertyRelative("m_MethodName")?.stringValue == method.Name)
                    return true;
            }
            return false;
        }

        private static void AddPersistentBinding(SerializedObject serialized, SerializedProperty eventProperty, Component target, MethodInfo method)
        {
            var calls = eventProperty.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (calls == null) throw new InvalidOperationException("UnityEvent persistent call serialization is unavailable.");
            var index = calls.arraySize;
            calls.InsertArrayElementAtIndex(index);
            var call = calls.GetArrayElementAtIndex(index);
            call.FindPropertyRelative("m_Target").objectReferenceValue = target;
            call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = target.GetType().AssemblyQualifiedName;
            call.FindPropertyRelative("m_MethodName").stringValue = method.Name;
            call.FindPropertyRelative("m_Mode").enumValueIndex = method.GetParameters().Length == 0 ? 1 : 0;
            call.FindPropertyRelative("m_CallState").enumValueIndex = 2;
        }

        private static GameObject CreateScreenRoot(JObject spec)
        {
            var root = new GameObject(spec["name"]?.Value<string>() ?? "Modern UI Screen", typeof(RectTransform), typeof(Canvas));
            Undo.RegisterCreatedObjectUndo(root, "Create Modern UI screen");
            var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scalerType = Type.GetType("UnityEngine.UI.CanvasScaler, UnityEngine.UI");
            if (scalerType != null)
            {
                var scaler = root.AddComponent(scalerType);
                var serialized = new SerializedObject(scaler);
                serialized.FindProperty("m_UiScaleMode")?.SetPropertyValue(1);
                serialized.FindProperty("m_ReferenceResolution")?.SetVector2Value(new Vector2(1920, 1080));
                serialized.FindProperty("m_MatchWidthOrHeight")?.SetFloatValue(0.5f);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var safeArea = new GameObject("Safe Area", typeof(RectTransform), typeof(ModernUISafeAreaFitter));
            safeArea.transform.SetParent(root.transform, false);
            return safeArea;
        }

        private static string ResolveOwnerPath(JObject spec, GameObject target)
        {
            var explicitPath = AssetPathUtility.SanitizeAssetPath(spec["owner_path"]?.Value<string>());
            if (!string.IsNullOrEmpty(explicitPath) && AssetPathUtility.IsValidAssetPath(explicitPath)) return explicitPath;
            var scenePath = target?.scene.path;
            return !string.IsNullOrEmpty(scenePath) ? scenePath : null;
        }

        private static string ResolvePrefabAssetPath(JToken target)
        {
            var path = target?.Type == JTokenType.String ? AssetPathUtility.SanitizeAssetPath(target.Value<string>()) : null;
            if (string.IsNullOrEmpty(path)) return null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return prefab != null && PrefabUtility.GetPrefabAssetType(prefab) != PrefabAssetType.NotAPrefab ? path : null;
        }

        private static void RemoveStaleManagedNodes(ModernUIManifest manifest, JArray requestedNodes, List<object> results)
        {
            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in requestedNodes?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) CollectNodeIds(node, requested);
            foreach (var stale in manifest.nodes.Where(node => !requested.Contains(node.id)).ToList())
            {
                var target = ModernUICommon.ResolveGlobalObject(stale.globalId);
                if (target != null && !EditorUtility.IsPersistent(target)) Undo.DestroyObjectImmediate(target);
                manifest.nodes.Remove(stale);
                results.Add(new { id = stale.id, removed = true, adopted = stale.adopted });
            }
        }

        private static void CollectNodeIds(JObject node, ISet<string> result)
        {
            var id = node?["id"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            foreach (var child in node?["children"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) CollectNodeIds(child, result);
        }

        private static List<string> RefreshAll(GameObject target)
        {
            return target.GetComponentsInChildren<Component>(true).Where(component => component != null && component.GetType().Namespace == ModernUICommon.NamespacePrefix)
                .SelectMany(ModernUIAdapterRegistry.Refresh).Distinct().ToList();
        }

        private static bool TryReadSpec(JObject @params, out JObject spec, out string error)
        {
            spec = @params?["spec"] as JObject; error = null;
            if (spec != null && @params?["spec_path"] != null) { error = "Provide exactly one of spec or spec_path."; return false; }
            if (spec != null) return true;
            var path = AssetPathUtility.SanitizeAssetPath(@params?["spec_path"]?.Value<string>());
            if (string.IsNullOrEmpty(path) || !AssetPathUtility.IsValidAssetPath(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { error = "Provide a specification object or an Assets-relative .json spec_path."; return false; }
            var absolute = ModernUICommon.GetAbsoluteAssetPath(path);
            if (!File.Exists(absolute)) { error = "Specification file was not found: " + path; return false; }
            try { spec = JObject.Parse(File.ReadAllText(absolute)); return true; }
            catch (Exception ex) { error = "Specification JSON could not be parsed: " + ex.Message; return false; }
        }

        private static List<string> ValidateSpec(JObject spec, string packagePath)
        {
            var errors = new List<string>();
            if (spec == null) { errors.Add("Specification is required."); return errors; }
            if (spec["spec_version"]?.Value<string>() != ModernUICommon.SpecVersion) errors.Add("spec_version must be '1'.");
            var nodes = spec["nodes"] as JArray;
            if (nodes == null || nodes.Count == 0) errors.Add("Specification requires at least one node.");
            var status = ModernUICommon.BuildStatus(packagePath);
            if (packagePath == null || ModernUICommon.GetModernUIComponentTypes().Count == 0) errors.Add("Modern UI Pack is not installed or its component types are unavailable.");
            foreach (var node in nodes?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) ValidateNode(node, errors);
            return errors;
        }

        private static void ValidateNode(JObject node, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(node["id"]?.Value<string>())) errors.Add("Every node requires an id.");
            foreach (var component in new[] { node["component"]?.Value<string>() }.Concat(node["components"]?.OfType<JObject>().Select(item => item["type"]?.Value<string>()) ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)))
                if (ModernUICommon.FindComponentType(component) == null) errors.Add("Unknown Modern UI component: " + component);
            foreach (var child in node["children"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) ValidateNode(child, errors);
        }

        private static string HashNode(JObject node) => ModernUICommon.StableHash(node.ToString(Newtonsoft.Json.Formatting.None));

        private static string HashCurrentState(GameObject target)
        {
            var text = string.Join("\n", target.GetComponents<Component>().Where(component => component != null && component.GetType().Namespace == ModernUICommon.NamespacePrefix)
                .OrderBy(component => component.GetType().FullName).Select(component => component.GetType().FullName + ":" + EditorJsonUtility.ToJson(component)));
            return ModernUICommon.StableHash(text);
        }

        private static ModernUIObjectMatch FindNearbyDonor(GameObject target, Type componentType)
        {
            var parent = target.transform.parent;
            if (parent == null) return null;
            var donor = parent.Cast<Transform>().Select(item => item.gameObject).FirstOrDefault(candidate => candidate != target && candidate.GetComponent(componentType) != null);
            return donor == null ? null : new ModernUIObjectMatch { gameObject = donor, score = 0.8f, reason = "nearest compatible sibling" };
        }
    }

    internal static class SerializedPropertyCompatibilityExtensions
    {
        internal static void SetPropertyValue(this SerializedProperty property, int value) { if (property != null) property.intValue = value; }
        internal static void SetVector2Value(this SerializedProperty property, Vector2 value) { if (property != null) property.vector2Value = value; }
        internal static void SetFloatValue(this SerializedProperty property, float value) { if (property != null) property.floatValue = value; }
    }
}

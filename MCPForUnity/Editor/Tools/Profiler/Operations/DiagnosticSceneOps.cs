using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.Profiler
{
    internal static class DiagnosticSceneOps
    {
        internal static JObject Serialized(UnityEngine.Object obj)
        {
            if (!obj) return null;
            var result = DiagnosticCommon.Identity(obj);
            result.Remove("instance_id");
            result["serialized"] = StableReferences(JObject.Parse(EditorJsonUtility.ToJson(obj)));
            return result;
        }
        static JToken StableReferences(JToken token)
        {
            if (token is JObject o && o.Count == 1 && o["instanceID"] != null)
            {
                var obj = EditorUtility.InstanceIDToObject((int)o["instanceID"]);
                if (!obj) return JValue.CreateNull();
                var identity = DiagnosticCommon.Identity(obj); identity.Remove("instance_id");
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId))
                { identity["guid"] = guid; identity["local_id"] = localId; }
                if (obj is Component component) { identity["scene"] = component.gameObject.scene.path; identity["hierarchy"] = DiagnosticCommon.Hierarchy(component.transform); }
                if (obj is GameObject gameObject) { identity["scene"] = gameObject.scene.path; identity["hierarchy"] = DiagnosticCommon.Hierarchy(gameObject.transform); }
                return identity;
            }
            if (token is JObject map) foreach (var property in map.Properties().ToArray()) property.Value = StableReferences(property.Value);
            else if (token is JArray array) for (int i = 0; i < array.Count; i++) array[i] = StableReferences(array[i]);
            return token;
        }
        static JObject EffectiveCamera(Camera camera)
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Rendering.HighDefinition.HDCamera")).FirstOrDefault(t => t != null);
                var cache = DiagnosticCommon.Member(type, "s_Cameras") as IDictionary;
                if (cache != null) foreach (DictionaryEntry pair in cache)
                {
                    if (!ReferenceEquals(DiagnosticCommon.Member(pair.Value, "camera"), camera)) continue;
                    var frame = DiagnosticCommon.Member(pair.Value, "frameSettings");
                    var stack = DiagnosticCommon.Member(DiagnosticCommon.Member(pair.Value, "volumeStack"), "components") as IDictionary;
                    return new JObject { ["status"] = "available", ["basis"] = "Last evaluated HDRP camera state; no new camera or volume profile was created.",
                        ["frame_settings"] = StableReferences(JObject.Parse(JsonUtility.ToJson(frame))),
                        ["blended_volumes"] = new JArray(stack?.Values.Cast<UnityEngine.Object>().OrderBy(v => v.GetType().FullName).Select(v => Serialized(v)) ?? Enumerable.Empty<JObject>()) };
                }
                return DiagnosticCommon.Unavailable("No evaluated HDRP camera state; render this camera first. Serialized overrides are recorded separately.");
            }
            catch (Exception ex) { return DiagnosticCommon.Unavailable("HDRP runtime adapter: " + ex.GetBaseException().Message); }
        }
        internal static JObject Settings()
        {
            var cameras = new JArray();
            foreach (var camera in UnityEngine.Resources.FindObjectsOfTypeAll<Camera>().Where(c => c.gameObject.scene.IsValid()).OrderBy(c => DiagnosticCommon.Hierarchy(c.transform)))
            {
                var overrides = new JArray(camera.GetComponents<Component>().Where(c => c && c.GetType().Name == "HDAdditionalCameraData").Select(c => Serialized(c)));
                cameras.Add(new JObject { ["path"] = DiagnosticCommon.Hierarchy(camera.transform), ["enabled"] = camera.enabled,
                    ["width"] = camera.pixelWidth, ["height"] = camera.pixelHeight, ["hdr"] = camera.allowHDR,
                    ["dynamic_resolution"] = camera.allowDynamicResolution, ["culling_mask"] = camera.cullingMask,
                    ["rendering_path"] = camera.actualRenderingPath.ToString(), ["target"] = Serialized(camera.targetTexture),
                    ["overrides"] = overrides, ["effective_hdrp"] = EffectiveCamera(camera) });
            }
            var scenes = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); scenes.Add(new JObject { ["path"] = s.path, ["loaded"] = s.isLoaded, ["dirty"] = s.isDirty }); }
            var relevant = UnityEngine.Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c && c.gameObject.scene.IsValid() &&
                (c.GetType().Name == "CustomPassVolume" || c.GetType().Name == "Volume")).OrderBy(c => DiagnosticCommon.Hierarchy(c.transform));
            var volumes = new JArray(relevant.Select(c => Serialized(c)));
            foreach (var volume in relevant.Where(v => v.GetType().Name == "Volume"))
            {
                var shared = (DiagnosticCommon.Member(volume, "m_InternalProfile") ?? DiagnosticCommon.Member(volume, "sharedProfile")) as UnityEngine.Object;
                if (shared) { var profile = Serialized(shared); var components = DiagnosticCommon.Member(shared, "components") as IEnumerable;
                    profile["components"] = new JArray(components?.Cast<UnityEngine.Object>().Select(c => Serialized(c)) ?? Enumerable.Empty<JObject>()); volumes.Add(profile); }
            }
            return new JObject { ["schema"] = 1, ["unity"] = Application.unityVersion, ["build_guid"] = Application.buildGUID,
                ["application_version"] = Application.version, ["platform"] = Application.platform.ToString(), ["editor_measurement"] = Application.isEditor,
                ["quality"] = QualitySettings.names[QualitySettings.GetQualityLevel()], ["quality_index"] = QualitySettings.GetQualityLevel(),
                ["render_pipeline"] = Serialized(GraphicsSettings.currentRenderPipeline), ["vsync"] = QualitySettings.vSyncCount,
                ["target_fps"] = Application.targetFrameRate, ["width"] = Screen.width, ["height"] = Screen.height,
                ["render_scale_width"] = ScalableBufferManager.widthScaleFactor, ["render_scale_height"] = ScalableBufferManager.heightScaleFactor,
                ["lod_bias"] = QualitySettings.lodBias, ["maximum_lod"] = QualitySettings.maximumLODLevel,
                ["fixed_delta_seconds"] = Time.fixedDeltaTime, ["time_scale"] = Time.timeScale,
                ["gpu"] = SystemInfo.graphicsDeviceName, ["graphics_api"] = SystemInfo.graphicsDeviceType.ToString(),
                ["cameras"] = cameras, ["volumes_and_custom_passes"] = volumes, ["scenes"] = scenes,
                ["terrain_tiles"] = new JArray(Terrain.activeTerrains.Select(t => DiagnosticCommon.Hierarchy(t.transform)).OrderBy(s => s)),
                ["unsaved_scene_warning"] = scenes.Any(s => (bool)s["dirty"]) };
        }
        internal static JObject CompareSettings(JObject a, JObject b)
        {
            var changed = new JArray(a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name)).Where(k => !JToken.DeepEquals(a[k], b[k])).OrderBy(k => k));
            return new JObject { ["matched"] = changed.Count == 0, ["changed_fields"] = changed };
        }
        internal static JObject Metric(object obj, string field)
        {
            try
            {
                var value = DiagnosticCommon.Member(obj, field);
                if (value == null || value is double d && (double.IsNaN(d) || double.IsInfinity(d))) return DiagnosticCommon.Unavailable("No completed measurement or this component version has no " + field + ".");
                return new JObject { ["status"] = "available", ["value"] = value is IDictionary dict ? new JValue(dict.Count) : JToken.FromObject(value) };
            }
            catch (Exception ex) { return DiagnosticCommon.Unavailable(ex.GetBaseException().Message); }
        }
        internal static JObject Storm()
        {
            var components = UnityEngine.Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(o => o && o.gameObject.scene.IsValid()).ToArray();
            var wind = new JArray(); var weather = new JArray(); var rain = new JArray(); var cars = new JArray();
            foreach (var obj in components)
            {
                string type = obj.GetType().Name;
                if (type == "StormWindInteractionSystem" || type == "WeatherManager")
                {
                    var metrics = new JObject();
                    var fields = type == "WeatherManager" ? new[] { "WeatherSeconds" } : new[] { "RegisteredReceivers", "ProbeCount", "Samples", "CacheHits", "Invalidations", "CriticalFallbacks", "BudgetFailures", "LastMilliseconds", "MaximumSampleAge", "LastPhysicsSeconds", "DueCount", "OverdueCount", "BudgetBypasses", "loadedTiles" };
                    foreach (var field in fields) metrics[field] = Metric(obj, field);
                    (type == "WeatherManager" ? weather : wind).Add(new JObject { ["path"] = DiagnosticCommon.Hierarchy(obj.transform), ["enabled"] = obj.isActiveAndEnabled, ["metrics"] = metrics });
                }
                if (type == "RCC_CarControllerV4")
                {
                    var damage = DiagnosticCommon.Member(obj, "damage");
                    var arrays = new HashSet<Array>(); long bytes = 0;
                    foreach (string field in new[] { "originalMeshData", "damagedMeshData" })
                        if (DiagnosticCommon.Member(damage, field) is IEnumerable meshes)
                            foreach (var mesh in meshes)
                                if (DiagnosticCommon.Member(mesh, "meshVerts") is Vector3[] verts && arrays.Add(verts)) bytes += verts.LongLength * 12;
                    if (DiagnosticCommon.Member(damage, "contactPoints") is Vector3[] contacts && arrays.Add(contacts)) bytes += contacts.LongLength * 12;
                    foreach (string field in new[] { "originalWheelData", "damagedWheelData" })
                        if (DiagnosticCommon.Member(damage, field) is Array wheels && arrays.Add(wheels)) bytes += wheels.LongLength * 28;
                    cars.Add(new JObject { ["path"] = DiagnosticCommon.Hierarchy(obj.transform), ["damage_cache_payload_bytes"] = damage == null ? (JToken)DiagnosticCommon.Unavailable("No RCC damage cache.") : new JValue(bytes),
                        ["unique_arrays"] = arrays.Count, ["basis"] = "Estimated payload of RCC damage mesh/wheel/contact arrays; excludes object headers, octree nodes, unused capacity and physical residency. Use memory_objects for measured captured arrays." });
                }
                if (type == "RaindropEffect") rain.Add(Rain(obj, DiagnosticCommon.Member(obj, "m_renderer")));
            }
            foreach (var volume in UnityEngine.Resources.FindObjectsOfTypeAll<ScriptableObject>().Where(o => o && o.GetType().Name == "RaindropEffectVolumeComponent"))
                rain.Add(Rain(volume, DiagnosticCommon.Member(volume, "m_renderer")));
            return new JObject { ["sampled_at_frame"] = Time.frameCount, ["playing"] = Application.isPlaying,
                ["fixed_time_seconds"] = Time.fixedTimeAsDouble, ["wind"] = wind, ["weather"] = weather, ["rcc"] = cars, ["rain_windows_or_volumes"] = rain,
                ["note"] = "Wind Due/Overdue/Bypasses count round-robin candidates in the last fixed step; critical/immediate receivers are excluded. BudgetFailures is cumulative. Empty lists mean no matching loaded component. Rain is last CPU submission, not GPU duration or total frame cost." };
        }
        static JObject Rain(UnityEngine.Object owner, object renderer)
        {
            var row = DiagnosticCommon.Identity(owner);
            if (owner is Component c) row["path"] = DiagnosticCommon.Hierarchy(c.transform);
            row["last_rendered_frame"] = Metric(renderer, "LastRenderedFrame");
            var frame = DiagnosticCommon.Member(renderer, "LastRenderedFrame");
            row["cpu_submission_ms"] = frame is int f && f >= 0 ? Metric(renderer, "LastRenderMilliseconds") : DiagnosticCommon.Unavailable("This renderer has not submitted a measured frame.");
            row["gpu_ms"] = DiagnosticCommon.Unavailable("Use captured GPU pass hierarchy; CPU submission time is not GPU execution.");
            return row;
        }
        internal static JObject RendererAudit(JObject p)
        {
            var rows = new JArray();
            var renderers = UnityEngine.Resources.FindObjectsOfTypeAll<Renderer>().Where(r => r && r.gameObject.scene.IsValid()).ToArray();
            var families = renderers.GroupBy(r => {
                var root = PrefabUtility.GetNearestPrefabInstanceRoot(r.gameObject);
                return root ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) : DiagnosticCommon.Hierarchy(r.transform.root);
            });
            foreach (var family in families)
            {
                var materials = family.SelectMany(r => r.sharedMaterials).Where(m => m).Distinct().ToArray();
                var meshes = family.Select(r => r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh).Where(m => m).ToArray();
                var lodGroups = family.Select(r => r.GetComponentInParent<LODGroup>()).Where(g => g).Distinct().ToArray();
                var lodRows = new JArray();
                foreach (var group in lodGroups)
                {
                    long baseline = 0;
                    var levels = new JArray();
                    foreach (var lod in group.GetLODs())
                    {
                        long indices = lod.renderers.Where(r => r).Select(r => r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh).Where(m => m).Sum(m => Enumerable.Range(0, m.subMeshCount).Sum(i => (long)m.GetIndexCount(i)));
                        if (levels.Count == 0) baseline = indices;
                        levels.Add(new JObject { ["transition_height"] = lod.screenRelativeTransitionHeight, ["indices"] = indices,
                            ["reduction_from_lod0"] = baseline == 0 ? JValue.CreateNull() : new JValue(1.0 - (double)indices / baseline) });
                    }
                    lodRows.Add(new JObject { ["path"] = DiagnosticCommon.Hierarchy(group.transform), ["levels"] = levels });
                }
                var textures = materials.SelectMany(m => m.GetTexturePropertyNames().Select(n => m.GetTexture(n))).Where(t => t).Distinct().ToArray();
                rows.Add(new JObject { ["family"] = family.Key, ["renderer_count"] = family.Count(), ["material_count"] = materials.Length,
                    ["submesh_slots_all_lods"] = meshes.Sum(m => m.subMeshCount), ["shadow_capable_renderers_all_lods"] = family.Count(r => r.shadowCastingMode != ShadowCastingMode.Off),
                    ["actual_shadow_submissions"] = DiagnosticCommon.Unavailable("Requires a selected captured frame; configuration counts are not submissions."),
                    ["texture_references"] = new JArray(textures.Select(t => DiagnosticCommon.Identity(t))), ["lod_groups"] = lodRows,
                    ["instancing_candidates"] = new JArray(family.Where(r => !(r is SkinnedMeshRenderer)).GroupBy(r => string.Join("|", r.sharedMaterials.Select(m => m ? m.GetInstanceID().ToString() : "null")) + "/" + (r.GetComponent<MeshFilter>()?.sharedMesh?.GetInstanceID().ToString() ?? "none")).Where(g => g.Count() > 1).Select(g => new JObject { ["count"] = g.Count(), ["material_instancing_enabled"] = g.First().sharedMaterials.All(m => m && m.enableInstancing), ["note"] = "Candidate only: verify shader, lightmap, property blocks, motion and SRP batching in the target build." })) });
            }
            return new JObject { ["family_count"] = rows.Count, ["families"] = DiagnosticCommon.Page(rows.OrderByDescending(r => (int)r["submesh_slots_all_lods"]), p),
                ["basis"] = "Loaded scene renderers, all LODs; ranked potential cost, not measured draws." };
        }
    }
}

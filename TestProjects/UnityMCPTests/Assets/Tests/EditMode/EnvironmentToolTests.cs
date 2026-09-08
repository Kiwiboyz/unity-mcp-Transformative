using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Environment;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityTests.Editor
{
    public class EnvironmentToolTests
    {
        [Test]
        public void CatalogStatus_IsAvailableWithoutHdrpOrExpanse()
        {
            var response = GetEnvironmentCatalog.HandleCommand(new JObject { ["action"] = "status" });
            Assert.That(response, Is.TypeOf<SuccessResponse>());
        }

        [Test]
        public void Preflight_RejectsWrongSchemaVersion()
        {
            var response = ManageEnvironment.Preflight(new JObject
            {
                ["spec"] = new JObject { ["schema_version"] = 999, ["changes"] = new JArray() }
            });
            Assert.That(response, Is.TypeOf<ErrorResponse>());
        }

        [Test]
        public void Describe_ReportsSemanticControlsForSupportedTypes()
        {
            var response = (SuccessResponse)GetEnvironmentCatalog.HandleCommand(new JObject { ["action"] = "describe", ["component"] = "StormVisualProfile" });
            var data = JObject.FromObject(response.Data);
            Assert.That(data["semantic_controls"]?.Value<string>("lightning_enabled"), Is.EqualTo("lightningEnabled"));
            Assert.That(data["semantic_controls"]?.Value<string>("intensity_thresholds"), Is.EqualTo("intensityThresholds"));
        }

        [Test]
        public void PersistentApply_RejectsMissingChanges()
        {
            var response = ManageEnvironment.HandleCommand(new JObject
            {
                ["action"] = "apply",
                ["spec"] = new JObject { ["schema_version"] = 1, ["changes"] = new JArray() }
            });
            Assert.That(response, Is.TypeOf<ErrorResponse>());
        }

        [Test]
        public void Apply_UsesSemanticCreativeControl()
        {
            var gameObject = new GameObject("Synthetic Cloud Context");
            var cloud = gameObject.AddComponent<CreativeCloudVolume>();
            try
            {
                var response = ManageEnvironment.HandleCommand(new JObject
                {
                    ["action"] = "apply",
                    ["spec"] = new JObject
                    {
                        ["schema_version"] = 1,
                        ["changes"] = new JArray
                        {
                            new JObject
                            {
                                ["target"] = EnvironmentTarget(cloud), ["semantic"] = "coverage", ["value"] = 0.8f
                            }
                        }
                    }
                });
                Assert.That(response, Is.TypeOf<SuccessResponse>());
                Assert.That(cloud.m_coverage, Is.EqualTo(0.8f).Within(0.0001f));
            }
            finally { Object.DestroyImmediate(gameObject); }
        }

        [Test]
        public void GoalPreviewAndRestore_RestoresSemanticChange()
        {
            var gameObject = new GameObject("Synthetic Goal Context");
            var fog = gameObject.AddComponent<CreativeFog>();
            try
            {
                var spec = new JObject
                {
                    ["schema_version"] = 1,
                    ["changes"] = new JArray { new JObject { ["target"] = EnvironmentTarget(fog), ["semantic"] = "visibility_distance", ["value"] = 250f } }
                };
                var begun = (SuccessResponse)ManageEnvironment.HandleCommand(new JObject { ["action"] = "begin_goal", ["spec"] = spec });
                var goalId = JObject.FromObject(begun.Data).Value<string>("goalId");
                Assert.That(goalId, Is.Not.Empty);
                Assert.That(ManageEnvironment.HandleCommand(new JObject { ["action"] = "preview_goal", ["goal_id"] = goalId }), Is.TypeOf<SuccessResponse>());
                Assert.That(fog.m_visibilityDistance, Is.EqualTo(250f).Within(0.0001f));
                Assert.That(ManageEnvironment.HandleCommand(new JObject { ["action"] = "restore_goal", ["goal_id"] = goalId }), Is.TypeOf<SuccessResponse>());
                Assert.That(fog.m_visibilityDistance, Is.EqualTo(1000f).Within(0.0001f));
            }
            finally { Object.DestroyImmediate(gameObject); }
        }

        [Test]
        public void Apply_UpdatesCurveAndListOnSupportedWeatherProfile()
        {
            const string assetPath = "Assets/Tests/EnvironmentSyntheticProfile.asset";
            AssetDatabase.DeleteAsset(assetPath);
            var profile = ScriptableObject.CreateInstance<StormVisualProfile>();
            AssetDatabase.CreateAsset(profile, assetPath);
            try
            {
                var response = ManageEnvironment.HandleCommand(new JObject
                {
                    ["action"] = "apply",
                    ["spec"] = new JObject
                    {
                        ["schema_version"] = 1,
                        ["changes"] = new JArray
                        {
                            new JObject
                            {
                                ["target"] = new JObject { ["asset_path"] = assetPath }, ["semantic"] = "cloud_coverage_by_intensity",
                                ["value"] = new JObject { ["keys"] = new JArray { new JObject { ["time"] = 0f, ["value"] = 0.2f }, new JObject { ["time"] = 1f, ["value"] = 0.9f } } }
                            },
                            new JObject
                            {
                                ["target"] = new JObject { ["asset_path"] = assetPath }, ["semantic"] = "intensity_thresholds", ["value"] = new JArray(0.1f, 0.5f, 0.95f)
                            }
                        }
                    }
                });
                Assert.That(response, Is.TypeOf<SuccessResponse>());
                Assert.That(profile.cloudCoverageByIntensity.Evaluate(0f), Is.EqualTo(0.2f).Within(0.0001f));
                Assert.That(profile.cloudCoverageByIntensity.Evaluate(1f), Is.EqualTo(0.9f).Within(0.0001f));
                Assert.That(profile.intensityThresholds, Is.EqualTo(new[] { 0.1f, 0.5f, 0.95f }));
            }
            finally { AssetDatabase.DeleteAsset(assetPath); }
        }

        [Test]
        public void Apply_CreatesSupportedEnvironmentAssetThroughSetupOperation()
        {
            const string assetPath = "Assets/Tests/EnvironmentCreatedProfile.asset";
            AssetDatabase.DeleteAsset(assetPath);
            try
            {
                var response = ManageEnvironment.HandleCommand(new JObject
                {
                    ["action"] = "apply",
                    ["spec"] = new JObject
                    {
                        ["schema_version"] = 1,
                        ["operations"] = new JArray { new JObject { ["kind"] = "create_asset", ["type"] = "StormVisualProfile", ["asset_path"] = assetPath } },
                        ["changes"] = new JArray()
                    }
                });
                Assert.That(response, Is.TypeOf<SuccessResponse>());
                Assert.That(AssetDatabase.LoadAssetAtPath<StormVisualProfile>(assetPath), Is.Not.Null);
            }
            finally { AssetDatabase.DeleteAsset(assetPath); }
        }

        private static JObject EnvironmentTarget(Object target) => new JObject
        {
            ["global_id"] = GlobalObjectId.GetGlobalObjectIdSlow(target).ToString()
        };
    }
}

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEditor;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.ModernUI;
using static MCPForUnityTests.Editor.TestUtilities;

namespace Michsky.MUIP
{
    // Synthetic fixtures only: these establish the public serialization/lifecycle shapes without copying vendor code.
    public sealed class ButtonManager : MonoBehaviour
    {
        public string label;
        public UnityEvent onClick = new UnityEvent();
        public void UpdateUI() { }
    }
}

namespace MCPForUnityTests.Editor.Tools
{
    public sealed class ModernUITestReceiver : MonoBehaviour
    {
        public int calls;
        public void HandleClick() { calls++; }
    }

    public class ModernUIToolsTests
    {
        private const string TempRoot = "Assets/Temp/ModernUIToolsTests";
        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(TempRoot + "/Modern UI Pack");
            System.IO.File.WriteAllText(System.IO.Path.Combine(Application.dataPath, "Temp/ModernUIToolsTests/Modern UI Pack/Read Me.txt"), "Modern UI Pack - v5.5.16");
            AssetDatabase.ImportAsset(TempRoot + "/Modern UI Pack/Read Me.txt", ImportAssetOptions.ForceUpdate);
            root = new GameObject("Modern UI Test Root", typeof(RectTransform), typeof(ModernUITestReceiver));
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (AssetDatabase.IsValidFolder(TempRoot)) AssetDatabase.DeleteAsset(TempRoot);
            CleanupEmptyParentFolders(TempRoot);
        }

        [Test]
        public void Catalog_Status_DetectsSyntheticModernUIType()
        {
            var result = ToJObject(GetModernUICatalog.HandleCommand(new JObject { ["action"] = "status", ["package_path"] = TempRoot + "/Modern UI Pack" }));
            Assert.That(result.Value<bool>("success"), Is.True, result.ToString());
            Assert.That(result["data"]["componentCount"].Value<int>(), Is.GreaterThanOrEqualTo(1));
            Assert.That(result["data"]["version"].Value<string>(), Is.EqualTo("5.5.16"));
        }

        [Test]
        public void Catalog_ResolveStyleSource_ReportsClearTypoMatch()
        {
            var donor = new GameObject("Loadout Button", typeof(RectTransform), typeof(Michsky.MUIP.ButtonManager));
            donor.transform.SetParent(root.transform);
            var response = GetModernUICatalog.HandleCommand(new JObject
            {
                ["action"] = "resolve_style_source", ["name"] = "Lodout Buton", ["component"] = "ButtonManager", ["near"] = "Modern UI Test Root"
            }) as SuccessResponse;
            var data = response?.Data as JObject;
            Assert.That(response, Is.Not.Null);
            Assert.That(data, Is.Not.Null);
            Assert.That(data["resolved"]?["name"]?.Value<string>(), Is.EqualTo("Loadout Button"));
            Assert.That(data.Value<bool>("requiresConfirmation"), Is.False);
        }

        [Test]
        public void Manage_Apply_CreatesAndConfiguresSyntheticComponent()
        {
            var result = ToJObject(ManageModernUI.HandleCommand(new JObject
            {
                ["action"] = "apply", ["package_path"] = TempRoot + "/Modern UI Pack",
                ["spec"] = new JObject
                {
                    ["spec_version"] = "1", ["target"] = "Modern UI Test Root", ["mode"] = "create",
                    ["nodes"] = new JArray(new JObject
                    {
                        ["id"] = "continue", ["name"] = "Continue Button", ["component"] = "ButtonManager",
                        ["properties"] = new JObject { ["label"] = "Continue" },
                        ["events"] = new JArray
                        {
                            new JObject { ["event"] = "onClick", ["target"] = "Modern UI Test Root", ["target_component"] = "ModernUITestReceiver", ["method"] = "HandleClick" }
                        },
                        ["layout"] = new JObject { ["anchor_min"] = new JObject { ["x"] = 0.2f, ["y"] = 0.2f }, ["anchor_max"] = new JObject { ["x"] = 0.8f, ["y"] = 0.4f } }
                    })
                }
            }));
            Assert.That(result.Value<bool>("success"), Is.True, result.ToString());
            var button = root.GetComponentInChildren<Michsky.MUIP.ButtonManager>();
            Assert.That(button, Is.Not.Null);
            Assert.That(button.label, Is.EqualTo("Continue"));
            Assert.That(button.onClick.GetPersistentEventCount(), Is.EqualTo(1));
            Assert.That(root.GetComponent<ModernUITestReceiver>().calls, Is.EqualTo(0), "Binding must not invoke the target method.");
        }
    }
}

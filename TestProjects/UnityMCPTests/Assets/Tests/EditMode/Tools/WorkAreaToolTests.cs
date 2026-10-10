using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Tools.WorkAreas;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public class WorkAreaToolTests
    {
        private string file;

        [SetUp]
        public void SetUp()
        {
            file = Path.Combine(Path.GetTempPath(), "mcp-work-areas-" + System.Guid.NewGuid().ToString("N") + ".json");
            WorkAreaStore.ResetForTests();
            WorkAreaStore.FilePath = file;
        }

        [TearDown]
        public void TearDown()
        {
            WorkAreaStore.ResetForTests();
            WorkAreaStore.FilePath = WorkAreaStore.DefaultFilePath;
            if (File.Exists(file)) File.Delete(file);
        }

        // ------------------------------------------------------------------ geometry

        [Test]
        public void Geometry_RectangleAreaCentroidAndWinding()
        {
            var corners = WorkAreaGeometry.RectangleCorners(new Vector3(10f, 0f, -20f), new Vector2(8f, 4f), 30f);
            Assert.AreEqual(32f, WorkAreaGeometry.Area(corners), 1e-3f);
            Assert.Greater(WorkAreaGeometry.SignedArea(corners), 0f, "Corners run counter-clockwise seen from above");
            var centroid = WorkAreaGeometry.Centroid(corners);
            Assert.AreEqual(10f, centroid.x, 1e-3f);
            Assert.AreEqual(-20f, centroid.z, 1e-3f);
            Assert.AreEqual(24f, WorkAreaGeometry.Perimeter(corners), 1e-3f);
        }

        [Test]
        public void Geometry_ConcavePolygonContainsTriangulatesAndClips()
        {
            // An L: 10 x 10 square missing its 5 x 5 north-east quarter.
            var l = Points(0, 0, 10, 0, 10, 5, 5, 5, 5, 10, 0, 10);
            Assert.AreEqual(75f, WorkAreaGeometry.Area(l), 1e-3f);
            Assert.IsTrue(WorkAreaGeometry.Contains(l, new Vector3(2f, 0f, 8f)));
            Assert.IsFalse(WorkAreaGeometry.Contains(l, new Vector3(8f, 0f, 8f)));
            var triangles = WorkAreaGeometry.Triangulate(l);
            Assert.AreEqual((l.Count - 2) * 3, triangles.Count);
            float sum = 0f;
            for (int i = 0; i < triangles.Count; i += 3)
                sum += WorkAreaGeometry.Area(new[] { l[triangles[i]], l[triangles[i + 1]], l[triangles[i + 2]] });
            Assert.AreEqual(75f, sum, 1e-3f);
            Assert.AreEqual(25f, WorkAreaGeometry.OverlapArea(l, Rect.MinMaxRect(0f, 0f, 5f, 5f)), 1e-3f);
            Assert.AreEqual(25f, WorkAreaGeometry.OverlapArea(l, Rect.MinMaxRect(5f, 0f, 20f, 20f)), 1e-3f);
            Assert.AreEqual(0f, WorkAreaGeometry.OverlapArea(l, Rect.MinMaxRect(6f, 6f, 20f, 20f)), 1e-3f);
        }

        [Test]
        public void Geometry_SelfCrossingOutlineIsNotSimple()
        {
            Assert.IsFalse(WorkAreaGeometry.IsSimple(Points(0, 0, 10, 10, 10, 0, 0, 10)));
            Assert.IsTrue(WorkAreaGeometry.IsSimple(Points(0, 0, 10, 0, 10, 10, 0, 10)));
        }

        // ------------------------------------------------------------------ MCP round trip

        [Test]
        public void CreateReadUpdateDelete_RoundTripsExactCoordinates_WithoutDirtyingTheScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.IsFalse(scene.isDirty);

            var created = Data(ManageWorkArea.HandleCommand(new JObject
            {
                ["action"] = "create", ["name"] = "Dock",
                ["vertices"] = new JArray(new JArray(0, 38, 0), new JArray(30, 38, 0), new JArray(30, 39, -20), new JArray(0, 40, -20))
            }));
            string id = (string)created["area"]["id"];

            var listed = Data(GetWorkArea.HandleCommand(new JObject()));
            Assert.AreEqual(1, (int)listed["count"]);
            Assert.AreEqual(id, (string)listed["active_id"]);
            var area = listed["areas"][0];
            Assert.AreEqual("polygon", (string)area["kind"]);
            Assert.AreEqual(600.0, (double)area["area_m2"], 1e-3);
            CollectionAssert.AreEqual(new[] { 30.0, 39.0, -20.0 }, area["vertices"][2].Select(v => (double)v).ToArray());
            Assert.IsTrue(File.Exists(file), "Areas are kept in the store file");

            Data(ManageWorkArea.HandleCommand(new JObject
            {
                ["action"] = "update", ["area"] = "Dock",
                ["vertices"] = new JArray(new JArray(0, 38, 0), new JArray(40, 38, 0), new JArray(40, 39, -20), new JArray(0, 40, -20)),
                ["y_range"] = new JArray(30, 60)
            }));
            var got = Data(GetWorkArea.HandleCommand(new JObject { ["action"] = "get", ["area"] = "active" }))["areas"][0];
            Assert.AreEqual(40.0, (double)got["vertices"][1][0], 1e-3);
            Assert.AreEqual(800.0, (double)got["area_m2"], 1e-3);
            Assert.AreEqual("manual", (string)got["height_range"]["mode"]);
            Assert.AreEqual(60.0, (double)got["height_range"]["max_y"], 1e-3);

            Data(ManageWorkArea.HandleCommand(new JObject { ["action"] = "delete", ["area"] = id }));
            Assert.AreEqual(0, (int)Data(GetWorkArea.HandleCommand(new JObject()))["count"]);
            Assert.IsFalse(File.Exists(file), "An empty store leaves no file behind");
            Assert.IsFalse(scene.isDirty, "Work areas never touch the scene");
        }

        [Test]
        public void CreateRect_KeepsItsFrame_AndUndoRestoresIt()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var created = Data(ManageWorkArea.HandleCommand(new JObject
            {
                ["action"] = "create", ["kind"] = "rect", ["center"] = new JArray(512, -300), ["size"] = new JArray(40, 20), ["yaw"] = 90
            }))["area"];
            Assert.AreEqual("rect", (string)created["kind"]);
            Assert.AreEqual(800.0, (double)created["area_m2"], 1e-2);
            Assert.AreEqual(90.0, (double)created["rect"]["yaw_deg"], 1e-3);
            // Turned 90°: 40 m along z, 20 m along x.
            Assert.AreEqual(20.0, (double)created["bounds"]["size"][0], 1e-2);
            Assert.AreEqual(40.0, (double)created["bounds"]["size"][2], 1e-2);

            Undo.IncrementCurrentGroup();
            Data(ManageWorkArea.HandleCommand(new JObject { ["action"] = "update", ["area"] = "active", ["size"] = new JArray(10, 10) }));
            Assert.AreEqual(100.0, (double)Data(GetWorkArea.HandleCommand(new JObject()))["areas"][0]["area_m2"], 1e-2);
            Undo.PerformUndo();
            Assert.AreEqual(800.0, (double)Data(GetWorkArea.HandleCommand(new JObject()))["areas"][0]["area_m2"], 1e-2);
        }

        [Test]
        public void StoreReloadsTheFile_AndSetActiveAndClearWork()
        {
            Data(ManageWorkArea.HandleCommand(new JObject { ["action"] = "create", ["center"] = new JArray(0, 0), ["size"] = new JArray(5, 5) }));
            Data(ManageWorkArea.HandleCommand(new JObject
            {
                ["action"] = "create", ["name"] = "Second", ["set_active"] = false,
                ["vertices"] = new JArray(new JArray(10, 0, 10), new JArray(20, 0, 10), new JArray(20, 0, 20))
            }));
            WorkAreaStore.ResetForTests();
            var listed = Data(GetWorkArea.HandleCommand(new JObject()));
            Assert.AreEqual(2, (int)listed["count"], "Areas survive a reload from the file");
            Assert.AreEqual("Area 1", (string)listed["areas"].First(a => (bool)a["active"])["name"]);

            Data(ManageWorkArea.HandleCommand(new JObject { ["action"] = "set_active", ["area"] = "second" }));
            Assert.AreEqual("Second", (string)Data(GetWorkArea.HandleCommand(new JObject { ["action"] = "get" }))["areas"][0]["name"]);

            Data(ManageWorkArea.HandleCommand(new JObject { ["action"] = "clear" }));
            Assert.AreEqual(0, (int)Data(GetWorkArea.HandleCommand(new JObject()))["count"]);
        }

        [Test]
        public void InvalidRequestsAreRejected()
        {
            Assert.IsInstanceOf<ErrorResponse>(ManageWorkArea.HandleCommand(new JObject { ["action"] = "explode" }));
            Assert.IsInstanceOf<ErrorResponse>(ManageWorkArea.HandleCommand(new JObject
            {
                ["action"] = "create", ["vertices"] = new JArray(new JArray(0, 0), new JArray(10, 10), new JArray(10, 0), new JArray(0, 10))
            }), "Crossing sides");
            Assert.IsInstanceOf<ErrorResponse>(ManageWorkArea.HandleCommand(new JObject { ["action"] = "create", ["kind"] = "rect" }));
            Assert.IsInstanceOf<ErrorResponse>(ManageWorkArea.HandleCommand(new JObject { ["action"] = "delete", ["area"] = "missing" }));
            Assert.IsInstanceOf<ErrorResponse>(GetWorkArea.HandleCommand(new JObject { ["action"] = "get" }), "No areas yet");
            Assert.IsInstanceOf<ErrorResponse>(GetWorkArea.HandleCommand(new JObject { ["action"] = "remove" }));
        }

        [Test]
        public void ToolsAreRegisteredWithTheirCapabilities()
        {
            CommandRegistry.Initialize();
            Assert.IsNotNull(CommandRegistry.GetHandler("get_work_area"));
            Assert.IsNotNull(CommandRegistry.GetHandler("manage_work_area"));
            var read = (McpForUnityToolAttribute)System.Attribute.GetCustomAttribute(typeof(GetWorkArea), typeof(McpForUnityToolAttribute));
            var write = (McpForUnityToolAttribute)System.Attribute.GetCustomAttribute(typeof(ManageWorkArea), typeof(McpForUnityToolAttribute));
            Assert.AreEqual(ToolCapability.Inspection, read.Capability);
            Assert.AreEqual(ToolCapability.ProjectAutomation, write.Capability);
        }

        private static JObject Data(object response)
        {
            Assert.IsInstanceOf<SuccessResponse>(response, JToken.FromObject(response).ToString());
            return (JObject)((SuccessResponse)response).Data;
        }

        private static List<Vector3> Points(params float[] xz)
        {
            var points = new List<Vector3>();
            for (int i = 0; i < xz.Length; i += 2) points.Add(new Vector3(xz[i], 0f, xz[i + 1]));
            return points;
        }
    }
}

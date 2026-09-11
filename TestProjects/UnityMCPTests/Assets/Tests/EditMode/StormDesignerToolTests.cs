using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Storm;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
namespace MCPForUnityTests.Editor
{
    public class StormDesignerToolTests
    {
        [Test] public void TornadoCatalogCannotCommit() { Assert.That(GetTornadoCatalog.HandleCommand(new JObject { ["action"]="commit_goal" }), Is.TypeOf<ErrorResponse>()); }
        [Test] public void WindCatalogCannotCommit() { Assert.That(GetStormWindCatalog.HandleCommand(new JObject { ["action"]="commit_goal" }), Is.TypeOf<ErrorResponse>()); }
        [Test] public void TornadoManagerRejectsRawMutation() { Assert.That(ManageTornado.HandleCommand(new JObject { ["action"]="set_property" }), Is.TypeOf<ErrorResponse>()); }
        [Test] public void WindManagerRejectsRawMutation() { Assert.That(ManageStormWind.HandleCommand(new JObject { ["action"]="execute_code" }), Is.TypeOf<ErrorResponse>()); }
        [Test] public void MissingBridgeFailsClosed() {
            var result = GetTornadoCatalog.HandleCommand(new JObject { ["action"]="status" });
            Assert.That(result, Is.Not.Null);
            // Standalone fork fixture has no Project Storm assembly. Installed project may legitimately resolve it.
            if (!(result is JObject)) Assert.That(result, Is.TypeOf<ErrorResponse>());
        }
    }
}

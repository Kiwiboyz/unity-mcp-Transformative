using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Gameplay;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor
{
    public class GameplayAuthoringToolTests
    {
        private static JObject Story(string op) => new JObject { ["domain"] = "story", ["request"] = new JObject { ["op"] = op, ["operationId"] = "t" } };
        [Test] public void ReadToolCannotMutate() { foreach (var op in new[] { "edit", "publish", "create", "route.add" }) Assert.That(GetGameplayAuthoring.HandleCommand(Story(op)), Is.TypeOf<ErrorResponse>(), op); }
        [Test] public void ManageToolRejectsUnlistedOperations() { foreach (var op in new[] { "execute_code", "set_property", "validate" }) Assert.That(ManageGameplayAuthoring.HandleCommand(Story(op)), Is.TypeOf<ErrorResponse>(), op); }
        [Test] public void UnknownDomainFailsClosed() { Assert.That(ManageGameplayAuthoring.HandleCommand(new JObject { ["domain"] = "scene", ["request"] = new JObject { ["op"] = "create" } }), Is.TypeOf<ErrorResponse>()); }
        [Test] public void AllowListsSeparateReadsFromMutations()
        {
            Assert.That(GameplayAuthoringToolBridge.Allowed("story", false, "validate"), Is.True);
            Assert.That(GameplayAuthoringToolBridge.Allowed("story", true, "validate"), Is.False);
            Assert.That(GameplayAuthoringToolBridge.Allowed("mission_designer", true, "story.test"), Is.True);
            Assert.That(GameplayAuthoringToolBridge.Allowed("mission_designer", false, "story.test"), Is.False);
        }
        [Test] public void MissingProjectBridgeFailsClosed()
        {
            var result = GetGameplayAuthoring.HandleCommand(new JObject { ["domain"] = "story", ["request"] = new JObject { ["op"] = "list" } });
            Assert.That(result, Is.Not.Null);
            // The standalone fork fixture has no Project Storm assembly; an installed project may legitimately resolve it.
            if (!(result is JObject)) Assert.That(result, Is.TypeOf<ErrorResponse>());
        }
    }
}

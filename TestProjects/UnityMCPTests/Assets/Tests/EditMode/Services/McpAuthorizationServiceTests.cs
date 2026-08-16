using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using NUnit.Framework;

namespace MCPForUnity.Editor.Tests.EditMode.Services
{
    [TestFixture]
    public class McpAuthorizationServiceTests
    {
        [SetUp]
        public void SetUp()
        {
            McpAuthorizationService.ResetApprovalsForTesting();
        }

        [Test]
        public void InspectionTool_IsAllowedWithoutProjectApproval()
        {
            var result = McpAuthorizationService.Authorize(new ToolMetadata
            {
                Name = "read_only",
                Capability = ToolCapability.Inspection
            });

            Assert.IsTrue(result.Allowed);
        }

        [Test]
        public void ProjectAutomation_RequiresLocalSessionApproval()
        {
            var result = McpAuthorizationService.Authorize(new ToolMetadata
            {
                Name = "change_scene",
                Capability = ToolCapability.ProjectAutomation
            });

            Assert.IsFalse(result.Allowed);
            Assert.AreEqual("approval_required", result.Code);
            Assert.AreEqual("ProjectAutomation", result.RequiredProfile);
        }

        [Test]
        public void UnclassifiedExtensionTool_FailsClosed()
        {
            var result = McpAuthorizationService.Authorize(new ToolMetadata
            {
                Name = "custom_host_tool",
                IsBuiltIn = false,
                Capability = ToolCapability.Unspecified
            });

            Assert.IsFalse(result.Allowed);
            Assert.AreEqual("HostSensitive", result.RequiredProfile);
        }
    }
}

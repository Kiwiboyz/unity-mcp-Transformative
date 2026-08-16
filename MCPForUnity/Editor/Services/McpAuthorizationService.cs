using System;
using System.IO;
using MCPForUnity.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Local-only authorization boundary for MCP tool execution. SessionState is
    /// deliberately used so approval survives domain reloads but ends when Unity closes.
    /// </summary>
    [InitializeOnLoad]
    internal static class McpAuthorizationService
    {
        private const string ProjectAutomationKey = "MCPForUnity.Security.ProjectAutomationApproved";
        private const string DebugExecutionExpiryKey = "MCPForUnity.Security.DebugExecutionExpiryUtcTicks";
        private const string ExternalImportRootKey = "MCPForUnity.Security.ExternalImportRoot";
        private const int DebugExecutionMinutes = 60;

        static McpAuthorizationService()
        {
            // Remove expired debug authority when the editor reloads.
            if (GetDebugExecutionExpiryUtc() <= DateTime.UtcNow)
                SessionState.SetString(DebugExecutionExpiryKey, string.Empty);
        }

        internal static McpAuthorizationResult Authorize(ToolMetadata metadata)
        {
            if (metadata == null)
                return McpAuthorizationResult.Deny("unknown_tool", "The requested MCP command is not a registered tool.");

            ToolCapability capability = ResolveCapability(metadata);
            switch (capability)
            {
                case ToolCapability.Inspection:
                    return McpAuthorizationResult.Allow();
                case ToolCapability.ProjectAutomation:
                    return IsProjectAutomationApproved
                        ? McpAuthorizationResult.Allow()
                        : McpAuthorizationResult.Deny("approval_required",
                            "Project Automation is disabled. Ask the user to enable 'MCP for Unity/Security/Enable Project Automation This Session' in Unity.",
                            "ProjectAutomation");
                case ToolCapability.DebugExecution:
                    return IsDebugExecutionApproved
                        ? McpAuthorizationResult.Allow()
                        : McpAuthorizationResult.Deny("approval_required",
                            "Debug Execution is disabled. Ask the user to enable 'MCP for Unity/Security/Enable Debug Execution For One Hour' in Unity.",
                            "DebugExecution");
                default:
                    return McpAuthorizationResult.Deny("approval_required",
                        "This tool has no approved capability classification and is blocked. Ask the user to classify it in source before enabling it.",
                        "HostSensitive");
            }
        }

        internal static ToolCapability ResolveCapability(ToolMetadata metadata)
        {
            if (metadata.Capability != ToolCapability.Unspecified)
                return metadata.Capability;

            // Existing first-party tools remain usable once the user approves the
            // automation session. New extension tools must opt in explicitly.
            return metadata.IsBuiltIn ? ToolCapability.ProjectAutomation : ToolCapability.HostSensitive;
        }

        internal static bool IsProjectAutomationApproved => SessionState.GetBool(ProjectAutomationKey, false);
        internal static bool IsDebugExecutionApproved => GetDebugExecutionExpiryUtc() > DateTime.UtcNow;

        internal static object GetSafeStatus()
        {
            return new
            {
                inspection = "enabled",
                project_automation = IsProjectAutomationApproved ? "enabled_this_unity_session" : "approval_required",
                debug_execution = IsDebugExecutionApproved ? "enabled_temporary" : "approval_required",
                external_import_root = string.IsNullOrWhiteSpace(SessionState.GetString(ExternalImportRootKey, string.Empty))
                    ? "not_approved" : "approved_this_unity_session"
            };
        }

        // Internal for focused EditMode coverage; no MCP command exposes this mutator.
        internal static void ApproveExternalImportRootForSession(string root)
        {
            SessionState.SetString(ExternalImportRootKey, string.IsNullOrWhiteSpace(root) ? string.Empty : Path.GetFullPath(root));
        }

        // Internal for deterministic EditMode coverage; no MCP command exposes it.
        internal static void ResetApprovalsForTesting()
        {
            SessionState.SetBool(ProjectAutomationKey, false);
            SessionState.SetString(DebugExecutionExpiryKey, string.Empty);
            SessionState.SetString(ExternalImportRootKey, string.Empty);
        }

        internal static bool TryResolveApprovedImportSource(string source, out string absolutePath, out string error)
        {
            absolutePath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Source path cannot be empty.";
                return false;
            }

            string normalized = source.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (IsNetworkOrDevicePath(source))
            {
                error = "Network, UNC, and device paths are not permitted for model imports.";
                return false;
            }

            try
            {
                if (source.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                {
                    absolutePath = Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
                    return true;
                }

                if (!Path.IsPathRooted(normalized))
                {
                    error = "Source path must be under Assets or an approved absolute local import root.";
                    return false;
                }

                string approvedRoot = SessionState.GetString(ExternalImportRootKey, string.Empty);
                if (string.IsNullOrWhiteSpace(approvedRoot))
                {
                    error = "approval_required: select 'MCP for Unity/Security/Approve External Import Root This Session' in Unity first.";
                    return false;
                }

                absolutePath = Path.GetFullPath(normalized);
                string root = EnsureTrailingSeparator(Path.GetFullPath(approvedRoot));
                if (!absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Source path is outside the Unity-approved external import root.";
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                error = "Source path is invalid.";
                return false;
            }
        }

        [MenuItem("MCP for Unity/Security/Enable Project Automation This Session", false, 200)]
        private static void EnableProjectAutomationThisSession()
        {
            if (EditorUtility.DisplayDialog("Enable Project Automation", "Allow MCP tools to modify this project until Unity closes? Inspection tools remain available without this approval.", "Enable this session", "Cancel"))
                SessionState.SetBool(ProjectAutomationKey, true);
        }

        [MenuItem("MCP for Unity/Security/Disable Project Automation", false, 201)]
        private static void DisableProjectAutomation()
        {
            SessionState.SetBool(ProjectAutomationKey, false);
            SessionState.SetString(DebugExecutionExpiryKey, string.Empty);
            SessionState.SetString(ExternalImportRootKey, string.Empty);
        }

        [MenuItem("MCP for Unity/Security/Enable Debug Execution For One Hour", false, 202)]
        private static void EnableDebugExecutionForOneHour()
        {
            if (!IsProjectAutomationApproved)
            {
                EditorUtility.DisplayDialog("Project Automation Required", "Enable Project Automation for this Unity session before enabling arbitrary debug code execution.", "OK");
                return;
            }
            if (EditorUtility.DisplayDialog("Enable Debug Execution", "Allow execute_code to compile and run arbitrary C# for one hour? Only enable this for a trusted local agent session.", "Enable for one hour", "Cancel"))
                SessionState.SetString(DebugExecutionExpiryKey, DateTime.UtcNow.AddMinutes(DebugExecutionMinutes).Ticks.ToString());
        }

        [MenuItem("MCP for Unity/Security/Approve External Import Root This Session", false, 203)]
        private static void ApproveExternalImportRootThisSession()
        {
            string selected = EditorUtility.OpenFolderPanel("Approve external model import root", string.Empty, string.Empty);
            if (!string.IsNullOrWhiteSpace(selected))
                ApproveExternalImportRootForSession(selected);
        }

        [MenuItem("MCP for Unity/Security/Show Policy Status", false, 220)]
        private static void ShowPolicyStatus()
        {
            string debug = IsDebugExecutionApproved ? "enabled until " + GetDebugExecutionExpiryUtc().ToLocalTime().ToString("t") : "disabled";
            EditorUtility.DisplayDialog("MCP for Unity Policy Status",
                "Inspection: enabled\nProject Automation: " + (IsProjectAutomationApproved ? "enabled this session" : "disabled") +
                "\nDebug Execution: " + debug + "\nExternal import root: " +
                (string.IsNullOrEmpty(SessionState.GetString(ExternalImportRootKey, string.Empty)) ? "none" : "approved this session"), "OK");
        }

        private static DateTime GetDebugExecutionExpiryUtc()
        {
            long ticks;
            return long.TryParse(SessionState.GetString(DebugExecutionExpiryKey, string.Empty), out ticks)
                ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        private static string EnsureTrailingSeparator(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        private static bool IsNetworkOrDevicePath(string path)
        {
            string trimmed = path.TrimStart();
            return trimmed.StartsWith("\\\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("\\\\?\\", StringComparison.Ordinal) || trimmed.StartsWith("\\\\.\\", StringComparison.Ordinal);
        }
    }

    internal readonly struct McpAuthorizationResult
    {
        internal bool Allowed { get; }
        internal string Code { get; }
        internal string Message { get; }
        internal string RequiredProfile { get; }

        private McpAuthorizationResult(bool allowed, string code, string message, string requiredProfile)
        {
            Allowed = allowed;
            Code = code;
            Message = message;
            RequiredProfile = requiredProfile;
        }

        internal static McpAuthorizationResult Allow() => new McpAuthorizationResult(true, null, null, null);
        internal static McpAuthorizationResult Deny(string code, string message, string profile = null) => new McpAuthorizationResult(false, code, message, profile);
    }
}

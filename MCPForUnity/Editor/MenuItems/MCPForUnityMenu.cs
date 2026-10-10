using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Setup;
using MCPForUnity.Editor.Tools.WorkAreas;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.MenuItems
{
    public static class MCPForUnityMenu
    {
        [MenuItem(ProductInfo.MenuRoot + "/Toggle MCP Window %#m", priority = 1)]
        public static void ToggleMCPWindow()
        {
            MCPForUnityEditorWindow.ShowWindow();
        }

        [MenuItem(ProductInfo.MenuRoot + "/Local Setup Window", priority = 2)]
        public static void ShowSetupWindow()
        {
            SetupWindowService.ShowSetupWindow();
        }


        [MenuItem(ProductInfo.MenuRoot + "/Edit EditorPrefs", priority = 3)]
        public static void ShowEditorPrefsWindow()
        {
            EditorPrefsWindow.ShowWindow();
        }

        private const string WorkAreaToolPath = ProductInfo.MenuRoot + "/Work Area Tool";

        /// <summary>Draw a rectangle or polygon in the Scene view that agents read with get_work_area.</summary>
        [MenuItem(WorkAreaToolPath, priority = 20)]
        public static void ToggleWorkAreaTool()
        {
            WorkAreaTool.Toggle();
        }

        [MenuItem(WorkAreaToolPath, true)]
        private static bool ValidateWorkAreaTool()
        {
            Menu.SetChecked(WorkAreaToolPath, WorkAreaTool.IsActive);
            return true;
        }
    }
}

using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Windows.Components.Branding
{
    /// <summary>
    /// Displays the Transformative MCP for Project Storm tornado-and-connections brand mark.
    /// The package icon is shared with Unity's Package Manager entry so the product has one
    /// consistent visual identity across its editor screens.
    /// </summary>
    public sealed class OceanMark : VisualElement
    {
        public OceanMark()
        {
            pickingMode = PickingMode.Ignore;

            Texture2D icon = LoadPackageIcon();
            if (icon == null)
                return;

            style.backgroundImage = new StyleBackground(icon);
            style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
        }

        private static Texture2D LoadPackageIcon()
        {
            string packageRoot = MCPForUnity.Editor.Helpers.AssetPathUtility.GetMcpPackageRootPath();
            return AssetDatabase.LoadAssetAtPath<Texture2D>($"{packageRoot}/package-icon.png");
        }
    }
}

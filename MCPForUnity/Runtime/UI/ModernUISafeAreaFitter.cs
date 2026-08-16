using UnityEngine;

namespace MCPForUnity.Runtime.UI
{
    /// <summary>Anchors a UI container to the current device safe area without editor-only dependencies.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class ModernUISafeAreaFitter : MonoBehaviour
    {
        [SerializeField] private RectTransform target;
        [SerializeField] private bool applyInEditor = true;
        private Rect lastSafeArea;
        private Vector2Int lastScreenSize;

        private void OnEnable() => Apply();
        private void OnValidate() => Apply();

        private void Update()
        {
            if (!Application.isPlaying && !applyInEditor) return;
            if (lastSafeArea != Screen.safeArea || lastScreenSize.x != Screen.width || lastScreenSize.y != Screen.height) Apply();
        }

        public void Apply()
        {
            target ??= transform as RectTransform;
            if (target == null || Screen.width <= 0 || Screen.height <= 0) return;
            var safeArea = Screen.safeArea;
            target.anchorMin = new Vector2(safeArea.xMin / Screen.width, safeArea.yMin / Screen.height);
            target.anchorMax = new Vector2(safeArea.xMax / Screen.width, safeArea.yMax / Screen.height);
            target.offsetMin = Vector2.zero;
            target.offsetMax = Vector2.zero;
            lastSafeArea = safeArea;
            lastScreenSize = new Vector2Int(Screen.width, Screen.height);
        }
    }
}

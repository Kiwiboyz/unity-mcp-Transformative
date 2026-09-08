using System.Collections.Generic;
using UnityEngine;

namespace MCPForUnityTests.Editor
{
    // Serialization-shape doubles only; they do not reproduce vendor implementation code.
    public class CreativeCloudVolume : MonoBehaviour
    {
        public float m_coverage = 0.2f;
        public float m_density = 100f;
        public Vector3 m_wind = Vector3.zero;
    }

    public class CreativeFog : MonoBehaviour
    {
        public float m_visibilityDistance = 1000f;
        public bool m_receiveDensityParticles;
    }

    public class StormVisualProfile : ScriptableObject
    {
        public AnimationCurve cloudCoverageByIntensity = AnimationCurve.Linear(0, 0, 1, 1);
        public bool lightningEnabled;
        public List<float> intensityThresholds = new List<float> { 0.25f, 0.75f };
    }
}

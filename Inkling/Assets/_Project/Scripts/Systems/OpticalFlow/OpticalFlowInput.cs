using UnityEngine;
using Magi.Inkling.Services;
using Magi.Inkling.Services.Core;

namespace Magi.Inkling.Systems.OpticalFlow
{
    /// <summary>
    /// Optional optical-flow-based force injector.
    /// Expects a flow texture (RG flow in UV space) and injects forces into the simulation.
    /// </summary>
    [DefaultExecutionOrder(-45)]
    public class OpticalFlowInput : MonoBehaviour, IService
    {
        [SerializeField] private Texture2D flowTexture;
        [SerializeField] private MonoBehaviour simulationWriterSource;
        [SerializeField] [Range(0f, 500f)] private float forceMultiplier = 50f;
        [SerializeField] private bool enabledModule = false;
        [SerializeField] private float sampleU = 0.5f;
        [SerializeField] private float sampleV = 0.5f;

        private ISimulationWriter writer;

        private void Awake()
        {
            if (simulationWriterSource is ISimulationWriter w)
                writer = w;

            var locator = ServiceLocator.Instance;
            if (locator != null)
            {
                locator.RegisterService(this);
            }
        }

        private void Update()
        {
            if (!enabledModule || writer == null || flowTexture == null) return;

            // Sample a few points and inject average flow as force
            Color flow = flowTexture.GetPixelBilinear(sampleU, sampleV);
            Vector2 dir = new Vector2(flow.r * 2f - 1f, flow.g * 2f - 1f);
            writer.InjectForce(new Vector2(0.5f, 0.5f), dir * forceMultiplier);
        }
    }
}

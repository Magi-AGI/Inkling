using UnityEngine;
using Magi.UnityTools.Patterns;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Services.Diagnostics
{
    /// <summary>
    /// Wires simulation velocity outputs into debug renderers (tracers, arrows, masks, splits, stats).
    /// Avoids manual scene assignment; toggle renderers individually.
    /// </summary>
    public class SimulationDebugBinder : MonoBehaviour
    {
        [SerializeField] private TracerSystem tracerSystem;
#if INKTOOLS_DEBUG
        [SerializeField] private VelocityArrowsRenderer arrowsRenderer;
        [SerializeField] private VelocityMaskRenderer maskRenderer;
        [SerializeField] private SplitVelocityRenderer splitRenderer;
        [SerializeField] private PressureOverlayRenderer pressureRenderer;
#endif
        [SerializeField] private VelocityStatsSystem statsSystem;

        private ISimulationReader sim;

        private void Awake()
        {
            sim = ServiceLocator.Instance?.Resolve<ISimulationReader>();
            if (tracerSystem == null) tracerSystem = FindAnyObjectByType<TracerSystem>();
#if INKTOOLS_DEBUG
            if (arrowsRenderer == null) arrowsRenderer = FindAnyObjectByType<VelocityArrowsRenderer>();
            if (maskRenderer == null) maskRenderer = FindAnyObjectByType<VelocityMaskRenderer>();
            if (splitRenderer == null) splitRenderer = FindAnyObjectByType<SplitVelocityRenderer>();
            if (pressureRenderer == null) pressureRenderer = FindAnyObjectByType<PressureOverlayRenderer>();
#endif
            if (statsSystem == null) statsSystem = FindAnyObjectByType<VelocityStatsSystem>();
        }

        private void LateUpdate()
        {
            if (sim == null) return;
            var velRT = sim.GetVelocityTexture();
            if (velRT == null) return;

            tracerSystem?.SetVelocityTexture(velRT);
#if INKTOOLS_DEBUG
            arrowsRenderer?.SetVelocityTexture(velRT);
            maskRenderer?.SetVelocityTexture(velRT);
            splitRenderer?.SetVelocityTexture(velRT);
            pressureRenderer?.SetVelocityTexture(velRT);
#endif
            if (statsSystem != null)
            {
                statsSystem.enabled = true; // ensure Update runs
            }
        }
    }
}

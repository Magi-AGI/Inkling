using UnityEngine;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.Helpers
{
    /// <summary>
    /// Shared test stub for ISimulationReader. MonoBehaviour so existing tests
    /// can use AddComponent&lt;StubSimulationReader&gt;().
    /// Replaces inline StubReader classes duplicated across CaptureServiceTests,
    /// CaptureMetadataLogsTests, etc.
    /// </summary>
    public class StubSimulationReader : MonoBehaviour, ISimulationReader
    {
        public RenderTexture Texture;
        public int SimResolution = 256;
        public float SimTimestep = 0.016f;
        public float SimViscosity = 0.0001f;
        public float SimVorticity = 5f;
        public float SimDissipation = 0.999f;
        public float SimVelocityDissipation = 0.99f;

        public int Resolution => SimResolution;
        public float Timestep => SimTimestep;
        public float Viscosity => SimViscosity;
        public float Vorticity => SimVorticity;
        public float Dissipation => SimDissipation;
        public float VelocityDissipation => SimVelocityDissipation;

        public RenderTexture GetDensityTexture() => Texture;
        public RenderTexture GetVelocityTexture() => null;
        public RenderTexture GetDisplayTexture() => Texture;
        public RenderTexture GetObstacleTexture() => null;
        public ComputeBuffer GetParticleBuffer() => null;
        public float GetLastFrameMs() => 0f;

        public (float advection, float diffusion, float pressure, float projection, float vorticity)
            GetDetailedTimings() => (0f, 0f, 0f, 0f, 0f);
    }
}

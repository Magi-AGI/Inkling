using UnityEngine;

namespace Magi.Inkling.Services
{
    /// <summary>
    /// Read-only interface for simulation state access.
    /// Used by systems that only need to observe the simulation (display, recording, etc.)
    /// </summary>
    public interface ISimulationReader
    {
        /// <summary>
        /// Simulation grid resolution (width and height in pixels).
        /// </summary>
        int Resolution { get; }

        /// <summary>
        /// Simulation timestep in seconds.
        /// </summary>
        float Timestep { get; }

        /// <summary>
        /// Global viscosity coefficient for fluid diffusion.
        /// </summary>
        float Viscosity { get; }

        /// <summary>
        /// Global vorticity confinement strength.
        /// </summary>
        float Vorticity { get; }

        /// <summary>
        /// Global density dissipation rate (0-1, higher = slower fade).
        /// </summary>
        float Dissipation { get; }

        /// <summary>
        /// Velocity field dissipation rate (0-1, higher = velocity persists longer).
        /// </summary>
        float VelocityDissipation { get; }

        /// <summary>
        /// Gets the density field render texture (ARGB, contains ink concentrations).
        /// </summary>
        RenderTexture GetDensityTexture();

        /// <summary>
        /// Gets the velocity field render texture (RG channels = velocity XY).
        /// </summary>
        RenderTexture GetVelocityTexture();

        /// <summary>
        /// Gets the final display render texture (post-gradient, composited).
        /// </summary>
        RenderTexture GetDisplayTexture();

        /// <summary>
        /// Gets the obstacle texture (RFloat, 1.0 = obstacle, 0.0 = free).
        /// Used by ObstacleSystem for GPU-based obstacle stamping.
        /// </summary>
        RenderTexture GetObstacleTexture();

        /// <summary>
        /// Gets the particle compute buffer for reading (iparticle structs).
        /// Used by systems that need to read particle data on GPU.
        /// </summary>
        ComputeBuffer GetParticleBuffer();

        /// <summary>
        /// Gets the total frame time for the last simulation frame in milliseconds.
        /// </summary>
        float GetLastFrameMs();

        /// <summary>
        /// Gets detailed per-pass timing breakdown.
        /// </summary>
        /// <returns>Tuple of (advection, diffusion, pressure, projection, vorticity) times in ms.</returns>
        (float advection, float diffusion, float pressure, float projection, float vorticity) GetDetailedTimings();
    }
}

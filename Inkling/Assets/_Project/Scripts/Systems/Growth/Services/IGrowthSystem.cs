using UnityEngine;

namespace Magi.Inkling.Systems.Growth
{
    /// <summary>
    /// Service interface for the seed/growth system.
    /// Manages the conversion of seeded particles to grown particles.
    /// </summary>
    public interface IGrowthSystem
    {
        /// <summary>Whether the growth system is initialized and ready.</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Plants a seed at the specified UV position.
        /// Seeds are placed into the particle buffer and will grow over time.
        /// </summary>
        /// <param name="uvPosition">Position in UV space [0,1]</param>
        /// <param name="seedType">Type of seed to plant</param>
        /// <param name="amount">Amount of seed to plant (default 1.0)</param>
        void PlantSeed(Vector2 uvPosition, SeedType seedType, float amount = 1f);

        /// <summary>
        /// Plants seeds in a region with random distribution.
        /// </summary>
        /// <param name="uvRegion">Region to plant in</param>
        /// <param name="count">Number of seeds to plant</param>
        /// <param name="seedType">Type of seed to plant</param>
        /// <param name="amount">Amount per seed</param>
        void PlantSeedsInRegion(Rect uvRegion, int count, SeedType seedType, float amount = 1f);

        /// <summary>
        /// Gets the current growth configuration.
        /// </summary>
        GrowthConfig Config { get; }

        /// <summary>
        /// Sets growth parameters at runtime.
        /// </summary>
        /// <param name="plantRate">Plant growth rate per second</param>
        /// <param name="electricityRate">Electricity growth rate per second</param>
        void SetGrowthRates(float plantRate, float electricityRate);
    }
}

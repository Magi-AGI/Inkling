using UnityEngine;

namespace Magi.Inkling.Services
{
    /// <summary>
    /// Write interface for simulation injection and stamping operations.
    /// Used by systems that inject ink or forces into the simulation (creatures, input, etc.)
    /// </summary>
    public interface ISimulationWriter
    {
        /// <summary>
        /// Injects a directional force at the specified UV position.
        /// </summary>
        /// <param name="position">UV position (0-1 range)</param>
        /// <param name="force">Force vector to apply</param>
        void InjectForce(Vector2 position, Vector2 force);

        /// <summary>
        /// Injects density (ink) at the specified UV position with a Gaussian falloff.
        /// </summary>
        /// <param name="position">UV position (0-1 range)</param>
        /// <param name="color">Color/density to inject</param>
        /// <param name="inkTypeIndex">Raw iparticle field index (0-9) matching InkTypeId enum.
        /// Use: Fire=0, Water=1, PlantSeeded=2, PlantGrown=3, Steam=4, Glitter=5,
        /// BlackBody=6, ElectricitySeeded=7, ElectricityGrown=8, Ice=9</param>
        void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0);

        /// <summary>
        /// Stamps a texture pattern onto the density field at the specified UV position.
        /// </summary>
        /// <param name="uvPosition">Center UV position for the stamp</param>
        /// <param name="stamp">Texture to stamp</param>
        /// <param name="densityMultiplier">Scalar multiplier for stamp intensity</param>
        /// <param name="useColorOverride">If true, use overrideColor instead of texture colors</param>
        /// <param name="overrideColor">Color to use when useColorOverride is true</param>
        void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier,
                          bool useColorOverride, Color overrideColor);

        /// <summary>
        /// Clears density in regions where the mask has low luminance (black areas).
        /// Used for carving out solid regions in creatures.
        /// </summary>
        /// <param name="uvPosition">Center UV position for the mask</param>
        /// <param name="mask">Texture mask where black regions will clear density</param>
        /// <param name="blackLuminanceThreshold">Luminance threshold below which density is cleared</param>
        void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f);

        /// <summary>
        /// Stamps obstacle regions into the simulation boundary conditions.
        /// </summary>
        /// <param name="uvPosition">Center UV position for the obstacle stamp</param>
        /// <param name="stamp">Texture defining obstacle regions</param>
        void StampObstacles(Vector2 uvPosition, Texture2D stamp);
    }
}

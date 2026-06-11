namespace Magi.Inkling.Systems.Growth
{
    /// <summary>
    /// Types of seeds that can be planted and grown in the simulation.
    /// </summary>
    public enum SeedType
    {
        /// <summary>Plant seed - grows into plant over time</summary>
        Plant = 0,

        /// <summary>Electricity seed - grows into lightning over time</summary>
        Electricity = 1
    }
}

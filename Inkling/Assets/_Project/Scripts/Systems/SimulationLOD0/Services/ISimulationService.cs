namespace Magi.Inkling.Services
{
    /// <summary>
    /// Combined interface providing full simulation access (read + write).
    /// Used for bootstrapping, testing, or systems that need complete control.
    /// </summary>
    /// <remarks>
    /// Prefer using <see cref="ISimulationReader"/> or <see cref="ISimulationWriter"/>
    /// individually when possible to maintain cleaner dependency boundaries.
    /// </remarks>
    public interface ISimulationService : ISimulationReader, ISimulationWriter
    {
        // Combined interface - inherits all members from both interfaces.
        // No additional members needed; this exists for convenience when
        // a system genuinely needs both read and write access.
    }
}

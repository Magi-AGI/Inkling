namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Debug toggle interface for simulation air/pressure debugging.
    /// Implemented by SimDriver. Used by DiagnosticsHUD to avoid direct type coupling.
    /// </summary>
    public interface ISimulationDebug
    {
        bool DebugZeroPressure { get; set; }
        bool DebugZeroVelocity { get; set; }
        bool DebugSkipAir { get; set; }
    }
}

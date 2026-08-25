namespace Magi.Inkling.Tests.PlayMode
{
    /// <summary>
    /// CP9f Slice B1 helper: converts a test-authored float into the active ifloat storage type so direct
    /// writes into iparticle fields compile in BOTH modes. In the default float/OFF build it returns the
    /// float unchanged (no precision change); in the transient half/ON build it requests the explicit
    /// float->half conversion that C# otherwise refuses. Read-only test values only — not production code.
    /// </summary>
    internal static class IFloatTestValue
    {
#if INKTOOLS_IFLOAT_HALF
        public static Unity.Mathematics.half FromFloat(float value) => (Unity.Mathematics.half)value;
#else
        public static float FromFloat(float value) => value;
#endif
    }
}

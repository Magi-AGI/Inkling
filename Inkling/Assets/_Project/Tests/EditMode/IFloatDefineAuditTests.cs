using NUnit.Framework;
using Magi.InkTools.Editor;

namespace Magi.Inkling.Tests.EditMode
{
    /// <summary>
    /// CP9f Slice B1 — drift tripwire. The generated HLSL include (single writer) and the compiled C#
    /// INKTOOLS_IFLOAT_HALF scripting define must always agree. This test catches BOTH drift directions,
    /// especially the dangerous shader-only case (the include is ON while the C# define is OFF), which the
    /// C#-side stride guard alone cannot see.
    ///
    /// Lives in the Inkling EditMode test assembly (not InkTools' own test assembly): the toggle sets the
    /// Standalone scripting define on the ACTIVE (Inkling) project, so this test's `#if INKTOOLS_IFLOAT_HALF`
    /// must compile in an Inkling assembly to observe the real C# state. It is also the only place Unity will
    /// import and export it, since InkTools is consumed as a package and its Tests/ folder is not imported.
    /// </summary>
    public class IFloatDefineAuditTests
    {
        // Compiled truth of the C# scripting define for the assembly's build target.
        private static bool CSharpHalf =>
#if INKTOOLS_IFLOAT_HALF
            true;
#else
            false;
#endif

        [Test]
        public void GeneratedInclude_ExistsAndMarkerParses()
        {
            int mode = InkToolsIFloatModeToggle.ReadGeneratedModeHalf();
            Assert.That(mode == 0 || mode == 1,
                $"Generated include missing or INKTOOLS_IFLOAT_MODE_HALF unparseable (got {mode}). " +
                "It must always exist with an explicit 0/1 value; regenerate via InkTools > ifloat Mode.");
        }

        [Test]
        public void GeneratedInclude_AgreesWithCSharpDefine()
        {
            int mode = InkToolsIFloatModeToggle.ReadGeneratedModeHalf();
            bool includeHalf = mode == 1;
            bool csharpHalf = CSharpHalf;

            // Both drift directions are called out explicitly.
            if (csharpHalf && !includeHalf)
                Assert.Fail("Drift: C# INKTOOLS_IFLOAT_HALF is ON but the generated include is OFF " +
                            "(MODE_HALF 0). Set mode only via InkTools > ifloat Mode.");
            if (!csharpHalf && includeHalf)
                Assert.Fail("Drift (shader-only — dangerous): the generated include is ON (MODE_HALF 1) " +
                            "but the C# INKTOOLS_IFLOAT_HALF define is OFF. A shader would use half storage " +
                            "while C# assumes float. Set mode only via InkTools > ifloat Mode.");

            Assert.AreEqual(csharpHalf, includeHalf,
                "The generated include mode must equal the compiled C# INKTOOLS_IFLOAT_HALF state.");
        }
    }
}

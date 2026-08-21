using System;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Magi.InkTools.Simulation;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests.EditMode
{
    /// <summary>
    /// CP9f Slice A — default-mode (float) particle layout contract + drift detection.
    /// </summary>
    public class ParticleLayoutContractTests
    {
        private const int ExpectedFloatStride = 14 * sizeof(float); // 56

        [Test]
        public void Iparticle_Is56ByteFloatLayout()
        {
            Assert.AreEqual(ExpectedFloatStride, Marshal.SizeOf<iparticle>(),
                "iparticle must be 56 bytes in the default float baseline.");
        }

        [Test]
        public void Iparticle_FieldsResolveToFloat()
        {
            var fields = typeof(iparticle).GetFields(BindingFlags.Instance | BindingFlags.Public);
            Assert.AreEqual(14, fields.Length, "iparticle must have exactly 14 channel/color fields.");
            foreach (var f in fields)
                Assert.AreEqual(typeof(float), f.FieldType,
                    $"iparticle.{f.Name} must be float in the default baseline.");
        }

        [Test]
        public void ReadbackMirrors_MatchIparticleStride()
        {
            var inkling = typeof(SimDriver).Assembly;
            var fluidSolverMirror = inkling.GetType(
                "Magi.Inkling.Systems.SimulationLOD0.SimulationDisplay_iparticle_gpu");
            Assert.IsNotNull(fluidSolverMirror,
                "FluidSolver.SimulationDisplay_iparticle_gpu mirror type not found.");
            var displayMirror = inkling.GetType(
                "Magi.Inkling.Systems.SimulationLOD0.SimulationDisplay+iparticle_gpu");
            Assert.IsNotNull(displayMirror, "SimulationDisplay.iparticle_gpu mirror type not found.");

            int iparticleStride = Marshal.SizeOf<iparticle>();
            Assert.AreEqual(ExpectedFloatStride, Marshal.SizeOf(fluidSolverMirror),
                "FluidSolver readback mirror must be 56 bytes.");
            Assert.AreEqual(ExpectedFloatStride, Marshal.SizeOf(displayMirror),
                "SimulationDisplay readback mirror must be 56 bytes.");
            Assert.AreEqual(iparticleStride, Marshal.SizeOf(fluidSolverMirror),
                "FluidSolver mirror stride must equal the iparticle stride.");
            Assert.AreEqual(iparticleStride, Marshal.SizeOf(displayMirror),
                "SimulationDisplay mirror stride must equal the iparticle stride.");
        }

        [Test]
        public void InkToolsIFloatHalf_IsDisabledInDefaultBaseline()
        {
#if INKTOOLS_IFLOAT_HALF
            Assert.Fail("INKTOOLS_IFLOAT_HALF is defined; the Slice A baseline must be float (half is Slice B).");
#else
            Assert.Pass("INKTOOLS_IFLOAT_HALF is off (float baseline), as required for Slice A.");
#endif
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DriftedMirror // 13 floats = 52 bytes, deliberately wrong
        {
            public float a, b, c, d, e, f, g, h, i, j, k, l, m;
        }

        private static void LayoutGuard(int stride, int mirrorStride)
        {
            if (stride != ExpectedFloatStride || mirrorStride != ExpectedFloatStride)
                throw new InvalidOperationException(
                    $"iparticle layout guard FAILED: stride={stride}, mirror={mirrorStride}, expected {ExpectedFloatStride}.");
        }

        [Test]
        public void LayoutGuard_PassesForRealLayout()
        {
            int stride = Marshal.SizeOf<iparticle>();
            Assert.DoesNotThrow(() => LayoutGuard(stride, stride));
        }

        [Test]
        public void LayoutGuard_ThrowsOnDrift()
        {
            Assert.AreNotEqual(ExpectedFloatStride, Marshal.SizeOf<DriftedMirror>(),
                "DriftedMirror must differ from 56 to be a valid negative fixture.");
            Assert.Throws<InvalidOperationException>(
                () => LayoutGuard(Marshal.SizeOf<iparticle>(), Marshal.SizeOf<DriftedMirror>()));
            Assert.Throws<InvalidOperationException>(
                () => LayoutGuard(28, ExpectedFloatStride));
        }
    }
}

using NUnit.Framework;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests.EditMode
{
    public class SimulationContextTests
    {
        [Test]
        public void DefaultParticleIndices_AreZeroAndOne()
        {
            var ctx = new SimulationContext();

            Assert.AreEqual(0, ctx.ParticleReadIndex);
            Assert.AreEqual(1, ctx.ParticleWriteIndex);
        }

        [Test]
        public void SwapParticleBuffers_SwapsIndices()
        {
            var ctx = new SimulationContext();

            ctx.SwapParticleBuffers();

            Assert.AreEqual(1, ctx.ParticleReadIndex);
            Assert.AreEqual(0, ctx.ParticleWriteIndex);
        }

        [Test]
        public void SwapParticleBuffers_TwiceRestoresOriginal()
        {
            var ctx = new SimulationContext();

            ctx.SwapParticleBuffers();
            ctx.SwapParticleBuffers();

            Assert.AreEqual(0, ctx.ParticleReadIndex);
            Assert.AreEqual(1, ctx.ParticleWriteIndex);
        }

        [Test]
        public void FieldDefaults_AreZeroOrNull()
        {
            var ctx = new SimulationContext();

            Assert.AreEqual(0, ctx.Resolution);
            Assert.AreEqual(0f, ctx.Timestep);
            Assert.AreEqual(0f, ctx.Viscosity);
            Assert.IsNull(ctx.Velocity);
            Assert.IsNull(ctx.Density);
            Assert.IsNull(ctx.FluidCompute);
            Assert.IsNull(ctx.ParticlesBuffer);
        }

        [Test]
        public void FieldAssignment_Roundtrips()
        {
            var ctx = new SimulationContext();

            ctx.Resolution = 512;
            ctx.Timestep = 0.016f;
            ctx.Viscosity = 0.001f;
            ctx.VorticityStrength = 5f;
            ctx.Dissipation = 0.999f;
            ctx.PressureIterations = 40;

            Assert.AreEqual(512, ctx.Resolution);
            Assert.AreEqual(0.016f, ctx.Timestep);
            Assert.AreEqual(0.001f, ctx.Viscosity);
            Assert.AreEqual(5f, ctx.VorticityStrength);
            Assert.AreEqual(0.999f, ctx.Dissipation);
            Assert.AreEqual(40, ctx.PressureIterations);
        }
    }
}

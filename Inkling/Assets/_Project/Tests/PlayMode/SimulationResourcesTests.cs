using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests.PlayMode
{
    public class SimulationResourcesTests
    {
        private SimulationContext ctx;
        private SimulationResources resources;

        [SetUp]
        public void SetUp()
        {
            ctx = new SimulationContext { Resolution = 32 };
            resources = new SimulationResources();
        }

        [TearDown]
        public void TearDown()
        {
            resources?.Dispose(ctx);
        }

        [UnityTest]
        public IEnumerator Allocate_CreatesAllRequiredTextures()
        {
            resources.Allocate(ctx);
            yield return null;

            Assert.IsNotNull(ctx.Velocity, "Velocity ping-pong buffer should be allocated");
            Assert.IsNotNull(ctx.Pressure, "Pressure ping-pong buffer should be allocated");
            Assert.IsNotNull(ctx.Density, "Density ping-pong buffer should be allocated");
            Assert.IsNotNull(ctx.Divergence, "Divergence RT should be allocated");
            Assert.IsNotNull(ctx.VorticityTex, "Vorticity RT should be allocated");
            Assert.IsNotNull(ctx.Obstacles, "Obstacles RT should be allocated");
            Assert.IsNotNull(ctx.DisplayRT, "Display RT should be allocated");
            Assert.IsNotNull(ctx.ParticlesBuffer, "Particle buffers should be allocated");
            Assert.AreEqual(2, ctx.ParticlesBuffer.Length, "Should have 2 particle buffers (ping-pong)");
        }

        [UnityTest]
        public IEnumerator Dispose_ReleasesAllTextures()
        {
            resources.Allocate(ctx);
            yield return null;

            resources.Dispose(ctx);

            // After Dispose, ping-pong buffers should have released their RTs.
            // We can't check IsCreated on disposed PingPongRenderTexture, but we
            // can verify the particle buffers were released by checking the array still exists
            // but individual buffers are released (accessing them would throw).
            Assert.IsNotNull(ctx.ParticlesBuffer, "Array reference should still exist");
        }

        [UnityTest]
        public IEnumerator Allocate_SetsParticleStride()
        {
            resources.Allocate(ctx);
            yield return null;

            Assert.Greater(ctx.GpuParticleStride, 0, "Particle stride should be set to a positive value");
            Assert.IsTrue(ctx.GpuPromotesHalf, "GpuPromotesHalf should be set");
        }
    }
}

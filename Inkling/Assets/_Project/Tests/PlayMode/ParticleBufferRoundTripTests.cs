using System.Collections;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.PlayMode
{
    /// <summary>
    /// CP9f Slice A — GPU round-trip through StructuredBuffer&lt;iparticle&gt; in default float mode.
    /// </summary>
    public class ParticleBufferRoundTripTests
    {
        // Distinct, half-exact fractions (k/16) so the same sentinels stay exact if Slice B flips to half.
        // Order matches the got[] read order below: fire..ice (0-9), metal (index 10), then red..alpha.
        private static readonly float[] Sentinels =
        {
            1f / 16f, 2f / 16f, 3f / 16f, 4f / 16f, 5f / 16f, 6f / 16f, 7f / 16f,
            8f / 16f, 9f / 16f, 10f / 16f, 15f / 16f, 11f / 16f, 12f / 16f, 13f / 16f, 14f / 16f
        };

        [UnityTest]
        public IEnumerator ParticleBuffer_RoundTripsAllChannels()
        {
            var probe = Resources.Load<ComputeShader>("ParticleRoundTripProbe");
            Assert.IsNotNull(probe, "ParticleRoundTripProbe.compute not found under a Resources folder.");
            int kernel = probe.FindKernel("WriteSentinels");

            // Let PlayMode advance one frame BEFORE the dispatch; the readback below is then fully
            // synchronous (WaitAllRequests) so its NativeArray is consumed in the same frame it completes.
            yield return null;

            const int count = 64;
            int stride = Marshal.SizeOf<iparticle>();
            var buffer = new ComputeBuffer(count, stride, ComputeBufferType.Structured);
            try
            {
                probe.SetBuffer(kernel, "_Particles", buffer);
                probe.SetInt("_Count", count);
                probe.Dispatch(kernel, Mathf.CeilToInt(count / 64f), 1, 1);

                var request = AsyncGPUReadback.Request(buffer);
                AsyncGPUReadback.WaitAllRequests();
                // Read/copy/assert in the SAME frame the readback completes — the request's NativeArray is
                // only guaranteed valid until the next frame, so no yield may intervene between here and the
                // GetData/sentinel copy below.
                Assert.IsFalse(request.hasError, "AsyncGPUReadback reported an error.");

                var data = request.GetData<iparticle>();
                var particle = data[0];
                float[] got =
                {
                    particle.fire, particle.water, particle.plantSeeded, particle.plantGrown,
                    particle.steam, particle.glitter, particle.blackBody,
                    particle.electricitySeeded, particle.electricityGrown, particle.ice,
                    particle.metal,
                    particle.red, particle.green, particle.blue, particle.alpha
                };

                for (int channel = 0; channel < Sentinels.Length; channel++)
                    Assert.AreEqual(Sentinels[channel], got[channel], 1e-6f,
                        $"Channel {channel} did not round-trip (expected {Sentinels[channel]}, got {got[channel]}).");
            }
            finally
            {
                buffer.Release();
            }
        }
    }
}

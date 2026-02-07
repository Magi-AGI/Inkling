using System.IO;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Magi.Inkling.Systems.Capture;

namespace Magi.Inkling.Tests.PlayMode
{
    public class CaptureServiceTests
    {
        [UnityTest]
        public IEnumerator Capture_WritesPngAndJson()
        {
            // Create a 4x4 RT with a known color
            var rt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = false
            };
            rt.Create();
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.magenta);
            RenderTexture.active = null;

            // Temp output directory
            string dir = Path.Combine(Path.GetTempPath(), "inkling_capture_test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            // Dummy reader that returns our RT
            var go = new GameObject("CaptureTest");
            var service = go.AddComponent<CaptureService>();
            var cfg = ScriptableObject.CreateInstance<CaptureConfig>();
            cfg.outputPath = dir;
            cfg.captureSimulationBuffer = false;
            cfg.resolutionScale = 1f;

            // Inject config and a stub reader via subclass
            var stub = go.AddComponent<StubReader>();
            stub.Texture = rt;
            service.GetType().GetField("config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(service, cfg);
            service.GetType().GetField("reader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(service, stub);

            service.CaptureRenderTexture(rt, Path.Combine(dir, "capture.png"));
            service.GetType().GetMethod("WriteMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(service, new object[] { Path.Combine(dir, "capture.json"), rt });

            yield return null; // allow async readback to finish

            Assert.IsTrue(File.Exists(Path.Combine(dir, "capture.png")), "PNG not written");
            Assert.IsTrue(File.Exists(Path.Combine(dir, "capture.json")), "JSON not written");

            // Clean up
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(cfg);
            Object.DestroyImmediate(go);
            Directory.Delete(dir, true);
        }

        // TODO: migrate to Helpers.StubSimulationReader
        private class StubReader : MonoBehaviour, Magi.InkTools.Simulation.ISimulationReader
        {
            public RenderTexture Texture;
            public float Timestep => 0f;
            public float Viscosity => 0f;
            public float Vorticity => 0f;
            public float Dissipation => 0f;
            public float VelocityDissipation => 0f;
            public RenderTexture GetDensityTexture() => Texture;
            public RenderTexture GetVelocityTexture() => null;
            public RenderTexture GetDisplayTexture() => Texture;
            public RenderTexture GetObstacleTexture() => null;
            public UnityEngine.ComputeBuffer GetParticleBuffer() => null;
            public int Resolution => Texture ? Texture.width : 0;
            public float GetLastFrameMs() => 0f;
            public (float advection, float diffusion, float pressure, float projection, float vorticity) GetDetailedTimings() => (0, 0, 0, 0, 0);
        }
    }
}

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Magi.Inkling.Systems.Capture;

namespace Magi.Inkling.Tests.PlayMode
{
    public class CaptureMetadataLogsTests
    {
        [UnityTest]
        public IEnumerator CaptureWritesStatusAndLogs()
        {
            var go = new GameObject("CaptureMetaTest");
            var service = go.AddComponent<CaptureService>();
            var cfg = ScriptableObject.CreateInstance<CaptureConfig>();
            cfg.outputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkling_capture_meta_test");
            cfg.captureSimulationBuffer = false;
            cfg.resolutionScale = 1f;

            // Inject config and stub reader
            var stubReader = go.AddComponent<StubReader>();
            stubReader.Texture = MakeTex(Color.cyan);
            service.GetType().GetField("config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, cfg);
            service.GetType().GetField("reader", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, stubReader);

            service.CaptureFrame();
            yield return null;

            var jsonPath = System.IO.Path.Combine(cfg.outputPath, $"capture_{Time.frameCount:D06}.json");
            Assert.IsTrue(System.IO.File.Exists(jsonPath), "JSON metadata not written");
            var json = System.IO.File.ReadAllText(jsonPath);
            Assert.IsTrue(json.Contains("captureStatus"), "Metadata missing captureStatus");
            Assert.IsTrue(json.Contains("logs"), "Metadata missing logs array");

            // Cleanup
            Object.DestroyImmediate(cfg);
            Object.DestroyImmediate(stubReader.Texture);
            Object.DestroyImmediate(go);
            if (System.IO.Directory.Exists(cfg.outputPath)) System.IO.Directory.Delete(cfg.outputPath, true);
        }

        private RenderTexture MakeTex(Color c)
        {
            var rt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            RenderTexture.active = rt;
            GL.Clear(true, true, c);
            RenderTexture.active = null;
            return rt;
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
            public (float advection, float diffusion, float pressure, float projection, float vorticity) GetDetailedTimings() => (0,0,0,0,0);
        }
    }
}

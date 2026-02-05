using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Magi.InkTools.Simulation;
using Magi.UnityTools.Patterns;
using Magi.Inkling.Services.ITUMS;
using Magi.InkTools.ITUMS;

namespace Magi.Inkling.Tests.PlayMode
{
    public class ITUMSEventLoggerTests
    {
        private class StubSimReader : MonoBehaviour, ISimulationReader, IService
        {
            public int resolution = 640;
            public int Resolution => resolution;
            public float Timestep => 0.016f;
            public float Viscosity => 0.1f;
            public float Vorticity => 0.2f;
            public float Dissipation => 0.99f;
            public float VelocityDissipation => 0.98f;
            public RenderTexture GetDensityTexture() => null;
            public RenderTexture GetVelocityTexture() => null;
            public RenderTexture GetDisplayTexture() => null;
            public RenderTexture GetObstacleTexture() => null;
            public ComputeBuffer GetParticleBuffer() => null;
            public float GetLastFrameMs() => 1f;
            public (float advection, float diffusion, float pressure, float projection, float vorticity) GetDetailedTimings()
                => (0f, 0f, 0f, 0f, 0f);
        }

        private class StubPersonaService : MonoBehaviour, IPersonaService, IService
        {
            public Persona CurrentPersona { get; private set; } = Persona.Normal;
            public float QuietScore { get; private set; }
            public float AggressiveScore { get; private set; }
            public event PersonaChanged OnPersonaChanged;

            public void RecordIdle(float deltaSeconds) => QuietScore += deltaSeconds;
            public void RecordStrokeSpeed(float speed) => AggressiveScore = speed;

            public void SetPersona(Persona persona)
            {
                var prev = CurrentPersona;
                CurrentPersona = persona;
                OnPersonaChanged?.Invoke(prev, CurrentPersona, QuietScore, AggressiveScore);
            }
        }

        [Test]
        public void NewEvent_IncludesEnvelopeFields()
        {
            // Service locator and stubs
            var locatorGo = new GameObject("ServiceLocator");
            var locator = locatorGo.AddComponent<ServiceLocator>();

            var simGo = new GameObject("SimReader");
            var sim = simGo.AddComponent<StubSimReader>();
            locator.RegisterService(sim);

            var personaGo = new GameObject("PersonaService");
            var persona = personaGo.AddComponent<StubPersonaService>();
            locator.RegisterService(persona);

            // Logger under test
            var cfg = ScriptableObject.CreateInstance<ITUMSConfig>();
            cfg.enableEventLogging = true;
            cfg.writeJsonlFile = false;

            var loggerGo = new GameObject("ITUMSLogger");
            loggerGo.SetActive(false);
            var logger = loggerGo.AddComponent<ITUMSEventLogger>();
            SetField(logger, "config", cfg);
            SetField(logger, "personaServiceSource", persona);
            SetField(logger, "buildVersionOverride", "test-build");
            SetField(logger, "simResolutionOverride", 0);
            loggerGo.SetActive(true); // triggers Awake

            var evt = InvokeNewEvent(logger, "stroke_sample");

            Assert.AreEqual("stroke_sample", evt["type"]);
            Assert.AreEqual("test-build", evt["buildVersion"]);
            Assert.AreEqual(sim.Resolution, evt["simResolution"]);
            Assert.AreEqual(persona.CurrentPersona.ToString(), evt["persona"]);
            Assert.IsTrue(evt.ContainsKey("sessionId"));
            Assert.IsTrue(evt.ContainsKey("timestamp"));
            Assert.IsTrue(evt.ContainsKey("frame"));

            // cleanup
            Object.DestroyImmediate(loggerGo);
            Object.DestroyImmediate(personaGo);
            Object.DestroyImmediate(simGo);
            Object.DestroyImmediate(locatorGo);
            ResetServiceLocator();
        }

        private static Dictionary<string, object> InvokeNewEvent(ITUMSEventLogger logger, string type)
        {
            var method = typeof(ITUMSEventLogger).GetMethod("NewEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            return (Dictionary<string, object>)method.Invoke(logger, new object[] { type });
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            field?.SetValue(target, value);
        }

        private static void ResetServiceLocator()
        {
            var prop = typeof(ServiceLocator).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            prop?.SetValue(null, null);
        }
    }
}

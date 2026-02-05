using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Magi.InkTools.Simulation;
using Magi.UnityTools.Patterns;
using Magi.Inkling.Services.Diagnostics;
using Magi.InkTools.ITUMS;

namespace Magi.Inkling.Services.ITUMS
{
    /// <summary>
    /// Lightweight ITUMS event logger (JSONL + LogSink).
    /// Non-intrusive: add to scene if enableEventLogging is desired.
    /// </summary>
    public class ITUMSEventLogger : MonoBehaviour, IService
    {
        [SerializeField] private ITUMSConfig config;
        [SerializeField] private MonoBehaviour personaServiceSource; // optional explicit reference
        [Header("Context")]
        [SerializeField] private string buildVersionOverride = string.Empty;
        [SerializeField] private int simResolutionOverride = 0;

        private IPersonaService personaService;
        private ISimulationReader simReader;
        private string sessionId;
        private string jsonlPath;
        private string buildVersion;
        private int simResolution;

        private void Awake()
        {
            sessionId = Guid.NewGuid().ToString();
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<ITUMSConfig>();
            }

            personaService = personaServiceSource as IPersonaService ??
                             ServiceLocator.Instance?.Resolve<IPersonaService>();
            simReader = ServiceLocator.Instance?.Resolve<ISimulationReader>();

            buildVersion = string.IsNullOrWhiteSpace(buildVersionOverride) ? Application.version : buildVersionOverride;
            simResolution = simResolutionOverride > 0 ? simResolutionOverride : simReader?.Resolution ?? 0;

            if (config.enableEventLogging && config.writeJsonlFile)
            {
                jsonlPath = Path.Combine(Application.persistentDataPath, config.jsonlFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);
            }
        }

        private void OnEnable()
        {
            if (personaService != null)
            {
                personaService.OnPersonaChanged += OnPersonaChanged;
            }
        }

        private void OnDisable()
        {
            if (personaService != null)
            {
                personaService.OnPersonaChanged -= OnPersonaChanged;
            }
        }

        private void OnPersonaChanged(Persona prev, Persona current, float quietScore, float avgStroke)
        {
            if (!config.enableEventLogging) return;
            var evt = NewEvent("persona_transition");
            evt["from"] = prev.ToString();
            evt["to"] = current.ToString();
            evt["quietSeconds"] = quietScore;
            evt["avgStrokeSpeed"] = avgStroke;
            evt["evalWindowSeconds"] = (personaService as PersonaService)?.Config?.evaluationPeriodSeconds ?? 0f;
            Emit(evt);
        }

        public void LogStrokeSample(Vector2 uvStart, Vector2 uvEnd, float speedUvPerSec, bool mirror)
        {
            if (!config.enableEventLogging || !config.logStrokeSamples) return;
            var evt = NewEvent("stroke_sample");
            evt["uvStart"] = uvStart;
            evt["uvEnd"] = uvEnd;
            evt["speedUvPerSec"] = speedUvPerSec;
            evt["mirror"] = mirror;
            evt["distance"] = (uvEnd - uvStart).magnitude;
            Emit(evt);
        }

        public void LogIdleTick(float deltaSeconds)
        {
            if (!config.enableEventLogging || !config.logIdleTicks) return;
            var evt = NewEvent("idle_tick");
            evt["deltaSeconds"] = deltaSeconds;
            Emit(evt);
        }

        public void LogAdaptiveResponse(string responseType, Persona persona, float value, string source)
        {
            if (!config.enableEventLogging || !config.logAdaptiveResponses) return;
            var evt = NewEvent("adaptive_response");
            evt["responseType"] = responseType;
            evt["persona"] = persona.ToString();
            evt["value"] = value;
            evt["source"] = source;
            Emit(evt);
        }

        public void LogGestureRecognized(string templateId, float score, string action)
        {
            if (!config.enableEventLogging || !config.logGestureRecognized) return;
            var evt = NewEvent("gesture_recognized");
            evt["templateId"] = templateId;
            evt["score"] = score;
            evt["action"] = action;
            Emit(evt);
        }

        private Dictionary<string, object> NewEvent(string type)
        {
            if (simResolution == 0 && simReader != null)
                simResolution = simReader.Resolution;

            var evt = new Dictionary<string, object>
            {
                ["type"] = type,
                ["sessionId"] = sessionId,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["frame"] = Time.frameCount,
                ["buildVersion"] = buildVersion,
                ["simResolution"] = simResolution
            };

            if (personaService != null)
                evt["persona"] = personaService.CurrentPersona.ToString();

            return evt;
        }

        private void Emit(Dictionary<string, object> evt)
        {
            LogSink.AddGlobal($"[ITUMS] {evt["type"]}");
            if (config.enableEventLogging && config.writeJsonlFile && !string.IsNullOrEmpty(jsonlPath))
            {
                try
                {
                    File.AppendAllText(jsonlPath, JsonUtility.ToJson(new Wrapper(evt)) + "\n");
                }
                catch (Exception e)
                {
                    LogSink.AddGlobal($"[ITUMS] Failed to write JSONL: {e.Message}");
                }
            }
        }

        // Unity's JsonUtility works on fields, so wrap dictionary values
        [Serializable]
        private class Wrapper
        {
            public Dictionary<string, object> data;
            public Wrapper(Dictionary<string, object> data) { this.data = data; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Magi.Inkling.Services.Core;
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

        private IPersonaService personaService;
        private string sessionId;
        private string jsonlPath;

        private void Awake()
        {
            sessionId = Guid.NewGuid().ToString();
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<ITUMSConfig>();
            }

            personaService = personaServiceSource as IPersonaService ??
                             ServiceLocator.Instance?.Resolve<IPersonaService>();

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
            var evt = new Dictionary<string, object>
            {
                ["type"] = "persona_transition",
                ["sessionId"] = sessionId,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["from"] = prev.ToString(),
                ["to"] = current.ToString(),
                ["quietSeconds"] = quietScore,
                ["avgStrokeSpeed"] = avgStroke
            };
            Emit(evt);
        }

        public void LogStrokeSample(Vector2 uvStart, Vector2 uvEnd, float speedUvPerSec, bool mirror)
        {
            if (!config.enableEventLogging || !config.logStrokeSamples) return;
            var evt = new Dictionary<string, object>
            {
                ["type"] = "stroke_sample",
                ["sessionId"] = sessionId,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["uvStart"] = uvStart,
                ["uvEnd"] = uvEnd,
                ["speedUvPerSec"] = speedUvPerSec,
                ["mirror"] = mirror
            };
            Emit(evt);
        }

        public void LogIdleTick(float deltaSeconds)
        {
            if (!config.enableEventLogging || !config.logIdleTicks) return;
            var evt = new Dictionary<string, object>
            {
                ["type"] = "idle_tick",
                ["sessionId"] = sessionId,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["deltaSeconds"] = deltaSeconds
            };
            Emit(evt);
        }

        public void LogAdaptiveResponse(string responseType, Persona persona, float value, string source)
        {
            if (!config.enableEventLogging || !config.logAdaptiveResponses) return;
            var evt = new Dictionary<string, object>
            {
                ["type"] = "adaptive_response",
                ["sessionId"] = sessionId,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["responseType"] = responseType,
                ["persona"] = persona.ToString(),
                ["value"] = value,
                ["source"] = source
            };
            Emit(evt);
        }

        public void LogGestureRecognized(string templateId, float score, string action)
        {
            if (!config.enableEventLogging || !config.logGestureRecognized) return;
            var evt = new Dictionary<string, object>
            {
                ["type"] = "gesture_recognized",
                ["sessionId"] = sessionId,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["templateId"] = templateId,
                ["score"] = score,
                ["action"] = action
            };
            Emit(evt);
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

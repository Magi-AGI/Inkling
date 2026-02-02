using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Magi.Inkling.Services;

namespace Magi.Inkling.Systems.Gestures
{
    /// <summary>
    /// Collects pointer strokes, runs gesture recognition, and routes actions to the simulation.
    /// This is a simple single-pointer manager; multi-touch can be added later.
    /// </summary>
    [DefaultExecutionOrder(-41)]
    public class GestureInputManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour simulationWriterSource; // ISimulationWriter
        [SerializeField] private GestureActionMap actionMap;
        [SerializeField] private List<GestureTemplate> templates = new List<GestureTemplate>();

        [Header("Thresholds")]
        [Tooltip("Minimum score to accept a gesture match (0-1).")]
        [Range(0f, 1f)]
        [SerializeField] private float minScore = 0.3f;

        private ISimulationWriter writer;
        private readonly List<Vector2> stroke = new();
        private bool collecting;

        private void Awake()
        {
            if (simulationWriterSource is ISimulationWriter w)
                writer = w;

            if (writer == null)
            {
                Debug.LogWarning("[GestureInputManager] ISimulationWriter not assigned; disabling.");
                Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("[GestureInputManager] ISimulationWriter not assigned; disabling.");
                enabled = false;
            }

            var locator = Magi.Inkling.Services.Core.ServiceLocator.Instance;
            if (locator != null)
            {
                locator.RegisterService(this);
            }
        }

        private void Update()
        {
            if (Mouse.current == null || writer == null) return;

            var mouse = Mouse.current;
            if (mouse.leftButton.wasPressedThisFrame)
            {
                stroke.Clear();
                collecting = true;
            }

            if (collecting)
            {
                Vector2 uv = new Vector2(
                    Mathf.Clamp01(mouse.position.ReadValue().x / Screen.width),
                    Mathf.Clamp01(mouse.position.ReadValue().y / Screen.height));
                stroke.Add(uv);
            }

            if (mouse.leftButton.wasReleasedThisFrame && collecting)
            {
                collecting = false;
                RecognizeAndDispatch();
            }
        }

        private void RecognizeAndDispatch()
        {
            var (template, score) = GestureRecognizer.Recognize(stroke, templates);
            if (template == null || score < minScore) return;

            if (actionMap != null && actionMap.TryGetAction(template.templateName, out string actionId))
            {
                DispatchAction(actionId);
            }
        }

        private void DispatchAction(string actionId)
        {
            // Minimal routing: examples for seeds and force line.
            switch (actionId)
            {
                case "seed.plant":
                    writer.InjectDensity(GetCentroid(), Color.white, 2); // plantSeeded
                    break;
                case "seed.electric":
                    writer.InjectDensity(GetCentroid(), Color.white, 7); // electricitySeeded
                    break;
                case "force.line":
                    InjectLineForce();
                    break;
                default:
                    break;
            }
        }

        private void InjectLineForce()
        {
            if (stroke.Count < 2) return;
            Vector2 start = stroke[0];
            Vector2 end = stroke[stroke.Count - 1];
            Vector2 dir = (end - start);
            if (dir.sqrMagnitude < 1e-6f) return;

            // Scale force by stroke length in UV space
            float forceScale = Mathf.Clamp(dir.magnitude * 200f, 0f, 300f);
            writer.InjectForce(GetCentroid(), dir.normalized * forceScale);
        }

        private Vector2 GetCentroid()
        {
            if (stroke.Count == 0) return new Vector2(0.5f, 0.5f);
            Vector2 sum = Vector2.zero;
            foreach (var p in stroke) sum += p;
            return sum / stroke.Count;
        }
    }
}

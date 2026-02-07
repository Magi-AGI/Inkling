using UnityEngine;
using UnityEngine.InputSystem;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Debug-only keyboard and mouse input for SimDriver.
    /// Handles ink type switching (number keys 1-8), reset (R key),
    /// and mouse injection (left/right button). Can be disabled in builds.
    /// </summary>
    [DefaultExecutionOrder(49)] // Run just before SimDriver (+50)
    public class SimDriverDebugInput : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour simulationWriterSource;

        [Header("Settings")]
        [SerializeField] private InkType currentInkType = InkType.Fire;
        [SerializeField] private bool autoInject = false;
        [SerializeField] private float injectionForce = 100f;

        private ISimulationWriter writer;
        private ISimulationReader reader;

        public enum InkType
        {
            Fire = 0,
            Water = 1,
            Metal = 2,
            Electricity = 3,
            Ice = 4,
            Plant = 5,
            Steam = 6,
            Dust = 7,
            Test = 8
        }

        private void Start()
        {
            if (simulationWriterSource == null)
            {
                simulationWriterSource = GetComponent<SimDriver>();
            }

            if (simulationWriterSource is ISimulationWriter w)
                writer = w;
            if (simulationWriterSource is ISimulationReader r)
                reader = r;

            if (writer == null)
            {
                Debug.LogWarning("[SimDriverDebugInput] No ISimulationWriter found; disabling.");
                enabled = false;
            }
        }

        private void Update()
        {
            if (writer == null) return;

            HandleKeyboard();
            HandleMouse();
        }

        private void HandleKeyboard()
        {
            if (Keyboard.current == null) return;

            if (Keyboard.current.digit1Key.wasPressedThisFrame) currentInkType = InkType.Fire;
            if (Keyboard.current.digit2Key.wasPressedThisFrame) currentInkType = InkType.Water;
            if (Keyboard.current.digit3Key.wasPressedThisFrame) currentInkType = InkType.Metal;
            if (Keyboard.current.digit4Key.wasPressedThisFrame) currentInkType = InkType.Electricity;
            if (Keyboard.current.digit5Key.wasPressedThisFrame) currentInkType = InkType.Ice;
            if (Keyboard.current.digit6Key.wasPressedThisFrame) currentInkType = InkType.Plant;
            if (Keyboard.current.digit7Key.wasPressedThisFrame) currentInkType = InkType.Steam;
            if (Keyboard.current.digit8Key.wasPressedThisFrame) currentInkType = InkType.Dust;

            if (Keyboard.current.rKey.wasPressedThisFrame)
            {
                Debug.Log("[SimDriverDebugInput] Reset requested via 'R' key.");
                // Reset is handled by SimDriver via ISimulationDebug or direct reference
                // For now, inject fresh density seeds
                writer.InjectDensity(new Vector2(0.5f, 0.5f), Color.white, 0);
                writer.InjectDensity(new Vector2(0.3f, 0.7f), new Color(1f, 0.5f, 0f, 1f), 0);
                writer.InjectDensity(new Vector2(0.7f, 0.3f), new Color(0f, 0.5f, 1f, 1f), 0);
            }
        }

        private void HandleMouse()
        {
            if (Mouse.current == null) return;

            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            bool isMoving = mouseDelta.magnitude > 0.01f;

            bool shouldInjectLeft = false;
            bool shouldInjectRight = false;

            if (Mouse.current.leftButton.isPressed)
                shouldInjectLeft = isMoving || Mouse.current.leftButton.wasPressedThisFrame;

            if (Mouse.current.rightButton.isPressed)
                shouldInjectRight = isMoving || Mouse.current.rightButton.wasPressedThisFrame;

            if (shouldInjectLeft || autoInject)
            {
                InjectAtMousePosition();
            }

            if (shouldInjectRight)
            {
                InjectAtMousePosition(InkType.Water);
            }
        }

        private void InjectAtMousePosition(InkType? overrideInkType = null)
        {
            if (Mouse.current == null) return;

            Vector2 mousePos = Mouse.current.position.ReadValue();
            Vector2 uv = new Vector2(
                Mathf.Clamp01(mousePos.x / Screen.width),
                Mathf.Clamp01(mousePos.y / Screen.height)
            );

            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            Vector2 velocity = new Vector2(mouseDelta.x, mouseDelta.y) * injectionForce;

            writer.InjectForce(uv, velocity);
            InkType inkType = overrideInkType ?? currentInkType;
            Color inkColor = GetInkTypeColor(inkType);
            writer.InjectDensity(uv, inkColor, GetParticleFieldIndex(inkType));
        }

        private Color GetInkTypeColor(InkType type)
        {
            switch (type)
            {
                case InkType.Fire:        return new Color(1f, 0f, 0f, 1f);
                case InkType.Water:       return new Color(0f, 1f, 0f, 1f);
                case InkType.Metal:       return new Color(0f, 0f, 1f, 1f);
                case InkType.Electricity: return new Color(0.5f, 0.5f, 1f, 1f);
                case InkType.Ice:         return new Color(0.7f, 0.9f, 1f, 1f);
                case InkType.Plant:       return new Color(0.3f, 0.8f, 0.2f, 1f);
                case InkType.Steam:       return new Color(0.9f, 0.9f, 0.9f, 0.7f);
                case InkType.Dust:        return new Color(0.7f, 0.6f, 0.5f, 0.8f);
                default:                  return Color.white;
            }
        }

        private int GetParticleFieldIndex(InkType type)
        {
            switch (type)
            {
                case InkType.Fire:        return 0;
                case InkType.Water:       return 1;
                case InkType.Plant:       return 2;
                case InkType.Metal:       return 6;
                case InkType.Steam:       return 4;
                case InkType.Dust:        return 5;
                case InkType.Electricity: return 7;
                case InkType.Ice:         return 9;
                default:                  return 0;
            }
        }

        private void OnGUI()
        {
            if (!Application.isEditor) return;

            // Show ink type below SimDriver's performance overlay
            int y = 150;
            GUI.Label(new Rect(10, y, 400, 20), $"Current Ink: {currentInkType} (Press 1-8 to change)");
            GUI.Label(new Rect(10, y + 20, 400, 20), "1=Fire, 2=Water, 3=Metal, 4=Electric, 5=Ice, 6=Plant, 7=Steam, 8=Dust");
        }
    }
}

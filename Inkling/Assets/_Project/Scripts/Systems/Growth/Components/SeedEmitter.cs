using UnityEngine;

namespace Magi.Inkling.Systems.Growth
{
    /// <summary>
    /// Component that emits seeds at its position.
    /// Can be placed on a GameObject to continuously or one-time plant seeds.
    /// </summary>
    [AddComponentMenu("Inkling/Growth/Seed Emitter")]
    public class SeedEmitter : MonoBehaviour
    {
        [Header("Seed Settings")]
        [SerializeField] private SeedType seedType = SeedType.Plant;

        [Tooltip("Amount of seed to plant (0-1)")]
        [Range(0f, 1f)]
        [SerializeField] private float amount = 0.5f;

        [Header("Emission Mode")]
        [Tooltip("If true, emits seeds continuously. If false, emits once on start.")]
        [SerializeField] private bool continuous = false;

        [Tooltip("Interval between emissions in continuous mode (seconds)")]
        [SerializeField] private float emissionInterval = 1f;

        [Header("Position Source")]
        [Tooltip("Reference to simulation surface for UV conversion. If null, uses direct UV positioning.")]
        [SerializeField] private Collider simulationCollider;

        [Header("Debug")]
        [SerializeField] private bool showGizmo = true;
        [SerializeField] private Color gizmoColor = Color.green;

        private IGrowthSystem growthSystem;
        private float lastEmissionTime;
        private bool hasEmittedOnce;

        private void Start()
        {
            _ = hasEmittedOnce; // silence unused-field warning; reserved for future state checks
            growthSystem = FindFirstObjectByType<GrowthSystem>();

            if (growthSystem == null)
            {
                Debug.LogWarning($"[SeedEmitter] No GrowthSystem found in scene. {name} will not function.");
                enabled = false;
                return;
            }

            // Emit once immediately if not continuous
            if (!continuous)
            {
                EmitSeed();
                hasEmittedOnce = true;
            }
        }

        private void Update()
        {
            if (!continuous || growthSystem == null || !growthSystem.IsInitialized) return;

            if (Time.time - lastEmissionTime >= emissionInterval)
            {
                EmitSeed();
                lastEmissionTime = Time.time;
            }
        }

        private void EmitSeed()
        {
            Vector2 uvPosition = GetUvPosition();

            // Skip if UV is invalid (off simulation surface)
            if (uvPosition.x < 0) return;

            growthSystem.PlantSeed(uvPosition, seedType, amount);
        }

        private Vector2 GetUvPosition()
        {
            if (simulationCollider != null)
            {
                // Try forward raycast
                Ray ray = new Ray(transform.position, Vector3.forward);
                if (simulationCollider.Raycast(ray, out RaycastHit hit, 100f))
                {
                    return hit.textureCoord;
                }

                // Try backward
                ray = new Ray(transform.position, Vector3.back);
                if (simulationCollider.Raycast(ray, out hit, 100f))
                {
                    return hit.textureCoord;
                }

                return new Vector2(-1, -1); // Invalid
            }

            // Fallback: treat transform position as UV directly
            return new Vector2(
                Mathf.Clamp01(transform.position.x),
                Mathf.Clamp01(transform.position.y)
            );
        }

        /// <summary>
        /// Manually trigger a seed emission.
        /// </summary>
        public void Emit()
        {
            if (growthSystem != null && growthSystem.IsInitialized)
            {
                EmitSeed();
            }
        }

        /// <summary>
        /// Change seed type at runtime.
        /// </summary>
        public void SetSeedType(SeedType type)
        {
            seedType = type;
        }

        /// <summary>
        /// Change seed amount at runtime.
        /// </summary>
        public void SetAmount(float newAmount)
        {
            amount = Mathf.Clamp01(newAmount);
        }

        private void OnDrawGizmos()
        {
            if (!showGizmo) return;
            DrawGizmos();
        }

        private void OnDrawGizmosSelected()
        {
            DrawGizmos();
        }

        private void DrawGizmos()
        {
            Gizmos.color = gizmoColor;

            // Draw position marker
            Gizmos.DrawWireSphere(transform.position, 0.05f);

            // Draw direction indicator (up = growth direction)
            Gizmos.DrawRay(transform.position, transform.up * 0.2f);

            // Different color for different seed types
            switch (seedType)
            {
                case SeedType.Plant:
                    Gizmos.color = Color.green;
                    break;
                case SeedType.Electricity:
                    Gizmos.color = Color.yellow;
                    break;
            }

            // Solid sphere at center
            Gizmos.DrawSphere(transform.position, 0.02f);
        }
    }
}

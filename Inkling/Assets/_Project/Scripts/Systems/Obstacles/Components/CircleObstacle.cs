using UnityEngine;

namespace Magi.Inkling.Systems.Obstacles
{
    /// <summary>
    /// Circular obstacle component that blocks ink flow.
    /// Registers with ObstacleSystem on Start and updates position each frame.
    /// </summary>
    [AddComponentMenu("Inkling/Obstacles/Circle Obstacle")]
    public class CircleObstacle : MonoBehaviour
    {
        [Header("Obstacle Settings")]
        [Tooltip("Use transform scale for radius (max of X, Y scale)")]
        [SerializeField] private bool useScaleAsRadius = true;

        [Tooltip("Manual radius in UV space (0-1, used when useScaleAsRadius is false)")]
        [SerializeField] private float radius = 0.05f;

        [Tooltip("Static obstacles persist; dynamic ones are cleared each frame")]
        [SerializeField] private bool isStatic = false;

        [Header("Position Source")]
        [Tooltip("Reference to simulation surface for UV conversion. If null, uses direct UV positioning.")]
        [SerializeField] private Collider simulationCollider;

        [Header("Debug")]
        [SerializeField] private bool showGizmo = true;
        [SerializeField] private Color gizmoColor = Color.cyan;

        private IObstacleSystem obstacleSystem;
        private int obstacleIndex = -1;
        private Vector2 lastUvPosition;
        private float lastRadius;

        public float Radius => useScaleAsRadius ? GetScaleRadius() : radius;

        private void Start()
        {
            // Find obstacle system
            obstacleSystem = FindFirstObjectByType<ObstacleSystem>();

            if (obstacleSystem == null)
            {
                Debug.LogWarning($"[CircleObstacle] No ObstacleSystem found in scene. {name} will not function.");
                enabled = false;
                return;
            }

            // Initial registration
            RegisterObstacle();
        }

        private void LateUpdate()
        {
            if (obstacleSystem == null || !obstacleSystem.IsInitialized) return;

            Vector2 uvPosition = GetUvPosition();
            float currentRadius = Radius;

            // Update if changed
            if (obstacleIndex >= 0 && (uvPosition != lastUvPosition || !Mathf.Approximately(currentRadius, lastRadius)))
            {
                obstacleSystem.UpdateCircleObstacle(obstacleIndex, uvPosition, currentRadius);
                lastUvPosition = uvPosition;
                lastRadius = currentRadius;
            }
            else if (obstacleIndex < 0 && !isStatic)
            {
                // Dynamic obstacle needs re-registration each frame
                RegisterObstacle();
            }
        }

        private void OnEnable()
        {
            if (obstacleSystem != null && obstacleIndex < 0)
            {
                RegisterObstacle();
            }
        }

        private void OnDisable()
        {
            if (obstacleSystem != null && obstacleIndex >= 0)
            {
                obstacleSystem.RemoveCircleObstacle(obstacleIndex);
                obstacleIndex = -1;
            }
        }

        private void RegisterObstacle()
        {
            Vector2 uvPosition = GetUvPosition();
            float currentRadius = Radius;

            obstacleIndex = obstacleSystem.AddCircleObstacle(uvPosition, currentRadius, isStatic);
            lastUvPosition = uvPosition;
            lastRadius = currentRadius;
        }

        private Vector2 GetUvPosition()
        {
            if (simulationCollider != null)
            {
                // Raycast to get UV coordinates
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
            }

            // Fallback: treat transform position as UV directly (useful for 2D setups)
            return new Vector2(
                Mathf.Clamp01(transform.position.x),
                Mathf.Clamp01(transform.position.y)
            );
        }

        private float GetScaleRadius()
        {
            Vector3 scale = transform.lossyScale;
            return Mathf.Max(scale.x, scale.y) * 0.5f; // Half of max scale as radius
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
            float displayRadius = Radius;

            // If we have a simulation collider, scale radius to world space
            if (simulationCollider != null)
            {
                Renderer rend = simulationCollider.GetComponent<Renderer>();
                if (rend != null)
                {
                    float worldWidth = rend.bounds.extents.x * 2f;
                    displayRadius = Radius * worldWidth;
                }
            }

            Gizmos.DrawWireSphere(transform.position, displayRadius);
        }
    }
}

using UnityEngine;

namespace Magi.Inkling.Systems.Obstacles
{
    /// <summary>
    /// Edge obstacle component that blocks ink flow along a line.
    /// Uses EdgeCollider2D points to define the edge shape.
    /// Triangulates the edge points into triangles for GPU rendering.
    /// </summary>
    [AddComponentMenu("Inkling/Obstacles/Edge Obstacle")]
    [RequireComponent(typeof(EdgeCollider2D))]
    public class EdgeObstacle : MonoBehaviour
    {
        [Header("Obstacle Settings")]
        [Tooltip("Static obstacles persist; dynamic ones are cleared each frame")]
        [SerializeField] private bool isStatic = false;

        [Tooltip("Edge thickness in UV space (the edge is extruded this amount)")]
        [SerializeField] private float thickness = 0.01f;

        [Header("Position Source")]
        [Tooltip("Reference to simulation surface for UV conversion.")]
        [SerializeField] private Collider simulationCollider;

        [Tooltip("Distance for raycasts to simulation surface")]
        [SerializeField] private float raycastDistance = 100f;

        [Header("Debug")]
        [SerializeField] private bool showGizmo = true;
        [SerializeField] private Color gizmoColor = Color.yellow;

        private IObstacleSystem obstacleSystem;
        private EdgeCollider2D edgeCollider;
        private int[] triangleIndices;
        private bool initialized;

        private void Start()
        {
            edgeCollider = GetComponent<EdgeCollider2D>();

            // Find obstacle system
            obstacleSystem = FindFirstObjectByType<ObstacleSystem>();

            if (obstacleSystem == null)
            {
                Debug.LogWarning($"[EdgeObstacle] No ObstacleSystem found in scene. {name} will not function.");
                enabled = false;
                return;
            }

            initialized = true;
        }

        private void LateUpdate()
        {
            if (!initialized || obstacleSystem == null || !obstacleSystem.IsInitialized) return;
            if (edgeCollider == null) return;

            RegisterEdgeAsTriangles();
        }

        private void RegisterEdgeAsTriangles()
        {
            Vector2[] points = edgeCollider.points;
            if (points.Length < 2) return;

            // For each segment, create a thick quad (2 triangles)
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector3 worldP0 = transform.TransformPoint(points[i]);
                Vector3 worldP1 = transform.TransformPoint(points[i + 1]);

                Vector2 uv0 = GetUvPosition(worldP0);
                Vector2 uv1 = GetUvPosition(worldP1);

                // Skip if UV conversion failed (off simulation)
                if (uv0.x < 0 || uv1.x < 0) continue;

                // Calculate perpendicular for thickness
                Vector2 dir = (uv1 - uv0).normalized;
                Vector2 perp = new Vector2(-dir.y, dir.x) * thickness * 0.5f;

                // Create quad vertices
                Vector2 v0 = uv0 - perp;
                Vector2 v1 = uv0 + perp;
                Vector2 v2 = uv1 - perp;
                Vector2 v3 = uv1 + perp;

                // Two triangles for the quad
                obstacleSystem.AddTriangleObstacle(v0, v1, v2, isStatic);
                obstacleSystem.AddTriangleObstacle(v1, v3, v2, isStatic);
            }
        }

        private Vector2 GetUvPosition(Vector3 worldPosition)
        {
            if (simulationCollider != null)
            {
                // Try forward raycast
                Ray ray = new Ray(worldPosition, Vector3.forward);
                if (simulationCollider.Raycast(ray, out RaycastHit hit, raycastDistance))
                {
                    return hit.textureCoord;
                }

                // Try backward
                ray = new Ray(worldPosition, Vector3.back);
                if (simulationCollider.Raycast(ray, out hit, raycastDistance))
                {
                    return hit.textureCoord;
                }

                return new Vector2(-1, -1); // Invalid
            }

            // Fallback: treat position as UV directly
            return new Vector2(
                Mathf.Clamp01(worldPosition.x),
                Mathf.Clamp01(worldPosition.y)
            );
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
            EdgeCollider2D edge = GetComponent<EdgeCollider2D>();
            if (edge == null) return;

            Gizmos.color = gizmoColor;
            Vector2[] points = edge.points;

            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector3 p0 = transform.TransformPoint(points[i]);
                Vector3 p1 = transform.TransformPoint(points[i + 1]);
                Gizmos.DrawLine(p0, p1);
            }
        }
    }
}

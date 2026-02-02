using UnityEngine;

namespace Magi.Inkling.Systems.Obstacles
{
    /// <summary>
    /// Polygon obstacle component that blocks ink flow within an arbitrary shape.
    /// Uses PolygonCollider2D points and triangulates them for GPU rendering.
    /// </summary>
    [AddComponentMenu("Inkling/Obstacles/Polygon Obstacle")]
    [RequireComponent(typeof(PolygonCollider2D))]
    public class PolygonObstacle : MonoBehaviour
    {
        [Header("Obstacle Settings")]
        [Tooltip("Static obstacles persist; dynamic ones are cleared each frame")]
        [SerializeField] private bool isStatic = false;

        [Header("Position Source")]
        [Tooltip("Reference to simulation surface for UV conversion.")]
        [SerializeField] private Collider simulationCollider;

        [Tooltip("Distance for raycasts to simulation surface")]
        [SerializeField] private float raycastDistance = 100f;

        [Header("Debug")]
        [SerializeField] private bool showGizmo = true;
        [SerializeField] private Color gizmoColor = Color.magenta;

        private IObstacleSystem obstacleSystem;
        private PolygonCollider2D polygonCollider;
        private int[] triangleIndices;
        private bool initialized;

        private void Start()
        {
            polygonCollider = GetComponent<PolygonCollider2D>();

            // Find obstacle system
            obstacleSystem = FindFirstObjectByType<ObstacleSystem>();

            if (obstacleSystem == null)
            {
                Debug.LogWarning($"[PolygonObstacle] No ObstacleSystem found in scene. {name} will not function.");
                enabled = false;
                return;
            }

            // Triangulate the polygon once on start
            TriangulatePolygon();

            initialized = true;
        }

        private void LateUpdate()
        {
            if (!initialized || obstacleSystem == null || !obstacleSystem.IsInitialized) return;
            if (polygonCollider == null || triangleIndices == null) return;

            RegisterPolygonAsTriangles();
        }

        private void TriangulatePolygon()
        {
            Vector2[] points = polygonCollider.points;
            if (points.Length < 3)
            {
                Debug.LogWarning($"[PolygonObstacle] {name} has fewer than 3 points, cannot triangulate.");
                return;
            }

            var triangulator = new Triangulator(points);
            triangleIndices = triangulator.Triangulate();

            if (triangleIndices.Length < 3)
            {
                Debug.LogWarning($"[PolygonObstacle] {name} triangulation failed.");
            }
        }

        private void RegisterPolygonAsTriangles()
        {
            Vector2[] points = polygonCollider.points;
            if (points.Length < 3 || triangleIndices == null || triangleIndices.Length < 3) return;

            // Convert all points to UV once
            Vector2[] uvPoints = new Vector2[points.Length];
            bool allValid = true;

            for (int i = 0; i < points.Length; i++)
            {
                Vector3 worldPos = transform.TransformPoint(points[i]);
                uvPoints[i] = GetUvPosition(worldPos);

                if (uvPoints[i].x < 0)
                {
                    allValid = false;
                }
            }

            if (!allValid)
            {
                // Some points are off-simulation, skip this frame
                return;
            }

            // Register each triangle
            for (int i = 0; i < triangleIndices.Length - 2; i += 3)
            {
                Vector2 v0 = uvPoints[triangleIndices[i]];
                Vector2 v1 = uvPoints[triangleIndices[i + 1]];
                Vector2 v2 = uvPoints[triangleIndices[i + 2]];

                obstacleSystem.AddTriangleObstacle(v0, v1, v2, isStatic);
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

        /// <summary>
        /// Call this if the polygon collider points change at runtime.
        /// </summary>
        public void RefreshTriangulation()
        {
            TriangulatePolygon();
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
            PolygonCollider2D poly = GetComponent<PolygonCollider2D>();
            if (poly == null) return;

            Gizmos.color = gizmoColor;
            Vector2[] points = poly.points;

            // Draw polygon outline
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p0 = transform.TransformPoint(points[i]);
                Vector3 p1 = transform.TransformPoint(points[(i + 1) % points.Length]);
                Gizmos.DrawLine(p0, p1);
            }

            // Draw triangles if available
            if (triangleIndices != null && triangleIndices.Length >= 3)
            {
                Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.3f);
                for (int i = 0; i < triangleIndices.Length - 2; i += 3)
                {
                    Vector3 v0 = transform.TransformPoint(points[triangleIndices[i]]);
                    Vector3 v1 = transform.TransformPoint(points[triangleIndices[i + 1]]);
                    Vector3 v2 = transform.TransformPoint(points[triangleIndices[i + 2]]);

                    Gizmos.DrawLine(v0, v1);
                    Gizmos.DrawLine(v1, v2);
                    Gizmos.DrawLine(v2, v0);
                }
            }
        }
    }
}

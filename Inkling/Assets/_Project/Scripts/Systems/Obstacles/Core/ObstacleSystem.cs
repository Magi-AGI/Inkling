using UnityEngine;
using Magi.Inkling.Services;

namespace Magi.Inkling.Systems.Obstacles
{
    /// <summary>
    /// GPU-based obstacle system that manages circle and triangle obstacles.
    /// Obstacles are stamped to the simulation's obstacle texture each frame.
    /// </summary>
    [DefaultExecutionOrder(40)] // Run before SimDriver (50) so obstacles are ready for simulation
    public class ObstacleSystem : MonoBehaviour, IObstacleSystem
    {
        [Header("Compute")]
        [SerializeField] private ComputeShader obstacleCompute;

        [Header("Capacity")]
        [SerializeField] private int maxCircleObstacles = 64;
        [SerializeField] private int maxTriangleObstacles = 256;

        [Header("Simulation Reference")]
        [Tooltip("Reference to simulation for obstacle texture. If null, will attempt to find ISimulationReader at runtime.")]
        [SerializeField] private MonoBehaviour simulationSource;
        [Header("Batched/Alt Pipeline")]
        [Tooltip("Experimental: use batched obstacle RT path (static/dynamic channels) instead of compute-buffer stamping.")]
        [SerializeField] private bool useBatchedObstacleBuffer = false;
        [Tooltip("Compute shader for batched obstacle stamping (static/dynamic channels).")]
        [SerializeField] private ComputeShader obstacleBufferCompute;

        // GPU buffers
        private ComputeBuffer circleBuffer;
        private ComputeBuffer triangleBuffer;

        // CPU mirror for updating
        private CircleObstacleData[] circles;
        private TriangleObstacleData[] triangles;

        // Kernel indices
        private int kernelStampCircles;
        private int kernelStampTriangles;
        private int kernelClearObstacles;
        private int kernelStampCirclesBatched;
        private int kernelStampTrianglesBatched;
        private int kernelClearObstacleBuffer;

        // Runtime state
        private ISimulationReader simReader;
        private int circleCount;
        private int triangleCount;
        private bool isInitialized;
        private bool buffersDirty;
        private bool obstacleBufferReady;
        private RenderTexture obstacleBufferRT;

        #region IObstacleSystem Implementation

        public int MaxCircleObstacles => maxCircleObstacles;
        public int MaxTriangleObstacles => maxTriangleObstacles;
        public int ActiveCircleCount => circleCount;
        public int ActiveTriangleCount => triangleCount;
        public bool IsInitialized => isInitialized;

        public int AddCircleObstacle(Vector2 center, float radius, bool isStatic = false)
        {
            if (!isInitialized) return -1;

            // Find first inactive slot
            for (int i = 0; i < maxCircleObstacles; i++)
            {
                if (!circles[i].IsActive)
                {
                    circles[i] = CircleObstacleData.Create(center, radius, isStatic);
                    circleCount++;
                    buffersDirty = true;
                    return i;
                }
            }

            Debug.LogWarning("[ObstacleSystem] Circle obstacle buffer full.");
            return -1;
        }

        public int AddTriangleObstacle(Vector2 v0, Vector2 v1, Vector2 v2, bool isStatic = false)
        {
            if (!isInitialized) return -1;

            // Find first inactive slot
            for (int i = 0; i < maxTriangleObstacles; i++)
            {
                if (!triangles[i].IsActive)
                {
                    triangles[i] = TriangleObstacleData.Create(v0, v1, v2, isStatic);
                    triangleCount++;
                    buffersDirty = true;
                    return i;
                }
            }

            Debug.LogWarning("[ObstacleSystem] Triangle obstacle buffer full.");
            return -1;
        }

        public void UpdateCircleObstacle(int index, Vector2 center, float radius)
        {
            if (!isInitialized || index < 0 || index >= maxCircleObstacles) return;
            if (!circles[index].IsActive) return;

            circles[index].center = center;
            circles[index].radius = radius;
            buffersDirty = true;
        }

        public void UpdateTriangleObstacle(int index, Vector2 v0, Vector2 v1, Vector2 v2)
        {
            if (!isInitialized || index < 0 || index >= maxTriangleObstacles) return;
            if (!triangles[index].IsActive) return;

            triangles[index].v0 = v0;
            triangles[index].v1 = v1;
            triangles[index].v2 = v2;
            buffersDirty = true;
        }

        public void RemoveCircleObstacle(int index)
        {
            if (!isInitialized || index < 0 || index >= maxCircleObstacles) return;
            if (!circles[index].IsActive) return;

            circles[index] = CircleObstacleData.Inactive;
            circleCount--;
            buffersDirty = true;
        }

        public void RemoveTriangleObstacle(int index)
        {
            if (!isInitialized || index < 0 || index >= maxTriangleObstacles) return;
            if (!triangles[index].IsActive) return;

            triangles[index] = TriangleObstacleData.Inactive;
            triangleCount--;
            buffersDirty = true;
        }

        public void ClearDynamicObstacles()
        {
            if (!isInitialized) return;

            for (int i = 0; i < maxCircleObstacles; i++)
            {
                if (circles[i].IsActive && !circles[i].IsStatic)
                {
                    circles[i] = CircleObstacleData.Inactive;
                    circleCount--;
                }
            }

            for (int i = 0; i < maxTriangleObstacles; i++)
            {
                if (triangles[i].IsActive && !triangles[i].IsStatic)
                {
                    triangles[i] = TriangleObstacleData.Inactive;
                    triangleCount--;
                }
            }

            buffersDirty = true;
        }

        public void ClearAllObstacles()
        {
            if (!isInitialized) return;

            for (int i = 0; i < maxCircleObstacles; i++)
                circles[i] = CircleObstacleData.Inactive;

            for (int i = 0; i < maxTriangleObstacles; i++)
                triangles[i] = TriangleObstacleData.Inactive;

            circleCount = 0;
            triangleCount = 0;
            buffersDirty = true;
        }

        public ComputeBuffer GetCircleBuffer()
        {
            return circleBuffer;
        }

        public ComputeBuffer GetTriangleBuffer()
        {
            return triangleBuffer;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (obstacleCompute == null)
            {
                Debug.LogError("[ObstacleSystem] Obstacle compute shader not assigned.");
                enabled = false;
                return;
            }

            // Find kernel indices
            kernelStampCircles = obstacleCompute.FindKernel("StampCircleObstacles");
            kernelStampTriangles = obstacleCompute.FindKernel("StampTriangleObstacles");
            kernelClearObstacles = obstacleCompute.FindKernel("ClearObstacles");

            // Create compute buffers
            circleBuffer = new ComputeBuffer(maxCircleObstacles, CircleObstacleData.Stride);
            triangleBuffer = new ComputeBuffer(maxTriangleObstacles, TriangleObstacleData.Stride);

            // Initialize CPU arrays
            circles = new CircleObstacleData[maxCircleObstacles];
            triangles = new TriangleObstacleData[maxTriangleObstacles];

            // Initialize with inactive obstacles
            ClearAllObstacles();

            isInitialized = true;
        }

        private void Start()
        {
            // Find simulation reader
            if (simulationSource != null && simulationSource is ISimulationReader reader)
            {
                simReader = reader;
            }
            else
            {
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb is ISimulationReader r)
                    {
                        simReader = r;
                        break;
                    }
                }
            }

            if (simReader == null)
            {
                Debug.LogWarning("[ObstacleSystem] No ISimulationReader found. " +
                               "Obstacles will not be stamped to simulation.");
            }

            // Optional obstacle buffer compute init
            if (useBatchedObstacleBuffer && obstacleBufferCompute != null)
            {
                try
                {
                    kernelStampCirclesBatched = obstacleBufferCompute.FindKernel("StampCirclesBatched");
                    kernelStampTrianglesBatched = obstacleBufferCompute.FindKernel("StampTrianglesBatched");
                    kernelClearObstacleBuffer = obstacleBufferCompute.FindKernel("ClearObstacleBuffer");
                    obstacleBufferReady = true;
                    Debug.Log("[ObstacleSystem] Batched obstacle buffer path ready.");
                }
                catch (System.Exception e)
                {
                    obstacleBufferReady = false;
                    Debug.LogWarning($"[ObstacleSystem] Batched obstacle buffer init failed ({e.Message}). Falling back to legacy path.");
                }
            }
        }

        private void LateUpdate()
        {
            if (!isInitialized) return;

            // Upload buffers if dirty
            if (buffersDirty)
            {
                circleBuffer.SetData(circles);
                triangleBuffer.SetData(triangles);
                buffersDirty = false;
            }

            // Stamp obstacles to simulation
            if (simReader != null)
            {
                StampObstaclesToTexture();
            }
        }

        private void OnDestroy()
        {
            circleBuffer?.Release();
            triangleBuffer?.Release();
            circleBuffer = null;
            triangleBuffer = null;
            isInitialized = false;
        }

        #endregion

        #region GPU Dispatch

        private void StampObstaclesToTexture()
        {
            var obstacleTexture = simReader.GetObstacleTexture();
            if (obstacleTexture == null) return;

            int resolution = simReader.Resolution;
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            if (useBatchedObstacleBuffer && obstacleBufferReady)
            {
                EnsureObstacleBuffer(obstacleTexture);

                obstacleBufferCompute.SetInt("_Width", obstacleBufferRT.width);
                obstacleBufferCompute.SetInt("_Height", obstacleBufferRT.height);

                // Clear
                obstacleBufferCompute.SetTexture(kernelClearObstacleBuffer, "_ObstacleBuffer", obstacleBufferRT);
                obstacleBufferCompute.Dispatch(kernelClearObstacleBuffer, threadGroups, threadGroups, 1);

                // Circles
                if (circleCount > 0)
                {
                    obstacleBufferCompute.SetBuffer(kernelStampCirclesBatched, "_Circles", circleBuffer);
                    obstacleBufferCompute.SetInt("_CircleCount", circleCount);
                    obstacleBufferCompute.SetTexture(kernelStampCirclesBatched, "_ObstacleBuffer", obstacleBufferRT);
                    obstacleBufferCompute.Dispatch(kernelStampCirclesBatched, threadGroups, threadGroups, 1);
                }

                // Triangles
                if (triangleCount > 0)
                {
                    obstacleBufferCompute.SetBuffer(kernelStampTrianglesBatched, "_Triangles", triangleBuffer);
                    obstacleBufferCompute.SetBuffer(kernelStampTrianglesBatched, "_TrianglesB", triangleBuffer);
                    obstacleBufferCompute.SetInt("_TriangleCount", triangleCount);
                    obstacleBufferCompute.SetTexture(kernelStampTrianglesBatched, "_ObstacleBuffer", obstacleBufferRT);
                    obstacleBufferCompute.Dispatch(kernelStampTrianglesBatched, threadGroups, threadGroups, 1);
                }

                // Copy to sim obstacle texture
                Graphics.Blit(obstacleBufferRT, obstacleTexture);
            }
            else
            {
                // Clear obstacles first (they're regenerated each frame)
                obstacleCompute.SetTexture(kernelClearObstacles, "_ObstaclesWrite", obstacleTexture);
                obstacleCompute.SetInt("_Resolution", resolution);
                obstacleCompute.Dispatch(kernelClearObstacles, threadGroups, threadGroups, 1);

                // Stamp circle obstacles
                if (circleCount > 0)
                {
                    obstacleCompute.SetBuffer(kernelStampCircles, "_CircleObstacles", circleBuffer);
                    obstacleCompute.SetTexture(kernelStampCircles, "_ObstaclesWrite", obstacleTexture);
                    obstacleCompute.SetInt("_CircleCount", maxCircleObstacles);
                    obstacleCompute.SetInt("_Resolution", resolution);
                    obstacleCompute.Dispatch(kernelStampCircles, threadGroups, threadGroups, 1);
                }

                // Stamp triangle obstacles
                if (triangleCount > 0)
                {
                    obstacleCompute.SetBuffer(kernelStampTriangles, "_TriangleObstacles", triangleBuffer);
                    obstacleCompute.SetTexture(kernelStampTriangles, "_ObstaclesWrite", obstacleTexture);
                    obstacleCompute.SetInt("_TriangleCount", maxTriangleObstacles);
                    obstacleCompute.SetInt("_Resolution", resolution);
                    obstacleCompute.Dispatch(kernelStampTriangles, threadGroups, threadGroups, 1);
                }
            }
        }

        private void EnsureObstacleBuffer(RenderTexture simObstacle)
        {
            if (obstacleBufferRT != null &&
                obstacleBufferRT.width == simObstacle.width &&
                obstacleBufferRT.height == simObstacle.height)
                return;

            if (obstacleBufferRT != null) obstacleBufferRT.Release();
            obstacleBufferRT = new RenderTexture(simObstacle.width, simObstacle.height, 0, RenderTextureFormat.RGFloat)
            {
                enableRandomWrite = true
            };
            obstacleBufferRT.Create();
        }

        #endregion

        #region Editor Helpers

        [ContextMenu("Add Test Circle")]
        private void AddTestCircle()
        {
            if (!isInitialized)
            {
                Debug.LogError("[ObstacleSystem] Not initialized. Enter play mode first.");
                return;
            }

            AddCircleObstacle(new Vector2(0.5f, 0.5f), 0.1f, true);
            Debug.Log($"[ObstacleSystem] Added test circle. Circle count: {circleCount}");
        }

        [ContextMenu("Add Test Triangle")]
        private void AddTestTriangle()
        {
            if (!isInitialized)
            {
                Debug.LogError("[ObstacleSystem] Not initialized. Enter play mode first.");
                return;
            }

            AddTriangleObstacle(
                new Vector2(0.3f, 0.3f),
                new Vector2(0.5f, 0.7f),
                new Vector2(0.7f, 0.3f),
                true);
            Debug.Log($"[ObstacleSystem] Added test triangle. Triangle count: {triangleCount}");
        }

        [ContextMenu("Clear All Obstacles")]
        private void EditorClearAll()
        {
            if (!isInitialized) return;
            ClearAllObstacles();
            Debug.Log("[ObstacleSystem] All obstacles cleared.");
        }

        #endregion
    }
}

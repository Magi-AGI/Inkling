using UnityEngine;
using UnityEngine.InputSystem;
using Magi.Inkling.Services;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Injects density using texture masks to create shaped patterns like creatures.
    /// Can be used for autonomous "inkling" creatures or player-controlled entities.
    /// </summary>
    [DefaultExecutionOrder(-50)] // Run before SimDriver (+50) so stamps are queued before simulation
    public class TexturedInjector : MonoBehaviour
    {
        [Header("Injection Settings")]
        [SerializeField] private SimDriver simDriverComponent;

        // Cached service interfaces for decoupled access
        private ISimulationWriter simWriter;
        private ISimulationReader simReader;
        [SerializeField] private Texture2D injectionMask;
        [SerializeField] private int maskResolution = 64;
        [Tooltip("Downsample mask to this resolution (square). Keeps injection lightweight and avoids huge masks.")]
        [SerializeField] private int maskTargetResolution = 64;

        [Header("Appearance")]
        [SerializeField] private bool useTextureColors = true;  // Use texture's actual colors instead of override
        [SerializeField] private Color inkColorOverride = Color.cyan;  // Only used if useTextureColors = false
        [SerializeField] private float densityMultiplier = 1.0f;  // Subtle density for fast dissipation
        [Range(0, 1)] [SerializeField] private float alphaThreshold = 0.1f;
        [Range(0, 1)] [SerializeField] private float blackLuminanceThreshold = 0.05f;
        [SerializeField] private bool enableBlackMaskClearing = false; // If false, skip obstacle/black masking to avoid flicker

        [Header("Movement")]
        [SerializeField] private bool autonomous = true;
        [SerializeField] private float moveSpeed = 0.1f;
#pragma warning disable 0414 // Field assigned but never used - reserved for future rotation feature
        [SerializeField] private float rotationSpeed = 45f;
#pragma warning restore 0414
        [SerializeField] private Vector2 movementBounds = new Vector2(0.9f, 0.9f); // UV bounds

        [Header("Behavior")]
        [SerializeField] private bool injectWhileMoving = true;
        [SerializeField] private float injectionInterval = 0.033f; // ~30Hz
        [SerializeField] private bool addVelocityTrail = true;
        [SerializeField] private float velocityScale = 50f;

        // State
        private Vector2 position = new Vector2(0.5f, 0.5f); // UV position
        private Vector2 velocity = Vector2.zero;
        private float nextInjectionTime = 0f;
        private Vector2 previousPosition;
        private bool maskValid = false;
        private int actualMaskWidth = 0;   // Actual texture width
        private int actualMaskHeight = 0;  // Actual texture height
        private Texture2D stampTexture;
        private bool hasLoggedFirstInjection = false;
        private bool useLinearColorSpace = false;

        private void Start()
        {
            // Cache color space at runtime (cannot query in field initializer)
            useLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
            _ = alphaThreshold; // silence unused-field warning (inspector-tunable)

            if (simDriverComponent == null)
            {
                Debug.LogError("[TexturedInjector] SimDriver reference is required. Please assign it in the Inspector.");
                enabled = false;
                return;
            }

            // Initialize service interfaces for decoupled access
            simWriter = simDriverComponent.AsWriter();
            simReader = simDriverComponent.AsReader();

            // Start at random position if autonomous
            if (autonomous)
            {
                position = new Vector2(
                    Random.Range(0.2f, 0.8f),
                    Random.Range(0.2f, 0.8f)
                );
            }

            previousPosition = position;

            // Validate and cache mask texture
            ValidateMask();
        }

        private void ValidateMask()
        {
            if (injectionMask == null)
            {
                Debug.LogWarning($"[TexturedInjector] No injection mask assigned on {gameObject.name}");
                maskValid = false;
                return;
            }
            if (injectionMask.width <= 0 || injectionMask.height <= 0)
            {
                Debug.LogWarning($"[TexturedInjector] Injection mask on {gameObject.name} has zero size; aborting.");
                maskValid = false;
                return;
            }

            // Check if texture is readable
            try
            {
                // Log actual texture dimensions
                Debug.Log($"[TexturedInjector] Texture '{injectionMask.name}' dimensions: {injectionMask.width}x{injectionMask.height}, " +
                         $"requesting {maskResolution}x{maskResolution}");

                // Build a cached stamp texture once; keep size controlled
                actualMaskWidth = Mathf.Clamp(maskTargetResolution, 4, 256);
                actualMaskHeight = actualMaskWidth;
                maskResolution = actualMaskWidth;

                if (stampTexture != null)
                {
                    Destroy(stampTexture);
                }

                stampTexture = new Texture2D(actualMaskWidth, actualMaskHeight, TextureFormat.RGBA32, false, /*linear*/ true);
                stampTexture.filterMode = FilterMode.Point;
                stampTexture.wrapMode = TextureWrapMode.Clamp;
                stampTexture.anisoLevel = 0;
                stampTexture.hideFlags = HideFlags.DontSave;

                // Runtime enforce sane sampling on the source mask (non-destructive; per-instance)
                injectionMask.filterMode = FilterMode.Point;
                injectionMask.wrapMode = TextureWrapMode.Clamp;

                // Blit/downsample via Graphics to avoid CPU per-pixel cost and driver crashes
                RenderTexture tmp = RenderTexture.GetTemporary(actualMaskWidth, actualMaskHeight, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(injectionMask, tmp);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = tmp;
                stampTexture.ReadPixels(new Rect(0, 0, actualMaskWidth, actualMaskHeight), 0, 0);
                stampTexture.Apply(false, false);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(tmp);

                maskValid = true;
                Debug.Log($"[TexturedInjector] Mask '{injectionMask.name}' loaded successfully ({actualMaskWidth}x{actualMaskHeight})");
            }
            catch (UnityException e)
            {
                Debug.LogError($"[TexturedInjector] Texture '{injectionMask.name}' is not readable! " +
                              $"To fix: Select texture in Project window → Inspector → " +
                              $"Enable 'Read/Write Enabled' → Apply.\nError: {e.Message}");
                maskValid = false;
            }
        }

        private void Update()
        {
            if (simWriter == null) return;
            if (!maskValid) return;

            // Update movement
            if (autonomous)
            {
                UpdateAutonomousMovement();
            }
            else
            {
                UpdatePlayerControlled();
            }

            // Inject at intervals
            if (Time.time >= nextInjectionTime)
            {
                bool shouldInject = injectWhileMoving || autonomous;

                if (shouldInject)
                {
                    InjectAtPosition(position);
                }

                nextInjectionTime = Time.time + injectionInterval;
            }

            previousPosition = position;
        }

        private void UpdateAutonomousMovement()
        {
            // Simple wandering behavior
            // Add random direction changes
            if (Random.value < 0.02f) // 2% chance per frame to change direction
            {
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * moveSpeed;
            }

            // Update position
            position += velocity * Time.deltaTime;

            // Bounce off boundaries
            if (position.x < 0.1f || position.x > movementBounds.x)
            {
                velocity.x = -velocity.x;
                position.x = Mathf.Clamp(position.x, 0.1f, movementBounds.x);
            }
            if (position.y < 0.1f || position.y > movementBounds.y)
            {
                velocity.y = -velocity.y;
                position.y = Mathf.Clamp(position.y, 0.1f, movementBounds.y);
            }
        }

        private void UpdatePlayerControlled()
        {
            // Use mouse position for player control
            if (Mouse.current != null)
            {
                Vector2 mousePos = Mouse.current.position.ReadValue();
                Vector2 targetUV = new Vector2(
                    Mathf.Clamp01(mousePos.x / Screen.width),
                    Mathf.Clamp01(mousePos.y / Screen.height)
                );

                // Smoothly move towards mouse
                position = Vector2.Lerp(position, targetUV, moveSpeed * 10f * Time.deltaTime);
            }
        }

        private void InjectAtPosition(Vector2 uvPosition)
        {
            if (simWriter == null || !maskValid || injectionMask == null) return;

            if (stampTexture == null)
            {
                ValidateMask();
                if (stampTexture == null) return;
            }

            // Overlap culling: skip if the mask quad does not intersect the sim plane.
            if (!OverlapsSim(uvPosition))
            {
                Debug.LogWarning($"[TexturedInjector] Skipping injection - creature at UV {uvPosition} is off-screen");
                return;
            }

            // No CPU-side pixel rewrite; we use the source mask directly and let the shader handle overrides

            if (!hasLoggedFirstInjection)
            {
                hasLoggedFirstInjection = true;
                Debug.Log($"[TexturedInjector] First injection at UV {uvPosition:F3} with mask {actualMaskWidth}x{actualMaskHeight}.");
            }

            // GPU stamp colored portions into the RT-based density field
            simWriter.StampDensity(
                uvPosition,
                stampTexture,
                densityMultiplier,
                useTextureColors ? false : true,
                inkColorOverride);

            if (enableBlackMaskClearing)
            {
                // Use the original mask to clear density in black regions so black inks
                // appear solid and do not advect/linger, and to update obstacle map.
                simWriter.ClearDensityWithMask(uvPosition, injectionMask, blackLuminanceThreshold);
            }

            // Inject velocity if moving
            if (addVelocityTrail)
            {
                Vector2 movementVelocity = (position - previousPosition) * velocityScale;
                if (movementVelocity.magnitude > 0.1f)
                {
                    simWriter.InjectForce(uvPosition, movementVelocity);
                }
            }

        }

        /// <summary>
        /// Returns true if the mask quad (centered at uvPosition) overlaps the simulation rect [0,1]x[0,1].
        /// Prevents dispatch when the mask is completely off the sim plane.
        /// </summary>
        private bool OverlapsSim(Vector2 uvPosition)
        {
            if (simReader == null || stampTexture == null) return true; // Can't cull without info

            // Compute mask size in UV space (same calculation as SimDriver.ProcessPendingOperations)
            float simRes = simReader.Resolution;
            if (simRes <= 0) return true; // Safety: allow if resolution not yet initialized

            Vector2 maskSizeUV = new Vector2(
                (float)stampTexture.width / simRes,
                (float)stampTexture.height / simRes
            );

            Vector2 half = maskSizeUV * 0.5f;
            float minX = uvPosition.x - half.x;
            float maxX = uvPosition.x + half.x;
            float minY = uvPosition.y - half.y;
            float maxY = uvPosition.y + half.y;

            return !(maxX < 0f || minX > 1f || maxY < 0f || minY > 1f);
        }

        /// <summary>
        /// Public methods for external control
        /// </summary>
        public void SetPosition(Vector2 uvPos)
        {
            position = new Vector2(
                Mathf.Clamp01(uvPos.x),
                Mathf.Clamp01(uvPos.y)
            );
        }

        public void SetVelocity(Vector2 vel)
        {
            velocity = vel;
        }

        public Vector2 GetPosition() => position;

        public void TriggerInjection()
        {
            InjectAtPosition(position);
        }

        /// <summary>
        /// Change the injection mask texture at runtime.
        /// Used by AnimatedCreature for frame-based animation.
        /// </summary>
        public void SetMask(Texture2D newMask)
        {
            if (newMask == null) return;

            injectionMask = newMask;
            ValidateMask();
        }

        /// <summary>
        /// Gets the current injection mask.
        /// </summary>
        public Texture2D GetMask() => injectionMask;

        private void OnDestroy()
        {
            if (stampTexture != null)
            {
                Destroy(stampTexture);
                stampTexture = null;
            }
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;

            // Draw position in scene view (for debugging)
            // Map UV (0-1) to world space for visualization
            Vector3 worldPos = new Vector3(position.x - 0.5f, position.y - 0.5f, 0) * 10f;

            // Draw filled sphere in ink color
            Gizmos.color = useTextureColors ? Color.white : inkColorOverride;
            Gizmos.DrawSphere(worldPos, 0.3f);

            // Draw wire outline
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(worldPos, 0.3f);

            // Draw velocity direction
            if (velocity.magnitude > 0.01f)
            {
                Gizmos.color = Color.yellow;
                Vector3 velDir = new Vector3(velocity.x, velocity.y, 0).normalized;
                Gizmos.DrawRay(worldPos, velDir * 0.8f);
            }

            // Draw bounds
            Gizmos.color = Color.gray;
            Vector3 boundsMin = new Vector3(0.1f - 0.5f, 0.1f - 0.5f, 0) * 10f;
            Vector3 boundsMax = new Vector3(movementBounds.x - 0.5f, movementBounds.y - 0.5f, 0) * 10f;
            Vector3 boundsSize = boundsMax - boundsMin;
            Gizmos.DrawWireCube(boundsMin + boundsSize * 0.5f, boundsSize);
        }
    }
}

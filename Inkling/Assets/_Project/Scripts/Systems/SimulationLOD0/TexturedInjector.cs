using UnityEngine;
using UnityEngine.InputSystem;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Injects density using texture masks to create shaped patterns like creatures.
    /// Can be used for autonomous "inkling" creatures or player-controlled entities.
    /// </summary>
    public class TexturedInjector : MonoBehaviour
    {
        [Header("Injection Settings")]
        [SerializeField] private SimDriver simDriver;
        [SerializeField] private Texture2D injectionMask;
        [SerializeField] private int maskResolution = 64;

        [Header("Appearance")]
        [SerializeField] private bool useTextureColors = true;  // Use texture's actual colors instead of override
        [SerializeField] private Color inkColorOverride = Color.cyan;  // Only used if useTextureColors = false
        [SerializeField] private float densityMultiplier = 1.0f;  // Subtle density for fast dissipation
        [Range(0, 1)] [SerializeField] private float alphaThreshold = 0.1f;

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
        private Color[] cachedMaskPixels;
        private bool maskValid = false;
        private int actualMaskWidth = 0;   // Actual texture width
        private int actualMaskHeight = 0;  // Actual texture height

        private void Start()
        {
            if (simDriver == null)
            {
                Debug.LogError("[TexturedInjector] SimDriver reference is required. Please assign it in the Inspector.");
                enabled = false;
                return;
            }

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

            // Check if texture is readable
            try
            {
                // Log actual texture dimensions
                Debug.Log($"[TexturedInjector] Texture '{injectionMask.name}' dimensions: {injectionMask.width}x{injectionMask.height}, " +
                         $"requesting {maskResolution}x{maskResolution}");

                // Use the actual texture dimensions, not maskResolution
                actualMaskWidth = injectionMask.width;
                actualMaskHeight = injectionMask.height;

                cachedMaskPixels = injectionMask.GetPixels(0, 0, actualMaskWidth, actualMaskHeight);

                // Update maskResolution to match actual texture (for display purposes)
                maskResolution = Mathf.Min(actualMaskWidth, actualMaskHeight);

                maskValid = true;
                Debug.Log($"[TexturedInjector] Mask '{injectionMask.name}' loaded successfully ({actualMaskWidth}x{actualMaskHeight})");
                Debug.Log($"[TexturedInjector] Pixel data: {cachedMaskPixels.Length} pixels loaded");

                // Sample a few pixels to verify data
                if (cachedMaskPixels.Length > 0)
                {
                    int centerIdx = (actualMaskHeight / 2) * actualMaskWidth + (actualMaskWidth / 2);
                    Color centerPixel = cachedMaskPixels[centerIdx];
                    Debug.Log($"[TexturedInjector] Center pixel RGBA: ({centerPixel.r:F3}, {centerPixel.g:F3}, {centerPixel.b:F3}, {centerPixel.a:F3})");

                    // Count non-transparent pixels
                    int nonTransparent = 0;
                    for (int i = 0; i < cachedMaskPixels.Length; i++)
                    {
                        if (cachedMaskPixels[i].a >= alphaThreshold)
                            nonTransparent++;
                    }
                    Debug.Log($"[TexturedInjector] Pixels above threshold ({alphaThreshold}): {nonTransparent} / {cachedMaskPixels.Length}");
                }
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
            // Debug status every 2 seconds
            if (Time.frameCount % 120 == 0)
            {
                Debug.Log($"[TexturedInjector] Status: simDriver={(simDriver != null ? "OK" : "NULL")}, " +
                         $"maskValid={maskValid}, autonomous={autonomous}, position={position}, " +
                         $"velocity={velocity}, injectionInterval={injectionInterval}\n" +
                         $"  TIME CHECK: Time.time={Time.time:F3}, nextInjectionTime={nextInjectionTime:F3}, " +
                         $"ready={(Time.time >= nextInjectionTime)}");
            }

            if (simDriver == null)
            {
                if (Time.frameCount % 120 == 0)
                    Debug.LogWarning("[TexturedInjector] SimDriver is null! Assign it in Inspector.");
                return;
            }

            if (!maskValid)
            {
                if (Time.frameCount % 120 == 0)
                    Debug.LogWarning("[TexturedInjector] Mask is not valid! Check texture Read/Write settings.");
                return;
            }

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
            if (simDriver == null || !maskValid || injectionMask == null)
            {
                if (Time.frameCount % 120 == 0)
                    Debug.LogWarning($"[TexturedInjector] InjectAtPosition aborted: simDriver={simDriver != null}, maskValid={maskValid}, mask={injectionMask != null}");
                return;
            }

            // Create stamped texture with color overrides applied if needed
            Texture2D stampTexture = new Texture2D(actualMaskWidth, actualMaskHeight, TextureFormat.RGBAHalf, false);
            Color[] stampPixels = new Color[cachedMaskPixels.Length];

            for (int i = 0; i < cachedMaskPixels.Length; i++)
            {
                Color maskColor = cachedMaskPixels[i];

                // Skip transparent pixels
                if (maskColor.a < alphaThreshold)
                {
                    stampPixels[i] = Color.clear;
                    continue;
                }

                // Apply density multiplier and color override if needed
                if (useTextureColors)
                {
                    stampPixels[i] = maskColor * densityMultiplier;
                }
                else
                {
                    stampPixels[i] = inkColorOverride * maskColor.a * densityMultiplier;
                }
            }

            stampTexture.SetPixels(stampPixels);
            stampTexture.Apply();

            // StampDensity now handles both black pixels (->bb channel) and colored pixels (->f/w/i channels)
            simDriver.StampDensity(uvPosition, stampTexture);

            // Inject velocity if moving
            if (addVelocityTrail)
            {
                Vector2 movementVelocity = (position - previousPosition) * velocityScale;
                if (movementVelocity.magnitude > 0.1f)
                {
                    simDriver.InjectForce(uvPosition, movementVelocity);
                }
            }

            // Cleanup
            Destroy(stampTexture);

            // Debug log occasionally
            if (Time.frameCount % 120 == 0)
            {
                Debug.Log($"[TexturedInjector] Stamped {actualMaskWidth}x{actualMaskHeight} texture at UV {uvPosition:F3}, " +
                         $"black pixels->bb (black body, quick dissipation), colored pixels->f/w/i (persistent inks)");
            }
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

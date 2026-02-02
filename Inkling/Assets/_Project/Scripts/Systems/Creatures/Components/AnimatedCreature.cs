using UnityEngine;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Systems.Creatures
{
    /// <summary>
    /// Component that manages creature animation state and updates TexturedInjector mask.
    /// Handles frame-based animation playback and state transitions.
    /// </summary>
    [AddComponentMenu("Inkling/Creatures/Animated Creature")]
    [RequireComponent(typeof(TexturedInjector))]
    public class AnimatedCreature : MonoBehaviour, ICreatureAnimator
    {
        [Header("Creature Definition")]
        [SerializeField] private CreatureDefinition definition;

        [Header("Initial State")]
        [SerializeField] private CreatureAnimationState initialState = CreatureAnimationState.Idle;
        [SerializeField] private bool loopInitialState = true;

        [Header("Movement Detection")]
        [Tooltip("Automatically switch between Idle/Move states based on velocity.")]
        [SerializeField] private bool autoDetectMovement = true;
        [Tooltip("Velocity threshold to trigger movement state.")]
        [SerializeField] private float movementThreshold = 0.01f;

        [Header("Debug")]
        [SerializeField] private bool logStateChanges = false;

        // Components
        private TexturedInjector injector;

        // Animation state
        private CreatureAnimationState currentState;
        private int currentFrame;
        private bool isLooping;
        private bool isAnimating;
        private float frameTimer;
        private float frameDuration;
        private Texture2D[] currentSprites;

        // One-shot tracking
        private CreatureAnimationState returnState;
        private bool isPlayingOneShot;

        // Movement tracking
        private Vector2 lastPosition;
        private bool wasMoving;

        #region ICreatureAnimator Implementation

        public CreatureAnimationState CurrentState => currentState;
        public int CurrentFrame => currentFrame;
        public bool IsAnimating => isAnimating;

        public void SetState(CreatureAnimationState state, bool loop = true)
        {
            if (definition == null) return;

            var sprites = definition.GetSpritesForState(state);
            if (sprites == null || sprites.Length == 0)
            {
                // Fallback to idle if state has no sprites
                sprites = definition.GetSpritesForState(CreatureAnimationState.Idle);
                state = CreatureAnimationState.Idle;
            }

            if (logStateChanges && currentState != state)
            {
                Debug.Log($"[AnimatedCreature] {gameObject.name}: {currentState} -> {state}");
            }

            currentState = state;
            currentSprites = sprites;
            currentFrame = 0;
            isLooping = loop;
            isAnimating = true;
            isPlayingOneShot = false;

            frameDuration = 1f / definition.GetFrameRateForState(state);
            frameTimer = 0f;

            ApplyCurrentFrame();
        }

        public void PlayOneShot(CreatureAnimationState state)
        {
            if (definition == null) return;

            var sprites = definition.GetSpritesForState(state);
            if (sprites == null || sprites.Length == 0) return;

            // Remember current state to return to
            returnState = currentState;
            isPlayingOneShot = true;

            SetState(state, loop: false);
        }

        public void Pause()
        {
            isAnimating = false;
        }

        public void Resume()
        {
            isAnimating = true;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            injector = GetComponent<TexturedInjector>();

            if (injector == null)
            {
                Debug.LogError($"[AnimatedCreature] {gameObject.name}: TexturedInjector component required.");
                enabled = false;
                return;
            }

            if (definition == null)
            {
                Debug.LogWarning($"[AnimatedCreature] {gameObject.name}: No CreatureDefinition assigned.");
            }
        }

        private void Start()
        {
            if (definition != null && definition.IsValid)
            {
                SetState(initialState, loopInitialState);
            }

            // Initialize movement tracking
            lastPosition = injector.GetPosition();
            wasMoving = false;
        }

        private void Update()
        {
            // Movement detection
            if (autoDetectMovement && !isPlayingOneShot)
            {
                UpdateMovementDetection();
            }

            if (!isAnimating || currentSprites == null || currentSprites.Length == 0)
                return;

            frameTimer += Time.deltaTime;

            if (frameTimer >= frameDuration)
            {
                frameTimer -= frameDuration;
                AdvanceFrame();
            }
        }

        private void UpdateMovementDetection()
        {
            Vector2 currentPos = injector.GetPosition();
            float velocity = (currentPos - lastPosition).magnitude / Time.deltaTime;
            lastPosition = currentPos;

            bool isMoving = velocity > movementThreshold;

            if (isMoving && !wasMoving)
            {
                OnStartMoving();
            }
            else if (!isMoving && wasMoving)
            {
                OnStopMoving();
            }

            wasMoving = isMoving;
        }

        #endregion

        #region Animation Logic

        private void AdvanceFrame()
        {
            currentFrame++;

            if (currentFrame >= currentSprites.Length)
            {
                if (isLooping)
                {
                    currentFrame = 0;
                }
                else
                {
                    currentFrame = currentSprites.Length - 1;
                    isAnimating = false;

                    // Return from one-shot
                    if (isPlayingOneShot)
                    {
                        isPlayingOneShot = false;
                        SetState(returnState, loop: true);
                        return;
                    }
                }
            }

            ApplyCurrentFrame();
        }

        private void ApplyCurrentFrame()
        {
            if (currentSprites == null || currentFrame >= currentSprites.Length)
                return;

            var sprite = currentSprites[currentFrame];
            if (sprite != null)
            {
                SetInjectorMask(sprite);
            }
        }

        private void SetInjectorMask(Texture2D mask)
        {
            injector.SetMask(mask);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Change creature definition at runtime.
        /// </summary>
        public void SetDefinition(CreatureDefinition newDefinition)
        {
            definition = newDefinition;

            if (definition != null && definition.IsValid)
            {
                SetState(CreatureAnimationState.Idle);
            }
        }

        /// <summary>
        /// Trigger a reaction animation based on positive/negative sentiment.
        /// </summary>
        public void React(bool positive)
        {
            PlayOneShot(positive ? CreatureAnimationState.Positive : CreatureAnimationState.Negative);
        }

        /// <summary>
        /// Called when creature starts moving.
        /// </summary>
        public void OnStartMoving()
        {
            if (!isPlayingOneShot && currentState != CreatureAnimationState.Move)
            {
                SetState(CreatureAnimationState.Move);
            }
        }

        /// <summary>
        /// Called when creature stops moving.
        /// </summary>
        public void OnStopMoving()
        {
            if (!isPlayingOneShot && currentState == CreatureAnimationState.Move)
            {
                SetState(CreatureAnimationState.Idle);
            }
        }

        /// <summary>
        /// Transition to befriended state.
        /// </summary>
        public void Befriend()
        {
            SetState(CreatureAnimationState.Befriended);
        }

        /// <summary>
        /// Trigger activation/special ability animation.
        /// </summary>
        public void Activate()
        {
            PlayOneShot(CreatureAnimationState.Activate);
        }

        #endregion

        #region Editor

        private void OnValidate()
        {
            if (Application.isPlaying && definition != null && definition.IsValid)
            {
                SetState(currentState, isLooping);
            }
        }

        #endregion
    }
}

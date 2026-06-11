using UnityEngine;
using System.Collections.Generic;
using Magi.Inkling.Systems.SimulationLOD0;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Systems.Creatures
{
    /// <summary>
    /// AI behavior component for creatures.
    /// Implements boid flocking, player-relative behaviors, and fluid advection.
    /// Works with TexturedInjector for movement and AnimatedCreature for animation.
    /// </summary>
    [AddComponentMenu("Inkling/Creatures/Creature Behavior")]
    public class CreatureBehavior : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private CreatureBehaviorConfig config;

        [Header("References")]
        [SerializeField] private Transform playerTarget;
        [SerializeField] private int playerInkType = 0;

        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = false;
        [SerializeField] private bool logBehaviorChanges = false;

        // Components
        private TexturedInjector injector;
        private AnimatedCreature animator;
        private ISimulationReader simReader;

        // State
        private Vector2 currentVelocity;
        private Vector2 currentDirection;
        private float currentSpeed;
        private Vector2 lastPosition;

        // Flocking cache
        private static List<CreatureBehavior> allCreatures = new List<CreatureBehavior>();
        private List<CreatureBehavior> nearbyCreatures = new List<CreatureBehavior>();

        #region Properties

        /// <summary>Current position in UV space.</summary>
        public Vector2 Position => injector != null ? injector.GetPosition() : Vector2.zero;

        /// <summary>Current movement direction (normalized).</summary>
        public Vector2 Direction => currentDirection;

        /// <summary>Ink type index for color affinity behaviors.</summary>
        public int InkType => config != null ? config.inkTypeIndex : 0;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _ = logBehaviorChanges; // silence unused-field warning until logging is wired
            injector = GetComponent<TexturedInjector>();
            animator = GetComponent<AnimatedCreature>();

            if (injector == null)
            {
                Debug.LogWarning($"[CreatureBehavior] {gameObject.name}: TexturedInjector not found. Creature movement disabled.");
                enabled = false;
                return;
            }
        }

        private void OnEnable()
        {
            allCreatures.Add(this);
        }

        private void OnDisable()
        {
            allCreatures.Remove(this);
        }

        private void Start()
        {
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<CreatureBehaviorConfig>();
                Debug.LogWarning($"[CreatureBehavior] {gameObject.name}: No config assigned, using defaults.");
            }

            // Find simulation reader for fluid advection
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb is ISimulationReader reader)
                {
                    simReader = reader;
                    break;
                }
            }

            // Initialize state
            lastPosition = Position;
            currentDirection = Random.insideUnitCircle.normalized;
            currentSpeed = config.baseSpeed;

            // Find player if not assigned
            if (playerTarget == null)
            {
                var player = GameObject.FindGameObjectWithTag("Player");
                if (player != null)
                {
                    playerTarget = player.transform;
                }
            }
        }

        private void Update()
        {
            if (config == null) return;

            // Calculate behavior forces
            Vector2 desiredDirection = CalculateBehavior();

            // Smooth direction changes
            currentDirection = Vector2.Lerp(
                desiredDirection.normalized,
                currentDirection,
                config.directionSmoothing
            ).normalized;

            // Apply movement
            currentVelocity = currentDirection * currentSpeed;
            Vector2 newPosition = Position + currentVelocity * Time.deltaTime;

            // Apply fluid advection if available
            if (simReader != null && config.fluidAdvectionWeight > 0)
            {
                Vector2 fluidVel = SampleFluidVelocity(Position);
                newPosition += fluidVel * config.fluidAdvectionWeight * Time.deltaTime;
            }

            // Boundary handling
            newPosition = HandleBoundaries(newPosition);

            // Update injector position
            injector.SetPosition(newPosition);
            injector.SetVelocity(currentVelocity);

            lastPosition = Position;
        }

        #endregion

        #region Behavior Calculation

        private Vector2 CalculateBehavior()
        {
            Vector2 direction = Vector2.zero;

            switch (config.behaviorType)
            {
                case CreatureBehaviorType.Escape:
                    direction = CalculateEscape();
                    break;

                case CreatureBehaviorType.Follow:
                    direction = CalculateFollow();
                    break;

                case CreatureBehaviorType.Stay:
                    direction = currentDirection * 0.1f; // Minimal drift
                    currentSpeed = config.baseSpeed * 0.2f;
                    break;

                case CreatureBehaviorType.Wander:
                    direction = CalculateWander();
                    break;

                case CreatureBehaviorType.ColorAffinity:
                    direction = CalculateColorAffinity();
                    break;

                case CreatureBehaviorType.Collide:
                    direction = CalculateFollow(); // Same as follow, different on contact
                    break;
            }

            // Add flocking if enabled
            if (config.enableFlocking)
            {
                Vector2 flockingForce = CalculateFlocking();
                direction += flockingForce;
            }

            return direction.magnitude > 0 ? direction.normalized : currentDirection;
        }

        private Vector2 CalculateEscape()
        {
            if (playerTarget == null) return CalculateWander();

            Vector2 playerUV = WorldToUV(playerTarget.position);
            float distance = Vector2.Distance(Position, playerUV);

            if (distance < config.playerDetectRadius)
            {
                // Flee from player
                Vector2 awayFromPlayer = (Position - playerUV).normalized;
                currentSpeed = Mathf.Lerp(config.maxSpeed, config.baseSpeed, distance / config.playerDetectRadius);
                return awayFromPlayer * config.playerInfluenceWeight + CalculateWander() * 0.3f;
            }

            return CalculateWander();
        }

        private Vector2 CalculateFollow()
        {
            if (playerTarget == null) return CalculateWander();

            Vector2 playerUV = WorldToUV(playerTarget.position);
            float distance = Vector2.Distance(Position, playerUV);

            if (distance > config.closeRange)
            {
                // Move toward player
                Vector2 towardPlayer = (playerUV - Position).normalized;
                currentSpeed = Mathf.Lerp(config.baseSpeed, config.maxSpeed,
                    Mathf.Clamp01(distance / config.playerDetectRadius));
                return towardPlayer * config.playerInfluenceWeight + CalculateWander() * 0.2f;
            }
            else
            {
                // At close range, orbit/idle
                currentSpeed = config.baseSpeed * 0.5f;
                return CalculateWander();
            }
        }

        private Vector2 CalculateColorAffinity()
        {
            if (playerTarget == null) return CalculateWander();

            Vector2 playerUV = WorldToUV(playerTarget.position);
            float distance = Vector2.Distance(Position, playerUV);

            bool sameColor = (playerInkType == config.inkTypeIndex);

            if (distance < config.playerDetectRadius)
            {
                if (sameColor && config.followMatchingColor)
                {
                    // Follow player with matching color
                    if (distance > config.closeRange)
                    {
                        Vector2 towardPlayer = (playerUV - Position).normalized;
                        currentSpeed = config.maxSpeed;

                        // Trigger positive reaction occasionally
                        if (animator != null && Random.value < 0.001f)
                        {
                            animator.React(true);
                        }

                        return towardPlayer * config.playerInfluenceWeight + CalculateWander() * 0.2f;
                    }
                    else
                    {
                        currentSpeed = config.baseSpeed * 0.3f;
                        return CalculateWander();
                    }
                }
                else if (!sameColor && config.escapeNonMatchingColor)
                {
                    // Flee from player with different color
                    Vector2 awayFromPlayer = (Position - playerUV).normalized;
                    currentSpeed = config.maxSpeed;

                    // Trigger negative reaction occasionally
                    if (animator != null && Random.value < 0.002f)
                    {
                        animator.React(false);
                    }

                    return awayFromPlayer * config.playerInfluenceWeight + CalculateWander() * 0.3f;
                }
            }

            return CalculateWander();
        }

        private Vector2 CalculateWander()
        {
            // Random direction changes
            if (Random.value < config.directionChangeChance)
            {
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                currentDirection = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }

            currentSpeed = config.baseSpeed;
            return currentDirection;
        }

        private Vector2 CalculateFlocking()
        {
            UpdateNearbyCreatures();

            if (nearbyCreatures.Count == 0)
                return Vector2.zero;

            Vector2 alignment = Vector2.zero;
            Vector2 cohesion = Vector2.zero;
            Vector2 separation = Vector2.zero;
            int neighborCount = 0;

            foreach (var other in nearbyCreatures)
            {
                if (other == this || other.InkType != this.InkType)
                    continue;

                float distance = Vector2.Distance(Position, other.Position);

                if (distance < config.flockingRadius && distance > 0.001f)
                {
                    // Alignment: match heading
                    alignment += other.Direction;

                    // Cohesion: move toward center
                    cohesion += other.Position;

                    // Separation: avoid crowding
                    if (distance < config.separationDistance)
                    {
                        separation += (Position - other.Position) / distance;
                    }

                    neighborCount++;
                }
            }

            if (neighborCount > 0)
            {
                alignment = (alignment / neighborCount).normalized * config.alignmentWeight;
                cohesion = ((cohesion / neighborCount) - Position).normalized * config.cohesionWeight;
                separation = separation.normalized * config.separationWeight;
            }

            return alignment + cohesion + separation;
        }

        #endregion

        #region Helpers

        private void UpdateNearbyCreatures()
        {
            nearbyCreatures.Clear();

            foreach (var creature in allCreatures)
            {
                if (creature == this) continue;

                float distance = Vector2.Distance(Position, creature.Position);
                if (distance < config.flockingRadius)
                {
                    nearbyCreatures.Add(creature);
                }
            }
        }

        private Vector2 HandleBoundaries(Vector2 position)
        {
            Vector2 result = position;
            float margin = config.boundaryMargin;
            Vector2 bounds = config.movementBounds;

            // Bounce off boundaries
            if (position.x < margin)
            {
                result.x = margin;
                currentDirection.x = Mathf.Abs(currentDirection.x);
            }
            else if (position.x > bounds.x)
            {
                result.x = bounds.x;
                currentDirection.x = -Mathf.Abs(currentDirection.x);
            }

            if (position.y < margin)
            {
                result.y = margin;
                currentDirection.y = Mathf.Abs(currentDirection.y);
            }
            else if (position.y > bounds.y)
            {
                result.y = bounds.y;
                currentDirection.y = -Mathf.Abs(currentDirection.y);
            }

            return result;
        }

        private Vector2 SampleFluidVelocity(Vector2 uvPosition)
        {
            // In the current architecture, we can't directly sample the velocity texture
            // from C#. This would require AsyncGPUReadback or a compute shader.
            // For now, return zero. Future: integrate with AgentSystem GPU sampling.
            return Vector2.zero;
        }

        private Vector2 WorldToUV(Vector3 worldPos)
        {
            // Simple world-to-UV conversion assuming simulation covers [-0.5, 0.5] range
            // Adjust based on actual simulation setup
            return new Vector2(
                Mathf.Clamp01(worldPos.x / 10f + 0.5f),
                Mathf.Clamp01(worldPos.y / 10f + 0.5f)
            );
        }

        #endregion

        #region Public API

        /// <summary>
        /// Set the player target for behavior calculations.
        /// </summary>
        public void SetPlayerTarget(Transform target, int inkType)
        {
            playerTarget = target;
            playerInkType = inkType;
        }

        /// <summary>
        /// Change behavior configuration at runtime.
        /// </summary>
        public void SetConfig(CreatureBehaviorConfig newConfig)
        {
            config = newConfig;
        }

        /// <summary>
        /// Force creature to flee to a random position outside the detection range.
        /// </summary>
        public void Scatter()
        {
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float distance = Random.Range(config.playerDetectRadius, config.movementBounds.x);

            Vector2 newPos = new Vector2(
                0.5f + Mathf.Cos(angle) * distance,
                0.5f + Mathf.Sin(angle) * distance
            );

            newPos = HandleBoundaries(newPos);
            injector.SetPosition(newPos);
        }

        #endregion

        #region Debug

        private void OnDrawGizmos()
        {
            if (!showDebugGizmos || !Application.isPlaying) return;

            Vector3 worldPos = UVToWorld(Position);

            // Draw position
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(worldPos, 0.2f);

            // Draw direction
            Gizmos.color = Color.yellow;
            Vector3 dirWorld = new Vector3(currentDirection.x, currentDirection.y, 0);
            Gizmos.DrawRay(worldPos, dirWorld * 0.5f);

            // Draw detection radius
            if (config != null)
            {
                Gizmos.color = new Color(0, 1, 0, 0.3f);
                float radiusWorld = config.playerDetectRadius * 10f;
                Gizmos.DrawWireSphere(worldPos, radiusWorld);

                // Draw flocking radius
                Gizmos.color = new Color(0, 0, 1, 0.2f);
                float flockRadiusWorld = config.flockingRadius * 10f;
                Gizmos.DrawWireSphere(worldPos, flockRadiusWorld);
            }
        }

        private Vector3 UVToWorld(Vector2 uv)
        {
            return new Vector3((uv.x - 0.5f) * 10f, (uv.y - 0.5f) * 10f, 0);
        }

        #endregion
    }
}

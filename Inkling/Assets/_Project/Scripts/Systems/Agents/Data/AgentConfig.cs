using UnityEngine;

namespace Magi.Inkling.Systems.Agents
{
    /// <summary>
    /// ScriptableObject containing agent simulation parameters.
    /// Used by AgentSystem to configure flocking and advection behavior.
    /// </summary>
    [CreateAssetMenu(fileName = "AgentConfig", menuName = "Inkling/Agent Config")]
    public class AgentConfig : ScriptableObject
    {
        [Header("Flocking")]
        [Tooltip("Radius in UV space for neighbor detection (0.01 = 1% of screen)")]
        [Range(0.001f, 0.2f)]
        public float neighborRadius = 0.05f;

        [Tooltip("Weight for velocity alignment with neighbors")]
        [Range(0f, 2f)]
        public float alignmentWeight = 1f;

        [Tooltip("Weight for steering toward flock center")]
        [Range(0f, 2f)]
        public float cohesionWeight = 1f;

        [Tooltip("Weight for steering away from nearby neighbors")]
        [Range(0f, 2f)]
        public float separationWeight = 1.5f;

        [Header("Movement")]
        [Tooltip("Maximum agent speed in UV units per second")]
        [Range(0.01f, 1f)]
        public float maxSpeed = 0.1f;

        [Tooltip("Maximum steering force magnitude")]
        [Range(0.01f, 0.5f)]
        public float maxForce = 0.05f;

        [Tooltip("Initial velocity magnitude for spawned agents")]
        [Range(0f, 0.5f)]
        public float initialSpeed = 0.02f;

        [Header("Fluid Coupling")]
        [Tooltip("How strongly agents follow fluid velocity (0=ignore, 1=full advection)")]
        [Range(0f, 1f)]
        public float advectionStrength = 0.5f;

        [Tooltip("How strongly agents participate in flocking (0=solo, 1=full flock)")]
        [Range(0f, 1f)]
        public float flockingStrength = 1f;

        [Header("Defaults")]
        [Tooltip("Default ink type index for spawned agents")]
        [Range(0, 15)]
        public int defaultInkType = 0;

        [Tooltip("Default behavior ID for spawned agents")]
        [Range(0, 7)]
        public int defaultBehaviorId = 0;
    }
}

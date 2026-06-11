using UnityEngine;

namespace Magi.Inkling.Systems.Agents
{
    /// <summary>
    /// Renders agents using Graphics.DrawProceduralIndirect.
    /// No CPU readback required - reads agent buffer directly in vertex shader.
    /// </summary>
    [RequireComponent(typeof(AgentSystem))]
    public class AgentRenderer : MonoBehaviour
    {
        [Header("Rendering")]
        [SerializeField] private Material agentMaterial;
        [SerializeField] private float pointSize = 4f;
        [SerializeField] private Color agentColor = Color.white;

        [Header("Debug")]
        [SerializeField] private bool showInactiveAgents = false;
        [SerializeField] private Color inactiveColor = new Color(1, 1, 1, 0.2f);

        private AgentSystem agentSystem;
        private MaterialPropertyBlock propertyBlock;
        private Bounds renderBounds;

        private static readonly int AgentsBufferId = Shader.PropertyToID("_Agents");
        private static readonly int AgentCountId = Shader.PropertyToID("_AgentCount");
        private static readonly int PointSizeId = Shader.PropertyToID("_PointSize");
        private static readonly int AgentColorId = Shader.PropertyToID("_AgentColor");
        private static readonly int InactiveColorId = Shader.PropertyToID("_InactiveColor");
        private static readonly int ShowInactiveId = Shader.PropertyToID("_ShowInactive");

        private void Awake()
        {
            agentSystem = GetComponent<AgentSystem>();
            propertyBlock = new MaterialPropertyBlock();

            // Large bounds to ensure rendering isn't culled
            renderBounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        }

        private void OnValidate()
        {
            pointSize = Mathf.Max(1f, pointSize);
        }

        private void LateUpdate()
        {
            if (agentMaterial == null || !agentSystem.IsInitialized)
                return;

            var buffer = agentSystem.GetAgentBuffer();
            if (buffer == null)
                return;

            // Update material properties
            propertyBlock.SetBuffer(AgentsBufferId, buffer);
            propertyBlock.SetInt(AgentCountId, agentSystem.MaxAgents);
            propertyBlock.SetFloat(PointSizeId, pointSize);
            propertyBlock.SetColor(AgentColorId, agentColor);
            propertyBlock.SetColor(InactiveColorId, inactiveColor);
            propertyBlock.SetFloat(ShowInactiveId, showInactiveAgents ? 1f : 0f);

            // Draw all agents as points
            Graphics.DrawProcedural(
                agentMaterial,
                renderBounds,
                MeshTopology.Points,
                agentSystem.MaxAgents,
                1,
                null,
                propertyBlock,
                UnityEngine.Rendering.ShadowCastingMode.Off,
                false,
                gameObject.layer
            );
        }

        private void OnDrawGizmosSelected()
        {
            // Draw spawn region hint
            Gizmos.color = new Color(0, 1, 0, 0.3f);
            Gizmos.DrawWireCube(
                new Vector3(0.5f, 0.5f, 0),
                new Vector3(0.4f, 0.4f, 0.01f)
            );
        }
    }
}

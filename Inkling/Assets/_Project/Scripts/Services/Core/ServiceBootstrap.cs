using UnityEngine;
using Magi.UnityTools.Patterns;
using Magi.Inkling.Services.ITUMS;
using Magi.InkTools.ITUMS;

namespace Magi.Inkling.Services.Core
{
    /// <summary>
    /// Creates game-specific services that must exist before ServiceLocator auto-discovery runs.
    /// Runs at -200 so it fires before ServiceLocator (-100).
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class ServiceBootstrap : MonoBehaviour
    {
        [Tooltip("If enabled, ensure a PersonaService exists (for ITUMS telemetry) even if not placed in scene.")]
        [SerializeField] private bool ensurePersonaService = true;
        [Tooltip("Optional default PersonaConfig to assign if PersonaService is auto-created.")]
        [SerializeField] private PersonaConfig defaultPersonaConfig;

        private void Awake()
        {
            if (ensurePersonaService &&
                FindAnyObjectByType<PersonaServiceBehaviour>(FindObjectsInactive.Include) == null)
            {
                var go = new GameObject("PersonaService");
                var svc = go.AddComponent<PersonaServiceBehaviour>();
                if (defaultPersonaConfig != null && svc.Config == null)
                {
                    svc.Config = defaultPersonaConfig;
                }
            }
        }
    }
}

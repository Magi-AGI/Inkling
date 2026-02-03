using System.Collections.Generic;
using UnityEngine;
using Magi.Inkling.Services.ITUMS;
using Magi.InkTools.ITUMS;

namespace Magi.Inkling.Services.Core
{
    /// <summary>
    /// Bootstrapper that registers known services into the ServiceLocator at runtime.
    /// Attach to the ServiceLocator GameObject or any early-running object.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ServiceBootstrap : MonoBehaviour
    {
        [Tooltip("Explicit service references to register. If empty, will attempt to find common services in scene.")]
        [SerializeField] private List<Object> explicitServices = new();
        [Tooltip("If enabled, ensure a PersonaService exists (for ITUMS telemetry) even if not placed in scene.")]
        [SerializeField] private bool ensurePersonaService = true;
        [Tooltip("Optional default PersonaConfig to assign if PersonaService is auto-created.")]
        [SerializeField] private PersonaConfig defaultPersonaConfig;

        private void Awake()
        {
            var locator = ServiceLocator.Instance ?? FindAnyObjectByType<ServiceLocator>(FindObjectsInactive.Include);
            if (locator == null)
            {
                Debug.LogWarning("[ServiceBootstrap] ServiceLocator not found; skipping bootstrap.");
                return;
            }

            var toRegister = new List<Object>(explicitServices);

            // Auto-discover common services if not explicitly listed
            if (toRegister.Count == 0)
            {
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (mb is IService) toRegister.Add(mb);
                }

                if (ensurePersonaService)
                {
                    var existingPersona = locator.Resolve<IPersonaService>();
                    if (existingPersona == null)
                    {
                        var go = new GameObject("PersonaService");
                        var svc = go.AddComponent<PersonaServiceBehaviour>();
                        if (defaultPersonaConfig != null && svc.Config == null)
                        {
                            svc.Config = defaultPersonaConfig;
                        }
                        toRegister.Add(svc);
                    }
                }
            }

            foreach (var obj in toRegister)
            {
                if (obj is IService svc)
                {
                    locator.RegisterService(svc);
                }
            }
        }
    }
}

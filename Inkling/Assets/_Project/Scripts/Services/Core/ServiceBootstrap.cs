using System.Collections.Generic;
using UnityEngine;

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

        private void Awake()
        {
            var locator = ServiceLocator.Instance ?? FindAnyObjectByType<ServiceLocator>();
            if (locator == null)
            {
                Debug.LogWarning("[ServiceBootstrap] ServiceLocator not found; skipping bootstrap.");
                return;
            }

            var toRegister = new List<Object>(explicitServices);

            // Auto-discover common services if not explicitly listed
            if (toRegister.Count == 0)
            {
                toRegister.AddRange(FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None));
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

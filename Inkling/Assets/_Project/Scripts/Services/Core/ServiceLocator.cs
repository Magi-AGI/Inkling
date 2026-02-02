using System;
using System.Collections.Generic;
using UnityEngine;

namespace Magi.Inkling.Services.Core
{
    /// <summary>
    /// Minimal service locator for Phase 7C.
    /// Register services via inspector list; Resolve<T>() to fetch at runtime.
    /// Not a full DI container—kept lightweight to avoid complexity.
    /// </summary>
    public class ServiceLocator : MonoBehaviour
    {
        [Tooltip("Services to register at Awake. Must implement IService marker.")]
        [SerializeField] private List<UnityEngine.Object> services = new();

        private readonly Dictionary<Type, object> registry = new();

        public static ServiceLocator Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            foreach (var obj in services)
            {
                if (obj is IService service)
                {
                    RegisterService(service);
                }
            }
        }

        public void RegisterService(IService service)
        {
            var type = service.GetType();
            // register concrete type
            registry[type] = service;
            // register first interface that implements IService (excluding IService itself)
            foreach (var iface in type.GetInterfaces())
            {
                if (iface == typeof(IService)) continue;
                if (typeof(IService).IsAssignableFrom(iface))
                {
                    registry[iface] = service;
                    break;
                }
            }
        }

        public T Resolve<T>() where T : class
        {
            registry.TryGetValue(typeof(T), out var svc);
            return svc as T;
        }

        public bool TryResolve<T>(out T svc) where T : class
        {
            svc = Resolve<T>();
            return svc != null;
        }
    }
}

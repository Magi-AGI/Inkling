using UnityEngine;
using Magi.InkTools.ITUMS;
using Magi.Inkling.Services.Core;
using Magi.Inkling.Services.Diagnostics;

namespace Magi.Inkling.Services.ITUMS
{
    /// <summary>
    /// Inkling-facing wrapper that registers the InkTools PersonaService with the ServiceLocator
    /// and emits LogSink telemetry on persona changes.
    /// </summary>
    public class PersonaServiceBehaviour : PersonaService, IService, IInitializable
    {
        public bool Initialized { get; private set; }

        public Result Initialize(ServiceLocator locator)
        {
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<PersonaConfig>();
            }

            locator?.RegisterService(this);
            Initialized = true;
            return Result.Success();
        }

        protected override void Awake()
        {
            base.Awake();
            var locator = ServiceLocator.Instance;
            locator?.RegisterService(this);
            OnPersonaChanged += HandlePersonaChanged;
            Initialized = true;
        }

        private void OnDestroy()
        {
            OnPersonaChanged -= HandlePersonaChanged;
        }

        private void HandlePersonaChanged(Persona previous, Persona current, float quietScore, float avgStroke)
        {
            LogSink.AddGlobal($"[PersonaService] Persona -> {current} (quiet={quietScore:F2}s, avgStroke={avgStroke:F3} u/s)");
        }

        public System.Threading.Tasks.Task<Result> InitializeAsync()
        {
            var r = Initialize(ServiceLocator.Instance);
            return System.Threading.Tasks.Task.FromResult(r);
        }
    }
}

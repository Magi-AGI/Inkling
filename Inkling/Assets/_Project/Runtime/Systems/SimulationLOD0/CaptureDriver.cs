using UnityEngine;
using UnityEngine.InputSystem;

namespace Inkling.Systems.SimulationLOD0
{
    public class CaptureDriver : MonoBehaviour
    {
        public SimulationRecorder recorder;
        public string scenario = "test01";

        void Start()
        {
            if (recorder != null)
            {
                recorder.BeginScenario(scenario);
            }
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                recorder?.CaptureFrame();
            }
        }
    }
}


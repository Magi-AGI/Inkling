using UnityEngine;

namespace Inkling.Dev
{
    public class RecordReplay : MonoBehaviour
    {
        public int seed = 12345;
        public bool recording;

        public void BeginRecord(int fixedSeed)
        {
            seed = fixedSeed;
            recording = true;
            Random.InitState(seed);
            // TODO: serialize inputs
        }

        public void EndRecord()
        {
            recording = false;
        }

        public void Replay()
        {
            Random.InitState(seed);
            // TODO: apply serialized inputs deterministically
        }
    }
}


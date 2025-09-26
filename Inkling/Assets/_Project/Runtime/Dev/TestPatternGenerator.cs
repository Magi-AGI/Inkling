using UnityEngine;

namespace Inkling.Dev
{
    public class TestPatternGenerator : MonoBehaviour
    {
        public RenderTexture target;
        public bool animate = true;
        public Color colorA = new Color(0.1f, 0.6f, 1f, 1f);
        public Color colorB = new Color(1f, 0.3f, 0.2f, 1f);

        void EnsureRT()
        {
            if (target == null)
            {
                target = new RenderTexture(512, 512, 0, RenderTextureFormat.ARGB32)
                {
                    enableRandomWrite = false
                };
                target.Create();
            }
        }

        void Update()
        {
            EnsureRT();
            float t = animate ? (0.5f + 0.5f * Mathf.Sin(Time.time)) : 1f;
            var c = Color.Lerp(colorA, colorB, t);
            var prev = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(true, true, c);
            RenderTexture.active = prev;
        }
    }
}


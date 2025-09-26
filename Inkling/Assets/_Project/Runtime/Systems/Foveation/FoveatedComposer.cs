using UnityEngine;

namespace Inkling.Systems.Foveation
{
    public class FoveatedComposer : MonoBehaviour
    {
        [Range(0.1f, 0.9f)] public float centerRadius = 0.33f;
        public Texture seamMask; // optional feather mask

        public void Compose(RenderTexture center, RenderTexture periphery, RenderTexture target)
        {
            // TODO: implement a proper shader-based composite with seam mask
            Graphics.Blit(center, target);
        }
    }
}


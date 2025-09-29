using UnityEngine;

namespace Magi.Inkling.Runtime.Systems.Foveation
{
    /// <summary>
    /// Composites foveated render layers with proper seam blending.
    /// Center region is high-quality, periphery is lower quality.
    /// </summary>
    public class FoveatedComposer : MonoBehaviour
    {
        [Header("Foveation Settings")]
        [Range(0.1f, 0.9f)] public float centerRadius = 0.33f;
        [Range(0.01f, 0.2f)] public float featherSize = 0.05f;
        [Range(0.5f, 4.0f)] public float blendPower = 2.0f;

        [Header("Blend Resources")]
        [SerializeField] private Material blendMaterial;
        [SerializeField] private Shader blendShader;

        private void Start()
        {
            if (blendMaterial == null && blendShader == null)
            {
                // Try to load the BlendSeam shader
                blendShader = Shader.Find("Inkling/BlendSeam");
                if (blendShader != null)
                {
                    blendMaterial = new Material(blendShader);
                }
                else
                {
                    Debug.LogWarning("[FoveatedComposer] BlendSeam shader not found, falling back to simple blit");
                }
            }
        }

        public void Compose(RenderTexture center, RenderTexture periphery, RenderTexture target)
        {
            if (blendMaterial == null)
            {
                // Fallback: simple blit without blending
                Graphics.Blit(center, target);
                return;
            }

            // Calculate center rect from radius
            float halfSize = centerRadius * 0.5f;
            Vector4 centerRect = new Vector4(
                0.5f - halfSize,  // x
                0.5f - halfSize,  // y
                centerRadius,     // width
                centerRadius      // height
            );

            // Set shader properties
            blendMaterial.SetTexture("_CenterTex", center);
            blendMaterial.SetTexture("_PeripheryTex", periphery);
            blendMaterial.SetVector("_CenterRect", centerRect);
            blendMaterial.SetFloat("_FeatherSize", featherSize);
            blendMaterial.SetFloat("_BlendPower", blendPower);

            // Perform composite
            Graphics.Blit(null, target, blendMaterial);
        }

        /// <summary>
        /// Compose with custom center rect for asymmetric foveation.
        /// </summary>
        public void Compose(RenderTexture center, RenderTexture periphery, RenderTexture target, Rect centerRect)
        {
            if (blendMaterial == null)
            {
                Graphics.Blit(center, target);
                return;
            }

            Vector4 rect = new Vector4(centerRect.x, centerRect.y, centerRect.width, centerRect.height);

            blendMaterial.SetTexture("_CenterTex", center);
            blendMaterial.SetTexture("_PeripheryTex", periphery);
            blendMaterial.SetVector("_CenterRect", rect);
            blendMaterial.SetFloat("_FeatherSize", featherSize);
            blendMaterial.SetFloat("_BlendPower", blendPower);

            Graphics.Blit(null, target, blendMaterial);
        }

        private void OnDestroy()
        {
            if (blendMaterial != null && blendShader != null)
            {
                // Only destroy if we created it
                Destroy(blendMaterial);
            }
        }
    }
}
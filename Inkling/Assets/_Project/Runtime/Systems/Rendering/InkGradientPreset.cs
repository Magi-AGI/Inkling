using System;
using UnityEngine;

namespace Magi.Inkling.Runtime.Systems.Rendering
{
    /// <summary>
    /// ScriptableObject for storing ink gradient presets.
    /// Provides fine visual control over how different ink types are rendered.
    /// </summary>
    [CreateAssetMenu(fileName = "InkGradientPreset", menuName = "Inkling/Ink Gradient Preset")]
    public class InkGradientPreset : ScriptableObject
    {
        [Header("Fire/Heat")]
        public Gradient fireGradient = CreateFireGradient();
        [Range(0, 3)] public float fireEmission = 2.0f;
        public AnimationCurve fireIntensityCurve = AnimationCurve.Linear(0, 0, 1, 1);

        [Header("Water/Fluid")]
        public Gradient waterGradient = CreateWaterGradient();
        [Range(0, 3)] public float waterEmission = 0.5f;
        public AnimationCurve waterIntensityCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Metal")]
        public Gradient metalGradient = CreateMetalGradient();
        [Range(0, 3)] public float metalEmission = 0.8f;
        public AnimationCurve metalIntensityCurve = AnimationCurve.Linear(0, 0, 1, 1);

        [Header("Electricity")]
        public Gradient electricityGradient = CreateElectricityGradient();
        [Range(0, 3)] public float electricityEmission = 2.5f;
        public AnimationCurve electricityIntensityCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [Range(0, 10)] public float electricityFlickerSpeed = 5.0f;

        [Header("Ice")]
        public Gradient iceGradient = CreateIceGradient();
        [Range(0, 3)] public float iceEmission = 0.7f;
        public AnimationCurve iceIntensityCurve = AnimationCurve.Linear(0, 0, 1, 1);

        [Header("Plant/Organic")]
        public Gradient plantGradient = CreatePlantGradient();
        [Range(0, 3)] public float plantEmission = 0.4f;
        public AnimationCurve plantIntensityCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Steam/Vapor")]
        public Gradient steamGradient = CreateSteamGradient();
        [Range(0, 3)] public float steamEmission = 0.6f;
        public AnimationCurve steamIntensityCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Dust/Particles")]
        public Gradient dustGradient = CreateDustGradient();
        [Range(0, 3)] public float dustEmission = 0.3f;
        public AnimationCurve dustIntensityCurve = AnimationCurve.Linear(0, 0, 1, 1);

        [Header("Global Settings")]
        [Range(0, 2)] public float globalSaturation = 1.0f;
        [Range(0, 2)] public float globalBrightness = 1.0f;
        [Range(0, 1)] public float edgeGlowStrength = 0.2f;

        /// <summary>
        /// Generate gradient textures for shader use
        /// </summary>
        public GradientTextures GenerateTextures(int resolution = 256)
        {
            var textures = new GradientTextures();

            textures.fireTexture = CreateGradientTexture(fireGradient, fireIntensityCurve, resolution);
            textures.waterTexture = CreateGradientTexture(waterGradient, waterIntensityCurve, resolution);
            textures.metalTexture = CreateGradientTexture(metalGradient, metalIntensityCurve, resolution);
            textures.electricityTexture = CreateGradientTexture(electricityGradient, electricityIntensityCurve, resolution);
            textures.iceTexture = CreateGradientTexture(iceGradient, iceIntensityCurve, resolution);
            textures.plantTexture = CreateGradientTexture(plantGradient, plantIntensityCurve, resolution);
            textures.steamTexture = CreateGradientTexture(steamGradient, steamIntensityCurve, resolution);
            textures.dustTexture = CreateGradientTexture(dustGradient, dustIntensityCurve, resolution);

            return textures;
        }

        /// <summary>
        /// Apply preset to material
        /// </summary>
        public void ApplyToMaterial(Material material)
        {
            if (material == null) return;

            var textures = GenerateTextures();

            material.SetTexture("_FireGradientTex", textures.fireTexture);
            material.SetTexture("_WaterGradientTex", textures.waterTexture);
            material.SetTexture("_MetalGradientTex", textures.metalTexture);
            material.SetTexture("_ElectricityGradientTex", textures.electricityTexture);
            material.SetTexture("_IceGradientTex", textures.iceTexture);
            material.SetTexture("_PlantGradientTex", textures.plantTexture);
            material.SetTexture("_SteamGradientTex", textures.steamTexture);
            material.SetTexture("_DustGradientTex", textures.dustTexture);

            material.SetFloat("_SaturationBoost", globalSaturation);
            material.SetFloat("_EdgeGlow", edgeGlowStrength);
        }

        private static Texture2D CreateGradientTexture(Gradient gradient, AnimationCurve curve, int width)
        {
            var texture = new Texture2D(width, 4, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            Color[] colors = new Color[width * 4];

            for (int x = 0; x < width; x++)
            {
                float t = x / (float)(width - 1);
                float curveValue = curve.Evaluate(t);
                Color gradientColor = gradient.Evaluate(t);

                // Apply curve to gradient
                gradientColor *= curveValue;

                // Fill all Y rows with same color (for potential 2D gradient mapping)
                for (int y = 0; y < 4; y++)
                {
                    colors[y * width + x] = gradientColor;
                }
            }

            texture.SetPixels(colors);
            texture.Apply();

            return texture;
        }

        #region Default Gradient Creators

        private static Gradient CreateFireGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.black, 0f),
                    new GradientColorKey(new Color(0.5f, 0f, 0f), 0.2f),
                    new GradientColorKey(new Color(1f, 0.3f, 0f), 0.5f),
                    new GradientColorKey(new Color(1f, 0.8f, 0f), 0.75f),
                    new GradientColorKey(new Color(1f, 1f, 0.8f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.1f),
                    new GradientAlphaKey(1f, 0.9f),
                    new GradientAlphaKey(0.8f, 1f)
                }
            );
            return gradient;
        }

        private static Gradient CreateWaterGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.9f, 0.95f, 1f, 0), 0f),
                    new GradientColorKey(new Color(0.2f, 0.5f, 0.8f), 0.3f),
                    new GradientColorKey(new Color(0.1f, 0.3f, 0.6f), 0.7f),
                    new GradientColorKey(new Color(0.05f, 0.1f, 0.3f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.2f, 0f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(1f, 1f)
                }
            );
            return gradient;
        }

        private static Gradient CreateMetalGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.3f, 0.3f, 0.35f), 0f),
                    new GradientColorKey(new Color(0.6f, 0.6f, 0.65f), 0.5f),
                    new GradientColorKey(new Color(0.9f, 0.9f, 0.95f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.5f, 0f),
                    new GradientAlphaKey(1f, 0.2f),
                    new GradientAlphaKey(1f, 1f)
                }
            );
            return gradient;
        }

        private static Gradient CreateElectricityGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.5f, 0.5f, 1f), 0f),
                    new GradientColorKey(new Color(0.7f, 0.7f, 1f), 0.3f),
                    new GradientColorKey(new Color(1f, 1f, 1f), 0.6f),
                    new GradientColorKey(new Color(0.8f, 0.8f, 1f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.1f),
                    new GradientAlphaKey(1f, 1f)
                }
            );
            return gradient;
        }

        private static Gradient CreateIceGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.85f, 0.95f, 1f), 0f),
                    new GradientColorKey(new Color(0.7f, 0.85f, 1f), 0.5f),
                    new GradientColorKey(new Color(0.5f, 0.7f, 0.9f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.3f, 0f),
                    new GradientAlphaKey(0.9f, 0.5f),
                    new GradientAlphaKey(1f, 1f)
                }
            );
            return gradient;
        }

        private static Gradient CreatePlantGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.2f, 0.3f, 0.1f), 0f),
                    new GradientColorKey(new Color(0.3f, 0.6f, 0.2f), 0.5f),
                    new GradientColorKey(new Color(0.5f, 0.8f, 0.3f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.3f, 0f),
                    new GradientAlphaKey(1f, 0.3f),
                    new GradientAlphaKey(1f, 1f)
                }
            );
            return gradient;
        }

        private static Gradient CreateSteamGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.95f, 0.95f, 0.95f), 0f),
                    new GradientColorKey(new Color(0.85f, 0.85f, 0.85f), 0.5f),
                    new GradientColorKey(new Color(0.7f, 0.7f, 0.7f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.5f, 0.3f),
                    new GradientAlphaKey(0.2f, 1f)
                }
            );
            return gradient;
        }

        private static Gradient CreateDustGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.7f, 0.6f, 0.5f), 0f),
                    new GradientColorKey(new Color(0.6f, 0.5f, 0.4f), 0.5f),
                    new GradientColorKey(new Color(0.4f, 0.35f, 0.3f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.7f, 0.5f),
                    new GradientAlphaKey(0.4f, 1f)
                }
            );
            return gradient;
        }

        #endregion

        [Serializable]
        public class GradientTextures
        {
            public Texture2D fireTexture;
            public Texture2D waterTexture;
            public Texture2D metalTexture;
            public Texture2D electricityTexture;
            public Texture2D iceTexture;
            public Texture2D plantTexture;
            public Texture2D steamTexture;
            public Texture2D dustTexture;

            public void Dispose()
            {
                if (fireTexture != null) DestroyImmediate(fireTexture);
                if (waterTexture != null) DestroyImmediate(waterTexture);
                if (metalTexture != null) DestroyImmediate(metalTexture);
                if (electricityTexture != null) DestroyImmediate(electricityTexture);
                if (iceTexture != null) DestroyImmediate(iceTexture);
                if (plantTexture != null) DestroyImmediate(plantTexture);
                if (steamTexture != null) DestroyImmediate(steamTexture);
                if (dustTexture != null) DestroyImmediate(dustTexture);
            }
        }
    }
}
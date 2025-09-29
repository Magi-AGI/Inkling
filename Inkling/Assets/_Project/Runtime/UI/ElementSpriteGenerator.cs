using UnityEngine;
using System.Collections.Generic;

namespace Magi.Inkling.Runtime.UI
{
    /// <summary>
    /// Generates simple colored sprites for element types at runtime.
    /// Attach to a GameObject and access the generated sprites via the static dictionary.
    /// </summary>
    public class ElementSpriteGenerator : MonoBehaviour
    {
        public static Dictionary<string, Sprite> ElementSprites { get; private set; }

        [Header("Sprite Settings")]
        [SerializeField] private int spriteSize = 32;
        [SerializeField] private bool generateOnAwake = true;

        [Header("Element Colors")]
        [SerializeField] private Color fireColor = new Color(1f, 0.4f, 0.1f, 1f);      // Orange-red
        [SerializeField] private Color waterColor = new Color(0.2f, 0.6f, 1f, 1f);     // Blue
        [SerializeField] private Color iceColor = new Color(0.7f, 0.9f, 1f, 1f);       // Light blue
        [SerializeField] private Color lightningColor = new Color(1f, 1f, 0.3f, 1f);   // Yellow

        private void Awake()
        {
            if (generateOnAwake)
            {
                GenerateSprites();
            }
        }

        public void GenerateSprites()
        {
            ElementSprites = new Dictionary<string, Sprite>();

            // Generate sprites for each element
            ElementSprites["Fire"] = CreateElementSprite(fireColor, "🔥");
            ElementSprites["Water"] = CreateElementSprite(waterColor, "💧");
            ElementSprites["Ice"] = CreateElementSprite(iceColor, "❄");
            ElementSprites["Lightning"] = CreateElementSprite(lightningColor, "⚡");

            Debug.Log($"[ElementSpriteGenerator] Generated {ElementSprites.Count} element sprites");
        }

        private Sprite CreateElementSprite(Color color, string symbol = null)
        {
            // Create a texture with a simple gradient or solid color
            Texture2D texture = new Texture2D(spriteSize, spriteSize);

            // Create a circular gradient
            Vector2 center = new Vector2(spriteSize * 0.5f, spriteSize * 0.5f);
            float radius = spriteSize * 0.4f;

            for (int y = 0; y < spriteSize; y++)
            {
                for (int x = 0; x < spriteSize; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);

                    if (distance <= radius)
                    {
                        // Inside the circle - create a gradient from center
                        float t = distance / radius;
                        float alpha = Mathf.Lerp(1f, 0.3f, t * t); // Quadratic falloff
                        Color pixelColor = color;
                        pixelColor.a = alpha;

                        // Add a bright center
                        if (distance < radius * 0.3f)
                        {
                            pixelColor = Color.Lerp(Color.white, pixelColor, distance / (radius * 0.3f));
                        }

                        texture.SetPixel(x, y, pixelColor);
                    }
                    else
                    {
                        // Outside the circle - transparent
                        texture.SetPixel(x, y, Color.clear);
                    }
                }
            }

            // Apply a simple border
            DrawCircleBorder(texture, center, radius, Color.Lerp(color, Color.black, 0.3f));

            texture.Apply();
            texture.filterMode = FilterMode.Bilinear;

            // Create sprite from texture
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0, 0, spriteSize, spriteSize),
                new Vector2(0.5f, 0.5f),
                spriteSize
            );

            return sprite;
        }

        private void DrawCircleBorder(Texture2D texture, Vector2 center, float radius, Color borderColor)
        {
            float borderWidth = 2f;

            for (int y = 0; y < spriteSize; y++)
            {
                for (int x = 0; x < spriteSize; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);

                    // Draw border
                    if (distance >= radius - borderWidth && distance <= radius)
                    {
                        Color existing = texture.GetPixel(x, y);
                        if (existing.a > 0)
                        {
                            texture.SetPixel(x, y, Color.Lerp(existing, borderColor, 0.5f));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Alternative: Generate simple square sprites with icons
        /// </summary>
        public static class SimpleSprites
        {
            public static Sprite CreateColoredSquare(Color color, int size = 32)
            {
                Texture2D texture = new Texture2D(size, size);
                Color[] pixels = new Color[size * size];

                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }

                texture.SetPixels(pixels);
                texture.Apply();

                return Sprite.Create(
                    texture,
                    new Rect(0, 0, size, size),
                    new Vector2(0.5f, 0.5f)
                );
            }
        }
    }

    /// <summary>
    /// ScriptableObject to store element sprites if you want persistent assets
    /// </summary>
    [CreateAssetMenu(fileName = "ElementSprites", menuName = "Inkling/Element Sprites")]
    public class ElementSpriteSet : ScriptableObject
    {
        [Header("Element Sprites")]
        public Sprite fireSprite;
        public Sprite waterSprite;
        public Sprite iceSprite;
        public Sprite lightningSprite;
        public Sprite steamSprite;
        public Sprite earthSprite;
        public Sprite metalSprite;
        public Sprite plantSprite;

        public Sprite GetSprite(string elementName)
        {
            switch (elementName.ToLower())
            {
                case "fire": return fireSprite;
                case "water": return waterSprite;
                case "ice": return iceSprite;
                case "lightning": return lightningSprite;
                case "steam": return steamSprite;
                case "earth": return earthSprite;
                case "metal": return metalSprite;
                case "plant": return plantSprite;
                default: return null;
            }
        }
    }
}
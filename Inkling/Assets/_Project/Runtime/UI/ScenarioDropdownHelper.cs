using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

namespace Magi.Inkling.Runtime.UI
{
    /// <summary>
    /// Helper to populate a dropdown with scenario options including colored sprites.
    /// Attach this to the GameObject with the Dropdown component.
    /// </summary>
    public class ScenarioDropdownHelper : MonoBehaviour
    {
        [Header("Dropdown Configuration")]
        [SerializeField] private Dropdown targetDropdown;
        [SerializeField] private bool autoPopulateOnStart = true;

        [Header("Scenario Definitions")]
        [SerializeField] private List<ScenarioOption> scenarios = new List<ScenarioOption>
        {
            new ScenarioOption { name = "Fire", color = new Color(1f, 0.4f, 0.1f, 1f) },
            new ScenarioOption { name = "Water", color = new Color(0.2f, 0.6f, 1f, 1f) },
            new ScenarioOption { name = "Ice", color = new Color(0.7f, 0.9f, 1f, 1f) },
            new ScenarioOption { name = "Lightning", color = new Color(1f, 1f, 0.3f, 1f) }
        };

        [System.Serializable]
        public class ScenarioOption
        {
            public string name;
            public Color color;
            public Sprite customSprite; // Optional: assign in Inspector if you have sprites
        }

        private Dictionary<string, Sprite> generatedSprites = new Dictionary<string, Sprite>();

        private void Start()
        {
            if (targetDropdown == null)
            {
                targetDropdown = GetComponent<Dropdown>();
            }

            if (autoPopulateOnStart && targetDropdown != null)
            {
                PopulateDropdown();
            }
        }

        public void PopulateDropdown()
        {
            if (targetDropdown == null) return;

            targetDropdown.ClearOptions();
            List<Dropdown.OptionData> options = new List<Dropdown.OptionData>();

            foreach (var scenario in scenarios)
            {
                Sprite sprite = scenario.customSprite;

                // If no custom sprite, generate one
                if (sprite == null)
                {
                    sprite = GetOrCreateSprite(scenario.name, scenario.color);
                }

                var option = new Dropdown.OptionData(scenario.name, sprite);
                options.Add(option);
            }

            targetDropdown.AddOptions(options);
        }

        private Sprite GetOrCreateSprite(string name, Color color)
        {
            if (generatedSprites.ContainsKey(name))
            {
                return generatedSprites[name];
            }

            // Create a simple 32x32 colored square with rounded corners
            int size = 32;
            Texture2D texture = new Texture2D(size, size);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Create rounded corners
                    float cornerRadius = 4f;
                    bool isCorner = false;

                    // Check if pixel is in corner region
                    if ((x < cornerRadius && y < cornerRadius) ||
                        (x >= size - cornerRadius && y < cornerRadius) ||
                        (x < cornerRadius && y >= size - cornerRadius) ||
                        (x >= size - cornerRadius && y >= size - cornerRadius))
                    {
                        // Calculate distance from corner
                        float cx = x < size / 2 ? cornerRadius : size - cornerRadius;
                        float cy = y < size / 2 ? cornerRadius : size - cornerRadius;
                        float dist = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));

                        if (dist > cornerRadius)
                        {
                            isCorner = true;
                        }
                    }

                    if (isCorner)
                    {
                        texture.SetPixel(x, y, Color.clear);
                    }
                    else
                    {
                        // Add subtle gradient
                        float gradient = 1f - (y / (float)size) * 0.2f;
                        Color pixelColor = color * gradient;
                        pixelColor.a = color.a;
                        texture.SetPixel(x, y, pixelColor);
                    }
                }
            }

            texture.Apply();
            texture.filterMode = FilterMode.Point; // Crisp pixels

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f // Pixels per unit
            );

            generatedSprites[name] = sprite;
            return sprite;
        }

        /// <summary>
        /// Get the currently selected scenario name
        /// </summary>
        public string GetSelectedScenario()
        {
            if (targetDropdown != null && targetDropdown.options.Count > 0)
            {
                return targetDropdown.options[targetDropdown.value].text;
            }
            return "Fire"; // Default
        }

        /// <summary>
        /// Set the dropdown to a specific scenario by name
        /// </summary>
        public void SetScenario(string scenarioName)
        {
            if (targetDropdown == null) return;

            for (int i = 0; i < targetDropdown.options.Count; i++)
            {
                if (targetDropdown.options[i].text == scenarioName)
                {
                    targetDropdown.value = i;
                    break;
                }
            }
        }
    }
}
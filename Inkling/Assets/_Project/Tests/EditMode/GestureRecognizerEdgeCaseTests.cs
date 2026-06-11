using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Magi.Inkling.Systems.Gestures;

namespace Magi.Inkling.Tests.EditMode
{
    public class GestureRecognizerEdgeCaseTests
    {
        [Test]
        public void Recognize_EmptyInput_ReturnsNullTemplate()
        {
            var templates = new[] { CreateTemplate("Line", new Vector2(0, 0.5f), new Vector2(1, 0.5f)) };

            var (tmpl, score) = GestureRecognizer.Recognize(new List<Vector2>(), templates);

            Assert.IsNull(tmpl);
            Assert.AreEqual(0f, score);
        }

        [Test]
        public void Recognize_SinglePointInput_ReturnsNullTemplate()
        {
            var templates = new[] { CreateTemplate("Line", new Vector2(0, 0.5f), new Vector2(1, 0.5f)) };
            var input = new List<Vector2> { new(0.5f, 0.5f) };

            var (tmpl, score) = GestureRecognizer.Recognize(input, templates);

            Assert.IsNull(tmpl);
            Assert.AreEqual(0f, score);
        }

        [Test]
        public void Recognize_EmptyTemplates_ReturnsNullTemplate()
        {
            var input = new List<Vector2> { new(0f, 0.5f), new(1f, 0.5f) };

            var (tmpl, score) = GestureRecognizer.Recognize(input, new GestureTemplate[0]);

            Assert.IsNull(tmpl);
            Assert.AreEqual(0f, score);
        }

        [Test]
        public void Recognize_NullTemplates_ReturnsNullTemplate()
        {
            var input = new List<Vector2> { new(0f, 0.5f), new(1f, 0.5f) };

            var (tmpl, score) = GestureRecognizer.Recognize(input, null);

            Assert.IsNull(tmpl);
            Assert.AreEqual(0f, score);
        }

        [Test]
        public void Recognize_TemplateWithNullPoints_SkipsTemplate()
        {
            var badTemplate = ScriptableObject.CreateInstance<GestureTemplate>();
            badTemplate.templateName = "Bad";
            badTemplate.points = null;

            var goodTemplate = CreateTemplate("Line", new Vector2(0, 0.5f), new Vector2(1, 0.5f));

            var input = new List<Vector2> { new(0f, 0.5f), new(1f, 0.5f) };
            var (tmpl, score) = GestureRecognizer.Recognize(input, new[] { badTemplate, goodTemplate });

            Assert.IsNotNull(tmpl);
            Assert.AreEqual("Line", tmpl.templateName);
            Assert.Greater(score, 0f);

            Object.DestroyImmediate(badTemplate);
            Object.DestroyImmediate(goodTemplate);
        }

        [Test]
        public void Recognize_IdenticalInput_ReturnsHighScore()
        {
            var template = CreateTemplate("Line",
                new Vector2(0f, 0.5f), new Vector2(0.25f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0.75f, 0.5f), new Vector2(1f, 0.5f));

            var input = new List<Vector2>
            {
                new(0f, 0.5f), new(0.25f, 0.5f), new(0.5f, 0.5f), new(0.75f, 0.5f), new(1f, 0.5f)
            };

            var (tmpl, score) = GestureRecognizer.Recognize(input, new[] { template });

            Assert.IsNotNull(tmpl);
            Assert.Greater(score, 0.8f, "Identical input should yield high score");

            Object.DestroyImmediate(template);
        }

        [Test]
        public void Recognize_VeryDifferentInput_ReturnsLowScore()
        {
            // Horizontal line template
            var template = CreateTemplate("Line",
                new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f));

            // Zigzag input — very different from straight line
            var input = new List<Vector2>
            {
                new(0f, 0f), new(0.2f, 1f), new(0.4f, 0f),
                new(0.6f, 1f), new(0.8f, 0f), new(1f, 1f)
            };

            var (tmpl, score) = GestureRecognizer.Recognize(input, new[] { template });

            Assert.IsNotNull(tmpl);
            Assert.Less(score, 0.9f, "Different input should score below near-perfect match");

            Object.DestroyImmediate(template);
        }

        [Test]
        public void Recognize_MultipleTemplates_ReturnsBestMatch()
        {
            var line = CreateTemplate("Line",
                new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f));

            var circle = CreateTemplate("Circle",
                new Vector2(0.5f, 0f), new Vector2(1f, 0.5f), new Vector2(0.5f, 1f),
                new Vector2(0f, 0.5f), new Vector2(0.5f, 0f));

            // Input is a horizontal line
            var input = new List<Vector2>
            {
                new(0f, 0.5f), new(0.25f, 0.5f), new(0.5f, 0.5f), new(0.75f, 0.5f), new(1f, 0.5f)
            };

            var (tmpl, score) = GestureRecognizer.Recognize(input, new[] { circle, line });

            Assert.IsNotNull(tmpl);
            Assert.AreEqual("Line", tmpl.templateName, "Line template should match a line input");

            Object.DestroyImmediate(line);
            Object.DestroyImmediate(circle);
        }

        private static GestureTemplate CreateTemplate(string name, params Vector2[] points)
        {
            var t = ScriptableObject.CreateInstance<GestureTemplate>();
            t.templateName = name;
            t.points = new List<Vector2>(points);
            return t;
        }
    }
}

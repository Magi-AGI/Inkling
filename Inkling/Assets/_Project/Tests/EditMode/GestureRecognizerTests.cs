using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Magi.Inkling.Systems.Gestures;

namespace Magi.Inkling.Tests.EditMode
{
    public class GestureRecognizerTests
    {
        [Test]
        public void Recognize_ReturnsBestTemplate()
        {
            var circle = ScriptableObject.CreateInstance<GestureTemplate>();
            circle.templateName = "Circle";
            circle.points = new List<Vector2>
            {
                new Vector2(0.5f,0f), new Vector2(1f,0.5f), new Vector2(0.5f,1f), new Vector2(0f,0.5f), new Vector2(0.5f,0f)
            };

            var line = ScriptableObject.CreateInstance<GestureTemplate>();
            line.templateName = "Line";
            line.points = new List<Vector2> { new Vector2(0f,0.5f), new Vector2(1f,0.5f) };

            var input = new List<Vector2> { new Vector2(0.5f,0f), new Vector2(1f,0.5f), new Vector2(0.5f,1f), new Vector2(0f,0.5f), new Vector2(0.5f,0f) };

            var (tmpl, score) = GestureRecognizer.Recognize(input, new[] { line, circle });
            Assert.IsNotNull(tmpl);
            Assert.AreEqual("Circle", tmpl.templateName);
            Assert.Greater(score, 0.3f);
        }
    }
}
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Magi.Inkling.Services.Diagnostics;

namespace Magi.Inkling.Tests.EditMode
{
    public class LogSinkTests
    {
        [Test]
        public void AddsAndClears()
        {
            var go = new GameObject("SinkTest");
            var sink = go.AddComponent<LogSink>();
            sink.Add("one");
            sink.Add("two");
            CollectionAssert.AreEqual(new[]{"one","two"}, sink.GetEntries());
            sink.Clear();
            Assert.IsEmpty(sink.GetEntries());
            Object.DestroyImmediate(go);
        }
    }
}
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests.EditMode
{
    public class OperationQueueTests
    {
        private const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

        private SimulationContext ctx;
        private OperationQueue queue;

        [SetUp]
        public void SetUp()
        {
            ctx = new SimulationContext { Resolution = 64 };
            queue = new OperationQueue(ctx);
        }

        private int GetListCount(string fieldName)
        {
            var field = typeof(OperationQueue).GetField(fieldName, NonPublic);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on OperationQueue");
            var list = field.GetValue(queue);
            return (int)list.GetType().GetProperty("Count").GetValue(list);
        }

        private object GetListItem(string fieldName, int index)
        {
            var field = typeof(OperationQueue).GetField(fieldName, NonPublic);
            var list = field.GetValue(queue);
            var indexer = list.GetType().GetProperty("Item");
            return indexer.GetValue(list, new object[] { index });
        }

        [Test]
        public void EnqueueForceInjection_IncrementsPendingCount()
        {
            queue.EnqueueForceInjection(new Vector2(0.5f, 0.5f), Vector2.right);

            Assert.AreEqual(1, GetListCount("pendingForceInjections"));
        }

        [Test]
        public void EnqueueDensityInjection_IncrementsPendingCount()
        {
            queue.EnqueueDensityInjection(new Vector2(0.3f, 0.7f), Color.red, 0);

            Assert.AreEqual(1, GetListCount("pendingDensityInjections"));
        }

        [Test]
        public void EnqueueDensityStamp_IncrementsPendingCount()
        {
            var tex = new Texture2D(4, 4);
            queue.EnqueueDensityStamp(new Vector2(0.5f, 0.5f), tex, 1f, false, Color.white);

            Assert.AreEqual(1, GetListCount("pendingDensityStamps"));

            Object.DestroyImmediate(tex);
        }

        [Test]
        public void EnqueueClearDensityMask_IncrementsPendingCount()
        {
            var tex = new Texture2D(4, 4);
            queue.EnqueueClearDensityMask(new Vector2(0.5f, 0.5f), tex, 0.2f);

            Assert.AreEqual(1, GetListCount("pendingClearDensityMasks"));

            Object.DestroyImmediate(tex);
        }

        [Test]
        public void MultipleEnqueues_AccumulateCorrectly()
        {
            queue.EnqueueForceInjection(Vector2.zero, Vector2.up);
            queue.EnqueueForceInjection(Vector2.one, Vector2.down);
            queue.EnqueueDensityInjection(Vector2.zero, Color.blue, 1);
            queue.EnqueueDensityInjection(Vector2.one, Color.green, 2);
            queue.EnqueueDensityInjection(Vector2.one * 0.5f, Color.red, 0);

            Assert.AreEqual(2, GetListCount("pendingForceInjections"));
            Assert.AreEqual(3, GetListCount("pendingDensityInjections"));
        }

        [Test]
        public void ProcessPending_WithNoCompute_DoesNotThrow()
        {
            // ctx has no compute shaders assigned — ProcessPending should exit gracefully
            queue.EnqueueForceInjection(Vector2.one * 0.5f, Vector2.right);
            queue.EnqueueDensityInjection(Vector2.one * 0.5f, Color.red, 0);

            Assert.DoesNotThrow(() => queue.ProcessPending());
        }

        [Test]
        public void EnqueuePreservesData()
        {
            var pos = new Vector2(0.3f, 0.7f);
            var force = new Vector2(10f, -5f);

            queue.EnqueueForceInjection(pos, force);

            var item = GetListItem("pendingForceInjections", 0);
            var itemPos = (Vector2)item.GetType().GetField("position").GetValue(item);
            var itemForce = (Vector2)item.GetType().GetField("force").GetValue(item);

            Assert.AreEqual(pos, itemPos);
            Assert.AreEqual(force, itemForce);
        }
    }
}

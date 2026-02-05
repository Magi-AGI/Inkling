using System.Collections.Generic;
using UnityEngine;
using Magi.UnityTools.Diagnostics;
using Magi.UnityTools.Patterns;

namespace Magi.Inkling.Services.Diagnostics
{
    /// <summary>
    /// Simple ring-buffer log sink to collect recent messages for diagnostics and capture metadata.
    /// </summary>
    public class LogSink : MonoBehaviour, ILogSink, IService
    {
        [SerializeField] private int capacity = 32;

        private readonly Queue<string> entries = new();

        public void Add(string message)
        {
            if (entries.Count >= capacity) entries.Dequeue();
            entries.Enqueue(message);
        }

        public IEnumerable<string> GetEntries() => entries;

        public static void AddGlobal(string message)
        {
            var sink = ServiceLocator.Instance?.Resolve<ILogSink>();
            sink?.Add(message);
        }

        public void Clear()
        {
            entries.Clear();
        }
    }
}

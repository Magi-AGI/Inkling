using System.Collections.Generic;
using UnityEngine;

namespace Magi.Inkling.Services.Diagnostics
{
    /// <summary>
    /// Simple ring-buffer log sink to collect recent messages for diagnostics and capture metadata.
    /// </summary>
    public class LogSink : MonoBehaviour
    {
        [SerializeField] private int capacity = 32;

        private readonly Queue<string> entries = new();

        public void Add(string message)
        {
            if (entries.Count >= capacity) entries.Dequeue();
            entries.Enqueue(message);
        }

        public IEnumerable<string> GetEntries() => entries;
    }
}

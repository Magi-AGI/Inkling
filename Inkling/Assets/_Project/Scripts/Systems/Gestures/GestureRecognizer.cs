using System.Collections.Generic;
using UnityEngine;

namespace Magi.Inkling.Systems.Gestures
{
    /// <summary>
    /// Lightweight P$-style recognizer: resample, scale, translate, then sum distance.
    /// Rotation invariance is omitted for simplicity (fits our straight-stroke gestures).
    /// </summary>
    public static class GestureRecognizer
    {
        private const int SampleCount = 64;
        private const float SquareSize = 1f;

        public static (GestureTemplate template, float score) Recognize(IReadOnlyList<Vector2> input, IReadOnlyList<GestureTemplate> templates)
        {
            if (input == null || input.Count < 2 || templates == null || templates.Count == 0)
                return (null, 0f);

            var candidate = Normalize(input);

            float bestDist = float.MaxValue;
            GestureTemplate best = null;

            foreach (var t in templates)
            {
                if (t == null || t.points == null || t.points.Count < 2) continue;

                var norm = Normalize(t.points);
                float d = PathDistance(candidate, norm);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = t;
                }
            }

            // Convert distance to a simple [0,1] score (smaller dist = higher score)
            float score = best == null ? 0f : 1f / (1f + bestDist);
            return (best, score);
        }

        private static List<Vector2> Normalize(IReadOnlyList<Vector2> pts)
        {
            var resampled = Resample(pts, SampleCount);
            var boxed = ScaleToSquare(resampled, SquareSize);
            var translated = TranslateToOrigin(boxed);
            return translated;
        }

        private static List<Vector2> Resample(IReadOnlyList<Vector2> pts, int n)
        {
            float pathLength = 0f;
            for (int i = 1; i < pts.Count; i++)
                pathLength += Vector2.Distance(pts[i - 1], pts[i]);
            float interval = pathLength / (n - 1);

            var resampled = new List<Vector2>(n) { pts[0] };
            float D = 0f;
            Vector2 prev = pts[0];

            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 curr = pts[i];
                float segment = Vector2.Distance(prev, curr);
                if (segment <= Mathf.Epsilon)
                {
                    prev = curr;
                    continue;
                }

                while (D + segment >= interval && resampled.Count < n)
                {
                    float t = (interval - D) / segment;
                    Vector2 np = Vector2.Lerp(prev, curr, t);
                    resampled.Add(np);
                    prev = np;
                    segment = Vector2.Distance(prev, curr);
                    D = 0f;
                }

                D += segment;
                prev = curr;
            }

            // Pad last point if we fell short
            while (resampled.Count < n)
                resampled.Add(pts[pts.Count - 1]);

            return resampled;
        }

        private static List<Vector2> ScaleToSquare(IReadOnlyList<Vector2> pts, float size)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                minX = Mathf.Min(minX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxX = Mathf.Max(maxX, p.x);
                maxY = Mathf.Max(maxY, p.y);
            }
            float width = maxX - minX;
            float height = maxY - minY;
            float scale = (width > height) ? size / width : size / height;
            var scaled = new List<Vector2>(pts.Count);
            foreach (var p in pts)
            {
                scaled.Add(new Vector2(
                    (p.x - minX) * scale,
                    (p.y - minY) * scale));
            }
            return scaled;
        }

        private static List<Vector2> TranslateToOrigin(IReadOnlyList<Vector2> pts)
        {
            Vector2 centroid = Vector2.zero;
            foreach (var p in pts) centroid += p;
            centroid /= pts.Count;
            var translated = new List<Vector2>(pts.Count);
            foreach (var p in pts) translated.Add(p - centroid);
            return translated;
        }

        private static float PathDistance(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
        {
            int count = Mathf.Min(a.Count, b.Count);
            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                sum += Vector2.Distance(a[i], b[i]);
            }
            return sum / count;
        }
    }
}

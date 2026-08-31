using System.Collections.Generic;
using UnityEngine;
using Magi.InkTools.Simulation;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Systems.Brush
{
    /// <summary>
    /// CP8p: persistent, CONTINUOUS directional ink emitters (Lake's right-mouse feature).
    ///
    /// An emitter is a fixed UV position plus a force vector. Every frame each emitter injects density
    /// AND injects force in its stored direction — "as per the left mouse, just continuous". Where the
    /// left-mouse brush emits only while you drag, these keep running once placed, which is what makes a
    /// repeatable Fire-into-Ice test possible by hand instead of only in the scenario runner.
    ///
    /// Deliberately owns NO input handling and NO simulation lookup: it takes an ISimulationWriter and a
    /// list of emitters. That keeps it unit-testable against StubSimulationWriter with no scene, no GPU,
    /// and no SimDriver — the input layer (BrushInputController) drives it.
    /// </summary>
    public class DirectionalEmitterController : MonoBehaviour
    {
        /// <summary>One continuous emitter: where it sits and which way it pushes.</summary>
        [System.Serializable]
        public struct Emitter
        {
            public Vector2 uv;
            public Vector2 force;
            public int inkType;
            public Color color;
        }

        [Header("Emission")]
        // CP8s removed the serialized `emitterInkType` field: emitters now take the ink from the live
        // selection at creation time (SimDriver.CurrentInkType), so a fixed inspector value would be
        // misleading — it would appear to control something it no longer controls.

        [Tooltip("Force applied per emitter per frame, multiplying the stored drag direction. Separate " +
                 "from BrushConfig.forceMultiplier because an emitter's push is continuous — it needs to " +
                 "be tuned against sustained flow, not against a one-off flick.")]
        [SerializeField] private float emitterForceMultiplier = 0.1f;

        [Tooltip("UV radius within which an RMB drag path is considered to CROSS an existing emitter, " +
                 "and therefore removes it instead of creating another. Generous enough to be usable by " +
                 "hand; roughly the visual size of an emitter.")]
        [SerializeField] private float removeRadiusUv = 0.04f;

        [Tooltip("Safety cap so repeated dragging cannot grow the list without bound.")]
        [SerializeField] private int maxEmitters = 32;

        private readonly List<Emitter> emitters = new();
        private ISimulationWriter writer;

        /// <summary>Live emitter list (read-only view for tests//gizmos).</summary>
        public IReadOnlyList<Emitter> Emitters => emitters;
        public int Count => emitters.Count;
        public float RemoveRadiusUv => removeRadiusUv;

        /// <summary>Injected by the input layer (or a test) — no FindObjectOfType here.</summary>
        public void SetWriter(ISimulationWriter simWriter) => writer = simWriter;

        /// <summary>
        /// Applies one RMB drag. Returns true if it CREATED an emitter, false if it REMOVED one (or more).
        ///
        /// Lake's rule: "if the space is empty, create an emitter with impulse in the direction of the
        /// mouse movement ... Where that path would cross an existing emitter, remove that emitter."
        /// So removal takes precedence — a drag that touches anything existing is a delete gesture, and
        /// must NOT also spawn a duplicate on top of what it just removed.
        /// </summary>
        /// <remarks>
        /// CP8s: <paramref name="inkType"/> is the CURRENTLY SELECTED ink, passed in by the input layer
        /// from the same source left-click painting reads. It is deliberately a required parameter rather
        /// than an optional one — a default would let a caller silently fall back to Fire, which is the
        /// exact bug this change exists to remove.
        ///
        /// The ink is SNAPSHOT at creation: the emitter stores it and never consults the live selection
        /// again. Changing the selected ink afterwards must not repaint emitters already placed, or you
        /// could never run a fire emitter and a water emitter at the same time.
        ///
        /// Removal stays ink-agnostic — dragging through an emitter deletes it whatever is selected.
        /// </remarks>
        public bool ApplyDragGesture(Vector2 startUv, Vector2 endUv, int inkType)
        {
            int removed = emitters.RemoveAll(e => SegmentDistance(startUv, endUv, e.uv) <= removeRadiusUv);
            if (removed > 0) return false;          // crossed something => this was a delete, not a create

            if (emitters.Count >= maxEmitters) return false;

            Vector2 dir = endUv - startUv;
            if (dir.sqrMagnitude < 1e-8f) return false;   // a click with no drag has no direction to push

            // Clamp exactly as SimDriver.CurrentInkType does; an out-of-range index would address a
            // particle channel that does not exist.
            // CP8w: upper bound is ColdSourceInkIndex, not Count-1, so a ColdAir emitter is placeable as
            // a CONTINUOUS cooler — sustained cold on water is exactly the freeze experiment.
            int ink = Mathf.Clamp(inkType, 0, SimulationContext.ColdSourceInkIndex);

            emitters.Add(new Emitter
            {
                uv = startUv,                               // emitter sits where the drag BEGAN…
                force = dir.normalized * emitterForceMultiplier,  // …and pushes the way it was drawn
                inkType = ink,
                color = InkKeyColor(ink),
            });
            return true;
        }

        /// <summary>Emits from every active emitter. Call once per frame from the input layer.</summary>
        public void Tick()
        {
            if (writer == null) return;
            for (int i = 0; i < emitters.Count; i++)
            {
                Emitter e = emitters[i];
                writer.InjectDensity(e.uv, e.color, e.inkType);

                // CP8w: a ColdAir emitter cools but does NOT push. It is a measuring instrument — if it
                // stirred the velocity field it would perturb the very freeze behaviour it exists to
                // observe, and you could not tell thermal effects from advection. Normal ink emitters
                // keep their CP8p directional force unchanged.
                if (!SimulationContext.IsColdSource(e.inkType))
                    writer.InjectForce(e.uv, e.force);
            }
        }

        public void Clear() => emitters.Clear();

        /// <summary>
        /// Distance from point p to segment ab, in UV. Segment (not endpoint) distance is what makes
        /// "where that path would cross an existing emitter" work for a fast drag that flies PAST an
        /// emitter without either endpoint landing near it.
        /// </summary>
        public static float SegmentDistance(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-10f) return Vector2.Distance(a, p);   // degenerate: a click, not a drag
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(a + t * ab, p);
        }

        /// <summary>
        /// Key colour per ink. Must stay in lockstep with the equivalent map in BrushInputController —
        /// an emitter and a left-click stroke of the same ink should look identical. CP8p only mapped the
        /// three inks the Fire-vs-Ice test used and fell back to red; CP8s covers all of them, because
        /// falling back to red would now paint e.g. Glitter with Fire's colour.
        /// </summary>
        private static Color InkKeyColor(int inkTypeIndex)
        {
            // CP8w: ColdAir before the clamp — otherwise it would render as Ice's cyan.
            if (SimulationContext.IsColdSource(inkTypeIndex))
                return new Color(0.8f, 1f, 0.95f, 1f);   // pale mint-frost, matches BrushInputController

            switch (Mathf.Clamp(inkTypeIndex, 0, (int)InkTypeId.Count - 1))
            {
                case 0: return new Color(1f, 0f, 0f, 1f);          // Fire
                case 1: return new Color(0f, 0f, 1f, 1f);          // Water
                case 2: return new Color(0f, 1f, 0f, 1f);          // PlantSeeded
                case 3: return new Color(0f, 0.5f, 0f, 1f);        // PlantGrown
                case 4: return new Color(0.49f, 0.49f, 0.49f, 1f); // Steam
                case 5: return new Color(1f, 0.5f, 1f, 1f);        // Glitter
                case 6: return new Color(0.1f, 0.1f, 0.1f, 1f);    // BlackBody
                case 7: return new Color(1f, 1f, 0f, 1f);          // ElectricitySeeded
                case 8: return new Color(0.5f, 0.5f, 0f, 1f);      // ElectricityGrown
                case 9: return new Color(0f, 1f, 1f, 1f);          // Ice
                case 10: return new Color(0.6f, 0.6f, 0.65f, 1f);  // Metal (placeholder silver; real color in M1)
                default: return new Color(1f, 0f, 0f, 1f);
            }
        }

#if UNITY_EDITOR
        // Editor affordance so placed emitters are visible while playtesting (Lake asked for some way to
        // see them). Drawn in UV space mapped across the XY unit square in front of the object.
        private void OnDrawGizmos()
        {
            foreach (var e in emitters)
            {
                // CP8s: draw each emitter in its OWN ink colour, so a scene with several emitters of
                // different inks is readable at a glance rather than uniformly orange.
                Gizmos.color = new Color(e.color.r, e.color.g, e.color.b, 0.9f);
                Vector3 p = new Vector3(e.uv.x, e.uv.y, 0f);
                Gizmos.DrawWireSphere(p, removeRadiusUv);
                Gizmos.DrawLine(p, p + new Vector3(e.force.x, e.force.y, 0f).normalized * 0.05f);
            }
        }
#endif
    }
}

using UnityEngine;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Shared utility for mapping screen/world coordinates to simulation UV space
    /// based on a target renderer's orientation and bounds.
    /// </summary>
    public static class SimulationUvUtility
    {
        /// <summary>
        /// Computes UV coordinate from screen position using a renderer as the projection surface.
        /// </summary>
        public static Vector2 ComputeUv(Vector2 screenPos, Renderer target, Camera cam, Vector2 lastUv)
        {
            if (cam == null)
            {
                return new Vector2(
                    Mathf.Clamp01(screenPos.x / Mathf.Max(1f, Screen.width)),
                    Mathf.Clamp01(screenPos.y / Mathf.Max(1f, Screen.height)));
            }

            Vector2 viewportUv = GetViewportUv(cam, screenPos);
            _ = lastUv; // preserved for call-site compatibility; viewport fallback is preferred.

            if (target == null)
            {
                return viewportUv;
            }

            if (!TryRaycastAgainstRenderer(screenPos, target, cam, out Vector3 hitWorld, out int normalAxis, out Plane mappingPlane))
            {
                return viewportUv;
            }

            if (TryMapUsingViewportPlane(cam, target.transform, mappingPlane, hitWorld, normalAxis, out Vector2 uv))
            {
                return uv;
            }

            return viewportUv;
        }

        private static bool TryRaycastAgainstRenderer(
            Vector2 screenPos,
            Renderer target,
            Camera cam,
            out Vector3 hitPoint,
            out int normalAxis,
            out Plane mappingPlane)
        {
            hitPoint = Vector3.zero;
            normalAxis = 1;
            mappingPlane = new Plane(Vector3.up, Vector3.zero);

            Ray ray = cam.ScreenPointToRay(screenPos);
            Transform t = target.transform;
            Vector3 center = target.bounds.center;

            // Pick the local axis most aligned with camera forward.
            Vector3[] normals =
            {
                t.right.normalized,
                t.up.normalized,
                t.forward.normalized
            };

            float bestAlignment = -1f;
            for (int i = 0; i < normals.Length; i++)
            {
                float alignment = Mathf.Abs(Vector3.Dot(cam.transform.forward, normals[i]));
                if (alignment > bestAlignment)
                {
                    bestAlignment = alignment;
                    normalAxis = i;
                }
            }

            if (bestAlignment < 0.05f)
            {
                return false;
            }

            Vector3 normal = normals[normalAxis];
            float rayDot = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(rayDot) < 0.01f)
            {
                return false;
            }

            // Ensure plane faces toward the ray for stable positive distances.
            if (rayDot > 0f)
            {
                normal = -normal;
            }

            mappingPlane = new Plane(normal, center);
            if (mappingPlane.Raycast(ray, out float dist))
            {
                if (dist < 0f) return false;
                hitPoint = ray.GetPoint(dist);
                return true;
            }

            return false;
        }

        private static bool TryMapUsingViewportPlane(
            Camera cam,
            Transform targetTransform,
            Plane plane,
            Vector3 hitWorld,
            int normalAxis,
            out Vector2 uv)
        {
            uv = Vector2.zero;

            // Raycast the camera viewport corners onto the same plane to derive
            // the visible mapping range. This avoids compression when display mesh
            // scale/view framing doesn't match full-screen UI presentation.
            if (!TryRaycastViewportPoint(cam, plane, 0f, 0f, out Vector3 blWorld)) return false;
            if (!TryRaycastViewportPoint(cam, plane, 1f, 0f, out Vector3 brWorld)) return false;
            if (!TryRaycastViewportPoint(cam, plane, 0f, 1f, out Vector3 tlWorld)) return false;
            if (!TryRaycastViewportPoint(cam, plane, 1f, 1f, out Vector3 trWorld)) return false;

            Vector3 localHit = targetTransform.InverseTransformPoint(hitWorld);
            Vector3 bl = targetTransform.InverseTransformPoint(blWorld);
            Vector3 br = targetTransform.InverseTransformPoint(brWorld);
            Vector3 tl = targetTransform.InverseTransformPoint(tlWorld);
            Vector3 tr = targetTransform.InverseTransformPoint(trWorld);

            GetPlaneAxesForNormal(normalAxis, out int axisU, out int axisV);

            float blU = GetAxisComponent(bl, axisU);
            float brU = GetAxisComponent(br, axisU);
            float tlU = GetAxisComponent(tl, axisU);
            float trU = GetAxisComponent(tr, axisU);
            float blV = GetAxisComponent(bl, axisV);
            float brV = GetAxisComponent(br, axisV);
            float tlV = GetAxisComponent(tl, axisV);
            float trV = GetAxisComponent(tr, axisV);

            float minU = Mathf.Min(blU, brU, tlU, trU);
            float maxU = Mathf.Max(blU, brU, tlU, trU);
            float minV = Mathf.Min(blV, brV, tlV, trV);
            float maxV = Mathf.Max(blV, brV, tlV, trV);

            if (Mathf.Abs(maxU - minU) < 0.0001f || Mathf.Abs(maxV - minV) < 0.0001f)
            {
                return false;
            }

            float u = Mathf.InverseLerp(minU, maxU, GetAxisComponent(localHit, axisU));
            float v = Mathf.InverseLerp(minV, maxV, GetAxisComponent(localHit, axisV));

            // Keep orientation consistent with screen-space direction.
            if (brU < blU) u = 1f - u; // screen-right should increase U
            if (tlV < blV) v = 1f - v; // screen-up should increase V

            uv = new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
            return true;
        }

        private static bool TryRaycastViewportPoint(Camera cam, Plane plane, float x, float y, out Vector3 hitWorld)
        {
            hitWorld = Vector3.zero;
            Ray ray = cam.ViewportPointToRay(new Vector3(x, y, 0f));
            if (!plane.Raycast(ray, out float dist)) return false;
            if (dist < 0f) return false;
            hitWorld = ray.GetPoint(dist);
            return true;
        }

        private static void GetPlaneAxesForNormal(int normalAxis, out int axisU, out int axisV)
        {
            switch (normalAxis)
            {
                case 0: // normal is local X -> YZ plane
                    axisU = 2;
                    axisV = 1;
                    break;
                case 1: // normal is local Y -> XZ plane (Unity Plane default)
                    axisU = 0;
                    axisV = 2;
                    break;
                default: // normal is local Z -> XY plane
                    axisU = 0;
                    axisV = 1;
                    break;
            }
        }

        private static float GetAxisComponent(Vector3 v, int axis)
        {
            return axis == 0 ? v.x : (axis == 1 ? v.y : v.z);
        }

        private static Vector2 GetViewportUv(Camera cam, Vector2 screenPos)
        {
            Vector3 vp = cam.ScreenToViewportPoint(new Vector3(screenPos.x, screenPos.y, 0f));
            return new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));
        }
    }
}

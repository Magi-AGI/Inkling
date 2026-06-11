using System.Runtime.InteropServices;
using UnityEngine;

namespace Magi.Inkling.Systems.Obstacles
{
    /// <summary>
    /// GPU struct for circular obstacles.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CircleObstacleData
    {
        public Vector2 center;  // UV position [0,1]
        public float radius;    // UV radius
        public uint flags;      // bit 0: active, bit 1: static

        public const int Stride = 16; // 2 floats + 1 float + 1 uint = 16 bytes

        public bool IsActive => (flags & 1) != 0;
        public bool IsStatic => (flags & 2) != 0;

        public static CircleObstacleData Create(Vector2 center, float radius, bool isStatic = false)
        {
            return new CircleObstacleData
            {
                center = center,
                radius = radius,
                flags = 1u | (isStatic ? 2u : 0u) // Active + static flag
            };
        }

        public static CircleObstacleData Inactive => new CircleObstacleData { flags = 0 };
    }

    /// <summary>
    /// GPU struct for triangle obstacles (used by edge and polygon obstacles).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TriangleObstacleData
    {
        public Vector2 v0;  // First vertex UV [0,1]
        public Vector2 v1;  // Second vertex UV [0,1]
        public Vector2 v2;  // Third vertex UV [0,1]
        public uint flags;  // bit 0: active, bit 1: static
        public uint padding; // Alignment padding

        public const int Stride = 32; // 6 floats + 2 uints = 32 bytes

        public bool IsActive => (flags & 1) != 0;
        public bool IsStatic => (flags & 2) != 0;

        public static TriangleObstacleData Create(Vector2 v0, Vector2 v1, Vector2 v2, bool isStatic = false)
        {
            return new TriangleObstacleData
            {
                v0 = v0,
                v1 = v1,
                v2 = v2,
                flags = 1u | (isStatic ? 2u : 0u),
                padding = 0
            };
        }

        public static TriangleObstacleData Inactive => new TriangleObstacleData { flags = 0 };
    }
}

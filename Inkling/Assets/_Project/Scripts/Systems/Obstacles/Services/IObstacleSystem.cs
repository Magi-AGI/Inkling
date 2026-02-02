using UnityEngine;

namespace Magi.Inkling.Systems.Obstacles
{
    /// <summary>
    /// Service interface for GPU obstacle management.
    /// Obstacles block/redirect ink flow in the simulation.
    /// </summary>
    public interface IObstacleSystem
    {
        /// <summary>Maximum number of circle obstacles the system can handle.</summary>
        int MaxCircleObstacles { get; }

        /// <summary>Maximum number of triangle obstacles the system can handle.</summary>
        int MaxTriangleObstacles { get; }

        /// <summary>Current count of active circle obstacles.</summary>
        int ActiveCircleCount { get; }

        /// <summary>Current count of active triangle obstacles.</summary>
        int ActiveTriangleCount { get; }

        /// <summary>Whether the obstacle system is initialized and ready.</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Adds a circular obstacle at the given UV position.
        /// </summary>
        /// <param name="center">UV position [0,1]</param>
        /// <param name="radius">Radius in UV space</param>
        /// <param name="isStatic">Static obstacles persist until removed; dynamic ones clear each frame</param>
        /// <returns>Obstacle index, or -1 if buffer full</returns>
        int AddCircleObstacle(Vector2 center, float radius, bool isStatic = false);

        /// <summary>
        /// Adds a triangular obstacle defined by three UV vertices.
        /// </summary>
        /// <param name="v0">First vertex UV [0,1]</param>
        /// <param name="v1">Second vertex UV [0,1]</param>
        /// <param name="v2">Third vertex UV [0,1]</param>
        /// <param name="isStatic">Static obstacles persist until removed; dynamic ones clear each frame</param>
        /// <returns>Obstacle index, or -1 if buffer full</returns>
        int AddTriangleObstacle(Vector2 v0, Vector2 v1, Vector2 v2, bool isStatic = false);

        /// <summary>
        /// Updates an existing circle obstacle's position and radius.
        /// </summary>
        /// <param name="index">Obstacle index from AddCircleObstacle</param>
        /// <param name="center">New UV position</param>
        /// <param name="radius">New radius</param>
        void UpdateCircleObstacle(int index, Vector2 center, float radius);

        /// <summary>
        /// Updates an existing triangle obstacle's vertices.
        /// </summary>
        /// <param name="index">Obstacle index from AddTriangleObstacle</param>
        /// <param name="v0">New first vertex</param>
        /// <param name="v1">New second vertex</param>
        /// <param name="v2">New third vertex</param>
        void UpdateTriangleObstacle(int index, Vector2 v0, Vector2 v1, Vector2 v2);

        /// <summary>
        /// Removes a circle obstacle by index.
        /// </summary>
        void RemoveCircleObstacle(int index);

        /// <summary>
        /// Removes a triangle obstacle by index.
        /// </summary>
        void RemoveTriangleObstacle(int index);

        /// <summary>
        /// Clears all dynamic (non-static) obstacles.
        /// Called automatically each frame before Update.
        /// </summary>
        void ClearDynamicObstacles();

        /// <summary>
        /// Clears all obstacles including static ones.
        /// </summary>
        void ClearAllObstacles();

        /// <summary>
        /// Gets the GPU compute buffer for circle obstacles (for use by compute shaders).
        /// </summary>
        ComputeBuffer GetCircleBuffer();

        /// <summary>
        /// Gets the GPU compute buffer for triangle obstacles (for use by compute shaders).
        /// </summary>
        ComputeBuffer GetTriangleBuffer();
    }
}

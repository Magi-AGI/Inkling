namespace Magi.Inkling.Systems.Creatures
{
    /// <summary>
    /// Interface for controlling creature animation state.
    /// </summary>
    public interface ICreatureAnimator
    {
        /// <summary>Current animation state.</summary>
        CreatureAnimationState CurrentState { get; }

        /// <summary>Current frame index within the animation.</summary>
        int CurrentFrame { get; }

        /// <summary>Whether the creature is currently animating.</summary>
        bool IsAnimating { get; }

        /// <summary>
        /// Transition to a new animation state.
        /// </summary>
        /// <param name="state">Target state.</param>
        /// <param name="loop">Whether to loop the animation.</param>
        void SetState(CreatureAnimationState state, bool loop = true);

        /// <summary>
        /// Play a one-shot animation, then return to previous state.
        /// </summary>
        /// <param name="state">State to play once.</param>
        void PlayOneShot(CreatureAnimationState state);

        /// <summary>
        /// Pause animation at current frame.
        /// </summary>
        void Pause();

        /// <summary>
        /// Resume animation from current frame.
        /// </summary>
        void Resume();
    }
}

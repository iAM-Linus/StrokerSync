namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Interface for motion sources that generate position/velocity data
    /// </summary>
    public interface IMotionSource
    {
        /// <summary>
        /// Called when the motion source is initialized
        /// </summary>
        void OnInit(StrokerSync plugin);

        /// <summary>
        /// Called to initialize JSON storables (save/load state)
        /// </summary>
        void OnInitStorables(StrokerSync plugin);

        /// <summary>
        /// Called every frame to generate motion data
        /// Returns true if new position data is available
        /// </summary>
        /// <param name="outPos">Output position (0.0-1.0)</param>
        /// <param name="outVelocity">Output velocity (0.0-1.0)</param>
        /// <returns>True if new motion data should be sent</returns>
        bool OnUpdate(ref float outPos, ref float outVelocity);

        /// <summary>
        /// Called when simulator position updates (for visual feedback)
        /// </summary>
        void OnSimulatorUpdate(float prevPos, float newPos, float deltaTime);

        /// <summary>
        /// Called when the motion source is destroyed
        /// </summary>
        void OnDestroy(StrokerSync plugin);

        /// <summary>
        /// Called when a new scene is loaded (session plugin persistence).
        /// Must reset all cached atom/component references from the previous scene.
        /// </summary>
        void OnSceneLoaded(StrokerSync plugin);
    }
}
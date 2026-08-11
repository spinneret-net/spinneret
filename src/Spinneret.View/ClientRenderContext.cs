namespace Spinneret.View
{
    /// <inheritdoc/>
    public sealed class ClientRenderContext : IRenderContext
    {
        /// <inheritdoc/>
        public bool IsClient => true;

        /// <inheritdoc/>
        public bool IsServer => false;

        /// <inheritdoc/>
        public bool IsPrerendering => false;
    }
}

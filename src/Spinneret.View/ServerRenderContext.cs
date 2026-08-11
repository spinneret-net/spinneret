using Microsoft.AspNetCore.Http;

namespace Spinneret.View
{
    /// <inheritdoc/>
    public sealed class ServerRenderContext(IHttpContextAccessor contextAccessor) : IRenderContext
    {
        /// <inheritdoc/>
        public bool IsClient => false;

        /// <inheritdoc/>
        public bool IsServer => true;

        /// <inheritdoc/>
        public bool IsPrerendering => !contextAccessor.HttpContext?.Response.HasStarted ?? false;
    }
}

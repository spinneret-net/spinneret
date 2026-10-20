namespace Spinneret.View
{
    /// <summary>Where and how the component is rendering.</summary>
    public interface IRenderContext
    {
        /// <summary>Rendering in the client (WebAssembly).</summary>
        public bool IsClient { get; }

        /// <summary>Rendering on the server (Blazor Server).</summary>
        public bool IsServer { get; }

        /// <summary>Prerendering on the server, before the response has started.</summary>
        public bool IsPrerendering { get; }
    }
}
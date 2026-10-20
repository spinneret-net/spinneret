#pragma warning disable BL0006 // only way to drive lifecycles headlessly

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spinneret.View.Tests;

/// <summary>Headless renderer driving real Blazor component lifecycles. Non-interactive with a render mode simulates a prerender; without one, a static SSR page.</summary>
public sealed class TestRenderer(IServiceProvider services, bool interactive = true, IComponentRenderMode? renderMode = null)
    : Renderer(services, NullLoggerFactory.Instance)
{
    private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
    private readonly RendererInfo _rendererInfo = new(interactive ? "Test" : "Static", interactive);

    protected override RendererInfo RendererInfo => _rendererInfo;

    protected override IComponentRenderMode? GetComponentRenderMode(IComponent component) => renderMode;
    private readonly List<Exception> _handledExceptions = [];
    private int _renderBatchCount;

    public override Dispatcher Dispatcher => _dispatcher;

    /// <summary>Exceptions that escaped a component's lifecycle and reached the renderer.</summary>
    public IReadOnlyList<Exception> HandledExceptions
    {
        get { lock (_handledExceptions) return _handledExceptions.ToArray(); }
    }

    /// <summary>Number of render batches produced so far.</summary>
    public int RenderBatchCount => Volatile.Read(ref _renderBatchCount);

    protected override void HandleException(Exception exception)
    {
        lock (_handledExceptions) _handledExceptions.Add(exception);
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        Interlocked.Increment(ref _renderBatchCount);
        return Task.CompletedTask;
    }

    /// <summary>Instantiates via the component factory, so [Inject] properties are set.</summary>
    public TComponent CreateComponent<TComponent>() where TComponent : IComponent =>
        (TComponent)InstantiateComponent(typeof(TComponent));

    public Task<int> AttachAndRenderAsync(IComponent component, ParameterView parameters) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var componentId = AssignRootComponentId(component);
            await RenderRootComponentAsync(componentId, parameters);
            return componentId;
        });

    public Task ReRenderRootAsync(int componentId, ParameterView parameters) =>
        Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, parameters));
}

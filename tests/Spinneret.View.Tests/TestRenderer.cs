#pragma warning disable BL0006 // Renderer is the only way to drive component lifecycles headlessly

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spinneret.View.Tests;

/// <summary>
/// Minimal headless renderer that drives real Blazor component lifecycles
/// (parameter setting, OnInitializedAsync, OnParametersSetAsync, StateHasChanged)
/// without producing any UI output.
/// </summary>
public sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
{
    private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
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

    /// <summary>Instantiates a component through the component factory, performing [Inject] property injection.</summary>
    public TComponent CreateComponent<TComponent>() where TComponent : IComponent =>
        (TComponent)InstantiateComponent(typeof(TComponent));

    /// <summary>Attaches the component as a root component and performs its first render with the given parameters.</summary>
    public Task<int> AttachAndRenderAsync(IComponent component, ParameterView parameters) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var componentId = AssignRootComponentId(component);
            await RenderRootComponentAsync(componentId, parameters);
            return componentId;
        });

    /// <summary>Re-renders an already attached root component with new parameters.</summary>
    public Task ReRenderRootAsync(int componentId, ParameterView parameters) =>
        Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, parameters));
}

using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spinneret.ViewModel;
using IComponent = Microsoft.AspNetCore.Components.IComponent;

namespace Spinneret.View;

public interface IView
{
    ViewState State { get; }
}

public interface IView<out T> : IView, IComponent where T : class, IViewModel
{
    public T ViewModel { get; }
}

public abstract class ViewBase<T> : ComponentBase, IView<T>, IAsyncDisposable where T : class, IViewModel
{
    [Inject] private IServiceProvider ServiceProvider { get; set; } = null!;
    [Inject] private ILogger<ViewBase<T>> Logger { get; set; } = null!;
    [Inject] private IRenderContext RenderContext { get; set; } = null!;
    [Inject] private IViewRefreshCoordinator RefreshCoordinator { get; set; } = null!;
    public ViewState State { get; private set; } = ViewState.Uninitialized;

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private IDisposable? _refreshSubscription;
    private bool _isRefreshing;

    private ViewModelWrapper? _effectiveViewModel;
    private ViewModelWrapper? _specifiedViewModel;

    /// <summary>
    /// Whether this view re-resolves and re-initializes its view model when a global refresh
    /// is broadcast. Views that own transient UI state and refresh themselves through another
    /// channel (e.g. the navigation menu) can opt out by overriding this to <c>false</c>.
    /// </summary>
    protected virtual bool ParticipatesInRefresh => true;
    private ViewModelWrapper SpecifiedViewModel
    {
        get
        {
            _specifiedViewModel ??= new ViewModelWrapper
            {
                ViewModel = ServiceProvider.GetRequiredService<T>(),
                OwnedByView = true
            };

            return _specifiedViewModel;
        }
    }

#pragma warning disable BL0007
    [Parameter]
    public T ViewModel
    {
        get => SpecifiedViewModel.ViewModel;
        set
        {
            if (_specifiedViewModel?.ViewModel != value)
            {
                _specifiedViewModel = new ViewModelWrapper
                {
                    ViewModel = value,
                    OwnedByView = false
                };
            }
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        Action action = State switch
        {
            ViewState.Uninitialized => () => base.BuildRenderTree(builder),
            ViewState.PreInitialized => () => base.BuildRenderTree(builder),
            ViewState.Initialized => () => base.BuildRenderTree(builder),
            ViewState.InitializationFailed => () => { },
            ViewState.Disposed => () => { },
        };

        action();
    }

    protected override async Task OnInitializedAsync()
    {
        if (RenderContext.IsPrerendering)
        {
            State = ViewState.PreInitialized;
            return;
        }
        
        await SafeExecute(() => InitializeViewModel(SpecifiedViewModel), "Initialize updated view model");

        State = ViewState.Initialized;

        // Only views that resolve their own view model from DI can refresh by re-resolving it.
        // View-model-first views (VM supplied by a parent) are re-created when that parent
        // rebuilds, so they never subscribe.
        if (ParticipatesInRefresh && _effectiveViewModel is { OwnedByView: true })
        {
            _refreshSubscription = RefreshCoordinator.Subscribe(() => InvokeAsync(RefreshAsync));
        }
    }

    private async Task RefreshAsync()
    {
        // OwnedByView is re-checked here because a parent can hand this view a view model after
        // it subscribed, transferring ownership away; the parent would then re-create it.
        // IsCancellationRequested guards the residual race where this view is being torn down
        // (e.g. by a redirect) at the same moment a refresh is broadcast: starting a load here
        // would only issue a request the imminent disposal immediately cancels.
        if (State != ViewState.Initialized || _isRefreshing || _cancellationTokenSource.IsCancellationRequested || _effectiveViewModel is not { OwnedByView: true })
        {
            return;
        }

        var current = _effectiveViewModel;

        _isRefreshing = true;
        try
        {
            await DisposeViewModel(current);
            _specifiedViewModel = null;
            await SafeExecute(() => InitializeViewModel(SpecifiedViewModel), "Refresh view model");
            StateHasChanged();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State != ViewState.Initialized)
        {
            return;
        }
        
        var specifiedViewModel = SpecifiedViewModel;
        var effectiveViewModel = _effectiveViewModel;
        var viewModelHasChanged = effectiveViewModel?.ViewModel != specifiedViewModel.ViewModel;
        if (viewModelHasChanged)
        {
            if (effectiveViewModel != null)
            {
                if (effectiveViewModel.OwnedByView)
                {
                    await DisposeViewModel(effectiveViewModel);
                }
                else
                {
                    DetachViewModel(effectiveViewModel);
                }
            }
            
            await SafeExecute(() => InitializeViewModel(specifiedViewModel), "Initialize updated view model");
        }
    }

    private async ValueTask InitializeViewModel(ViewModelWrapper viewModel)
    {
        _effectiveViewModel = viewModel;
        
        if (viewModel.ViewModel is ViewModelBase vmBaseClass)
        {
            var exceptionService = ServiceProvider.GetService<IViewModelExceptionService>();
            if (exceptionService != null)
            {
                vmBaseClass.ExceptionService = exceptionService;
            }
        }
        
        try
        {
            await viewModel.InitializeAsync(_cancellationTokenSource.Token);
        }
        catch
        {
            State = ViewState.InitializationFailed;
            throw;
        }

        viewModel.ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var viewModel = _effectiveViewModel;
        if (viewModel == null || viewModel.ViewModel != sender) return;
        
        var propertyName = e.PropertyName ?? "*";

        if (viewModel.AddPropertyChangeAndTryToAcquireUpdateLock(propertyName))
        {
            _ = SafeExecute(() => viewModel.EnqueueUpdate(
                    () => InvokeAsync(StateHasChanged), 
                    _cancellationTokenSource.Token), 
                "Update ViewModel");    
        }
    }

    private async Task SafeExecute(Func<ValueTask> action, string context)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            Logger.LogWarning(ex, "Cancellation during {Context}", context);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error during {Context}", context);
        }
    }
    
    private static bool IsCancellation(Exception ex)
    {
        return ex switch
        {
            OperationCanceledException or TaskCanceledException or ObjectDisposedException => true,
            AggregateException ae when ae.InnerExceptions.All(IsCancellation) => true,
            HttpRequestException { InnerException: TaskCanceledException } => true,
            _ when ex.Message.Contains("Operation cancelled by user", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (State == ViewState.Disposed)
        {
            return;
        }

        _refreshSubscription?.Dispose();
        _refreshSubscription = null;

        await _cancellationTokenSource.CancelAsync();

        if (_specifiedViewModel is { OwnedByView: true } && _specifiedViewModel.ViewModel != _effectiveViewModel?.ViewModel)
        {
            await DisposeViewModel(_specifiedViewModel);
        }
        
        if (_effectiveViewModel != null)
        {
            if (_effectiveViewModel.OwnedByView)
            {
                await DisposeViewModel(_effectiveViewModel);
            }
            else
            {
                DetachViewModel(_effectiveViewModel);
            }
        }
        
        State = ViewState.Disposed;
        GC.SuppressFinalize(this);
    }

    private void DetachViewModel(ViewModelWrapper viewModel)
    {
        viewModel.ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private async Task DisposeViewModel(ViewModelWrapper viewModel)
    {
        DetachViewModel(viewModel);
        
        if (viewModel.ViewModel is IDisposable disposableViewModel)
        {
            disposableViewModel.Dispose();
        }

        if (viewModel.ViewModel is IAsyncDisposable asyncDisposableViewModel)
        {
            await asyncDisposableViewModel.DisposeAsync();
        }
    }
    
    private class ViewModelWrapper
    {
        public required T ViewModel { get; init; }
        public required bool OwnedByView { get; init; }
        private readonly HashSet<string> _changedProperties = [];
        private bool _updateLockAcquired;

        public bool AddPropertyChangeAndTryToAcquireUpdateLock(string propertyName)
        {
            lock (_changedProperties)
            {
                _changedProperties.Add(propertyName);

                if (_updateLockAcquired)
                    return false;

                _updateLockAcquired = true;
                
                return true;
            }
        }

        private HashSet<string> GetChangedPropertiesAndReleaseUpdateLock()
        {
            HashSet<string> properties;
            lock (_changedProperties)
            {
                properties = new HashSet<string>(_changedProperties);
                _changedProperties.Clear();
                _updateLockAcquired = false;
            }

            return properties;
        }
        
        public async ValueTask EnqueueUpdate(Func<Task> onStateUpdated, CancellationToken cancellationToken)
        {
            // Yield to allow the current event handler to finish
            await Task.Yield();

            var properties = GetChangedPropertiesAndReleaseUpdateLock();

            if (!cancellationToken.IsCancellationRequested)
            {
                await ViewModel.UpdateAsync(properties);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await onStateUpdated();
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await ViewModel.InitializeAsync(cancellationToken);
        }
    }
}

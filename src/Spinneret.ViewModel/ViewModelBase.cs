using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Spinneret.ViewModel;

public abstract class ViewModelBase : BindableBase, IViewModel, IValidationStateProvider
{
    private bool _initializationPending = true;
    private CancellationToken? _cancellationToken;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    [field: AllowNull, MaybeNull]
    public IValidationState ValidationState
    {
        get
        {
            if (field != null) 
                return field;
            
            field = new ValidationState();
            ValidationState.PropertyChanged += ValidationState_PropertyChanged;
            return field;
        }
    }
    
    /// <summary>
    /// Creates the backing collection for a <see cref="RowCollection{TRow}"/> property. Declare it lazily,
    /// so it can capture this view model — a field initializer cannot:
    /// <code>
    /// [field: AllowNull, MaybeNull]
    /// public RowCollection&lt;MyRow&gt; Rows => field ??= CreateRowCollection&lt;MyRow&gt;();
    /// </code>
    /// </summary>
    protected RowCollection<TRow> CreateRowCollection<TRow>([CallerMemberName] string? propertyName = null)
        where TRow : BindableBase =>
        new(this, propertyName ?? throw new ArgumentNullException(nameof(propertyName)));

    /// <summary>
    /// Adopts a nested view model, re-raising its changes on this one at the nested path
    /// (<c>Head.SelectedId</c>) that <c>ViewModelParser</c> and <c>RequestTracker</c> key on. A nested
    /// view model that is not adopted is invisible to both: its edits never reach
    /// <see cref="OnUpdateAsync"/>, so the form silently never notices them.
    /// <para>
    /// Declare the property lazily so it can capture this view model — a field initializer cannot:
    /// <code>
    /// [field: AllowNull, MaybeNull]
    /// public MultilingualTextViewModel Name => field ??= Nested(new MultilingualTextViewModel());
    /// </code>
    /// </para>
    /// </summary>
    protected TViewModel Nested<TViewModel>(TViewModel viewModel, [CallerMemberName] string? propertyName = null)
        where TViewModel : BindableBase
    {
        var name = propertyName ?? throw new ArgumentNullException(nameof(propertyName));

        viewModel.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName))
                return;

            RaisePropertyChanged($"{name}.{e.PropertyName}");
        };

        return viewModel;
    }

    private void ValidationState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RaisePropertyChanged(nameof(ValidationState));
    }

    // Implemented explicitly so the lifecycle entry points are called only through IViewModel
    // (which is how the view drives them) and cannot be shadowed by a derived class; derived
    // classes participate via OnInitializeAsync / OnUpdateAsync.
    async Task IViewModel.InitializeAsync(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;

        if (_initializationPending)
        {
            await _updateLock.WaitAsync(cancellationToken);
            try
            {
                await OnInitializeAsync();
            }
            finally
            {
                _updateLock.Release();
            }
            _initializationPending = false;
        }
    }

    async Task IViewModel.UpdateAsync(ICollection<string> changedProperties)
    {
        if (_cancellationToken is null)
            throw new InvalidOperationException(
                $"{nameof(IViewModel.InitializeAsync)} must be called before {nameof(IViewModel.UpdateAsync)}.");

        await _updateLock.WaitAsync(_cancellationToken.Value);
        try
        {
            await OnUpdateAsync(changedProperties);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    protected virtual Task OnInitializeAsync()
    {
        return Task.CompletedTask;
    }
    
    protected virtual Task OnUpdateAsync(ICollection<string> changedProperties)
    {
        return Task.CompletedTask;
    }

    private int _activeTasks;
    public bool IsBusy => _activeTasks > 0;

    protected async Task Run(Func<CancellationToken, Task> function)
    {
        IncrementActiveWorkers();
        try
        {
            await Execute(function);
        }
        finally
        {
            DecrementActiveWorkers();
        }
    }

    protected async Task<bool> RunIfNotBusy(Func<CancellationToken, Task> function)
    {
        if (!StartBusyWork()) return false;

        try
        {
            await Execute(function);
        }
        finally
        {
            DecrementActiveWorkers();
        }

        return true;
    }
    
    /// <summary>
    /// Handles exceptions thrown inside <see cref="Run"/> / <see cref="RunIfNotBusy"/>.
    /// Assigned by the owning view (property injection); internal so consumers cannot
    /// overwrite it mid-lifetime.
    /// </summary>
    public IViewModelExceptionService? ExceptionService { get; internal set; }

    private async Task Execute(Func<CancellationToken, Task> function)
    {
        try
        {
            await function(_cancellationToken ?? CancellationToken.None);
        }
        catch (Exception e)
        {
            if (ExceptionService == null || !ExceptionService.Handle(this, e))
            {
                throw;
            }
        }
    }

    private bool StartBusyWork()
    {
        var res = Interlocked.CompareExchange(ref _activeTasks, 1, 0);
        if (res != 0) return false;
        RaisePropertyChanged(nameof(IsBusy));
        return true;
    }

    private void IncrementActiveWorkers()
    {
        var res = Interlocked.Increment(ref _activeTasks);
        if (res == 1)
        {
            RaisePropertyChanged(nameof(IsBusy));
        }
    }

    private void DecrementActiveWorkers()
    {
        var res = Interlocked.Decrement(ref _activeTasks);
        if (res == 0)
        {
            RaisePropertyChanged(nameof(IsBusy));
        }
    }
}
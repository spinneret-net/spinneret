namespace Spinneret.ViewModel.Tests;

public class ViewModelBaseTests
{
    [Test]
    public async Task InitializeAsync_first_call_runs_OnInitializeAsync()
    {
        var sut = new TestViewModel();

        await sut.InitializeAsync(CancellationToken.None);

        await Assert.That(sut.InitializeCount).IsEqualTo(1);
    }

    [Test]
    public async Task InitializeAsync_second_call_does_not_initialize_again()
    {
        var sut = new TestViewModel();

        await sut.InitializeAsync(CancellationToken.None);
        await sut.InitializeAsync(CancellationToken.None);

        await Assert.That(sut.InitializeCount).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateAsync_passes_the_changed_properties_to_OnUpdateAsync()
    {
        var sut = new TestViewModel();
        await sut.InitializeAsync(CancellationToken.None);
        var changed = new[] { "Name", "Age" };

        await sut.UpdateAsync(changed);

        await Assert.That(sut.Updates.Count).IsEqualTo(1);
        await Assert.That(string.Join(",", sut.Updates[0])).IsEqualTo("Name,Age");
    }

    [Test]
    public async Task UpdateAsync_before_initialization_throws_with_a_clear_message()
    {
        var sut = new TestViewModel();

        var exception = await Assert.That(async () => await sut.UpdateAsync(["Name"]))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("InitializeAsync must be called before UpdateAsync");
    }

    [Test]
    public async Task ValidationState_returns_the_same_instance_on_every_access()
    {
        var sut = new TestViewModel();

        var first = sut.ValidationState;
        var second = sut.ValidationState;

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task ValidationState_change_is_re_raised_on_the_view_model()
    {
        var sut = new TestViewModel();
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.ValidationState.AddError("Name", "required");

        await Assert.That(raised.Contains("ValidationState")).IsTrue();
    }

    [Test]
    public async Task Nested_re_raises_the_nested_view_models_changes_at_the_nested_path()
    {
        var sut = new TestViewModel();
        var nested = sut.NestedPublic(new ChildViewModel(), "Head");
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        nested.Value = "edited";

        await Assert.That(string.Join(",", raised)).IsEqualTo("Head.Value");
    }

    [Test]
    public async Task CreateRowCollection_names_the_collection_after_the_calling_property()
    {
        var sut = new TestViewModel();
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.Rows.Add(new ChildViewModel());

        await Assert.That(string.Join(",", raised)).IsEqualTo("Rows");
    }

    [Test]
    public async Task IsBusy_is_true_while_Run_is_executing_and_false_after()
    {
        var sut = new TestViewModel();
        var gate = new TaskCompletionSource();

        var running = sut.RunPublic(_ => gate.Task);
        var busyDuring = sut.IsBusy;
        gate.SetResult();
        await running;

        await Assert.That(busyDuring).IsTrue();
        await Assert.That(sut.IsBusy).IsFalse();
    }

    [Test]
    public async Task Run_raises_IsBusy_when_work_starts_and_when_it_ends()
    {
        var sut = new TestViewModel();
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await sut.RunPublic(_ => Task.CompletedTask);

        await Assert.That(string.Join(",", raised)).IsEqualTo("IsBusy,IsBusy");
    }

    [Test]
    public async Task Run_passes_the_token_from_InitializeAsync_to_the_work()
    {
        var sut = new TestViewModel();
        using var cts = new CancellationTokenSource();
        await sut.InitializeAsync(cts.Token);
        CancellationToken? seen = null;

        await sut.RunPublic(token =>
        {
            seen = token;
            return Task.CompletedTask;
        });

        await Assert.That(seen.HasValue && seen.Value == cts.Token).IsTrue();
    }

    [Test]
    public async Task Run_without_initialization_passes_a_none_token()
    {
        var sut = new TestViewModel();
        CancellationToken? seen = null;

        await sut.RunPublic(token =>
        {
            seen = token;
            return Task.CompletedTask;
        });

        await Assert.That(seen.HasValue && seen.Value == CancellationToken.None).IsTrue();
    }

    [Test]
    public async Task Run_without_an_exception_service_rethrows()
    {
        var sut = new TestViewModel();

        await Assert.That(async () => await sut.RunPublic(_ => throw new InvalidOperationException("boom")))
            .Throws<InvalidOperationException>();
        await Assert.That(sut.IsBusy).IsFalse();
    }

    [Test]
    public async Task Run_with_a_handling_exception_service_swallows_the_exception()
    {
        var sut = new TestViewModel();
        var service = new FakeExceptionService(handles: true);
        sut.ExceptionService = service;
        var exception = new InvalidOperationException("boom");

        await sut.RunPublic(_ => throw exception);

        await Assert.That(service.Handled.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(service.Handled[0].Vm, sut)).IsTrue();
        await Assert.That(ReferenceEquals(service.Handled[0].Exception, exception)).IsTrue();
        await Assert.That(sut.IsBusy).IsFalse();
    }

    [Test]
    public async Task Run_with_a_non_handling_exception_service_rethrows()
    {
        var sut = new TestViewModel();
        sut.ExceptionService = new FakeExceptionService(handles: false);

        await Assert.That(async () => await sut.RunPublic(_ => throw new InvalidOperationException("boom")))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RunIfNotBusy_returns_false_while_other_work_is_running()
    {
        var sut = new TestViewModel();
        var gate = new TaskCompletionSource();
        var first = sut.RunIfNotBusyPublic(_ => gate.Task);

        var second = await sut.RunIfNotBusyPublic(_ => Task.CompletedTask);
        gate.SetResult();

        await Assert.That(second).IsFalse();
        await Assert.That(await first).IsTrue();
    }

    [Test]
    public async Task RunIfNotBusy_runs_again_after_the_previous_work_finished()
    {
        var sut = new TestViewModel();
        await sut.RunIfNotBusyPublic(_ => Task.CompletedTask);

        var second = await sut.RunIfNotBusyPublic(_ => Task.CompletedTask);

        await Assert.That(second).IsTrue();
    }

    private sealed class ChildViewModel : BindableBase
    {
        private string? _value;

        public string? Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }
    }

    private sealed class TestViewModel : ViewModelBase
    {
        private RowCollection<ChildViewModel>? _rows;

        public int InitializeCount { get; private set; }
        public List<ICollection<string>> Updates { get; } = [];

        public RowCollection<ChildViewModel> Rows => _rows ??= CreateRowCollection<ChildViewModel>();

        protected override Task OnInitializeAsync()
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        protected override Task OnUpdateAsync(ICollection<string> changedProperties)
        {
            Updates.Add(changedProperties);
            return Task.CompletedTask;
        }

        public Task RunPublic(Func<CancellationToken, Task> function) => Run(function);

        public Task<bool> RunIfNotBusyPublic(Func<CancellationToken, Task> function) => RunIfNotBusy(function);

        public TNested NestedPublic<TNested>(TNested viewModel, string name) where TNested : BindableBase =>
            Nested(viewModel, name);
    }

    private sealed class FakeExceptionService(bool handles) : IViewModelExceptionService
    {
        public List<(IViewModel Vm, Exception Exception)> Handled { get; } = [];

        public bool Handle(IViewModel vm, Exception e)
        {
            Handled.Add((vm, e));
            return handles;
        }
    }
}

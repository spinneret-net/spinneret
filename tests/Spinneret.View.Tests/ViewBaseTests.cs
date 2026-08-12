using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.ViewModel;

namespace Spinneret.View.Tests;

public class ViewBaseTests
{
    // ----- Lifecycle: initialization -----

    [Test]
    public async Task State_before_first_render_is_Uninitialized()
    {
        var harness = new Harness();

        var view = harness.Renderer.CreateComponent<OwnedVmView>();

        await Assert.That(view.State).IsEqualTo(ViewState.Uninitialized);
    }

    [Test]
    public async Task OnInitializedAsync_interactive_render_initializes_owned_view_model_and_sets_state_Initialized()
    {
        var harness = new Harness();

        var (view, _) = await harness.RenderAsync<OwnedVmView>();

        await Assert.That(view.State).IsEqualTo(ViewState.Initialized);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(1);
        await Assert.That(harness.CreatedViewModels[0].InitializeCallCount).IsEqualTo(1);
        await Assert.That(harness.CreatedViewModels[0].PropertyChangedHandlerCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnInitializedAsync_prerendering_sets_state_PreInitialized_and_skips_view_model_initialization()
    {
        var harness = new Harness(prerendering: true);

        var (view, _) = await harness.RenderAsync<OwnedVmView>();

        await Assert.That(view.State).IsEqualTo(ViewState.PreInitialized);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(0);
        await Assert.That(harness.Coordinator.SubscribeCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnInitializedAsync_owned_view_model_subscribes_to_refresh_coordinator()
    {
        var harness = new Harness();

        await harness.RenderAsync<OwnedVmView>();

        await Assert.That(harness.Coordinator.SubscribeCallCount).IsEqualTo(1);
        await Assert.That(harness.Coordinator.ActiveRefreshSubscriptionCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnInitializedAsync_parameter_supplied_view_model_is_initialized_but_does_not_subscribe()
    {
        var harness = new Harness();
        var parameterViewModel = new FakeViewModel();

        var (view, _) = await harness.RenderAsync<OwnedVmView>(parameterViewModel);

        await Assert.That(view.State).IsEqualTo(ViewState.Initialized);
        await Assert.That(parameterViewModel.InitializeCallCount).IsEqualTo(1);
        await Assert.That(parameterViewModel.PropertyChangedHandlerCount).IsEqualTo(1);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(0);
        await Assert.That(harness.Coordinator.SubscribeCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnInitializedAsync_view_opting_out_of_refresh_participation_does_not_subscribe()
    {
        var harness = new Harness();

        var (view, _) = await harness.RenderAsync<NonParticipatingView>();

        await Assert.That(view.State).IsEqualTo(ViewState.Initialized);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(1);
        await Assert.That(harness.Coordinator.SubscribeCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnInitializedAsync_view_model_initialization_failure_sets_state_InitializationFailed_and_does_not_subscribe()
    {
        var harness = new Harness();
        harness.OnViewModelCreated = vm => vm.ThrowOnInitialize = true;

        var (view, _) = await harness.RenderAsync<OwnedVmView>();

        await Assert.That(view.State).IsEqualTo(ViewState.InitializationFailed);
        await Assert.That(harness.CreatedViewModels[0].PropertyChangedHandlerCount).IsEqualTo(0);
        await Assert.That(harness.Coordinator.SubscribeCallCount).IsEqualTo(0);
        // The failure is still reported through SafeExecute's logger, not the renderer.
        await Assert.That(harness.Renderer.HandledExceptions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OnInitializedAsync_property_changes_after_a_failed_initialization_do_not_trigger_updates()
    {
        var harness = new Harness();
        harness.OnViewModelCreated = vm => vm.ThrowOnInitialize = true;
        await harness.RenderAsync<OwnedVmView>();
        var viewModel = harness.CreatedViewModels.Single();
        var renderCountBefore = harness.Renderer.RenderBatchCount;

        viewModel.Raise("Name");

        // Negative check: give a would-be update pipeline time to run before asserting.
        await Task.Delay(100);
        await Assert.That(viewModel.UpdateCallCount).IsEqualTo(0);
        await Assert.That(harness.Renderer.RenderBatchCount).IsEqualTo(renderCountBefore);
    }

    // ----- ViewModel property -----

    [Test]
    public async Task ViewModel_getter_resolves_owned_view_model_from_DI_and_caches_it()
    {
        var harness = new Harness();
        var view = harness.Renderer.CreateComponent<OwnedVmView>();

        var first = view.ViewModel;
        var second = view.ViewModel;

        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(ReferenceEquals(first, harness.CreatedViewModels[0])).IsTrue();
    }

    [Test]
    public async Task ViewModel_setter_supplied_instance_is_returned_by_the_getter_without_resolving_from_DI()
    {
        var harness = new Harness();
        var view = harness.Renderer.CreateComponent<OwnedVmView>();
        var suppliedViewModel = new FakeViewModel();

#pragma warning disable BL0005 // deliberately exercising the public setter directly
        view.ViewModel = suppliedViewModel;
#pragma warning restore BL0005

        await Assert.That(ReferenceEquals(view.ViewModel, suppliedViewModel)).IsTrue();
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(0);
    }

    // ----- Refresh coordination -----

    [Test]
    public async Task Refresh_disposes_owned_view_model_and_initializes_a_new_instance()
    {
        var harness = new Harness();
        var (view, _) = await harness.RenderAsync<OwnedVmView>();
        var originalViewModel = harness.CreatedViewModels.Single();
        var renderCountBefore = harness.Renderer.RenderBatchCount;

        await harness.Coordinator.RequestRefreshAsync();

        await Assert.That(originalViewModel.DisposeCallCount).IsEqualTo(1);
        await Assert.That(originalViewModel.AsyncDisposeCallCount).IsEqualTo(1);
        await Assert.That(originalViewModel.PropertyChangedHandlerCount).IsEqualTo(0);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(2);
        await Assert.That(harness.CreatedViewModels[1].InitializeCallCount).IsEqualTo(1);
        await Assert.That(ReferenceEquals(view.ViewModel, harness.CreatedViewModels[1])).IsTrue();
        await Assert.That(view.State).IsEqualTo(ViewState.Initialized);
        await Assert.That(harness.Renderer.RenderBatchCount > renderCountBefore).IsTrue();
    }

    [Test]
    public async Task Refresh_request_arriving_while_a_refresh_is_in_progress_is_ignored()
    {
        var harness = new Harness();
        await harness.RenderAsync<OwnedVmView>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnViewModelCreated = vm => vm.InitializeGate = gate;

        var firstRefresh = harness.Coordinator.RequestRefreshAsync();
        var secondRefresh = harness.Coordinator.RequestRefreshAsync();
        await secondRefresh; // returns immediately: the second RefreshAsync sees _isRefreshing
        gate.SetResult();
        await firstRefresh;

        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Refresh_view_model_initialization_failure_sets_state_InitializationFailed_and_blocks_further_refreshes()
    {
        var harness = new Harness();
        var (view, _) = await harness.RenderAsync<OwnedVmView>();
        harness.OnViewModelCreated = vm => vm.ThrowOnInitialize = true;

        await harness.Coordinator.RequestRefreshAsync();

        await Assert.That(view.State).IsEqualTo(ViewState.InitializationFailed);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(2);

        // A subsequent refresh is a no-op because the view is no longer Initialized.
        await harness.Coordinator.RequestRefreshAsync();

        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Refresh_after_ownership_transferred_to_a_parameter_view_model_does_nothing()
    {
        var harness = new Harness();
        var (_, componentId) = await harness.RenderAsync<OwnedVmView>();
        var parameterViewModel = new FakeViewModel();
        await harness.ReRenderAsync(componentId, parameterViewModel);

        await harness.Coordinator.RequestRefreshAsync();

        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(1);
        await Assert.That(parameterViewModel.DisposeCallCount).IsEqualTo(0);
        await Assert.That(parameterViewModel.InitializeCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_invoked_after_dispose_does_not_reinitialize_the_view()
    {
        var harness = new Harness();
        var (view, _) = await harness.RenderAsync<OwnedVmView>();
        var handlers = harness.Coordinator.SnapshotRefreshHandlers();
        await view.DisposeAsync();

        // Replay the handler the view registered before it was disposed (mirrors a refresh
        // racing a teardown).
        await Task.WhenAll(handlers.Select(handler => handler()));

        await Assert.That(view.State).IsEqualTo(ViewState.Disposed);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(1);
        await Assert.That(harness.CreatedViewModels[0].InitializeCallCount).IsEqualTo(1);
    }

    // ----- Parameter changes -----

    [Test]
    public async Task OnParametersSetAsync_new_parameter_view_model_detaches_the_old_one_without_disposing_it()
    {
        var harness = new Harness();
        var viewModelA = new FakeViewModel();
        var viewModelB = new FakeViewModel();
        var (_, componentId) = await harness.RenderAsync<OwnedVmView>(viewModelA);

        await harness.ReRenderAsync(componentId, viewModelB);

        await Assert.That(viewModelA.PropertyChangedHandlerCount).IsEqualTo(0);
        await Assert.That(viewModelA.DisposeCallCount).IsEqualTo(0);
        await Assert.That(viewModelA.AsyncDisposeCallCount).IsEqualTo(0);
        await Assert.That(viewModelB.InitializeCallCount).IsEqualTo(1);
        await Assert.That(viewModelB.PropertyChangedHandlerCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnParametersSetAsync_parameter_view_model_replacing_an_owned_one_disposes_the_owned_instance()
    {
        var harness = new Harness();
        var (view, componentId) = await harness.RenderAsync<OwnedVmView>();
        var ownedViewModel = harness.CreatedViewModels.Single();
        var parameterViewModel = new FakeViewModel();

        await harness.ReRenderAsync(componentId, parameterViewModel);

        await Assert.That(ownedViewModel.DisposeCallCount).IsEqualTo(1);
        await Assert.That(ownedViewModel.AsyncDisposeCallCount).IsEqualTo(1);
        await Assert.That(parameterViewModel.InitializeCallCount).IsEqualTo(1);
        await Assert.That(ReferenceEquals(view.ViewModel, parameterViewModel)).IsTrue();
    }

    [Test]
    public async Task OnParametersSetAsync_same_parameter_view_model_instance_is_not_reinitialized()
    {
        var harness = new Harness();
        var viewModel = new FakeViewModel();
        var (_, componentId) = await harness.RenderAsync<OwnedVmView>(viewModel);

        await harness.ReRenderAsync(componentId, viewModel);

        await Assert.That(viewModel.InitializeCallCount).IsEqualTo(1);
        await Assert.That(viewModel.PropertyChangedHandlerCount).IsEqualTo(1);
    }

    // ----- PropertyChanged -> UpdateAsync pipeline -----

    [Test]
    public async Task PropertyChanged_triggers_UpdateAsync_with_the_changed_property_name_and_rerenders()
    {
        var harness = new Harness();
        await harness.RenderAsync<OwnedVmView>();
        var viewModel = harness.CreatedViewModels.Single();
        var renderCountBefore = harness.Renderer.RenderBatchCount;

        viewModel.Raise("Name");

        await TestWait.UntilAsync(() => viewModel.UpdateCallCount >= 1);
        await Assert.That(viewModel.UpdatedProperties).Contains("Name");
        await TestWait.UntilAsync(() => harness.Renderer.RenderBatchCount > renderCountBefore);
    }

    [Test]
    public async Task PropertyChanged_with_null_property_name_is_delivered_as_a_wildcard()
    {
        var harness = new Harness();
        await harness.RenderAsync<OwnedVmView>();
        var viewModel = harness.CreatedViewModels.Single();

        viewModel.Raise(null);

        await TestWait.UntilAsync(() => viewModel.UpdateCallCount >= 1);
        await Assert.That(viewModel.UpdatedProperties).Contains("*");
    }

    [Test]
    public async Task PropertyChanged_rapid_changes_are_batched_into_at_most_one_update_per_change()
    {
        var harness = new Harness();
        await harness.RenderAsync<OwnedVmView>();
        var viewModel = harness.CreatedViewModels.Single();

        viewModel.Raise("First");
        viewModel.Raise("Second");

        await TestWait.UntilAsync(() =>
            viewModel.UpdatedProperties.Contains("First") && viewModel.UpdatedProperties.Contains("Second"));
        // The update lock coalesces changes raised before the pending update drains, so the
        // two changes arrive in at most two UpdateAsync calls (exact count is timing dependent).
        await Assert.That(viewModel.UpdateCallCount is >= 1 and <= 2).IsTrue();
    }

    [Test]
    public async Task PropertyChanged_events_from_a_replaced_view_model_are_ignored()
    {
        var harness = new Harness();
        var viewModelA = new FakeViewModel();
        var viewModelB = new FakeViewModel();
        var (_, componentId) = await harness.RenderAsync<OwnedVmView>(viewModelA);
        await harness.ReRenderAsync(componentId, viewModelB);

        viewModelA.Raise("Stale");
        viewModelB.Raise("Fresh");

        await TestWait.UntilAsync(() => viewModelB.UpdateCallCount >= 1);
        await Assert.That(viewModelA.UpdateCallCount).IsEqualTo(0);
        await Assert.That(viewModelB.UpdatedProperties).Contains("Fresh");
    }

    // ----- Disposal -----

    [Test]
    public async Task DisposeAsync_disposes_the_owned_view_model_and_removes_the_refresh_subscription()
    {
        var harness = new Harness();
        var (view, _) = await harness.RenderAsync<OwnedVmView>();
        var viewModel = harness.CreatedViewModels.Single();

        await view.DisposeAsync();

        await Assert.That(view.State).IsEqualTo(ViewState.Disposed);
        await Assert.That(viewModel.DisposeCallCount).IsEqualTo(1);
        await Assert.That(viewModel.AsyncDisposeCallCount).IsEqualTo(1);
        await Assert.That(viewModel.PropertyChangedHandlerCount).IsEqualTo(0);
        await Assert.That(harness.Coordinator.ActiveRefreshSubscriptionCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeAsync_detaches_but_does_not_dispose_a_parameter_supplied_view_model()
    {
        var harness = new Harness();
        var parameterViewModel = new FakeViewModel();
        var (view, _) = await harness.RenderAsync<OwnedVmView>(parameterViewModel);

        await view.DisposeAsync();

        await Assert.That(view.State).IsEqualTo(ViewState.Disposed);
        await Assert.That(parameterViewModel.DisposeCallCount).IsEqualTo(0);
        await Assert.That(parameterViewModel.AsyncDisposeCallCount).IsEqualTo(0);
        await Assert.That(parameterViewModel.PropertyChangedHandlerCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeAsync_called_twice_disposes_the_view_model_only_once()
    {
        var harness = new Harness();
        var (view, _) = await harness.RenderAsync<OwnedVmView>();
        var viewModel = harness.CreatedViewModels.Single();

        await view.DisposeAsync();
        await view.DisposeAsync();

        await Assert.That(viewModel.DisposeCallCount).IsEqualTo(1);
        await Assert.That(viewModel.AsyncDisposeCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_before_initialization_sets_state_Disposed_without_creating_a_view_model()
    {
        var harness = new Harness();
        var view = harness.Renderer.CreateComponent<OwnedVmView>();

        await view.DisposeAsync();

        await Assert.That(view.State).IsEqualTo(ViewState.Disposed);
        await Assert.That(harness.CreatedViewModels.Count).IsEqualTo(0);
    }

    // ----- Exception service wiring -----

    [Test]
    public async Task Initialization_sets_the_registered_exception_service_on_a_ViewModelBase_derived_view_model()
    {
        var harness = new Harness(registerExceptionService: true);

        await harness.RenderAsync<BaseVmView>();

        var viewModel = harness.CreatedBaseViewModels.Single();
        await Assert.That(ReferenceEquals(viewModel.ExceptionService, harness.ExceptionService)).IsTrue();
    }

    [Test]
    public async Task Initialization_without_a_registered_exception_service_leaves_it_null()
    {
        var harness = new Harness();

        await harness.RenderAsync<BaseVmView>();

        var viewModel = harness.CreatedBaseViewModels.Single();
        await Assert.That(viewModel.ExceptionService).IsNull();
    }

    // ----- Test harness -----

    private sealed class Harness
    {
        public FakeRefreshCoordinator Coordinator { get; } = new();
        public FakeExceptionService ExceptionService { get; } = new();
        public List<FakeViewModel> CreatedViewModels { get; } = [];
        public List<FakeViewModelBase> CreatedBaseViewModels { get; } = [];
        public Action<FakeViewModel>? OnViewModelCreated { get; set; }
        public TestRenderer Renderer { get; }

        public Harness(bool prerendering = false, bool registerExceptionService = false)
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IRenderContext>(new FakeRenderContext { IsPrerendering = prerendering });
            services.AddSingleton<IViewRefreshCoordinator>(Coordinator);
            services.AddTransient(_ =>
            {
                var viewModel = new FakeViewModel();
                OnViewModelCreated?.Invoke(viewModel);
                lock (CreatedViewModels) CreatedViewModels.Add(viewModel);
                return viewModel;
            });
            services.AddTransient(_ =>
            {
                var viewModel = new FakeViewModelBase();
                lock (CreatedBaseViewModels) CreatedBaseViewModels.Add(viewModel);
                return viewModel;
            });

            if (registerExceptionService)
            {
                services.AddSingleton<IViewModelExceptionService>(ExceptionService);
            }

            Renderer = new TestRenderer(services.BuildServiceProvider());
        }

        public async Task<(TView View, int ComponentId)> RenderAsync<TView>(FakeViewModel? parameterViewModel = null)
            where TView : IComponent
        {
            var view = Renderer.CreateComponent<TView>();
            var componentId = await Renderer.AttachAndRenderAsync(view, ToParameters(parameterViewModel));
            return (view, componentId);
        }

        public Task ReRenderAsync(int componentId, FakeViewModel? parameterViewModel = null) =>
            Renderer.ReRenderRootAsync(componentId, ToParameters(parameterViewModel));

        private static ParameterView ToParameters(FakeViewModel? viewModel) =>
            viewModel == null
                ? ParameterView.Empty
                : ParameterView.FromDictionary(new Dictionary<string, object?> { ["ViewModel"] = viewModel });
    }
}

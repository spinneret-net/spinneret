using System.ComponentModel;
using Spinneret.ViewModel;

namespace Spinneret.View.Tests;

/// <summary>
/// No-op view model base for types that only need to exist so assembly scanning
/// (StartupExtensions/ViewResolver) can discover them.
/// </summary>
public abstract class StubViewModel : IViewModel
{
#pragma warning disable CS0067 // event is required by the interface, never raised by these stubs
    public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateAsync(ICollection<string> changedProperties) => Task.CompletedTask;
}

// A view model with exactly one view.
public sealed class SingleViewModel : StubViewModel;

public sealed class SingleView : ViewBase<SingleViewModel>;

// A view model with two views where one matches the "<Name>ViewModel" -> "<Name>" convention.
public sealed class DuoViewModel : StubViewModel;

public sealed class Duo : ViewBase<DuoViewModel>;

public sealed class DuoAlternate : ViewBase<DuoViewModel>;

// A view model with two views where neither matches the naming convention.
public sealed class TrioViewModel : StubViewModel;

public sealed class TrioFirstView : ViewBase<TrioViewModel>;

public sealed class TrioSecondView : ViewBase<TrioViewModel>;

// A view model with no view at all.
public sealed class UnmappedViewModel : StubViewModel;

// Views used by the ViewBase lifecycle tests.
public class OwnedVmView : ViewBase<FakeViewModel>;

public sealed class NonParticipatingView : ViewBase<FakeViewModel>
{
    protected override bool ParticipatesInRefresh => false;
}

public sealed class BaseVmView : ViewBase<FakeViewModelBase>;

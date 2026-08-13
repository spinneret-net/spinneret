using System.ComponentModel;

namespace Spinneret.ViewModel;

/// <summary>
/// The lifecycle contract a view drives: initialize once, then update after batches of
/// property changes. Derive from <see cref="ViewModelBase"/> rather than implementing this
/// directly — the base class owns the locking and busy-tracking the view relies on.
/// </summary>
public interface IViewModel : INotifyPropertyChanged
{
    public Task InitializeAsync(CancellationToken cancellationToken);
    public Task UpdateAsync(ICollection<string> changedProperties);
}

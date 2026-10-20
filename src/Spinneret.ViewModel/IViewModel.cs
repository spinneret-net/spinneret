using System.ComponentModel;

namespace Spinneret.ViewModel;

/// <summary>The lifecycle a view drives; derive from <see cref="ViewModelBase"/> rather than implementing it.</summary>
public interface IViewModel : INotifyPropertyChanged
{
    public Task InitializeAsync(CancellationToken cancellationToken);
    public Task UpdateAsync(ICollection<string> changedProperties);
}

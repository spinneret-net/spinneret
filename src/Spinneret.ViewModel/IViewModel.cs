using System.ComponentModel;

namespace Spinneret.ViewModel;

public interface IViewModel : INotifyPropertyChanged
{
    public Task InitializeAsync(CancellationToken cancellationToken);
    public Task UpdateAsync(ICollection<string> changedProperties);
}
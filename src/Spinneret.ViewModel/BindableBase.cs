using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Spinneret.ViewModel;

public abstract class BindableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, string propertyName, Action? onChanged = null)
    {
        return SetProperty(ref storage, value, onChanged, propertyName);
    }

    protected bool SetProperty<T>(ref T storage, T value, Action? onChanged = null, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;

        storage = value;
        onChanged?.Invoke();
        RaisePropertyChanged(propertyName);

        return true;
    }

    protected bool SetProperty<T>(ref T storage, T value, PropertyChangedEventHandler onPropertyChanged, string propertyName, Action? onChanged = null)
        where T : INotifyPropertyChanged
    {
        return SetProperty(ref storage, value, onPropertyChanged, onChanged, propertyName);
    }

    protected bool SetProperty<T>(ref T storage, T value, PropertyChangedEventHandler onPropertyChanged, Action? onChanged = null, [CallerMemberName] string? propertyName = null)
        where T : INotifyPropertyChanged?
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;

        if (storage != null)
        {
            storage.PropertyChanged -= onPropertyChanged;
        }

        storage = value;

        if (value != null)
        {
            value.PropertyChanged -= onPropertyChanged;
            value.PropertyChanged += onPropertyChanged;
        }

        onChanged?.Invoke();

        RaisePropertyChanged(propertyName);

        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Lets a <see cref="RowCollection{TRow}"/> raise changes on the view model that owns it.</summary>
    internal void RaisePropertyChangedFor(string propertyName) => RaisePropertyChanged(propertyName);
}
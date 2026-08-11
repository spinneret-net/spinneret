using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Spinneret.ViewModel;

/// <summary>
/// The rows of a collection a view model parses with <c>NestMany</c>. A row's field binding and its
/// validation error are both keyed by the row's position — <c>Rows[2].Name</c> — so the position is
/// part of the collection's state, not just of its contents. This owns the three things that have to
/// stay in step with it:
/// <list type="bullet">
/// <item>each row's <see cref="INotifyPropertyChanged.PropertyChanged"/> subscription,</item>
/// <item>the row's validation errors, which follow it when its index moves and go away when it does,</item>
/// <item>the change notification: a row edit is raised at the row's own path, so only that row is
/// revalidated, while adding, removing or moving rows is raised as a change to the collection.</item>
/// </list>
/// </summary>
public sealed class RowCollection<TRow>(ViewModelBase owner, string name) : ObservableCollection<TRow>
    where TRow : BindableBase
{
    /// <summary>
    /// The order as of the last notification. A <see cref="NotifyCollectionChangedAction.Reset"/> — which
    /// is what <see cref="Collection{T}.Clear"/> and <see cref="ReplaceAll"/> raise — carries no items, so
    /// the rows that left and the indexes they left behind can only be recovered from a remembered order.
    /// </summary>
    private List<TRow> _previous = [];

    /// <summary>Swaps the whole list in one notification, for pages that rebuild their rows functionally.</summary>
    public void ReplaceAll(IEnumerable<TRow> rows)
    {
        var replacement = rows.ToList();

        Items.Clear();
        foreach (var row in replacement)
            Items.Add(row);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        var previous = _previous;
        var current = this.ToList();

        foreach (var row in previous.Where(row => !current.Contains(row)))
            row.PropertyChanged -= OnRowChanged;

        foreach (var row in current.Where(row => !previous.Contains(row)))
            row.PropertyChanged += OnRowChanged;

        ReindexErrors(previous, current);
        _previous = current;

        base.OnCollectionChanged(e);
        owner.RaisePropertyChangedFor(name);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        var index = IndexOf((TRow)sender!);
        if (index < 0 || string.IsNullOrEmpty(e.PropertyName))
            return;

        owner.RaisePropertyChangedFor($"{name}[{index}].{e.PropertyName}");
    }

    private void ReindexErrors(List<TRow> previous, List<TRow> current)
    {
        var errors = owner.ValidationState.Errors.ToList();
        if (errors.Count == 0)
            return;

        var currentIndexes = new Dictionary<TRow, int>();
        for (var index = 0; index < current.Count; index++)
            currentIndexes[current[index]] = index;

        // Every moved error is removed before any is re-added: two rows swapping places would otherwise
        // have the first row's new key overwritten by the second row's removal of its old one.
        var moved = new List<(string Key, string Error)>();

        for (var previousIndex = 0; previousIndex < previous.Count; previousIndex++)
        {
            var prefix = $"{name}[{previousIndex}].";
            var rowErrors = errors.Where(error => error.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (rowErrors.Count == 0)
                continue;

            var stillPresent = currentIndexes.TryGetValue(previous[previousIndex], out var currentIndex);
            if (stillPresent && currentIndex == previousIndex)
                continue;

            foreach (var error in rowErrors)
                owner.ValidationState.RemoveError(error.Key);

            if (!stillPresent)
                continue;

            foreach (var error in rowErrors)
                moved.Add(($"{name}[{currentIndex}].{error.Key[prefix.Length..]}", error.Error));
        }

        foreach (var (key, error) in moved)
            owner.ValidationState.AddError(key, error);
    }
}

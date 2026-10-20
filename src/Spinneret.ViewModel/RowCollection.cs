using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Spinneret.ViewModel;

/// <summary>Rows parsed with <c>NestMany</c>, keeping index-keyed errors (<c>Rows[2].Name</c>) with their row as it moves.</summary>
/// <remarks>A row edit is raised at the row's own path; adding, removing or moving rows is raised on the collection.</remarks>
public sealed class RowCollection<TRow> : ObservableCollection<TRow>
    where TRow : BindableBase
{
    private readonly ViewModelBase owner;
    private readonly string name;

    internal RowCollection(ViewModelBase owner, string name)
    {
        this.owner = owner;
        this.name = name;
    }

    // A Reset carries no items, so departed rows and their indexes come from the remembered order.
    private List<TRow> _previous = [];

    /// <summary>Replaces all rows with a single Reset notification.</summary>
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

        // Remove all moved errors before re-adding any, so swapped rows don't clobber each other's keys.
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

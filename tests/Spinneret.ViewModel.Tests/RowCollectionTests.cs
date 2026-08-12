using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;

namespace Spinneret.ViewModel.Tests;

public class RowCollectionTests
{
    [Test]
    public async Task Row_edit_is_raised_at_the_rows_own_path()
    {
        var owner = new Owner();
        owner.Rows.Add(new Row());
        owner.Rows.Add(new Row());
        owner.RaisedProperties.Clear();

        owner.Rows[1].Name = "edited";

        await Assert.That(owner.RaisedProperties).IsEquivalentTo(["Rows[1].Name"]);
    }

    [Test]
    public async Task Structural_change_is_raised_as_a_change_to_the_collection()
    {
        var owner = new Owner();

        owner.Rows.Add(new Row());

        await Assert.That(owner.RaisedProperties).IsEquivalentTo(["Rows"]);
    }

    [Test]
    public async Task Removed_row_stops_raising()
    {
        var owner = new Owner();
        var row = new Row();
        owner.Rows.Add(row);
        owner.Rows.Remove(row);
        owner.RaisedProperties.Clear();

        row.Name = "edited";

        await Assert.That(owner.RaisedProperties).IsEmpty();
    }

    [Test]
    public async Task Cleared_row_stops_raising()
    {
        var owner = new Owner();
        var row = new Row();
        owner.Rows.Add(row);
        owner.Rows.Clear();
        owner.RaisedProperties.Clear();

        row.Name = "edited";

        await Assert.That(owner.RaisedProperties).IsEmpty();
    }

    [Test]
    public async Task ReplaceAll_subscribes_the_new_rows_and_drops_the_old()
    {
        var owner = new Owner();
        var original = new Row();
        owner.Rows.Add(original);
        var replacement = new Row();
        owner.Rows.ReplaceAll([replacement]);
        owner.RaisedProperties.Clear();

        original.Name = "ignored";
        replacement.Name = "edited";

        await Assert.That(owner.RaisedProperties).IsEquivalentTo(["Rows[0].Name"]);
    }

    [Test]
    public async Task ReplaceAll_raises_a_single_collection_change_on_the_owner()
    {
        var owner = new Owner();
        owner.Rows.Add(new Row());
        owner.RaisedProperties.Clear();

        owner.Rows.ReplaceAll([new Row(), new Row()]);

        await Assert.That(owner.RaisedProperties).IsEquivalentTo(["Rows"]);
    }

    [Test]
    public async Task ReplaceAll_raises_a_reset_collection_changed_event()
    {
        var owner = new Owner();
        var actions = new List<NotifyCollectionChangedAction>();
        owner.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        owner.Rows.ReplaceAll([new Row()]);

        await Assert.That(actions).IsEquivalentTo([NotifyCollectionChangedAction.Reset]);
        await Assert.That(owner.Rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Row_keeps_its_errors_when_a_later_row_is_removed()
    {
        var owner = new Owner();
        Row first = new(), second = new();
        owner.Rows.Add(first);
        owner.Rows.Add(second);
        owner.AddError(0, "Name");

        owner.Rows.Remove(second);

        await Assert.That(owner.ValidationState.GetError("Rows[0].Name")).IsEqualTo("error:0.Name");
    }

    [Test]
    public async Task Errors_of_a_removed_row_are_dropped()
    {
        var owner = new Owner();
        Row first = new(), second = new();
        owner.Rows.Add(first);
        owner.Rows.Add(second);
        owner.AddError(1, "Name");

        owner.Rows.Remove(second);

        await Assert.That(owner.ValidationState.HasErrors).IsFalse();
    }

    [Test]
    public async Task Errors_follow_a_row_that_shifts_down()
    {
        var owner = new Owner();
        Row first = new(), second = new();
        owner.Rows.Add(first);
        owner.Rows.Add(second);
        owner.AddError(1, "Name");

        owner.Rows.Remove(first);

        await Assert.That(owner.ValidationState.GetError("Rows[0].Name")).IsEqualTo("error:1.Name");
        await Assert.That(owner.ValidationState.GetError("Rows[1].Name")).IsNull();
    }

    [Test]
    public async Task Errors_of_two_moved_rows_both_survive()
    {
        var owner = new Owner();
        Row first = new(), second = new();
        owner.Rows.Add(first);
        owner.Rows.Add(second);
        owner.AddError(0, "Name");
        owner.AddError(1, "Name");

        owner.Rows.Move(0, 1);

        await Assert.That(owner.ValidationState.GetError("Rows[0].Name")).IsEqualTo("error:1.Name");
        await Assert.That(owner.ValidationState.GetError("Rows[1].Name")).IsEqualTo("error:0.Name");
    }

    [Test]
    public async Task Clearing_drops_every_row_error()
    {
        var owner = new Owner();
        owner.Rows.Add(new Row());
        owner.AddError(0, "Name");

        owner.Rows.Clear();

        await Assert.That(owner.ValidationState.HasErrors).IsFalse();
    }

    [Test]
    public async Task Errors_outside_the_collection_are_untouched()
    {
        var owner = new Owner();
        owner.Rows.Add(new Row());
        owner.ValidationState.AddError("Name", "error:Name");

        owner.Rows.Clear();

        await Assert.That(owner.ValidationState.GetError("Name")).IsEqualTo("error:Name");
    }

    private sealed class Row : BindableBase
    {
        private string? _name;

        public string? Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }

    private sealed class Owner : ViewModelBase
    {
        [field: AllowNull, MaybeNull]
        public RowCollection<Row> Rows => field ??= CreateRowCollection<Row>();

        public List<string> RaisedProperties { get; } = [];

        public Owner() => PropertyChanged += (_, e) => RaisedProperties.Add(e.PropertyName!);

        public void AddError(int index, string property) =>
            ValidationState.AddError($"Rows[{index}].{property}", $"error:{index}.{property}");
    }
}

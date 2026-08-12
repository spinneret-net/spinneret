using System.ComponentModel;

namespace Spinneret.ViewModel.Tests;

public class BindableBaseTests
{
    [Test]
    public async Task SetProperty_new_value_raises_PropertyChanged_with_property_name()
    {
        var person = new Person();

        person.Name = "Ada";

        await Assert.That(person.Raised).IsEquivalentTo(["Name"]);
    }

    [Test]
    public async Task SetProperty_new_value_returns_true()
    {
        var person = new Person();

        var changed = person.SetNameExplicit("Ada");

        await Assert.That(changed).IsTrue();
    }

    [Test]
    public async Task SetProperty_equal_value_raises_nothing_and_returns_false()
    {
        var person = new Person { Name = "Ada" };
        person.Raised.Clear();

        var changed = person.SetNameExplicit("Ada");

        await Assert.That(changed).IsFalse();
        await Assert.That(person.Raised).IsEmpty();
    }

    [Test]
    public async Task SetProperty_explicit_name_overload_raises_the_given_name()
    {
        var person = new Person();

        person.SetNameExplicit("Ada");

        await Assert.That(person.Raised).IsEquivalentTo(["CustomName"]);
    }

    [Test]
    public async Task SetProperty_onChanged_callback_is_invoked_once_per_actual_change()
    {
        var person = new Person();

        person.Tracked = "a";
        person.Tracked = "a";
        person.Tracked = "b";

        await Assert.That(person.OnChangedCalls).IsEqualTo(2);
    }

    [Test]
    public async Task SetProperty_notifying_overload_subscribes_the_new_value()
    {
        var person = new Person();
        var child = new Child();
        person.Child = child;

        child.Value = "edited";

        await Assert.That(person.ChildChanges).IsEqualTo(1);
    }

    [Test]
    public async Task SetProperty_notifying_overload_unsubscribes_the_replaced_value()
    {
        var person = new Person();
        var old = new Child();
        person.Child = old;
        person.Child = new Child();
        person.ChildChanges = 0;

        old.Value = "edited";

        await Assert.That(person.ChildChanges).IsEqualTo(0);
    }

    [Test]
    public async Task SetProperty_notifying_overload_raises_for_the_property_itself()
    {
        var person = new Person();

        person.Child = new Child();

        await Assert.That(person.Raised).IsEquivalentTo(["Child"]);
    }

    [Test]
    public async Task SetProperty_notifying_overload_setting_null_unsubscribes_and_raises()
    {
        var person = new Person();
        var child = new Child();
        person.Child = child;
        person.Raised.Clear();
        person.ChildChanges = 0;

        person.Child = null;
        child.Value = "edited";

        await Assert.That(person.Raised).IsEquivalentTo(["Child"]);
        await Assert.That(person.ChildChanges).IsEqualTo(0);
    }

    [Test]
    public async Task RaisePropertyChanged_raises_the_given_name()
    {
        var person = new Person();

        person.Raise("Anything");

        await Assert.That(person.Raised).IsEquivalentTo(["Anything"]);
    }

    private sealed class Person : BindableBase
    {
        private string? _name;
        private string? _tracked;
        private Child? _child;

        public List<string> Raised { get; } = [];
        public int OnChangedCalls { get; private set; }
        public int ChildChanges { get; set; }

        public Person() => PropertyChanged += (_, e) => Raised.Add(e.PropertyName!);

        public string? Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public bool SetNameExplicit(string? value) => SetProperty(ref _name, value, "CustomName");

        public string? Tracked
        {
            get => _tracked;
            set => SetProperty(ref _tracked, value, () => OnChangedCalls++);
        }

        public Child? Child
        {
            get => _child;
            set => SetProperty(ref _child, value, OnChildChanged);
        }

        private void OnChildChanged(object? sender, PropertyChangedEventArgs e) => ChildChanges++;

        public void Raise(string name) => RaisePropertyChanged(name);
    }

    private sealed class Child : BindableBase
    {
        private string? _value;

        public string? Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }
    }
}

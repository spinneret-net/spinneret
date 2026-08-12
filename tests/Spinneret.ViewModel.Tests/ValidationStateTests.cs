namespace Spinneret.ViewModel.Tests;

public class ValidationStateTests
{
    [Test]
    public async Task HasErrors_is_false_for_a_new_state()
    {
        var sut = new ValidationState();

        await Assert.That(sut.HasErrors).IsFalse();
        await Assert.That(sut.Errors.ToList()).IsEmpty();
    }

    [Test]
    public async Task AddError_stores_the_error_retrievable_by_key()
    {
        var sut = new ValidationState();

        sut.AddError("Name", "required");

        await Assert.That(sut.GetError("Name")).IsEqualTo("required");
        await Assert.That(sut.HasErrors).IsTrue();
        await Assert.That(sut.Errors.ToList()).IsEquivalentTo([("Name", "required")]);
    }

    [Test]
    public async Task GetError_unknown_key_returns_null()
    {
        var sut = new ValidationState();

        await Assert.That(sut.GetError("Missing")).IsNull();
    }

    [Test]
    public async Task AddError_first_error_raises_HasErrors_Errors_and_UnboundErrors()
    {
        var sut = new ValidationState();
        var raised = CollectRaised(sut);

        sut.AddError("Name", "required");

        await Assert.That(string.Join(",", raised)).IsEqualTo("Errors,UnboundErrors,HasErrors");
    }

    [Test]
    public async Task AddError_second_error_does_not_raise_HasErrors_again()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");
        var raised = CollectRaised(sut);

        sut.AddError("Age", "required");

        await Assert.That(string.Join(",", raised)).IsEqualTo("Errors,UnboundErrors");
    }

    [Test]
    public async Task AddError_same_key_and_error_raises_nothing()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");
        var raised = CollectRaised(sut);

        sut.AddError("Name", "required");

        await Assert.That(raised).IsEmpty();
    }

    [Test]
    public async Task AddError_same_key_new_error_replaces_the_error()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");

        sut.AddError("Name", "too short");

        await Assert.That(sut.GetError("Name")).IsEqualTo("too short");
        await Assert.That(sut.Errors.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task AddError_bound_key_raises_BoundErrors_instead_of_UnboundErrors()
    {
        var sut = new ValidationState();
        sut.RegisterBoundProperty("Name");
        var raised = CollectRaised(sut);

        sut.AddError("Name", "required");

        await Assert.That(string.Join(",", raised)).IsEqualTo("Errors,BoundErrors,HasErrors");
    }

    [Test]
    public async Task RemoveError_removes_the_error_and_raises()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");
        var raised = CollectRaised(sut);

        sut.RemoveError("Name");

        await Assert.That(sut.GetError("Name")).IsNull();
        await Assert.That(sut.HasErrors).IsFalse();
        await Assert.That(string.Join(",", raised)).IsEqualTo("Errors,UnboundErrors,HasErrors");
    }

    [Test]
    public async Task RemoveError_unknown_key_raises_nothing()
    {
        var sut = new ValidationState();
        var raised = CollectRaised(sut);

        sut.RemoveError("Missing");

        await Assert.That(raised).IsEmpty();
    }

    [Test]
    public async Task RemoveError_with_remaining_errors_does_not_raise_HasErrors()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");
        sut.AddError("Age", "required");
        var raised = CollectRaised(sut);

        sut.RemoveError("Name");

        await Assert.That(sut.HasErrors).IsTrue();
        await Assert.That(string.Join(",", raised)).IsEqualTo("Errors,UnboundErrors");
    }

    [Test]
    public async Task ClearErrors_removes_everything()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");
        sut.AddError("Age", "required");

        sut.ClearErrors();

        await Assert.That(sut.HasErrors).IsFalse();
        await Assert.That(sut.Errors.ToList()).IsEmpty();
    }

    [Test]
    public async Task ClearErrors_on_an_empty_state_raises_nothing()
    {
        var sut = new ValidationState();
        var raised = CollectRaised(sut);

        sut.ClearErrors();

        await Assert.That(raised).IsEmpty();
    }

    [Test]
    public async Task ClearErrors_raises_only_the_partitions_that_had_errors()
    {
        var sut = new ValidationState();
        sut.RegisterBoundProperty("Name");
        sut.AddError("Name", "required");
        var raised = CollectRaised(sut);

        sut.ClearErrors();

        await Assert.That(string.Join(",", raised)).IsEqualTo("Errors,BoundErrors,HasErrors");
    }

    [Test]
    public async Task BoundErrors_and_UnboundErrors_partition_by_registered_properties()
    {
        var sut = new ValidationState();
        sut.RegisterBoundProperty("Name");
        sut.AddError("Name", "bound error");
        sut.AddError("Age", "unbound error");

        await Assert.That(sut.BoundErrors.ToList()).IsEquivalentTo([("Name", "bound error")]);
        await Assert.That(sut.UnboundErrors.ToList()).IsEquivalentTo([("Age", "unbound error")]);
    }

    [Test]
    public async Task RegisterBoundProperty_with_an_existing_error_moves_it_and_raises_both_partitions()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");
        var raised = CollectRaised(sut);

        sut.RegisterBoundProperty("Name");

        await Assert.That(sut.BoundErrors.ToList()).IsEquivalentTo([("Name", "required")]);
        await Assert.That(string.Join(",", raised)).IsEqualTo("BoundErrors,UnboundErrors");
    }

    [Test]
    public async Task RegisterBoundProperty_without_an_error_raises_nothing()
    {
        var sut = new ValidationState();
        var raised = CollectRaised(sut);

        sut.RegisterBoundProperty("Name");

        await Assert.That(raised).IsEmpty();
    }

    [Test]
    public async Task RegisterBoundProperty_twice_raises_nothing_the_second_time()
    {
        var sut = new ValidationState();
        sut.AddError("Name", "required");
        sut.RegisterBoundProperty("Name");
        var raised = CollectRaised(sut);

        sut.RegisterBoundProperty("Name");

        await Assert.That(raised).IsEmpty();
    }

    private static List<string?> CollectRaised(ValidationState state)
    {
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }
}

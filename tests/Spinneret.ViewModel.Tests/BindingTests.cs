using Spinneret.Functional;

namespace Spinneret.ViewModel.Tests;

public class BindingTests
{
    [Test]
    public async Task Create_member_chain_expression_sets_and_gets_the_value()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.Nested.Value);

        sut.SetValue("updated");

        await Assert.That(sut.PropertyPath).IsEqualTo("Nested.Value");
        await Assert.That(sut.GetValue()).IsEqualTo("updated");
        await Assert.That(viewModel.Nested.Value).IsEqualTo("updated");
    }

    [Test]
    public async Task Create_deep_member_chain_uses_the_full_property_path()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.GroupEditor.Label.Value);

        sut.SetValue("updated");

        await Assert.That(sut.PropertyPath).IsEqualTo("GroupEditor.Label.Value");
        await Assert.That(sut.GetValue()).IsEqualTo("updated");
        await Assert.That(viewModel.GroupEditor.Label.Value).IsEqualTo("updated");
    }

    [Test]
    public async Task Create_indexed_expression_sets_and_gets_the_value()
    {
        var viewModel = new TestViewModel();
        var index = 1;
        var sut = Binding.Create(viewModel, x => x.Options[index].Label);

        sut.SetValue("updated");

        await Assert.That(sut.PropertyPath).IsEqualTo("Options[1].Label");
        await Assert.That(sut.GetValue()).IsEqualTo("updated");
        await Assert.That(viewModel.Options[1].Label).IsEqualTo("updated");
    }

    [Test]
    public async Task Create_indexed_expression_uses_distinct_validation_keys_per_index()
    {
        var viewModel = new TestViewModel();
        var index0 = 0;
        var index1 = 1;
        var invalid = new Func<string, Result<string, string>>(_ => Result<string, string>.Error("bad"));

        var binding0 = Binding.Create(viewModel, x => x.Options[index0].Label, invalid, x => x);
        var binding1 = Binding.Create(viewModel, x => x.Options[index1].Label, invalid, x => x);
        binding0.SetValue("x");
        binding1.SetValue("y");

        await Assert.That(binding0.PropertyPath).IsEqualTo("Options[0].Label");
        await Assert.That(binding1.PropertyPath).IsEqualTo("Options[1].Label");
        var errors = viewModel.ValidationState.Errors.ToList();
        await Assert.That(errors.Contains(("Options[0].Label", "bad"))).IsTrue();
        await Assert.That(errors.Contains(("Options[1].Label", "bad"))).IsTrue();
    }

    [Test]
    public async Task Create_captured_index_local_binds_each_item_separately()
    {
        var viewModel = new TestViewModel();
        var bindings = new List<Binding>();
        for (var i = 0; i < viewModel.Options.Count; i++)
        {
            var index = i;
            bindings.Add(Binding.Create(viewModel, x => x.Options[index].Label));
        }

        bindings[0].SetValue("first-updated");
        bindings[1].SetValue("second-updated");

        await Assert.That(bindings[0].PropertyPath).IsEqualTo("Options[0].Label");
        await Assert.That(bindings[1].PropertyPath).IsEqualTo("Options[1].Label");
        await Assert.That(viewModel.Options[0].Label).IsEqualTo("first-updated");
        await Assert.That(viewModel.Options[1].Label).IsEqualTo("second-updated");
    }

    [Test]
    public async Task Create_unsupported_expression_throws_a_clear_exception()
    {
        var viewModel = new TestViewModel();

        var exception = Assert.Throws<ArgumentException>(() => Binding.Create(viewModel, x => x.GetCurrentOption().Label));

        await Assert.That(exception.Message).Contains("Unsupported binding expression");
    }

    [Test]
    public async Task Create_dictionary_indexer_quotes_the_key_in_the_property_path()
    {
        var viewModel = new TestViewModel();
        var key = "lang_en";

        var sut = Binding.Create(viewModel, x => x.LocalizedLabels[key]);

        await Assert.That(sut.PropertyPath).IsEqualTo("LocalizedLabels[\"lang_en\"]");
    }

    [Test]
    public async Task SetValue_through_dictionary_indexer_writes_the_entry()
    {
        var viewModel = new TestViewModel();
        var key = "lang_en";
        var sut = Binding.Create(viewModel, x => x.LocalizedLabels[key]);

        sut.SetValue("Updated English");

        await Assert.That(viewModel.LocalizedLabels["lang_en"]).IsEqualTo("Updated English");
        await Assert.That(sut.GetValue()).IsEqualTo("Updated English");
    }

    [Test]
    public async Task Create_same_expression_twice_produces_the_same_property_path()
    {
        var viewModel = new TestViewModel();
        var index = 0;

        var sut1 = Binding.Create(viewModel, x => x.Options[index].Label);
        var sut2 = Binding.Create(viewModel, x => x.Options[index].Label);

        await Assert.That(sut1.PropertyPath).IsEqualTo("Options[0].Label");
        await Assert.That(sut2.PropertyPath).IsEqualTo("Options[0].Label");
    }

    [Test]
    public async Task Create_different_indices_produce_distinct_property_paths()
    {
        var viewModel = new TestViewModel();
        var index0 = 0;
        var index1 = 1;

        var sut0 = Binding.Create(viewModel, x => x.Options[index0].Label);
        var sut1 = Binding.Create(viewModel, x => x.Options[index1].Label);

        await Assert.That(sut0.PropertyPath).IsNotEqualTo(sut1.PropertyPath);
    }

    [Test]
    public async Task Create_property_path_expression_returns_the_cached_binding_for_the_same_target()
    {
        var viewModel = new TestViewModel();

        var sut1 = Binding.Create(viewModel, x => x.Nested.Value);
        var sut2 = Binding.Create(viewModel, x => x.Nested.Value);

        await Assert.That(ReferenceEquals(sut1, sut2)).IsTrue();
    }

    [Test]
    public async Task Create_different_captured_contexts_with_the_same_index_produce_distinct_bindings()
    {
        // Regression: two row contexts that currently evaluate to the same indexer path
        // must not collide in the cache (removing a row would otherwise make a
        // virtualization-scrolled-in row's field write to the wrong row).
        var viewModel = new TestViewModel();
        var rowA = new RowContext { Index = 0 };
        var rowB = new RowContext { Index = 0 };

        var bindingA = CreateBindingForRow(viewModel, rowA);
        var bindingB = CreateBindingForRow(viewModel, rowB);

        await Assert.That(ReferenceEquals(bindingA, bindingB)).IsFalse();
    }

    [Test]
    public async Task Create_same_captured_context_across_calls_returns_the_cached_binding()
    {
        // The cache must still work for the common case: the same row context across
        // re-renders (each call creates a fresh compiler display class, so the cache
        // key has to look through the display class at the captured value).
        var viewModel = new TestViewModel();
        var context = new RowContext { Index = 1 };

        var binding1 = CreateBindingForRow(viewModel, context);
        var binding2 = CreateBindingForRow(viewModel, context);

        await Assert.That(ReferenceEquals(binding1, binding2)).IsTrue();
    }

    [Test]
    public async Task Create_new_context_at_a_previously_cached_index_writes_to_the_correct_row()
    {
        // After a row is removed and a different context occupies a previously-cached
        // index, a binding for the new context must write to the new context's row.
        var viewModel = new TestViewModel();
        viewModel.Options.Clear();
        for (var i = 0; i < 5; i++)
            viewModel.Options.Add(new OptionModel { Label = $"row-{i}" });
        var rowA = new RowContext { Index = 0 };
        var rowB = new RowContext { Index = 1 };
        var rowC = new RowContext { Index = 2 };
        _ = CreateBindingForRow(viewModel, rowA);
        _ = CreateBindingForRow(viewModel, rowB);
        _ = CreateBindingForRow(viewModel, rowC);

        // Simulate removing rowB: the collection shifts, rowC's index updates, and a
        // new rowD scrolls into view at the index rowC previously occupied.
        viewModel.Options.RemoveAt(1);
        rowC.Index = 1;
        var rowD = new RowContext { Index = 2 };
        var bindingD = CreateBindingForRow(viewModel, rowD);
        bindingD.SetValue("d-wrote");

        await Assert.That(viewModel.Options[2].Label).IsEqualTo("d-wrote");
        await Assert.That(viewModel.Options[1].Label).IsEqualTo("row-2");
    }

    [Test]
    public async Task SetValue_conversion_failure_adds_a_validation_error_at_the_path()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.Nested.Number, ParseInt);

        sut.SetValue("not-a-number");

        await Assert.That(sut.HasConversionError).IsTrue();
        await Assert.That(sut.HasError).IsTrue();
        await Assert.That(sut.GetError()).IsEqualTo("not an int");
        await Assert.That(viewModel.ValidationState.GetError("Nested.Number")).IsEqualTo("not an int");
        await Assert.That(viewModel.Nested.Number).IsEqualTo(0);
    }

    [Test]
    public async Task SetValue_valid_value_after_a_conversion_failure_clears_the_error_and_writes()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.Nested.Number, ParseInt);
        sut.SetValue("not-a-number");

        sut.SetValue("42");

        await Assert.That(sut.HasConversionError).IsFalse();
        await Assert.That(sut.HasError).IsFalse();
        await Assert.That(viewModel.ValidationState.GetError("Nested.Number")).IsNull();
        await Assert.That(viewModel.Nested.Number).IsEqualTo(42);
    }

    [Test]
    public async Task GetValue_uses_the_convert_from_target_conversion()
    {
        var viewModel = new TestViewModel();
        viewModel.Nested.Number = 7;

        var sut = Binding.Create(viewModel, x => x.Nested.Number, ParseInt);

        await Assert.That(sut.GetValue()).IsEqualTo("7");
    }

    [Test]
    public async Task Create_with_a_converter_round_trips_the_value()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.Nested.Number, new IntConverter());

        sut.SetValue("13");

        await Assert.That(viewModel.Nested.Number).IsEqualTo(13);
        await Assert.That(sut.GetValue()).IsEqualTo("13");
    }

    [Test]
    public async Task GetValue_null_property_value_returns_an_empty_string()
    {
        var viewModel = new TestViewModel();

        var sut = Binding.Create(viewModel, x => x.NullableText);

        await Assert.That(sut.GetValue()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GetValue_null_intermediate_in_the_path_returns_an_empty_string()
    {
        var viewModel = new TestViewModel();

        var sut = Binding.Create(viewModel, x => x.MaybeNested!.Value);

        await Assert.That(sut.GetValue()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SetValue_null_intermediate_in_the_path_throws_invalid_operation()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.MaybeNested!.Value);

        var exception = Assert.Throws<InvalidOperationException>(() => sut.SetValue("x"));

        await Assert.That(exception.Message).Contains("MaybeNested.Value");
        await Assert.That(exception.Message).Contains("intermediate value is null");
    }

    [Test]
    public async Task SetValue_read_only_expression_target_throws_invalid_operation()
    {
        var viewModel = new TestViewModel();
        var index = 0;
        var sut = Binding.Create(viewModel, x => x.Options[index].ReadOnlyLabel);

        var exception = Assert.Throws<InvalidOperationException>(() => sut.SetValue("x"));

        await Assert.That(exception.Message).Contains("not writable");
    }

    [Test]
    public async Task GetError_reflects_errors_added_directly_to_the_validation_state()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.Nested.Value);

        viewModel.ValidationState.AddError("Nested.Value", "external error");

        await Assert.That(sut.GetError()).IsEqualTo("external error");
        await Assert.That(sut.HasError).IsTrue();
        await Assert.That(sut.HasConversionError).IsFalse();
    }

    [Test]
    public async Task RegisterBoundProperty_makes_errors_at_the_path_bound()
    {
        var viewModel = new TestViewModel();
        var sut = Binding.Create(viewModel, x => x.Nested.Value);

        sut.RegisterBoundProperty();
        viewModel.ValidationState.AddError("Nested.Value", "bad");

        await Assert.That(viewModel.ValidationState.BoundErrors.ToList())
            .IsEquivalentTo([("Nested.Value", "bad")]);
        await Assert.That(viewModel.ValidationState.UnboundErrors.ToList()).IsEmpty();
    }

    private static Result<int, string> ParseInt(string text) =>
        int.TryParse(text, out var value)
            ? Result<int, string>.Ok(value)
            : Result<int, string>.Error("not an int");

    private static Binding CreateBindingForRow(TestViewModel viewModel, RowContext context)
    {
        return Binding.Create(viewModel, x => x.Options[context.Index].Label);
    }

    private sealed class IntConverter : IConverter<string, int>
    {
        public Result<int, string> ConvertTo(string value) => ParseInt(value);
        public string ConvertFrom(int value) => value.ToString();
    }

    private sealed class RowContext
    {
        public int Index { get; set; }
    }

    private sealed class TestViewModel : IValidationStateProvider
    {
        public IValidationState ValidationState { get; } = new ValidationState();
        public NestedModel Nested { get; set; } = new();
        public NestedModel? MaybeNested { get; set; }
        public string? NullableText { get; set; }
        public GroupEditorModel GroupEditor { get; set; } = new();
        public List<OptionModel> Options { get; } =
        [
            new OptionModel { Label = "first" },
            new OptionModel { Label = "second" }
        ];
        public Dictionary<string, string> LocalizedLabels { get; } = new()
        {
            ["lang_en"] = "English",
            ["lang_sv"] = "Svenska"
        };

        public OptionModel GetCurrentOption() => Options[0];
    }

    private sealed class NestedModel
    {
        public string Value { get; set; } = "initial";
        public int Number { get; set; }
    }

    private sealed class OptionModel
    {
        public string Label { get; set; } = string.Empty;
        public string ReadOnlyLabel => Label;
    }

    private sealed class GroupEditorModel
    {
        public NestedModel Label { get; set; } = new();
    }
}

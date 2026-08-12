namespace Spinneret.Functional.Tests;

public class LinqExtensionsTests
{
    [Test]
    public async Task Choose_with_reference_type_selector_keeps_only_non_null_results()
    {
        var source = new[] { 1, 2, 3, 4 };

        var chosen = source.Choose(x => x % 2 == 0 ? x.ToString() : null);

        await Assert.That(chosen).IsEquivalentTo(["2", "4"]);
    }

    [Test]
    public async Task Choose_with_reference_type_selector_on_empty_source_returns_empty()
    {
        var source = Array.Empty<int>();

        var chosen = source.Choose(x => x.ToString());

        await Assert.That(chosen.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Choose_with_reference_type_selector_returning_only_nulls_returns_empty()
    {
        var source = new[] { 1, 2, 3 };

        var chosen = source.Choose(string? (_) => null);

        await Assert.That(chosen.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Choose_with_identity_selector_filters_null_elements()
    {
        var source = new string?[] { "a", null, "b", null };

        var chosen = source.Choose(x => x);

        await Assert.That(chosen).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Choose_with_nullable_struct_selector_keeps_only_values()
    {
        var source = new[] { "1", "two", "3", "four" };

        var chosen = source.Choose(x => int.TryParse(x, out var parsed) ? parsed : (int?)null);

        await Assert.That(chosen).IsEquivalentTo([1, 3]);
    }

    [Test]
    public async Task Choose_with_nullable_struct_selector_on_empty_source_returns_empty()
    {
        var source = Array.Empty<string>();

        var chosen = source.Choose(x => (int?)x.Length);

        await Assert.That(chosen.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Choose_with_nullable_struct_selector_returning_only_nulls_returns_empty()
    {
        var source = new[] { 1, 2, 3 };

        var chosen = source.Choose(_ => (int?)null);

        await Assert.That(chosen.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Choose_defers_selector_execution_until_enumeration()
    {
        var calls = 0;
        var source = new[] { 1, 2, 3 };

        var chosen = source.Choose(x =>
        {
            calls++;
            return x % 2 == 0 ? (int?)x : null;
        });

        await Assert.That(calls).IsEqualTo(0);

        _ = chosen.ToList();

        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task Choose_re_enumerating_runs_selector_again()
    {
        var calls = 0;
        var source = new[] { "a", "b" };

        var chosen = source.Choose(x =>
        {
            calls++;
            return x;
        });

        _ = chosen.ToList();
        _ = chosen.ToList();

        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task Choose_preserves_source_order()
    {
        var source = new[] { 5, 1, 4, 2, 3 };

        var chosen = source.Choose(x => x > 2 ? (int?)x : null).ToList();

        await Assert.That(chosen).IsEquivalentTo([5, 4, 3]);
    }
}

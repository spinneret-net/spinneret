using System.Text.Json;

namespace Spinneret.Functional.Tests;

public class EitherTests
{
    [Test]
    public async Task Match_on_first_branch_invokes_first_function()
    {
        var either = new Either<int, string>(42);

        var reduced = either.Match(i => $"first:{i}", s => $"second:{s}");

        await Assert.That(reduced).IsEqualTo("first:42");
    }

    [Test]
    public async Task Match_on_second_branch_invokes_second_function()
    {
        var either = new Either<int, string>("hello");

        var reduced = either.Match(i => $"first:{i}", s => $"second:{s}");

        await Assert.That(reduced).IsEqualTo("second:hello");
    }

    [Test]
    public async Task Switch_on_first_branch_invokes_only_first_action()
    {
        var either = new Either<int, string>(7);
        var firstCalls = new List<int>();
        var secondCalls = new List<string>();

        either.Switch(firstCalls.Add, secondCalls.Add);

        await Assert.That(firstCalls).IsEquivalentTo([7]);
        await Assert.That(secondCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Switch_on_second_branch_invokes_only_second_action()
    {
        var either = new Either<int, string>("hello");
        var firstCalls = new List<int>();
        var secondCalls = new List<string>();

        either.Switch(firstCalls.Add, secondCalls.Add);

        await Assert.That(firstCalls.Count).IsEqualTo(0);
        await Assert.That(secondCalls).IsEquivalentTo(["hello"]);
    }

    [Test]
    public async Task Map_on_first_branch_transforms_first_value_and_keeps_branch()
    {
        var either = new Either<int, string>(21);

        var mapped = either.Map(i => i * 2, s => s.Length.ToString());

        var reduced = mapped.Match(i => $"first:{i}", s => $"second:{s}");
        await Assert.That(reduced).IsEqualTo("first:42");
    }

    [Test]
    public async Task Map_on_second_branch_transforms_second_value_and_keeps_branch()
    {
        var either = new Either<int, string>("hello");

        var mapped = either.Map(i => (long)i, s => s.ToUpperInvariant());

        var reduced = mapped.Match(l => $"first:{l}", s => $"second:{s}");
        await Assert.That(reduced).IsEqualTo("second:HELLO");
    }

    [Test]
    public async Task Map_on_first_branch_does_not_invoke_second_function()
    {
        var either = new Either<int, string>(1);
        var invoked = false;

        either.Map(i => i, s =>
        {
            invoked = true;
            return s;
        });

        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Reverse_moves_first_branch_value_to_second_branch()
    {
        var either = new Either<int, string>(5);

        var reversed = either.Reverse();

        var reduced = reversed.Match(s => $"first:{s}", i => $"second:{i}");
        await Assert.That(reduced).IsEqualTo("second:5");
    }

    [Test]
    public async Task Reverse_moves_second_branch_value_to_first_branch()
    {
        var either = new Either<int, string>("hello");

        var reversed = either.Reverse();

        var reduced = reversed.Match(s => $"first:{s}", i => $"second:{i}");
        await Assert.That(reduced).IsEqualTo("first:hello");
    }

    [Test]
    public async Task Reverse_twice_round_trips_to_original()
    {
        var either = new Either<int, string>("hello");

        var roundTripped = either.Reverse().Reverse();

        await Assert.That(roundTripped).IsEqualTo(either);
    }

    [Test]
    public async Task TraverseResult_on_first_branch_with_ok_result_wraps_mapped_value()
    {
        var either = new Either<int, string>(21);

        var traversed = either.TraverseResult(
            i => Result.Ok<int, string>(i * 2),
            s => Result.Ok<string, string>(s));

        await Assert.That(traversed)
            .IsEqualTo(Result.Ok<Either<int, string>, string>(new Either<int, string>(42)));
    }

    [Test]
    public async Task TraverseResult_on_first_branch_with_error_result_propagates_error()
    {
        var either = new Either<int, string>(21);

        var traversed = either.TraverseResult(
            _ => Result.Error<int, string>("failed"),
            s => Result.Ok<string, string>(s));

        await Assert.That(traversed)
            .IsEqualTo(Result.Error<Either<int, string>, string>("failed"));
    }

    [Test]
    public async Task TraverseResult_on_second_branch_with_ok_result_wraps_mapped_value()
    {
        var either = new Either<int, string>("hello");

        var traversed = either.TraverseResult(
            i => Result.Ok<int, string>(i),
            s => Result.Ok<string, string>(s.ToUpperInvariant()));

        await Assert.That(traversed)
            .IsEqualTo(Result.Ok<Either<int, string>, string>(new Either<int, string>("HELLO")));
    }

    [Test]
    public async Task TraverseResult_on_second_branch_with_error_result_propagates_error()
    {
        var either = new Either<int, string>("hello");

        var traversed = either.TraverseResult(
            i => Result.Ok<int, string>(i),
            _ => Result.Error<string, string>("failed"));

        await Assert.That(traversed)
            .IsEqualTo(Result.Error<Either<int, string>, string>("failed"));
    }

    [Test]
    public async Task TraverseResult_on_first_branch_does_not_invoke_second_function()
    {
        var either = new Either<int, string>(1);
        var invoked = false;

        either.TraverseResult(
            i => Result.Ok<int, string>(i),
            s =>
            {
                invoked = true;
                return Result.Ok<string, string>(s);
            });

        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Equality_holds_for_same_branch_and_equal_value()
    {
        var first = new Either<int, string>(42);
        var second = new Either<int, string>(42);

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task Equality_fails_for_same_branch_and_different_values()
    {
        var first = new Either<int, string>(1);
        var second = new Either<int, string>(2);

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task Json_round_trip_preserves_first_branch_value()
    {
        var either = new Either<int, string>(42);

        var json = JsonSerializer.Serialize(either);
        var deserialized = JsonSerializer.Deserialize<Either<int, string>>(json);

        await Assert.That(deserialized).IsEqualTo(either);
    }

    [Test]
    public async Task Json_round_trip_preserves_second_branch_value()
    {
        var either = new Either<int, string>("hello");

        var json = JsonSerializer.Serialize(either);
        var deserialized = JsonSerializer.Deserialize<Either<int, string>>(json);

        await Assert.That(deserialized).IsEqualTo(either);
    }

    [Test]
    public async Task Json_wire_contract_is_tag_value1_value2()
    {
        // Pins the persisted shape: renaming the private fields breaks stored data.
        var json = JsonSerializer.Serialize(new Either<int, string>(42));

        await Assert.That(json).IsEqualTo("""{"tag":1,"value1":42,"value2":null}""");
    }

    [Test]
    public async Task First_factory_selects_first_branch_when_types_are_ambiguous()
    {
        var either = Either<string, string>.First("value");

        var matched = either.Match(s => $"first:{s}", s => $"second:{s}");

        await Assert.That(matched).IsEqualTo("first:value");
    }

    [Test]
    public async Task Second_factory_selects_second_branch_when_types_are_ambiguous()
    {
        var either = Either<string, string>.Second("value");

        var matched = either.Match(s => $"first:{s}", s => $"second:{s}");

        await Assert.That(matched).IsEqualTo("second:value");
    }

    [Test]
    public async Task ToString_names_the_branch_and_value()
    {
        await Assert.That(new Either<int, string>(42).ToString()).IsEqualTo("First(42)");
        await Assert.That(new Either<int, string>("hi").ToString()).IsEqualTo("Second(hi)");
    }
}

using System.Reflection;

namespace Spinneret.Queue.Tests;

// DeadLetter restates DeadLetterEntry, so a field added to one must be mirrored on the other.
public class DeadLetterContractTests
{
    [Test]
    public async Task Exposes_every_field_the_writer_records()
    {
        var written = typeof(DeadLetterEntry).GetProperties().Select(p => (p.Name, p.PropertyType));
        var readable = typeof(DeadLetter).GetProperties().Select(p => (p.Name, p.PropertyType)).ToHashSet();

        var missing = written.Where(p => !readable.Contains(p)).Select(p => p.Name).ToArray();

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task Adds_only_the_two_fields_the_store_itself_knows()
    {
        var written = typeof(DeadLetterEntry).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var added = typeof(DeadLetter).GetProperties()
            .Select(p => p.Name)
            .Where(name => !written.Contains(name))
            .ToArray();

        await Assert.That(added).IsEquivalentTo(new[] { nameof(DeadLetter.DeadLetteredAt) });
    }
}

// A closed union: an out-of-tree case would make application switches silently non-exhaustive.
public class ResendDeadLetterErrorTests
{
    [Test]
    public async Task Cannot_be_derived_from_outside_its_own_declaration()
    {
        // With only a private constructor, a derived record elsewhere doesn't compile.
        var constructors = typeof(ResendDeadLetterError)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(c => c.GetParameters() is not [{ ParameterType: var p }] || p != typeof(ResendDeadLetterError))
            .ToArray();

        await Assert.That(constructors).IsNotEmpty();
        await Assert.That(constructors.All(c => c.IsPrivate)).IsTrue();
    }

    [Test]
    public async Task Declares_every_case_as_a_nested_type()
    {
        var cases = typeof(ResendDeadLetterError).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ResendDeadLetterError)))
            .ToArray();

        await Assert.That(cases.Select(t => t.Name).ToArray()).IsEquivalentTo(new[]
        {
            nameof(ResendDeadLetterError.NotFound),
            nameof(ResendDeadLetterError.UnknownCommandType),
            nameof(ResendDeadLetterError.InvalidPayload),
        });
        await Assert.That(cases.All(t => t.IsNested && t.IsSealed)).IsTrue();
    }
}

public class DeadLetterQueryTests
{
    [Test]
    public async Task Defaults_to_a_page_a_screen_can_show()
    {
        await Assert.That(new DeadLetterQuery().PageSize).IsEqualTo(50);
        await Assert.That(new DeadLetterQuery().Cursor).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(DeadLetterQuery.MaxPageSize + 1)]
    public async Task Rejects_a_page_size_no_store_will_serve(int pageSize)
    {
        // Thrown rather than clamped: validating a query-string page size is the application's job.
        await Assert.That(() => new DeadLetterQuery { PageSize = pageSize })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(1)]
    [Arguments(DeadLetterQuery.MaxPageSize)]
    public async Task Accepts_the_range_boundaries(int pageSize) =>
        await Assert.That(new DeadLetterQuery { PageSize = pageSize }.PageSize).IsEqualTo(pageSize);
}

public class DeadLetterStorageTests
{
    [Test]
    [Arguments(DeadLetterSource.Queue, "Queue")]
    [Arguments(DeadLetterSource.Scheduler, "Scheduler")]
    public async Task Persists_a_source_as_its_member_name(DeadLetterSource source, string expected) =>
        await Assert.That(DeadLetterStorage.FormatSource(source)).IsEqualTo(expected);

    [Test]
    [Arguments(DeadLetterSource.Queue)]
    [Arguments(DeadLetterSource.Scheduler)]
    public async Task Round_trips_every_source(DeadLetterSource source) =>
        await Assert.That(DeadLetterStorage.ParseSource(DeadLetterStorage.FormatSource(source)))
            .IsEqualTo(source);

    [Test]
    [Arguments("queue")]
    [Arguments("QUEUE")]
    [Arguments("Unknown")]
    [Arguments("0")]
    [Arguments("")]
    public async Task Rejects_a_value_that_names_no_source(string stored)
    {
        // Case matters: accepting "queue" would bless a store this library didn't write.
        await Assert.That(() => DeadLetterStorage.ParseSource(stored)).Throws<InvalidOperationException>();
    }
}

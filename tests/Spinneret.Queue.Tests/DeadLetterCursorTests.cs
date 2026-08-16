namespace Spinneret.Queue.Tests;

/// <summary>
/// The cursor is handed to applications and comes back through a query string, so it must survive
/// that trip intact and reject anything else clearly.
/// </summary>
public class DeadLetterCursorTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 15, 10, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task Round_trips_its_position()
    {
        var encoded = new DeadLetterCursor(At, "task-1").Encode();

        var decoded = DeadLetterCursor.Decode(encoded);

        await Assert.That(decoded.DeadLetteredAt).IsEqualTo(At);
        await Assert.That(decoded.IdempotencyKey).IsEqualTo("task-1");
    }

    [Test]
    public async Task Keeps_sub_second_precision()
    {
        // The MSSQL store compares this value for equality against a DATETIME2(3) column, so losing
        // milliseconds here would skip or repeat rows at a page boundary.
        var precise = At.AddMilliseconds(123);

        var decoded = DeadLetterCursor.Decode(new DeadLetterCursor(precise, "task-1").Encode());

        await Assert.That(decoded.DeadLetteredAt).IsEqualTo(precise);
    }

    [Test]
    [Arguments("task:with:colons")]
    [Arguments("task/with/slashes")]
    [Arguments("task with spaces")]
    [Arguments("tåsk-ünïcode")]
    public async Task Round_trips_keys_that_collide_with_its_own_encoding(string key)
    {
        // Cloud Tasks ids and scheduler job keys are not restricted to a safe alphabet, and the
        // payload is split on its first separator precisely so a key may contain more of them.
        var decoded = DeadLetterCursor.Decode(new DeadLetterCursor(At, key).Encode());

        await Assert.That(decoded.IdempotencyKey).IsEqualTo(key);
    }

    [Test]
    public async Task Encodes_to_something_url_safe()
    {
        var encoded = new DeadLetterCursor(At, "task/1+2=3").Encode();

        await Assert.That(encoded).DoesNotContain("/");
        await Assert.That(encoded).DoesNotContain("+");
        await Assert.That(encoded).DoesNotContain("=");
    }

    [Test]
    [Arguments("not-base64!!")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Rejects_a_cursor_it_did_not_produce(string cursor) =>
        await Assert.That(() => DeadLetterCursor.Decode(cursor)).Throws<ArgumentException>();

    [Test]
    [Arguments("no-separator")]
    [Arguments("notanumber:task-1")]
    [Arguments(":task-1")]
    [Arguments("638000000000000000:")]
    public async Task Rejects_a_well_formed_encoding_of_a_malformed_position(string payload)
    {
        var encoded = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(payload));

        await Assert.That(() => DeadLetterCursor.Decode(encoded)).Throws<ArgumentException>();
    }
}

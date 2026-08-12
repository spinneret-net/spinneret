namespace Spinneret.Queue.Tests;

public class QueuePolicyTests
{
    [Test]
    public async Task Default_policy_has_documented_default_values()
    {
        var policy = QueuePolicy.Default;

        await Assert.That(policy.Channel).IsNull();
        await Assert.That(policy.ResolvedChannel).IsEqualTo("default");
        await Assert.That(policy.MaxAttempts).IsEqualTo(7);
        await Assert.That(policy.MaxAge).IsEqualTo(TimeSpan.FromDays(1));
        await Assert.That(policy.MinBackoff).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(policy.MaxBackoff).IsEqualTo(TimeSpan.FromMinutes(10));
        await Assert.That(policy.OnErrorResult).IsEqualTo(ErrorResultAction.DeadLetter);
        await Assert.That(policy.OnExhausted).IsEqualTo(ExhaustedAction.DeadLetter);
    }

    [Test]
    public async Task ResolvedChannel_declared_channel_is_returned()
    {
        var policy = new QueuePolicy { Channel = "bulk" };

        await Assert.That(policy.ResolvedChannel).IsEqualTo("bulk");
    }

    [Test]
    [Arguments(1, 10)]
    [Arguments(2, 20)]
    [Arguments(3, 40)]
    [Arguments(6, 320)]
    public async Task BackoffFor_doubles_from_min_backoff_per_attempt(int attempt, int expectedSeconds)
    {
        var policy = QueuePolicy.Default;

        var backoff = policy.BackoffFor(attempt);

        await Assert.That(backoff).IsEqualTo(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Test]
    public async Task BackoffFor_first_attempt_returns_min_backoff()
    {
        var policy = new QueuePolicy { MinBackoff = TimeSpan.FromSeconds(3) };

        var backoff = policy.BackoffFor(1);

        await Assert.That(backoff).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    [Arguments(7)]
    [Arguments(10)]
    [Arguments(1000)]
    public async Task BackoffFor_at_or_past_cap_returns_max_backoff(int attempt)
    {
        var policy = QueuePolicy.Default;

        var backoff = policy.BackoffFor(attempt);

        await Assert.That(backoff).IsEqualTo(policy.MaxBackoff);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-5)]
    public async Task BackoffFor_non_positive_attempt_clamps_to_min_backoff(int attempt)
    {
        var policy = QueuePolicy.Default;

        var backoff = policy.BackoffFor(attempt);

        await Assert.That(backoff).IsEqualTo(policy.MinBackoff);
    }

    [Test]
    public async Task BackoffFor_extreme_attempt_number_does_not_overflow()
    {
        var policy = QueuePolicy.Default;

        var backoff = policy.BackoffFor(int.MaxValue);

        await Assert.That(backoff).IsEqualTo(policy.MaxBackoff);
    }

    [Test]
    [Arguments(31)]
    [Arguments(int.MaxValue)]
    public async Task BackoffFor_large_min_backoff_at_high_attempt_caps_without_overflow(int attempt)
    {
        // 30 min * 2^30 overflows TimeSpan tick arithmetic; the cap must win before multiplying.
        var policy = new QueuePolicy { MinBackoff = TimeSpan.FromMinutes(30), MaxBackoff = TimeSpan.FromHours(1) };

        var backoff = policy.BackoffFor(attempt);

        await Assert.That(backoff).IsEqualTo(policy.MaxBackoff);
    }
}

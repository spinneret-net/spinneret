using Spinneret.Functional;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Tests;

// ---------------------------------------------------------------------------------------------
// Hand-rolled fakes for the scheduler abstractions. No mocking library is used.
// ---------------------------------------------------------------------------------------------

public sealed record TestRequest(string Name) : IRequest<Unit>;

public sealed record OtherTestRequest(int Number) : IRequest<Unit>;

public sealed record StringResponseRequest(string Name) : IRequest<string>;

public sealed class RecordingRecurringJobScheduler : IRecurringJobScheduler
{
    public List<(string Key, object Request, Schedule Schedule, CancellationToken Ct)> Registrations { get; } = [];
    public List<(string Key, CancellationToken Ct)> Unregistrations { get; } = [];

    /// <summary>Keys that fail every time — a permanently broken job.</summary>
    public HashSet<string> FailingKeys { get; } = [];

    /// <summary>Keys that fail the given number of times and then succeed — a transient outage.</summary>
    public Dictionary<string, int> FailingAttempts { get; } = [];

    /// <summary>Attempts made per key, successful or not.</summary>
    public Dictionary<string, int> Attempts { get; } = [];

    public Task RegisterAsync<TResponse>(
        string key, IRequest<TResponse> request, Schedule schedule, CancellationToken ct = default)
    {
        RecordAttempt(key, "Registration");
        Registrations.Add((key, request, schedule, ct));
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string key, CancellationToken ct = default)
    {
        RecordAttempt(key, "Unregistration");
        Unregistrations.Add((key, ct));
        return Task.CompletedTask;
    }

    private void RecordAttempt(string key, string what)
    {
        Attempts[key] = Attempts.GetValueOrDefault(key) + 1;

        if (FailingKeys.Contains(key))
            throw new InvalidOperationException($"{what} failed for '{key}'.");

        if (FailingAttempts.GetValueOrDefault(key) is var remaining and > 0)
        {
            FailingAttempts[key] = remaining - 1;
            throw new InvalidOperationException($"{what} failed for '{key}' ({remaining} left).");
        }
    }
}

public sealed record RetiredJob(string Key) : IRetiredRecurringJob;

public sealed class FakeRecurringJob(string key, Schedule schedule, IRequest<Unit> request) : IRecurringJob<Unit>
{
    public string Key => key;
    public Schedule Schedule => schedule;
    public IRequest<Unit> CreateRequest() => request;
}

/// <summary>A job whose request returns something other than <see cref="Unit"/>.</summary>
public sealed class FakeStringResponseJob(string key, Schedule schedule, IRequest<string> request)
    : IRecurringJob<string>
{
    public string Key => key;
    public Schedule Schedule => schedule;
    public IRequest<string> CreateRequest() => request;
}

public sealed class ThrowingScheduleJob(string key) : IRecurringJob<Unit>
{
    public string Key => key;
    public Schedule Schedule => throw new FormatException($"Schedule unreadable for '{key}'.");
    public IRequest<Unit> CreateRequest() => new TestRequest(key);
}

public sealed class ThrowingCreateRequestJob(string key) : IRecurringJob<Unit>
{
    public string Key => key;
    public Schedule Schedule => Schedule.Cron("* * * * *", "Europe/Stockholm");
    public IRequest<Unit> CreateRequest() => throw new InvalidOperationException($"CreateRequest failed for '{key}'.");
}

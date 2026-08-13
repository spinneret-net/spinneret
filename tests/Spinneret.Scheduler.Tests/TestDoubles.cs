using Spinneret.Mediator;

namespace Spinneret.Scheduler.Tests;

// ---------------------------------------------------------------------------------------------
// Hand-rolled fakes for the scheduler abstractions. No mocking library is used.
// ---------------------------------------------------------------------------------------------

public sealed record TestRequest(string Name) : IRequest<Unit>;

public sealed record OtherTestRequest(int Number) : IRequest<Unit>;

public sealed class RecordingRecurringJobScheduler : IRecurringJobScheduler
{
    public List<(string Key, IRequest<Unit> Request, Schedule Schedule, CancellationToken Ct)> Registrations { get; } = [];
    public List<(string Key, CancellationToken Ct)> Unregistrations { get; } = [];
    public HashSet<string> FailingKeys { get; } = [];

    public Task RegisterAsync(string key, IRequest<Unit> request, Schedule schedule, CancellationToken ct = default)
    {
        if (FailingKeys.Contains(key))
            throw new InvalidOperationException($"Registration failed for '{key}'.");

        Registrations.Add((key, request, schedule, ct));
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string key, CancellationToken ct = default)
    {
        if (FailingKeys.Contains(key))
            throw new InvalidOperationException($"Unregistration failed for '{key}'.");

        Unregistrations.Add((key, ct));
        return Task.CompletedTask;
    }
}

public sealed record RetiredJob(string Key) : IRetiredRecurringJob;

public sealed class FakeRecurringJob(string key, Schedule schedule, IRequest<Unit> request) : IRecurringJob
{
    public string Key => key;
    public Schedule Schedule => schedule;
    public IRequest<Unit> CreateRequest() => request;
}

public sealed class ThrowingScheduleJob(string key) : IRecurringJob
{
    public string Key => key;
    public Schedule Schedule => throw new FormatException($"Schedule unreadable for '{key}'.");
    public IRequest<Unit> CreateRequest() => new TestRequest(key);
}

public sealed class ThrowingCreateRequestJob(string key) : IRecurringJob
{
    public string Key => key;
    public Schedule Schedule => Schedule.Cron("* * * * *", "Europe/Stockholm");
    public IRequest<Unit> CreateRequest() => throw new InvalidOperationException($"CreateRequest failed for '{key}'.");
}

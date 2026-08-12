using Microsoft.Extensions.DependencyInjection;
using Spinneret.Mediator;

namespace Spinneret.Scheduler;

public static class StartupExtensions
{
    /// <summary>
    /// Registers a recurring job inline — sugar for implementing <see cref="IRecurringJob"/> when
    /// the job is just "enqueue this request on this schedule" and a dedicated class would be noise.
    /// The provider's installer picks it up at startup like any other <see cref="IRecurringJob"/>.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="key">Stable identifier for the job; must be unique across all recurring jobs.</param>
    /// <param name="schedule">When the job is enqueued.</param>
    /// <param name="requestFactory">Builds the request to enqueue on each run.</param>
    public static IServiceCollection AddRecurringJob(
        this IServiceCollection services, string key, Schedule schedule, Func<IRequest<Unit>> requestFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(requestFactory);

        return services.AddSingleton<IRecurringJob>(new DelegateRecurringJob(key, schedule, requestFactory));
    }
}

internal sealed class DelegateRecurringJob(string key, Schedule schedule, Func<IRequest<Unit>> requestFactory)
    : IRecurringJob
{
    public string Key => key;
    public Schedule Schedule => schedule;
    public IRequest<Unit> CreateRequest() => requestFactory();
}

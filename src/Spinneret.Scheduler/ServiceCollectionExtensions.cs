using Spinneret.Mediator;
using Spinneret.Scheduler;

// ReSharper disable once CheckNamespace — DI namespace for discoverability.
namespace Microsoft.Extensions.DependencyInjection
{
    public static class SchedulerServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the scheduler composed in <paramref name="configure"/> and installs every declared <see cref="IRecurringJob"/> at startup.
        /// </summary>
        /// <example>
        /// <code>
        /// services.AddScheduler(s => s.UseMssqlStorage(configuration).UseInHostSweeper());
        /// </code>
        /// </example>
        public static IServiceCollection AddScheduler(this IServiceCollection services, Action<SchedulerBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var builder = new SchedulerBuilder(services);
            configure(builder);
            builder.Build();
            return services;
        }

        /// <summary>
        /// Registers a recurring job inline, without implementing <see cref="IRecurringJob"/>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="key">Stable identifier, unique across all recurring jobs.</param>
        /// <param name="schedule">When the job is enqueued.</param>
        /// <param name="requestFactory">Builds the request to enqueue on each run.</param>
        public static IServiceCollection AddRecurringJob<TResponse>(
            this IServiceCollection services, string key, Schedule schedule, Func<IRequest<TResponse>> requestFactory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(schedule);
            ArgumentNullException.ThrowIfNull(requestFactory);

            return services.AddSingleton<IRecurringJob>(new DelegateRecurringJob<TResponse>(key, schedule, requestFactory));
        }

        /// <summary>
        /// Removes the stored recurring job under <paramref name="key"/> at startup; see <see cref="IRetiredRecurringJob"/>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="key">The key the retired job used to be installed under.</param>
        public static IServiceCollection RetireRecurringJob(this IServiceCollection services, string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            return services.AddSingleton<IRetiredRecurringJob>(new RetiredRecurringJob(key));
        }
    }
}

namespace Spinneret.Scheduler
{
    using Spinneret.Functional;
    using Spinneret.Mediator;

    internal sealed class DelegateRecurringJob<TResponse>(
        string key, Schedule schedule, Func<IRequest<TResponse>> requestFactory)
        : IRecurringJob<TResponse>
    {
        public string Key => key;
        public Schedule Schedule => schedule;
        public IRequest<TResponse> CreateRequest() => requestFactory();
    }

    internal sealed class RetiredRecurringJob(string key) : IRetiredRecurringJob
    {
        public string Key => key;
    }
}

using Spinneret.Mediator;
using Spinneret.Scheduler;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection
{
    public static class SchedulerServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the scheduler, composed in <paramref name="configure"/> from a storage and an
        /// optional timer — see <see cref="SchedulerBuilder"/> — plus the startup installer that
        /// asserts every declared <see cref="IRecurringJob"/> into it.
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
        /// Registers a recurring job inline — sugar for implementing <see cref="IRecurringJob"/> when
        /// the job is just "enqueue this request on this schedule" and a dedicated class would be noise.
        /// The scheduler's installer picks it up at startup like any other <see cref="IRecurringJob"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <param name="key">Stable identifier for the job; must be unique across all recurring jobs.</param>
        /// <param name="schedule">
        /// When the job is enqueued. Read it from configuration here if the cadence differs per
        /// environment — <c>Schedule.Parse(configuration["Jobs:MonthClose"]!)</c>.
        /// </param>
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
        /// Declares that a recurring job previously installed under <paramref name="key"/> is gone, so
        /// the installer removes its stored definition at startup. Deleting the job from code is not
        /// enough on its own — see <see cref="IRetiredRecurringJob"/> for why, and for how long to
        /// leave the retirement in place.
        /// </summary>
        /// <param name="services"></param>
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

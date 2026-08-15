using Spinneret.Functional;
using Spinneret.Mediator;
using Spinneret.Scheduler;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection
{
    public static class SchedulerServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the transport-independent scheduler services: the startup installer that asserts
        /// every declared <see cref="IRecurringJob"/> into the scheduler. Called by the provider
        /// packages' own registration — and called last, so a provider can order it after whatever the
        /// installer needs in place first — so applications wire up a scheduler by calling that one
        /// instead of this.
        /// </summary>
        public static IServiceCollection AddRecurringJobInstaller(this IServiceCollection services)
        {
            services.AddHostedService<RecurringJobInstaller>();
            return services;
        }

        /// <summary>
        /// Runs the registered <see cref="ISchedulerSweep"/> on a timer, dispatching jobs as they
        /// fall due. Independent of where those jobs are stored: pair it with any scheduler provider.
        /// </summary>
        /// <remarks>
        /// Opt-in per host — unlike the recurring-job installer, no provider registers this for you,
        /// because a host that declares or schedules jobs is not necessarily one that should dispatch
        /// them. Registration order does not matter. A host that scales to zero should map the HTTP
        /// trigger from <c>Spinneret.Scheduler.Http</c> instead, and drive it from an external cron.
        /// </remarks>
        public static IServiceCollection AddSchedulerSweeper(this IServiceCollection services) =>
            services.AddSchedulerSweeperCore(configure: null);

        /// <summary>
        /// Overload for hosts that set the sweep interval in code. To bind it from configuration
        /// instead, call <c>services.Configure&lt;SchedulerOptions&gt;(section)</c> — this package
        /// deliberately takes no dependency on the configuration binder.
        /// </summary>
        public static IServiceCollection AddSchedulerSweeper(
            this IServiceCollection services,
            Action<SchedulerOptions> configure) =>
            services.AddSchedulerSweeperCore(configure ?? throw new ArgumentNullException(nameof(configure)));

        private static IServiceCollection AddSchedulerSweeperCore(
            this IServiceCollection services,
            Action<SchedulerOptions>? configure)
        {
            var builder = services.AddOptions<SchedulerOptions>();
            if (configure is not null)
                builder.Configure(configure);

            builder
                .Validate(o => o.SweepInterval > TimeSpan.Zero,
                    "Scheduler:Sweeper:SweepInterval must be positive — it is the sweep cadence and "
                    + "the backoff a stalled provider relies on.")
                .ValidateOnStart();

            services.AddHostedService<SchedulerSweeperService>();
            return services;
        }

        /// <summary>
        /// Registers a recurring job inline — sugar for implementing <see cref="IRecurringJob"/> when
        /// the job is just "enqueue this request on this schedule" and a dedicated class would be noise.
        /// The provider's installer picks it up at startup like any other <see cref="IRecurringJob"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <param name="key">Stable identifier for the job; must be unique across all recurring jobs.</param>
        /// <param name="schedule">
        /// When the job is enqueued. Read it from configuration here if the cadence differs per
        /// environment — <c>Schedule.Parse(configuration["Jobs:MonthClose"]!)</c>.
        /// </param>
        /// <param name="requestFactory">Builds the request to enqueue on each run.</param>
        public static IServiceCollection AddRecurringJob(
            this IServiceCollection services, string key, Schedule schedule, Func<IRequest<Unit>> requestFactory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(schedule);
            ArgumentNullException.ThrowIfNull(requestFactory);

            return services.AddSingleton<IRecurringJob>(new DelegateRecurringJob(key, schedule, requestFactory));
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

    internal sealed class DelegateRecurringJob(string key, Schedule schedule, Func<IRequest<Unit>> requestFactory)
        : IRecurringJob
    {
        public string Key => key;
        public Schedule Schedule => schedule;
        public IRequest<Unit> CreateRequest() => requestFactory();
    }

    internal sealed class RetiredRecurringJob(string key) : IRetiredRecurringJob
    {
        public string Key => key;
    }
}

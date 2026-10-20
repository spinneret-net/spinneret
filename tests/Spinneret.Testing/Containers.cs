using Google.Api.Gax;
using Google.Cloud.Firestore;
using Testcontainers.Firestore;
using Testcontainers.MsSql;
using TUnit.Core.Interfaces;

namespace Spinneret.Testing;

/// <summary>
/// One SQL Server container for the whole test session; each test isolates itself with unique
/// table names inside the shared database.
/// </summary>
public sealed class MssqlContainerFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// One Firestore emulator for the whole test session; each test isolates itself with a unique
/// collection name, mirroring how the SQL suites isolate with unique table names.
/// </summary>
/// <remarks>
/// The emulator is reached through <c>FIRESTORE_EMULATOR_HOST</c> and
/// <see cref="EmulatorDetection.EmulatorOnly"/> rather than an explicit endpoint, because that is
/// the wiring the Firestore docs tell hosts to use — so the fixture exercises the documented path.
/// The environment variable is process-global, which is safe here: it is set once, before any
/// <see cref="FirestoreDb"/> in the test assembly is built.
/// </remarks>
public sealed class FirestoreEmulatorFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// Pinned rather than floating, the same way the SQL Server fixture pins its server image: an
    /// emulator that changes underneath the suite turns a red build into a mystery.
    /// </summary>
    private const string Image = "gcr.io/google.com/cloudsdktool/google-cloud-cli:580.0.0-emulators";

    public const string ProjectId = "spinneret-tests";

    private readonly FirestoreContainer _container = new FirestoreBuilder(Image).Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // GetEmulatorEndpoint() renders "http://host:port/"; the variable wants a bare host:port.
        var endpoint = new Uri(_container.GetEmulatorEndpoint());
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", $"{endpoint.Host}:{endpoint.Port}");
    }

    public FirestoreDb CreateDb() =>
        new FirestoreDbBuilder { ProjectId = ProjectId, EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

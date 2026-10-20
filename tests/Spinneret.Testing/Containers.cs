using Google.Api.Gax;
using Google.Cloud.Firestore;
using Testcontainers.Firestore;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;

namespace Spinneret.Testing;

public interface ISqlDatabaseFixture
{
    string ConnectionString { get; }
}

// One container per session; tests isolate with unique table names.
public sealed class MssqlContainerFixture : ISqlDatabaseFixture, IAsyncInitializer, IAsyncDisposable
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

// One container per session; tests isolate with unique table names.
// Connection limit raised: parallel suites with pooled data sources exceed the default 100.
public sealed class PostgresContainerFixture : ISqlDatabaseFixture, IAsyncInitializer, IAsyncDisposable
{
    // Pinned so a red build is never caused by an image change.
    private const string Image = "postgres:17.6-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithCommand("-c", "max_connections=1000")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

// One emulator per session; tests isolate with unique collection names.
// Wired through FIRESTORE_EMULATOR_HOST, the documented path; set once before any FirestoreDb is built.
public sealed class FirestoreEmulatorFixture : IAsyncInitializer, IAsyncDisposable
{
    // Pinned so a red build is never caused by an image change.
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

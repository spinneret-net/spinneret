using Spinneret.Sql.Conformance;

namespace Spinneret.Composition.Tests;

// The shared composition suites (tests/Spinneret.Sql.Conformance/Composition), run on every SQL dialect.

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class CloudTasksOverMssqlTests(MssqlContainerFixture database) : CloudTasksOverSqlConformance(new MssqlTestDialect(database));

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerOverCloudTasksTests(MssqlContainerFixture database) : SchedulerOverCloudTasksConformance(new MssqlTestDialect(database));

[InheritsTests]
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class CloudTasksOverPostgresTests(PostgresContainerFixture database) : CloudTasksOverSqlConformance(new PostgresTestDialect(database));

[InheritsTests]
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresSchedulerOverCloudTasksTests(PostgresContainerFixture database) : SchedulerOverCloudTasksConformance(new PostgresTestDialect(database));

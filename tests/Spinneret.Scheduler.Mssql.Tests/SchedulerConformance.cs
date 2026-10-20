using Spinneret.Sql.Conformance;

namespace Spinneret.Scheduler.Mssql.Tests;

// Binds the shared scheduler conformance suites to SQL Server.

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerTests(MssqlContainerFixture database) : SqlSchedulerConformance(new MssqlTestDialect(database));

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerSchemaInitializerTests(MssqlContainerFixture database) : SqlSchedulerSchemaConformance(new MssqlTestDialect(database));

[InheritsTests]
public sealed class MssqlSchedulerRegistrationTests() : SqlSchedulerRegistrationConformance(new MssqlTestDialect());

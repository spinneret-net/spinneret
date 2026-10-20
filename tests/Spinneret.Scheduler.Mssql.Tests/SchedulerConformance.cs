using Spinneret.Sql.Conformance;

namespace Spinneret.Scheduler.Mssql.Tests;

// The shared conformance suites (tests/Spinneret.Sql.Conformance/Scheduler), run on Mssql.
// Each class only binds a suite to this database; the scenarios live in the base class.

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerTests(MssqlContainerFixture database) : SqlSchedulerConformance(new MssqlTestDialect(database));

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlSchedulerSchemaInitializerTests(MssqlContainerFixture database) : SqlSchedulerSchemaConformance(new MssqlTestDialect(database));

[InheritsTests]
public sealed class MssqlSchedulerRegistrationTests() : SqlSchedulerRegistrationConformance(new MssqlTestDialect());

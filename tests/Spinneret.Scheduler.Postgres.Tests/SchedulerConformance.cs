using Spinneret.Sql.Conformance;

namespace Spinneret.Scheduler.Postgres.Tests;

// The shared conformance suites (tests/Spinneret.Sql.Conformance/Scheduler), run on Postgres.
// Each class only binds a suite to this database; the scenarios live in the base class.

[InheritsTests]
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresSchedulerTests(PostgresContainerFixture database) : SqlSchedulerConformance(new PostgresTestDialect(database));

[InheritsTests]
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresSchedulerSchemaInitializerTests(PostgresContainerFixture database) : SqlSchedulerSchemaConformance(new PostgresTestDialect(database));

[InheritsTests]
public sealed class PostgresSchedulerRegistrationTests() : SqlSchedulerRegistrationConformance(new PostgresTestDialect());

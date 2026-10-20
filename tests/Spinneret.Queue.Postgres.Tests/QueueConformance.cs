using Spinneret.Sql.Conformance;

namespace Spinneret.Queue.Postgres.Tests;

// The shared conformance suites (tests/Spinneret.Sql.Conformance/Queue), run on Postgres.
// Each class only binds a suite to this database; the scenarios live in the base class.

[InheritsTests]
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresQueueDeliveryTests(PostgresContainerFixture database) : SqlQueueDeliveryConformance(new PostgresTestDialect(database));

[InheritsTests]
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresDeadLetterStoreTests(PostgresContainerFixture database) : SqlDeadLetterStoreConformance(new PostgresTestDialect(database));

[InheritsTests]
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresQueueSchemaInitializerTests(PostgresContainerFixture database) : SqlQueueSchemaConformance(new PostgresTestDialect(database));

[InheritsTests]
public sealed class PostgresQueueRegistrationTests() : SqlQueueRegistrationConformance(new PostgresTestDialect());

using Spinneret.Sql.Conformance;

namespace Spinneret.Queue.Postgres.Tests;

// Binds the shared suites in tests/Spinneret.Sql.Conformance/Queue to PostgreSQL.

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

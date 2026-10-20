using Spinneret.Sql.Conformance;

namespace Spinneret.Queue.Mssql.Tests;

// Binds the shared suites in tests/Spinneret.Sql.Conformance/Queue to SQL Server.

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlQueueDeliveryTests(MssqlContainerFixture database) : SqlQueueDeliveryConformance(new MssqlTestDialect(database));

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlDeadLetterStoreTests(MssqlContainerFixture database) : SqlDeadLetterStoreConformance(new MssqlTestDialect(database));

[InheritsTests]
[ClassDataSource<MssqlContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class MssqlQueueSchemaInitializerTests(MssqlContainerFixture database) : SqlQueueSchemaConformance(new MssqlTestDialect(database));

[InheritsTests]
public sealed class MssqlQueueRegistrationTests() : SqlQueueRegistrationConformance(new MssqlTestDialect());

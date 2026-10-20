using Spinneret.Sql.Conformance;

namespace Spinneret.Queue.Mssql.Tests;

// The shared conformance suites (tests/Spinneret.Sql.Conformance/Queue), run on Mssql.
// Each class only binds a suite to this database; the scenarios live in the base class.

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

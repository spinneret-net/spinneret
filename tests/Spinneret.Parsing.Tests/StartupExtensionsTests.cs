using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Parsing.Tests;

public class StartupExtensionsTests
{
    private class FakeServiceCollection : List<ServiceDescriptor>, IServiceCollection;

    [Test]
    public async Task AddModelParser_registers_singleton_model_parser_instance()
    {
        var services = new FakeServiceCollection();

        services.AddModelParser("missing");

        var descriptor = Expect.Single(services);

        await Assert.That(descriptor.ServiceType).IsEqualTo(typeof(IModelParser<string>));
        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(descriptor.ImplementationInstance).IsNotNull().And.IsTypeOf<ModelParser<string>>();
    }

    [Test]
    public async Task AddModelParser_returns_the_same_service_collection_for_chaining()
    {
        var services = new FakeServiceCollection();

        var returned = services.AddModelParser("missing");

        await Assert.That(returned).IsSameReferenceAs(services);
    }

    [Test]
    public async Task AddModelParser_registered_parser_uses_the_given_missing_property_error()
    {
        var services = new FakeServiceCollection();

        services.AddModelParser("configured-error");

        var registeredParser = (IModelParser<string>)Expect.Single(services).ImplementationInstance!;
        var parseRes = registeredParser.Parse(
            new TestObject { StringProperty = null! },
            parser => parser.Require(x => x.StringProperty));
        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error).IsEqualTo("configured-error");
    }
}

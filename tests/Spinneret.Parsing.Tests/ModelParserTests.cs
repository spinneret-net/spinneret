namespace Spinneret.Parsing.Tests;

public class ModelParserTests
{
    [Test]
    public async Task Parse_with_no_errors_returns_ok_with_parsed_value()
    {
        var modelParser = new ModelParser<string>("missing");
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        var parseRes = modelParser.Parse(sut, parser => parser.Require(x => x.StringProperty));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task Parse_with_errors_returns_error_and_discards_parsed_value()
    {
        var modelParser = new ModelParser<string>("missing");
        var sut = new TestObject
        {
            StringProperty = null!
        };

        var parseRes = modelParser.Parse(sut, parser => parser.Require(x => x.StringProperty));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error).IsEqualTo("missing");
    }

    [Test]
    public async Task Parse_uses_configured_missing_property_error_for_missing_values()
    {
        var modelParser = new ModelParser<int>(404);
        var sut = new TestObject
        {
            StringProperty = null!
        };

        var parseRes = modelParser.Parse(sut, parser => parser.Require(x => x.StringProperty));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.Error).IsEqualTo(404);
    }

    [Test]
    public async Task Parse_failed_member_yields_default_value_inside_parse_function()
    {
        var modelParser = new ModelParser<string>("missing");
        var sut = new TestObject
        {
            NullableStructProperty = null
        };

        var observedValue = -1;

        modelParser.Parse(sut, parser =>
        {
            observedValue = parser.Require(x => x.NullableStructProperty);
            return 0;
        });

        await Assert.That(observedValue).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_reusing_the_same_model_parser_does_not_leak_errors_between_calls()
    {
        var modelParser = new ModelParser<string>("missing");

        var failingRes = modelParser.Parse(
            new TestObject { StringProperty = null! },
            parser => parser.Require(x => x.StringProperty));
        var succeedingRes = modelParser.Parse(
            new TestObject { StringProperty = "test" },
            parser => parser.Require(x => x.StringProperty));

        Expect.SingleError(failingRes);
        var res = Expect.Ok(succeedingRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task Parse_via_interface_behaves_like_concrete_parser()
    {
        IModelParser<string> modelParser = new ModelParser<string>("missing");
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        var parseRes = modelParser.Parse(sut, parser => parser.Require(x => x.StringProperty));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }
}

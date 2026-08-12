using Spinneret.Functional;

namespace Spinneret.Parsing.Tests;

public class PropertyParserCoreTests
{
    private static readonly ModelParser<string> ModelParser = new("Required Property");

    [Test]
    public async Task Parse_struct_property_returns_parsed_value()
    {
        var sut = new TestObject
        {
            StructProperty = 1
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Parse(x => x.StructProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_with_failing_parser_records_error()
    {
        var sut = new TestObject
        {
            StructProperty = 1
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Parse(
            x => x.StructProperty,
            x => TestParseResult<int>.Error($"{x}_error")));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StructProperty");
        await Assert.That(error.Error).IsEqualTo("1_error");
    }

    [Test]
    public async Task Parse_string_property_is_trimmed_before_parsing()
    {
        var sut = new TestObject
        {
            StringProperty = "  test  "
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Parse(x => x.StringProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task Parse_null_values_are_passed_to_parser_without_null_handling()
    {
        var sut = new TestObject
        {
            StringProperty = null!,
            StructProperty = 1,
            NullableStructProperty = 2,
            ManyProperty = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.Parse(x => x.StringProperty, x => TestParseResult.Ok($"{x}_parsed")),
            B = parser.Parse(x => x.StructProperty, x => TestParseResult.Ok($"{x}_parsed")),
            C = parser.Parse(x => x.NullableStructProperty, x => TestParseResult.Ok($"{x}_parsed")),
            D = parser.Parse(x => x.ManyProperty, x => TestParseResult.Ok($"{x}_parsed"))
        });

        var res = Expect.Ok(parseRes);

        await Assert.That(res.A).IsEqualTo("_parsed");
        await Assert.That(res.B).IsEqualTo("1_parsed");
        await Assert.That(res.C).IsEqualTo("2_parsed");
        await Assert.That(res.D).IsEqualTo("_parsed");
    }

    [Test]
    public async Task Parse_null_nested_class_is_passed_to_parser_without_null_handling()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.Parse(x => x.OnlyChildClass, x => TestParseResult.Ok($"{x}_parsed"))
        });

        var res = Expect.Ok(parseRes);

        await Assert.That(res.A).IsEqualTo("_parsed");
    }

    [Test]
    public async Task Parse_multiple_properties_returns_all_parsed_values()
    {
        var sut = new TestObject
        {
            StringProperty = "test",
            StructProperty = 1,
            NullableStructProperty = 2,
            ManyProperty = ["A", "B", "C"]
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.Require(x => x.StringProperty, x => TestParseResult.Ok($"{x}_parsed")),
            B = parser.Parse(x => x.StructProperty, x => TestParseResult.Ok($"{x}_parsed")),
            C = parser.Optional(x => x.NullableStructProperty, x => TestParseResult.Ok($"{x}_parsed")),
            D = parser.Many(x => x.ManyProperty, x => TestParseResult.Ok($"{x}_parsed"))
        });

        var res = Expect.Ok(parseRes);

        await Assert.That(res.A).IsEqualTo("test_parsed");
        await Assert.That(res.B).IsEqualTo("1_parsed");
        await Assert.That(res.C).IsEqualTo("2_parsed");
        await Assert.That(res.D).IsEquivalentTo(["A_parsed", "B_parsed", "C_parsed"]);
    }

    [Test]
    public async Task Parse_multiple_properties_with_single_failure_returns_only_that_error()
    {
        var sut = new TestObject
        {
            StringProperty = "test",
            StructProperty = 1,
            NullableStructProperty = 2,
            ManyProperty = ["A", "B", "C"]
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.Require(x => x.StringProperty, x => TestParseResult.Ok($"{x}_parsed")),
            B = parser.Parse(x => x.StructProperty, x => TestParseResult.Ok($"{x}_parsed")),
            C = parser.Optional(x => x.NullableStructProperty, x => TestParseResult<string>.Error($"{x}_error")),
            D = parser.Many(x => x.ManyProperty, x => TestParseResult.Ok($"{x}_parsed"))
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("NullableStructProperty");
        await Assert.That(error.Error).IsEqualTo("2_error");
    }

    [Test]
    public async Task Model_property_exposes_the_model_being_parsed()
    {
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        TestObject? observedModel = null;

        ModelParser.Parse(sut, parser =>
        {
            observedModel = parser.Model;
            return 0;
        });

        await Assert.That(observedModel).IsSameReferenceAs(sut);
    }

    [Test]
    public async Task WithModel_parses_the_mapped_model()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser
            .WithModel(x => x.OnlyChildClass)
            .Require(x => x.StringProperty));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task WithModel_errors_flow_to_outer_result_without_prefix()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = null!
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser
            .WithModel(x => x.OnlyChildClass)
            .Require(x => x.StringProperty));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Either_extension_parses_first_alternative_with_first_parser()
    {
        var sut = new Either<ChildClass, int>(new ChildClass
        {
            StringProperty = "test"
        });

        var parseRes = ModelParser.Parse(sut, parser => parser.Either(
            (_, p) => new ChildClass { StringProperty = p.Require(x => x.StringProperty) },
            (x, _) => x));

        var res = Expect.Ok(parseRes);

        var parsedChild = res.Reduce(
            child => child,
            _ => throw new InvalidOperationException("Expected first alternative."));

        await Assert.That(parsedChild.StringProperty).IsEqualTo("test");
    }

    [Test]
    public async Task Either_extension_parses_second_alternative_with_second_parser()
    {
        var sut = new Either<ChildClass, int>(41);

        var parseRes = ModelParser.Parse(sut, parser => parser.Either(
            (x, _) => x,
            (x, _) => x + 1));

        var res = Expect.Ok(parseRes);

        var parsedValue = res.Reduce(
            _ => throw new InvalidOperationException("Expected second alternative."),
            value => value);

        await Assert.That(parsedValue).IsEqualTo(42);
    }

    [Test]
    public async Task Either_extension_records_errors_from_the_selected_alternative()
    {
        var sut = new Either<ChildClass, int>(new ChildClass
        {
            StringProperty = null!
        });

        var parseRes = ModelParser.Parse(sut, parser => parser.Either(
            (_, p) => new ChildClass { StringProperty = p.Require(x => x.StringProperty) },
            (x, _) => x));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }
}

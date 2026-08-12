using Spinneret.Functional;

namespace Spinneret.Parsing.Tests;

public class PropertyParserOptionalTests
{
    private static readonly ModelParser<string> ModelParser = new("Required Property");

    [Test]
    public async Task Optional_string_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Optional(
            x => x.StringProperty,
            _ => TestParseResult.Ok("parsed_test")));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("parsed_test");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Optional_string_that_is_missing_returns_null_without_error(string? value)
    {
        var sut = new TestObject
        {
            StringProperty = value!
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Optional(x => x.StringProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsNull();
    }

    [Test]
    public async Task Optional_nullable_string_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            NullableStringProperty = "test"
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Optional(
            x => x.NullableStringProperty,
            _ => TestParseResult.Ok("parsed_test")));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("parsed_test");
    }

    [Test]
    public async Task Optional_nullable_string_that_is_null_returns_null_without_error()
    {
        var sut = new TestObject
        {
            NullableStringProperty = null
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Optional(x => x.NullableStringProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsNull();
    }

    [Test]
    public async Task Optional_class_from_struct_member_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            NullableStructProperty = 1
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Optional(
            x => x.NullableStructProperty,
            _ => TestParseResult.Ok("parsed_test")));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("parsed_test");
    }

    [Test]
    public async Task Optional_class_from_struct_member_that_is_null_returns_null_without_error()
    {
        var sut = new TestObject
        {
            NullableStructProperty = null
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Optional(
            x => x.NullableStructProperty,
            _ => TestParseResult.Ok("parsed_test")));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsNull();
    }

    [Test]
    public async Task Optional_with_failing_parser_records_parser_error()
    {
        var sut = new TestObject
        {
            NullableStructProperty = 2
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Optional(
            x => x.NullableStructProperty,
            x => TestParseResult<string>.Error($"{x}_error")));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("NullableStructProperty");
        await Assert.That(error.Error).IsEqualTo("2_error");
    }

    [Test]
    public async Task OptionalStruct_from_struct_member_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            NullableStructProperty = 1
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.OptionalStruct(
            x => x.NullableStructProperty,
            TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo(1);
    }

    [Test]
    public async Task OptionalStruct_from_struct_member_that_is_null_returns_null_without_error()
    {
        var sut = new TestObject
        {
            NullableStructProperty = null
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.OptionalStruct(x => x.NullableStructProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsNull();
    }

    [Test]
    public async Task OptionalStruct_from_class_member_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.OptionalStruct(
            x => x.StringProperty,
            _ => TestParseResult.Ok(1)));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo(1);
    }

    [Test]
    public async Task OptionalStruct_from_class_member_that_is_null_returns_null_without_error()
    {
        var sut = new TestObject
        {
            StringProperty = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.OptionalStruct(
            x => x.StringProperty,
            _ => TestParseResult.Ok(1)));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task OptionalStruct_from_class_member_that_is_empty_or_whitespace_returns_null_without_error(string value)
    {
        // Consistent with Optional: empty/whitespace strings are treated as missing.
        var sut = new TestObject
        {
            StringProperty = value
        };

        var parserInvoked = false;

        var parseRes = ModelParser.Parse(sut, parser => parser.OptionalStruct(
            x => x.StringProperty,
            _ =>
            {
                parserInvoked = true;
                return TestParseResult.Ok(1);
            }));

        var res = Expect.Ok(parseRes);

        await Assert.That(parserInvoked).IsFalse();
        await Assert.That(res).IsNull();
    }

    [Test]
    public async Task OptionalStruct_with_failing_parser_records_parser_error()
    {
        var sut = new TestObject
        {
            NullableStructProperty = 3
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.OptionalStruct(
            x => x.NullableStructProperty,
            x => Result<int, string>.Error($"{x}_error")));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("NullableStructProperty");
        await Assert.That(error.Error).IsEqualTo("3_error");
    }
}

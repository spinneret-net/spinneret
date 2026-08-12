using Spinneret.Functional;

namespace Spinneret.Parsing.Tests;

public class PropertyParserRequireTests
{
    private static readonly ModelParser<string> ModelParser = new("Required Property");

    [Test]
    public async Task Require_string_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.StringProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task Require_string_with_surrounding_whitespace_passes_trimmed_value_to_parser()
    {
        var sut = new TestObject
        {
            StringProperty = "  test  "
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.StringProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Require_string_that_is_missing_records_required_error(string? value)
    {
        var sut = new TestObject
        {
            StringProperty = value!
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.StringProperty, TestParseResult.Ok));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Require_nullable_string_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            NullableStringProperty = "test"
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.NullableStringProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Require_nullable_string_that_is_missing_records_required_error(string? value)
    {
        var sut = new TestObject
        {
            NullableStringProperty = value
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.NullableStringProperty, TestParseResult.Ok));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("NullableStringProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Require_without_parser_on_class_member_returns_value()
    {
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.StringProperty));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task Require_without_parser_on_null_class_member_records_required_error()
    {
        var sut = new TestObject
        {
            StringProperty = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.StringProperty));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Require_without_parser_on_nullable_struct_member_returns_value()
    {
        var sut = new TestObject
        {
            NullableStructProperty = 7
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.NullableStructProperty));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo(7);
    }

    [Test]
    public async Task Require_without_parser_on_null_struct_member_records_required_error()
    {
        var sut = new TestObject
        {
            NullableStructProperty = null
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.NullableStructProperty));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("NullableStructProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Require_nullable_struct_with_value_returns_parsed_value()
    {
        var sut = new TestObject
        {
            NullableStructProperty = 1
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(
            x => x.NullableStructProperty,
            TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo(1);
    }

    [Test]
    public async Task Require_nullable_struct_that_is_null_records_required_error()
    {
        var sut = new TestObject
        {
            NullableStructProperty = null
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(
            x => x.NullableStructProperty,
            TestParseResult.Ok));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("NullableStructProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Require_with_failing_parser_records_parser_error()
    {
        var sut = new TestObject
        {
            StringProperty = "test"
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(
            x => x.StringProperty,
            x => TestParseResult<string>.Error($"{x}_error")));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task Require_multiple_missing_properties_accumulates_all_errors_in_order()
    {
        var sut = new TestObject
        {
            StringProperty = null!,
            NullableStructProperty = null
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.Require(x => x.StringProperty),
            B = parser.Require(x => x.NullableStructProperty)
        });

        var errors = Expect.Error(parseRes).ToList();

        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(errors[0].PropertyName).IsEqualTo("StringProperty");
        await Assert.That(errors[0].Error).IsEqualTo("Required Property");
        await Assert.That(errors[1].PropertyName).IsEqualTo("NullableStructProperty");
        await Assert.That(errors[1].Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Require_multi_segment_expression_uses_dotted_property_path()
    {
        var sut = new DeepNestedTestObject
        {
            GroupEditor = new GroupEditorObject
            {
                Label = null!
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Require(x => x.GroupEditor.Label));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("GroupEditor.Label");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }
}

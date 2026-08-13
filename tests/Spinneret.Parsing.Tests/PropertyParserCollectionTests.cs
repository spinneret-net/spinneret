namespace Spinneret.Parsing.Tests;

public class PropertyParserCollectionTests
{
    private static readonly ModelParser<string> ModelParser = new("Required Property");

    [Test]
    public async Task Many_with_valid_items_returns_all_parsed_values_in_order()
    {
        var sut = new TestObject
        {
            ManyProperty = ["A", "B", "C"]
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Many(x => x.ManyProperty, TestParseResult.Ok));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEquivalentTo(["A", "B", "C"]);
    }

    [Test]
    public async Task Many_with_failing_item_records_indexed_error_and_returns_empty()
    {
        var sut = new TestObject
        {
            ManyProperty = ["A", "B", "C"]
        };

        var parseRes = ModelParser.Parse(sut, parser =>
        {
            var res = parser.Many(
                x => x.ManyProperty,
                x => x == "C" ? TestParseResult<string>.Error("some_error") : TestParseResult.Ok(x)).ToList();

            return res.Count;
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(Expect.Error(parseRes).Count()).IsEqualTo(1);
        await Assert.That(error.PropertyName).IsEqualTo("ManyProperty[2]");
        await Assert.That(error.Error).IsEqualTo("some_error");
    }

    [Test]
    public async Task Many_with_failing_item_returns_the_successfully_parsed_items()
    {
        var sut = new TestObject
        {
            ManyProperty = ["A", "B", "C"]
        };

        List<string> items = [];

        ModelParser.Parse(sut, parser =>
        {
            items = parser.Many(
                x => x.ManyProperty,
                x => x == "C" ? TestParseResult<string>.Error("some_error") : TestParseResult.Ok(x)).ToList();

            return 0;
        });

        await Assert.That(items).IsEquivalentTo(["A", "B"]);
    }

    [Test]
    public async Task Many_with_multiple_failing_items_accumulates_all_indexed_errors()
    {
        var sut = new TestObject
        {
            ManyProperty = ["A", "bad1", "bad2"]
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Many(
            x => x.ManyProperty,
            x => x.StartsWith("bad") ? TestParseResult<string>.Error($"{x}_error") : TestParseResult.Ok(x)).ToList());

        var errors = Expect.Error(parseRes).ToList();

        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(errors[0].PropertyName).IsEqualTo("ManyProperty[1]");
        await Assert.That(errors[0].Error).IsEqualTo("bad1_error");
        await Assert.That(errors[1].PropertyName).IsEqualTo("ManyProperty[2]");
        await Assert.That(errors[1].Error).IsEqualTo("bad2_error");
    }

    [Test]
    public async Task Many_with_null_and_failing_items_accumulates_all_indexed_errors_and_returns_valid_items()
    {
        var sut = new TestObject
        {
            ManyProperty = ["A", null!, "bad"]
        };

        var itemCount = -1;

        var parseRes = ModelParser.Parse(sut, parser =>
        {
            var res = parser.Many(
                x => x.ManyProperty,
                x => x == "bad" ? TestParseResult<string>.Error($"{x}_error") : TestParseResult.Ok(x)).ToList();
            itemCount = res.Count;
            return res;
        });

        var errors = Expect.Error(parseRes).ToList();

        await Assert.That(itemCount).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(errors[0].PropertyName).IsEqualTo("ManyProperty[1]");
        await Assert.That(errors[0].Error).IsEqualTo("Required Property");
        await Assert.That(errors[1].PropertyName).IsEqualTo("ManyProperty[2]");
        await Assert.That(errors[1].Error).IsEqualTo("bad_error");
    }

    [Test]
    public async Task Many_with_null_collection_records_required_error_and_returns_empty()
    {
        var sut = new TestObject
        {
            ManyProperty = null!
        };

        var itemCount = -1;

        var parseRes = ModelParser.Parse(sut, parser =>
        {
            var res = parser.Many(x => x.ManyProperty, TestParseResult.Ok).ToList();
            itemCount = res.Count;
            return res;
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(itemCount).IsEqualTo(0);
        await Assert.That(error.PropertyName).IsEqualTo("ManyProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Many_with_null_item_records_indexed_required_error_and_returns_valid_items()
    {
        var sut = new TestObject
        {
            ManyProperty = ["A", null!, "C"]
        };

        var itemCount = -1;

        var parseRes = ModelParser.Parse(sut, parser =>
        {
            var res = parser.Many(x => x.ManyProperty, TestParseResult.Ok).ToList();
            itemCount = res.Count;
            return res;
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(itemCount).IsEqualTo(2);
        await Assert.That(error.PropertyName).IsEqualTo("ManyProperty[1]");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task NestMany_with_valid_items_returns_all_parsed_values()
    {
        var sut = new NestedTestObject
        {
            ClassChildren =
            [
                new ChildClass { StringProperty = "one" },
                new ChildClass { StringProperty = "two" }
            ]
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.NestMany(
            x => x.ClassChildren,
            nestedParser => nestedParser.Require(x => x.StringProperty)).ToList());

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEquivalentTo(["one", "two"]);
    }

    [Test]
    public async Task NestMany_with_failing_item_records_error_with_indexed_path()
    {
        var sut = new NestedTestObject
        {
            ClassChildren =
            [
                new ChildClass { StringProperty = "test" }
            ]
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            ClassChildren = parser.NestMany(x => x.ClassChildren, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("ClassChildren[0].StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task NestMany_with_multiple_failing_items_accumulates_all_indexed_errors()
    {
        var sut = new NestedTestObject
        {
            ClassChildren =
            [
                new ChildClass { StringProperty = "one" },
                new ChildClass { StringProperty = "two" }
            ]
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.NestMany(
            x => x.ClassChildren,
            nestedParser => nestedParser.Require(
                x => x.StringProperty,
                x => TestParseResult<string>.Error($"{x}_error"))).ToList());

        var errors = Expect.Error(parseRes).ToList();

        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(errors[0].PropertyName).IsEqualTo("ClassChildren[0].StringProperty");
        await Assert.That(errors[0].Error).IsEqualTo("one_error");
        await Assert.That(errors[1].PropertyName).IsEqualTo("ClassChildren[1].StringProperty");
        await Assert.That(errors[1].Error).IsEqualTo("two_error");
    }

    [Test]
    public async Task NestMany_with_null_collection_records_required_error_and_returns_empty()
    {
        var sut = new NestedTestObject
        {
            ClassChildren = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            ClassChildren = parser.NestMany(x => x.ClassChildren, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("ClassChildren");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task NestMany_with_null_item_records_indexed_required_error_and_returns_valid_items()
    {
        var sut = new NestedTestObject
        {
            ClassChildren =
            [
                new ChildClass { StringProperty = "one" },
                null!
            ]
        };

        var itemCount = -1;

        var parseRes = ModelParser.Parse(sut, parser =>
        {
            var res = parser.NestMany(
                x => x.ClassChildren,
                nestedParser => nestedParser.Require(x => x.StringProperty)).ToList();
            itemCount = res.Count;
            return res;
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(itemCount).IsEqualTo(1);
        await Assert.That(error.PropertyName).IsEqualTo("ClassChildren[1]");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task NestMany_on_named_field_list_uses_field_path_in_error()
    {
        var sut = new GroupWithFieldsTestObject
        {
            Fields =
            [
                new ChildClass { StringProperty = "invalid" }
            ]
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.NestMany(
            x => x.Fields,
            nestedParser => new
            {
                A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
            }).ToList());

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("Fields[0].StringProperty");
        await Assert.That(error.Error).IsEqualTo("invalid_error");
    }
}

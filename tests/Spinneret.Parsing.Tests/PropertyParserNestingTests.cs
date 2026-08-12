namespace Spinneret.Parsing.Tests;

public class PropertyParserNestingTests
{
    private static readonly ModelParser<string> ModelParser = new("Required Property");

    [Test]
    public async Task Nest_parses_nested_model_and_prefixes_error_paths()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Nest(
            x => x.OnlyChildClass,
            nestedParser => nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OnlyChildClass.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task Nest_with_valid_nested_model_returns_parsed_value()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Nest(
            x => x.OnlyChildClass,
            nestedParser => nestedParser.Require(x => x.StringProperty)));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task Nest_with_multi_segment_expression_uses_full_path_as_prefix()
    {
        var sut = new DeepNestedTestObject
        {
            GroupEditor = new GroupEditorObject
            {
                Label = new ChildClass
                {
                    StringProperty = "test"
                }
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Nest(
            x => x.GroupEditor.Label,
            nestedParser => new
            {
                A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
            }));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("GroupEditor.Label.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task Nest_within_nest_chains_prefixes_in_error_path()
    {
        var sut = new DeepNestedTestObject
        {
            GroupEditor = new GroupEditorObject
            {
                Label = new ChildClass
                {
                    StringProperty = null!
                }
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Nest(
            x => x.GroupEditor,
            groupParser => groupParser.Nest(
                x => x.Label,
                labelParser => labelParser.Require(x => x.StringProperty))));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("GroupEditor.Label.StringProperty");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Nest_with_null_nested_model_records_required_error_without_invoking_nested_parser()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = null!
        };

        var nestedParserInvoked = false;

        var parseRes = ModelParser.Parse(sut, parser => parser.Nest(
            x => x.OnlyChildClass,
            nestedParser =>
            {
                nestedParserInvoked = true;
                return nestedParser.Require(x => x.StringProperty);
            }));

        var error = Expect.SingleError(parseRes);

        await Assert.That(nestedParserInvoked).IsFalse();
        await Assert.That(error.PropertyName).IsEqualTo("OnlyChildClass");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task Nest_with_multi_segment_expression_and_null_member_records_full_path_required_error()
    {
        var sut = new DeepNestedTestObject
        {
            GroupEditor = new GroupEditorObject
            {
                Label = null!
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.Nest(
            x => x.GroupEditor.Label,
            nestedParser => nestedParser.Require(x => x.StringProperty)));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("GroupEditor.Label");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task NestRequired_with_valid_nested_model_returns_parsed_value()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => parser.NestRequired(
            x => x.OnlyChildClass,
            nestedParser => nestedParser.Require(x => x.StringProperty)));

        var res = Expect.Ok(parseRes);

        await Assert.That(res).IsEqualTo("test");
    }

    [Test]
    public async Task NestRequired_with_failing_nested_parser_records_prefixed_error()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.NestRequired(x => x.OnlyChildClass, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OnlyChildClass.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task NestRequired_with_null_nested_model_records_required_error()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.NestRequired(x => x.OnlyChildClass, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OnlyChildClass");
        await Assert.That(error.Error).IsEqualTo("Required Property");
    }

    [Test]
    public async Task NestRequired_on_struct_member_prefixes_nested_error_path()
    {
        var sut = new NestedTestObject
        {
            OnlyChildStruct = new ChildStruct
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => new NestedTestObject
        {
            OnlyChildStruct = parser.NestRequired(x => x.OnlyChildStruct, nestedParser =>
                new ChildStruct
                {
                    StringProperty = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OnlyChildStruct.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task NestOptional_class_with_failing_nested_parser_records_prefixed_error()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.NestOptional(x => x.OnlyChildClass, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OnlyChildClass.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task NestOptional_class_that_is_null_returns_null_without_error()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.NestOptional(x => x.OnlyChildClass, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var res = Expect.Ok(parseRes);

        await Assert.That(res.A).IsNull();
    }

    [Test]
    public async Task NestOptional_nullable_struct_with_failing_nested_parser_records_prefixed_error()
    {
        var sut = new NestedTestObject
        {
            OptionalChildStruct = new ChildStruct
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.NestOptional(x => x.OptionalChildStruct, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OptionalChildStruct.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task NestOptional_nullable_struct_that_is_null_returns_null_without_error()
    {
        var sut = new NestedTestObject
        {
            OptionalChildStruct = null
        };

        var parseRes = ModelParser.Parse(sut, parser => new
        {
            A = parser.NestOptional(x => x.OptionalChildStruct, nestedParser =>
                new
                {
                    A = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var res = Expect.Ok(parseRes);

        await Assert.That(res.A).IsNull();
    }

    [Test]
    public async Task NestOptionalStruct_from_nullable_struct_with_failing_nested_parser_records_prefixed_error()
    {
        var sut = new NestedTestObject
        {
            OptionalChildStruct = new ChildStruct
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => new NestedTestObject
        {
            OptionalChildStruct = parser.NestOptionalStruct(x => x.OptionalChildStruct, nestedParser =>
                new ChildStruct
                {
                    StringProperty = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OptionalChildStruct.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task NestOptionalStruct_from_nullable_struct_that_is_null_returns_null_without_error()
    {
        var sut = new NestedTestObject
        {
            OptionalChildStruct = null
        };

        var parseRes = ModelParser.Parse(sut, parser => new NestedTestObject
        {
            OptionalChildStruct = parser.NestOptionalStruct(x => x.OptionalChildStruct, nestedParser =>
                new ChildStruct
                {
                    StringProperty = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var res = Expect.Ok(parseRes);

        await Assert.That(res.OptionalChildStruct).IsNull();
    }

    [Test]
    public async Task NestOptionalStruct_from_class_with_failing_nested_parser_records_prefixed_error()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = new ChildClass
            {
                StringProperty = "test"
            }
        };

        var parseRes = ModelParser.Parse(sut, parser => new NestedTestObject
        {
            OptionalChildStruct = parser.NestOptionalStruct(x => x.OnlyChildClass, nestedParser =>
                new ChildStruct
                {
                    StringProperty = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("OnlyChildClass.StringProperty");
        await Assert.That(error.Error).IsEqualTo("test_error");
    }

    [Test]
    public async Task NestOptionalStruct_from_class_that_is_null_returns_null_without_error()
    {
        var sut = new NestedTestObject
        {
            OnlyChildClass = null!
        };

        var parseRes = ModelParser.Parse(sut, parser => new NestedTestObject
        {
            OptionalChildStruct = parser.NestOptionalStruct(x => x.OnlyChildClass, nestedParser =>
                new ChildStruct
                {
                    StringProperty = nestedParser.Require(x => x.StringProperty, x => TestParseResult<string>.Error($"{x}_error"))
                }
            )
        });

        var res = Expect.Ok(parseRes);

        await Assert.That(res.OptionalChildStruct).IsNull();
    }
}

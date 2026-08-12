using System.Linq.Expressions;

namespace Spinneret.Parsing.Tests;

public class ExpressionPropertyPathTests
{
    private class PathModel
    {
        public string Name { get; init; } = "";
        public int Number { get; init; }
        public PathChild Child { get; init; } = new();
        public string Field = "";
        public static string StaticName { get; set; } = "";
    }

    private class PathChild
    {
        public string Name { get; init; } = "";
        public PathChild? Inner { get; init; }
    }

    [Test]
    public async Task GetDottedPath_single_property_returns_property_name()
    {
        Expression<Func<PathModel, string>> expression = x => x.Name;

        var path = ExpressionPropertyPath.GetDottedPath(expression);

        await Assert.That(path).IsEqualTo("Name");
    }

    [Test]
    public async Task GetDottedPath_nested_properties_returns_dotted_path()
    {
        Expression<Func<PathModel, string>> expression = x => x.Child.Name;

        var path = ExpressionPropertyPath.GetDottedPath(expression);

        await Assert.That(path).IsEqualTo("Child.Name");
    }

    [Test]
    public async Task GetDottedPath_deeply_nested_properties_returns_full_dotted_path()
    {
        Expression<Func<PathModel, string>> expression = x => x.Child.Inner!.Name;

        var path = ExpressionPropertyPath.GetDottedPath(expression);

        await Assert.That(path).IsEqualTo("Child.Inner.Name");
    }

    [Test]
    public async Task GetDottedPath_field_access_returns_field_name()
    {
        Expression<Func<PathModel, string>> expression = x => x.Field;

        var path = ExpressionPropertyPath.GetDottedPath(expression);

        await Assert.That(path).IsEqualTo("Field");
    }

    [Test]
    public async Task GetDottedPath_strips_boxing_conversion()
    {
        Expression<Func<PathModel, object>> expression = x => x.Number;

        var path = ExpressionPropertyPath.GetDottedPath(expression);

        await Assert.That(path).IsEqualTo("Number");
    }

    [Test]
    public async Task GetDottedPath_method_call_throws_argument_exception()
    {
        Expression<Func<PathModel, string>> expression = x => x.ToString()!;

        var exception = Assert.Throws<ArgumentException>(() => ExpressionPropertyPath.GetDottedPath(expression));

        await Assert.That(exception.ParamName).IsEqualTo("expression");
    }

    [Test]
    public async Task GetDottedPath_parameter_only_expression_throws_argument_exception()
    {
        Expression<Func<PathModel, PathModel>> expression = x => x;

        var exception = Assert.Throws<ArgumentException>(() => ExpressionPropertyPath.GetDottedPath(expression));

        await Assert.That(exception.ParamName).IsEqualTo("expression");
    }

    [Test]
    public async Task TryGetDottedPath_valid_expression_returns_true_with_path()
    {
        Expression<Func<PathModel, string>> expression = x => x.Child.Name;

        var success = ExpressionPropertyPath.TryGetDottedPath(expression, out var path);

        await Assert.That(success).IsTrue();
        await Assert.That(path).IsEqualTo("Child.Name");
    }

    [Test]
    public async Task TryGetDottedPath_method_call_returns_false_with_null_path()
    {
        Expression<Func<PathModel, string>> expression = x => x.ToString()!;

        var success = ExpressionPropertyPath.TryGetDottedPath(expression, out var path);

        await Assert.That(success).IsFalse();
        await Assert.That(path).IsNull();
    }

    [Test]
    public async Task TryGetDottedPath_captured_variable_member_returns_false()
    {
        var captured = new PathModel { Name = "captured" };
        Expression<Func<PathModel, string>> expression = _ => captured.Name;

        var success = ExpressionPropertyPath.TryGetDottedPath(expression, out var path);

        await Assert.That(success).IsFalse();
        await Assert.That(path).IsNull();
    }

    [Test]
    public async Task TryGetDottedPath_static_member_returns_false()
    {
        Expression<Func<PathModel, string>> expression = _ => PathModel.StaticName;

        var success = ExpressionPropertyPath.TryGetDottedPath(expression, out var path);

        await Assert.That(success).IsFalse();
        await Assert.That(path).IsNull();
    }

    [Test]
    public async Task TryGetDottedPath_constant_expression_returns_false()
    {
        Expression<Func<PathModel, string>> expression = _ => "constant";

        var success = ExpressionPropertyPath.TryGetDottedPath(expression, out var path);

        await Assert.That(success).IsFalse();
        await Assert.That(path).IsNull();
    }
}

using System.Linq.Expressions;

namespace Spinneret.Parsing;

public static class ExpressionPropertyPath
{
    public static string GetDottedPath(LambdaExpression expression)
    {
        if (!TryGetDottedPath(expression, out var path))
        {
            throw new ArgumentException("Expression is not a field or property.", nameof(expression));
        }

        return path!;
    }

    public static bool TryGetDottedPath(LambdaExpression expression, out string? path)
    {
        var segments = new Stack<string>();
        var current = StripConvert(expression.Body);

        while (current is MemberExpression memberExpression)
        {
            segments.Push(memberExpression.Member.Name);
            current = StripConvert(memberExpression.Expression!);
        }

        if (current is not ParameterExpression || segments.Count == 0)
        {
            path = null;
            return false;
        }

        path = string.Join(".", segments);
        return true;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression)
        {
            expression = unaryExpression.Operand;
        }

        return expression;
    }
}

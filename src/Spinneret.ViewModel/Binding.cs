using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Spinneret.Functional;

namespace Spinneret.ViewModel;

public class Binding(
    IValidationStateProvider target, 
    string propertyPath,
    Func<object?> getPropertyValue,
    Action<object?> setPropertyValue,
    Func<string, Result<object, string>> convertToTarget,
    Func<object?, string> convertFromTarget)
{
    private static readonly ConditionalWeakTable<IValidationStateProvider, Dictionary<string, Binding>> Cache = new();
    
    private string? _conversionError;

    public string PropertyPath { get; } = propertyPath;
    
    public void RegisterBoundProperty()
    {
        target.ValidationState.RegisterBoundProperty(PropertyPath);
    }

    public void SetValue(string value)
    {
        var res = convertToTarget(value);
        res.Iter(SetValue, SetError);
    }

    private void SetValue(object value)
    {
        if (_conversionError != null)
        {
            target.ValidationState.RemoveError(PropertyPath);
            _conversionError = null;
        }

        setPropertyValue(value);
    }

    private void SetError(string error)
    {
        _conversionError = error;
        target.ValidationState.AddError(PropertyPath, error);
    }

    public string GetValue()
    {
        var propertyValue = getPropertyValue();

        return propertyValue == null
            ? string.Empty
            : convertFromTarget(propertyValue);
    }

    public string? GetError()
    {
        return target.ValidationState.GetError(PropertyPath);
    }

    public bool HasConversionError => _conversionError != null;
    public bool HasError => GetError() != null;

    public static Binding Create<TViewModel>(TViewModel target, Expression<Func<TViewModel, string?>> expr)
        where TViewModel : IValidationStateProvider
    {
        return Create(
            target,
            expr,
            Result<string?, string>.Ok,
            x => x ?? string.Empty);
    }

    public static Binding Create<TViewModel, TValue>(
        TViewModel target,
        Expression<Func<TViewModel, TValue>> expr,
        Func<string, Result<TValue, string>> convertToTarget)
        where TViewModel : IValidationStateProvider
    {
        return Create(
            target,
            expr,
            convertToTarget,
            x => Convert.ToString(x, CultureInfo.CurrentCulture) ?? string.Empty);
    }

    public static Binding Create<TViewModel, TValue>(
        TViewModel target,
        Expression<Func<TViewModel, TValue>> expr,
        IConverter<string, TValue> converter)
        where TViewModel : IValidationStateProvider
    {
        return Create(
            target,
            expr,
            converter.ConvertTo,
            converter.ConvertFrom
        );
    }
    
    public static Binding Create<TViewModel, TValue>(
        TViewModel target,
        Expression<Func<TViewModel, TValue>> expr,
        Func<string, Result<TValue, string>> convertToTarget,
        Func<TValue, string> convertFromTarget) 
        where TViewModel : IValidationStateProvider
    {
        var cachedBindings = Cache.GetValue(target, _ => new Dictionary<string, Binding>());

        var hasPropertyPath = TryGetPropertyPath(expr, out var propertyPath);
        string pathString;
        string keyPath;
        if (hasPropertyPath)
        {
            pathString = string.Join(".", propertyPath!.Select(p => p.Name));
            keyPath = string.Join(">", propertyPath!.Select(p => $"{p.DeclaringType!.FullName}.{p.Name}"));
        }
        else
        {
            pathString = GetExpressionPath(expr, target);
            var closureHash = GetClosureIdentityHash(expr.Body);
            keyPath = $"expr:{pathString}|closures:{closureHash}";
        }
        var key = $"{keyPath}|{convertToTarget.Method}|{convertFromTarget.Method}";

        lock (cachedBindings)
        {
            if (cachedBindings.TryGetValue(key, out var existing))
                return existing;
        
            Func<object?> getValue;
            Action<object?> setValue;
            if (hasPropertyPath)
            {
                getValue = () => GetPathValue(target, propertyPath!);
                setValue = value => SetPathValue(target, propertyPath!, value, pathString);
            }
            else
            {
                var getter = CompileGetter(expr);
                var setter = CompileSetter(expr, pathString);
                getValue = () => getter(target);
                setValue = value => setter(target, value);
            }
            
            var binding = new Binding(
                target,
                pathString,
                getValue,
                setValue,
                x => convertToTarget(x).Map(o => (object)o!),
                x => x != null ? convertFromTarget((TValue)x) : string.Empty
            );
            
            cachedBindings[key] = binding;

            return binding;
        }
    }
    
    private static bool TryGetPropertyPath(LambdaExpression expression, out List<PropertyInfo>? propertyPath)
    {
        var members = new List<PropertyInfo>();
        var current = StripConvert(expression.Body);

        while (current is MemberExpression memberExpression)
        {
            if (memberExpression.Member is not PropertyInfo propertyInfo) {
                propertyPath = null;
                return false;
            }

            members.Add(propertyInfo);
            current = memberExpression.Expression!;
        }

        if (current is not ParameterExpression)
        {
            propertyPath = null;
            return false;
        }

        members.Reverse();
        propertyPath = members;
        return true;
    }

    private static Func<TViewModel, object?> CompileGetter<TViewModel, TValue>(
        Expression<Func<TViewModel, TValue>> expression)
    {
        var body = Expression.Convert(StripConvert(expression.Body), typeof(object));
        return Expression.Lambda<Func<TViewModel, object?>>(body, expression.Parameters[0]).Compile();
    }

    private static Action<TViewModel, object?> CompileSetter<TViewModel, TValue>(
        Expression<Func<TViewModel, TValue>> expression,
        string pathString)
    {
        var valueParameter = Expression.Parameter(typeof(object), "value");
        var targetBody = StripConvert(expression.Body);

        if (!IsWritableTarget(targetBody))
        {
            // Defer the failure to call time rather than throwing while the binding is being created,
            // so a read-only target is usable by consumers that only read (e.g. surfacing a validation
            // error at a path). This mirrors the property-path branch, which only fails on a real set.
            return (_, _) => throw new InvalidOperationException($"Binding expression '{pathString}' is not writable.");
        }

        var assignExpression = BuildAssignExpression(targetBody, valueParameter);
        var lambda = Expression.Lambda<Action<TViewModel, object?>>(assignExpression, expression.Parameters[0], valueParameter);
        var compiled = lambda.Compile();

        return (target, value) =>
        {
            try
            {
                compiled(target, value);
            }
            catch (Exception e) when (e is NullReferenceException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                throw new InvalidOperationException(
                    $"Cannot set property '{pathString}' because an intermediate value is null or an index is out of range.",
                    e);
            }
        };
    }

    private static Expression BuildAssignExpression(Expression targetBody, ParameterExpression valueParameter)
    {
        if (targetBody is MethodCallExpression methodCallExpression && IsIndexerCall(methodCallExpression))
        {
            var setterMethod = ResolveIndexerSetter(methodCallExpression)
                               ?? throw new ArgumentException("Binding indexer expression does not have a setter.");

            var valueCast = Expression.Convert(valueParameter, methodCallExpression.Type);
            var setterArguments = methodCallExpression.Arguments.Append(valueCast);
            return Expression.Call(methodCallExpression.Object!, setterMethod, setterArguments);
        }

        var valueAsTargetType = Expression.Convert(valueParameter, targetBody.Type);
        return Expression.Assign(targetBody, valueAsTargetType);
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression)
        {
            expression = unaryExpression.Operand;
        }

        return expression;
    }

    private static bool IsWritableTarget(Expression expression)
    {
        return expression switch
        {
            MemberExpression member => member.Member is PropertyInfo { CanWrite: true } || member.Member is FieldInfo,
            IndexExpression index => index.Indexer?.CanWrite ?? true,
            MethodCallExpression methodCall when IsIndexerCall(methodCall) => HasIndexerSetter(methodCall),
            _ => false
        };
    }

    private static bool HasIndexerSetter(MethodCallExpression methodCallExpression)
    {
        return ResolveIndexerSetter(methodCallExpression) != null;
    }

    private static MethodInfo? ResolveIndexerSetter(MethodCallExpression methodCallExpression)
    {
        var getter = methodCallExpression.Method;
        if (!getter.Name.Equals("get_Item", StringComparison.Ordinal))
            return null;

        var type = getter.DeclaringType;
        if (type == null)
            return null;

        var setterParameterTypes = getter.GetParameters()
            .Select(x => x.ParameterType)
            .Concat([getter.ReturnType])
            .ToArray();

        return type.GetMethod("set_Item", setterParameterTypes);
    }

    private static string GetExpressionPath<TViewModel, TValue>(
        Expression<Func<TViewModel, TValue>> expression,
        TViewModel target)
    {
        return BuildPathSegment(expression.Body, expression.Parameters[0], target)
               ?? throw new ArgumentException("Binding expression path is empty.");
    }

    private static string? BuildPathSegment<TViewModel>(
        Expression expression,
        ParameterExpression rootParameter,
        TViewModel target)
    {
        expression = StripConvert(expression);

        return expression switch
        {
            ParameterExpression => null,
            MemberExpression memberExpression => AppendMember(
                BuildPathSegment(memberExpression.Expression!, rootParameter, target),
                memberExpression.Member.Name),
            IndexExpression indexExpression => AppendIndexer(
                BuildPathSegment(indexExpression.Object!, rootParameter, target),
                indexExpression.Arguments.Select(argument => EvaluateSubExpression(argument, rootParameter, target)).ToList()),
            MethodCallExpression methodCallExpression when IsIndexerCall(methodCallExpression) => AppendIndexer(
                BuildPathSegment(methodCallExpression.Object!, rootParameter, target),
                methodCallExpression.Arguments.Select(argument => EvaluateSubExpression(argument, rootParameter, target)).ToList()),
            _ => throw new ArgumentException($"Unsupported binding expression '{expression}'.")
        };
    }

    private static bool IsIndexerCall(MethodCallExpression methodCallExpression)
    {
        return methodCallExpression.Method.Name == "get_Item" &&
               methodCallExpression.Arguments.Count > 0 &&
               methodCallExpression.Object != null;
    }

    private static string AppendMember(string? parent, string memberName)
    {
        return string.IsNullOrEmpty(parent)
            ? memberName
            : $"{parent}.{memberName}";
    }

    private static string AppendIndexer(string? parent, IReadOnlyList<object?> indices)
    {
        var indexValues = string.Join(",", indices.Select(FormatIndexValue));
        return $"{parent}[{indexValues}]";
    }

    private static object? EvaluateSubExpression<TViewModel>(
        Expression expression,
        ParameterExpression rootParameter,
        TViewModel target)
    {
        var converted = Expression.Convert(expression, typeof(object));
        var lambda = Expression.Lambda<Func<TViewModel, object?>>(converted, rootParameter);
        return lambda.Compile()(target);
    }

    private static string GetClosureIdentityHash(Expression body)
    {
        var closures = new List<object>();
        CollectClosures(body, closures);
        return closures.Count == 0
            ? string.Empty
            : string.Join(",", closures.Select(HashClosureValue));
    }

    private static string HashClosureValue(object value)
    {
        return value.GetType().IsValueType
            ? value.GetHashCode().ToString(CultureInfo.InvariantCulture)
            : RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture);
    }

    private static void CollectClosures(Expression? expression, List<object> closures)
    {
        if (expression == null) return;

        switch (expression)
        {
            case MemberExpression { Expression: ConstantExpression { Value: { } target } } member
                when target.GetType().GetCustomAttribute<CompilerGeneratedAttribute>() != null:
                var capturedValue = GetMemberValue(member.Member, target);
                if (capturedValue != null)
                    closures.Add(capturedValue);
                break;
            case MemberExpression member:
                CollectClosures(member.Expression, closures);
                break;
            case MethodCallExpression call:
                CollectClosures(call.Object, closures);
                foreach (var arg in call.Arguments)
                    CollectClosures(arg, closures);
                break;
            case IndexExpression index:
                CollectClosures(index.Object, closures);
                foreach (var arg in index.Arguments)
                    CollectClosures(arg, closures);
                break;
            case UnaryExpression unary:
                CollectClosures(unary.Operand, closures);
                break;
            case BinaryExpression binary:
                CollectClosures(binary.Left, closures);
                CollectClosures(binary.Right, closures);
                break;
            case ConditionalExpression conditional:
                CollectClosures(conditional.Test, closures);
                CollectClosures(conditional.IfTrue, closures);
                CollectClosures(conditional.IfFalse, closures);
                break;
        }
    }

    private static object? GetMemberValue(MemberInfo member, object target)
    {
        return member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property => property.GetValue(target),
            _ => null
        };
    }

    private static string FormatIndexValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static object? GetPathValue(object target, IReadOnlyList<PropertyInfo> propertyPath)
    {
        var current = target;
        foreach (var property in propertyPath)
        {
            if (current == null)
            {
                return null;
            }

            current = property.GetValue(current);
        }

        return current;
    }

    private static void SetPathValue(
        object target,
        List<PropertyInfo> propertyPath,
        object? value,
        string propertyPathString)
    {
        if (propertyPath.Count == 0)
        {
            throw new ArgumentException("Binding property path is empty.");
        }

        var current = target;
        for (var i = 0; i < propertyPath.Count - 1; i++)
        {
            if (current == null)
            {
                throw new InvalidOperationException($"Cannot set property '{propertyPathString}' because an intermediate value is null.");
            }

            current = propertyPath[i].GetValue(current);
        }

        if (current == null)
        {
            throw new InvalidOperationException($"Cannot set property '{propertyPathString}' because an intermediate value is null.");
        }

        propertyPath[^1].SetValue(current, value, null);
    }
}
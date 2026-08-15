using System.Collections.Concurrent;
using System.Linq.Expressions;
using Spinneret.Functional;

namespace Spinneret.Parsing;

/// <summary>
/// Selects and parses properties of <typeparamref name="TModel"/> inside a
/// <see cref="IModelParser{TError}.Parse{TModel,TParsed}"/> call, recording an error per failing property
/// instead of stopping at the first one. Created by the library and handed to the parse
/// lambda — not constructible by consumers.
/// <para>
/// String values are trimmed before parsing, and a whitespace-only string counts as missing
/// for <c>Require</c>/<c>Optional</c> — the boundary treats "  " the same as absent input.
/// </para>
/// <para>
/// When a property fails, its error is recorded and <c>default</c> is returned so the
/// remaining properties still parse; the enclosing <see cref="IModelParser{TError}.Parse{TModel,TParsed}"/>
/// returns the full error list instead of a model.
/// </para>
/// </summary>
public sealed class PropertyParser<TModel, TError>
{
    private static readonly ConcurrentDictionary<(Type MemberType, string Path), Delegate> CompiledAccessors = new();

    private readonly List<string> _parsedProperties;
    private readonly List<InvalidProperty<TError>> _errors;
    private readonly TError _errorForMissingProperty;
    private readonly string? _prefix;

    internal PropertyParser(
        TModel model,
        List<string> parsedProperties,
        List<InvalidProperty<TError>> errors,
        TError errorForMissingProperty,
        string? prefix = null)
    {
        Model = model;
        _parsedProperties = parsedProperties;
        _errors = errors;
        _errorForMissingProperty = errorForMissingProperty;
        _prefix = prefix;
    }

    /// <summary>The model being parsed.</summary>
    public TModel Model { get; }

    internal PropertyParser<TNewModel, TError> ReplaceModel<TNewModel>(TNewModel model)
    {
        return new PropertyParser<TNewModel, TError>(
            model,
            _parsedProperties,
            _errors,
            _errorForMissingProperty,
            _prefix);
    }

    /// <summary>Continues parsing against a projection of the current model, keeping the same error scope.</summary>
    public PropertyParser<TNewModel, TError> WithModel<TNewModel>(Func<TModel, TNewModel> mapper)
    {
        return ReplaceModel(mapper(Model));
    }

    private void AddError(string propertyName, TError error)
    {
        _errors.Add(new InvalidProperty<TError>
        {
            PropertyName = _prefix == null ? propertyName : $"{_prefix}.{propertyName}",
            Error = error
        });
    }

    private static string GetIndexedMemberName(string memberName, int index)
    {
        return $"{memberName}[{index}]";
    }

    private PropertyParser<TNested, TError> CreateNested<TNested>(TNested model, string errorPrefix)
    {
        return new PropertyParser<TNested, TError>(
            model,
            _parsedProperties,
            _errors,
            _errorForMissingProperty,
            _prefix == null ? errorPrefix : $"{_prefix}.{errorPrefix}");
    }

    /// <summary>Parses a property with the given parser, recording its error under the property's name.</summary>
    public TParsed Parse<TMember, TParsed>(
        Expression<Func<TModel, TMember>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);
        var res = parser(memberValue);
        return MatchResult(memberName, res);
    }

    /// <summary>Requires a reference-typed property to be present (and non-whitespace for strings).</summary>
    public TMember Require<TMember>(
        Expression<Func<TModel, TMember?>> expression) where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TMember, TError>.Error(_errorForMissingProperty)
            : Result<TMember, TError>.Ok(memberValue);

        return MatchResult(memberName, res);
    }

    /// <summary>Requires a reference-typed property to be present, then parses it.</summary>
    public TParsed Require<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser) where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TParsed, TError>.Error(_errorForMissingProperty)
            : parser(memberValue);

        return MatchResult(memberName, res);
    }

    /// <summary>Requires a nullable value-typed property to be present, then parses it.</summary>
    public TParsed Require<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser) where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = memberValue is null
            ? Result<TParsed, TError>.Error(_errorForMissingProperty)
            : parser(memberValue.Value);

        return MatchResult(memberName, res);
    }

    /// <summary>Requires a nullable value-typed property to be present.</summary>
    public TMember Require<TMember>(
        Expression<Func<TModel, TMember?>> expression) where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = memberValue is null
            ? Result<TMember, TError>.Error(_errorForMissingProperty)
            : Result<TMember, TError>.Ok(memberValue.Value);

        return MatchResult(memberName, res);
    }

    /// <summary>Requires a nested model to be present and parses it with a nested parser scoped to its property path.</summary>
    public TParsed NestRequired<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        if (memberValue == null)
        {
            AddError(memberName, _errorForMissingProperty);
            return default!;
        }

        var nestedPropertyParser = CreateNested(memberValue, memberName);

        return nestedParser(nestedPropertyParser);
    }

    /// <summary>
    /// Requires a collection property to be present and parses each element, recording errors
    /// under indexed names ("Items[3]"). Elements that parse successfully are returned even when
    /// others fail — the enclosing parse still fails on any recorded error.
    /// </summary>
    public IEnumerable<TParsed> Many<TMember, TParsed>(
        Expression<Func<TModel, IEnumerable<TMember>?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
    {
        var memberName = GetMemberName(expression);
        var memberValues = GetMemberValue(expression, memberName)?.ToList();

        if (memberValues == null)
        {
            AddError(memberName, _errorForMissingProperty);
            return [];
        }

        var res = new List<TParsed>();
        for (var i = 0; i < memberValues.Count; ++i)
        {
            var memberValue = memberValues[i];

            if (memberValue == null)
            {
                AddError(GetIndexedMemberName(memberName, i), _errorForMissingProperty);
                continue;
            }

            var memberIndex = i;
            parser(memberValue).Switch(
                res.Add,
                error => AddError(GetIndexedMemberName(memberName, memberIndex), error));
        }

        return res;
    }

    /// <summary>
    /// Requires a collection of nested models to be present and parses each with a nested parser
    /// scoped to its indexed property path ("Items[3].Name"). Elements that parse successfully
    /// are returned even when others fail — the enclosing parse still fails on any recorded error.
    /// </summary>
    public IEnumerable<TParsed> NestMany<TNestedModel, TParsed>(
        Expression<Func<TModel, IEnumerable<TNestedModel>?>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
    {
        var memberName = GetMemberName(expression);
        var memberValues = GetMemberValue(expression, memberName)?.ToList();

        if (memberValues == null)
        {
            AddError(memberName, _errorForMissingProperty);
            return [];
        }

        var res = new List<TParsed>();
        for (var i = 0; i < memberValues.Count; ++i)
        {
            var memberValue = memberValues[i];

            if (memberValue == null)
            {
                AddError(GetIndexedMemberName(memberName, i), _errorForMissingProperty);
                continue;
            }

            var nestedPropertyParser = CreateNested(memberValue, GetIndexedMemberName(memberName, i));

            res.Add(nestedParser(nestedPropertyParser));
        }

        return res;
    }

    /// <summary>Parses a reference-typed property when present; absent (or whitespace string) yields null without error.</summary>
    public TParsed? Optional<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : class where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue).Map(TParsed? (x) => x);

        return MatchResult(memberName, res);
    }

    /// <summary>Parses a nullable value-typed property when present; absent yields null without error.</summary>
    public TParsed? Optional<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : class where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = (memberValue is null)
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue.Value).Map(TParsed? (x) => x);

        return MatchResult(memberName, res);
    }

    /// <summary>Parses a nested model when present; absent yields null without error.</summary>
    public TParsed? NestOptional<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : class where TNestedModel : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        if (memberValue == null)
        {
            return null;
        }

        var nestedPropertyParser = CreateNested(memberValue, memberName);

        return nestedParser(nestedPropertyParser);
    }

    /// <summary>Parses a nullable value-typed nested model when present; absent yields null without error.</summary>
    public TParsed? NestOptional<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : class where TNestedModel : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        if (memberValue == null)
        {
            return null;
        }

        var nestedPropertyParser = CreateNested(memberValue.Value, memberName);

        return nestedParser(nestedPropertyParser);
    }

    /// <summary>Parses a nullable value-typed property into a value type when present; absent yields null without error.</summary>
    public TParsed? OptionalStruct<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : struct where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = memberValue is null
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue.Value).Map(x => (TParsed?)x);

        return MatchResult(memberName, res);
    }

    /// <summary>Parses a reference-typed property into a value type when present; absent (or whitespace string) yields null without error.</summary>
    public TParsed? OptionalStruct<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : struct where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue).Map(x => (TParsed?)x);

        return MatchResult(memberName, res);
    }

    /// <summary>Parses a nested model into a value type when present; absent yields null without error.</summary>
    public TParsed? NestOptionalStruct<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : struct where TNestedModel : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        if (memberValue == null)
        {
            return null;
        }

        var nestedPropertyParser = CreateNested(memberValue, memberName);

        return nestedParser(nestedPropertyParser);
    }

    /// <summary>Parses a nullable value-typed nested model into a value type when present; absent yields null without error.</summary>
    public TParsed? NestOptionalStruct<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : struct where TNestedModel : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression, memberName);

        if (memberValue == null)
        {
            return null;
        }

        var nestedPropertyParser = CreateNested(memberValue.Value, memberName);

        return nestedParser(nestedPropertyParser);
    }

    private string GetMemberName<TDelegate>(Expression<TDelegate> expression)
    {
        var memberName = ExpressionPropertyPath.GetDottedPath(expression);
        _parsedProperties.Add(_prefix == null ? memberName : $"{_prefix}.{memberName}");
        return memberName;
    }

    private TMember GetMemberValue<TMember>(Expression<Func<TModel, TMember>> expression, string memberPath)
    {
        // Safe to cache by path: GetMemberName has already validated the expression is a plain
        // member chain off the lambda parameter, so path + member type fully determine it.
        var accessor = (Func<TModel, TMember>)CompiledAccessors.GetOrAdd(
            (typeof(TMember), memberPath),
            static (_, expr) => expr.Compile(),
            expression);

        var value = accessor(Model);

        return value is string stringValue
            ? (TMember)(object)stringValue.Trim()
            : value;
    }

    private T MatchResult<T>(string propertyName, Result<T, TError> res)
    {
        return res.Match(
            x => x,
            e =>
            {
                AddError(propertyName, e);
                return default!;
            });
    }
}

/// <summary>Combinators for parsing models that are an <see cref="Either{T1, T2}"/> of two shapes.</summary>
public static class PropertyParserExtensions
{
    /// <summary>
    /// Parses whichever case the model holds, using the matching parser, and returns the
    /// parsed value as an <see cref="Either{TParsed1, TParsed2}"/>. Errors are recorded in
    /// the enclosing parse as usual.
    /// </summary>
    public static Either<TParsed1, TParsed2> ParseEither<TModel1, TModel2, TParsed1, TParsed2, TError>(
        this PropertyParser<Either<TModel1, TModel2>, TError> parser,
        Func<TModel1, PropertyParser<TModel1, TError>, TParsed1> parser1,
        Func<TModel2, PropertyParser<TModel2, TError>, TParsed2> parser2
    )
    {
        return parser.Model.Map(
            x => parser1(x, parser.ReplaceModel(x)),
            x => parser2(x, parser.ReplaceModel(x)));
    }
}

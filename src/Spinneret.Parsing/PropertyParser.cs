using System.Linq.Expressions;
using Spinneret.Functional;

namespace Spinneret.Parsing;

public class PropertyParser<TModel, TError>(
    TModel model,
    List<string> parsedProperties,
    List<InvalidProperty<TError>> errors,
    TError errorForMissingProperty,
    string? prefix = null)
{
    public TModel Model { get; } = model;

    internal PropertyParser<TNewModel, TError> ReplaceModel<TNewModel>(TNewModel model)
    {
        return new PropertyParser<TNewModel, TError>(
            model,
            parsedProperties,
            errors,
            errorForMissingProperty,
            prefix);
    }

    public PropertyParser<TNewModel, TError> WithModel<TNewModel>(Func<TModel, TNewModel> mapper)
    {
        return ReplaceModel(mapper(Model));
    }

    private void AddError(string propertyName, TError error)
    {
        errors.Add(new InvalidProperty<TError>
        {
            PropertyName = prefix == null ? propertyName : $"{prefix}.{propertyName}", 
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
            parsedProperties,
            errors,
            errorForMissingProperty,
            prefix == null ? errorPrefix : $"{prefix}.{errorPrefix}");
    }
    
    public TParsed Parse<TMember, TParsed>(
        Expression<Func<TModel, TMember>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);
        var res = parser(memberValue);
        return ReduceResult(memberName, res);
    }

    public TParsed Nest<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        if (memberValue == null)
        {
            AddError(memberName, errorForMissingProperty);
            return default!;
        }

        var nestedPropertyParser = CreateNested(memberValue, memberName);
        return nestedParser(nestedPropertyParser);
    }
    
    public TMember Require<TMember>(
        Expression<Func<TModel, TMember?>> expression) where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TMember, TError>.Error(errorForMissingProperty)
            : Result<TMember, TError>.Ok(memberValue);

        return ReduceResult(memberName, res);
    }
    
    public TParsed Require<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser) where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TParsed, TError>.Error(errorForMissingProperty)
            : parser(memberValue);

        return ReduceResult(memberName, res);
    }
    
    public TParsed Require<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser) where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = memberValue is null
            ? Result<TParsed, TError>.Error(errorForMissingProperty)
            : parser(memberValue.Value);

        return ReduceResult(memberName, res);
    }
    
    public TMember Require<TMember>(
        Expression<Func<TModel, TMember?>> expression) where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = memberValue is null
            ? Result<TMember, TError>.Error(errorForMissingProperty)
            : Result<TMember, TError>.Ok(memberValue.Value);

        return ReduceResult(memberName, res);
    }

    public TParsed NestRequired<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel>> expression, 
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        if (memberValue == null)
        {
            AddError(memberName, errorForMissingProperty);
            return default!;
        }
        
        var nestedPropertyParser = CreateNested(memberValue, memberName);
        
        return nestedParser(nestedPropertyParser);
    }

    public IEnumerable<TParsed> Many<TMember, TParsed>(
        Expression<Func<TModel, IEnumerable<TMember>?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
    {
        var memberName = GetMemberName(expression);
        var memberValues = GetMemberValue(expression)?.ToList();

        if (memberValues == null)
        {
            AddError(memberName, errorForMissingProperty);
            return [];
        }

        var res = new List<TParsed>();
        var hasErrors = false;
        for (var i = 0; i < memberValues.Count; ++i)
        {
            var memberValue = memberValues[i];

            if (memberValue == null)
            {
                AddError(GetIndexedMemberName(memberName, i), errorForMissingProperty);
                hasErrors = true;
                continue;
            }

            var memberIndex = i;
            var parseSucceeded = parser(memberValue).Reduce(
                parsed => {
                    res.Add(parsed);
                    return true;
                },
                error =>
                {
                    AddError(GetIndexedMemberName(memberName, memberIndex), error);
                    return false;
                });

            if (!parseSucceeded)
            {
                hasErrors = true;
            }
        }

        return hasErrors ? [] : res;
    }

    public IEnumerable<TParsed> NestMany<TNestedModel, TParsed>(
        Expression<Func<TModel, IEnumerable<TNestedModel>?>> expression, 
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
    {
        var memberName = GetMemberName(expression);
        var memberValues = GetMemberValue(expression)?.ToList();

        if (memberValues == null)
        {
            AddError(memberName, errorForMissingProperty);
            return [];
        }

        var res = new List<TParsed>();
        for (var i = 0; i < memberValues.Count; ++i)
        {
            var memberValue = memberValues[i];

            if (memberValue == null)
            {
                AddError(GetIndexedMemberName(memberName, i), errorForMissingProperty);
                return [];
            }

            var nestedPropertyParser = CreateNested(memberValue, GetIndexedMemberName(memberName, i));

            res.Add(nestedParser(nestedPropertyParser));
        }

        return res;
    }

    public TParsed? Optional<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : class where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue).Map(TParsed? (x) => x);

        return ReduceResult(memberName, res);
    }

    public TParsed? Optional<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : class where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = (memberValue is null)
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue.Value).Map(TParsed? (x) => x);

        return ReduceResult(memberName, res);
    }

    public TParsed? NestOptional<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression, 
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : class where TNestedModel : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        if (memberValue == null)
        {
            return null;
        }

        var nestedPropertyParser = CreateNested(memberValue, memberName);

        return nestedParser(nestedPropertyParser);
    }

    public TParsed? NestOptional<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression, 
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : class where TNestedModel : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        if (memberValue == null)
        {
            return null;
        }

        var nestedPropertyParser = CreateNested(memberValue.Value, memberName);

        return nestedParser(nestedPropertyParser);
    }

    public TParsed? OptionalStruct<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : struct where TMember : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = memberValue is null
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue.Value).Map(x => (TParsed?)x);

        return ReduceResult(memberName, res);
    }

    public TParsed? OptionalStruct<TMember, TParsed>(
        Expression<Func<TModel, TMember?>> expression,
        Func<TMember, Result<TParsed, TError>> parser)
        where TParsed : struct where TMember : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        var res = memberValue is null || memberValue is string value && string.IsNullOrWhiteSpace(value)
            ? Result<TParsed?, TError>.Ok(null)
            : parser(memberValue).Map(x => (TParsed?)x);

        return ReduceResult(memberName, res);
    }

    public TParsed? NestOptionalStruct<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression,
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : struct where TNestedModel : class
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

        if (memberValue == null)
        {
            return null;
        }

        var nestedPropertyParser = CreateNested(memberValue, memberName);

        return nestedParser(nestedPropertyParser);
    }

    public TParsed? NestOptionalStruct<TNestedModel, TParsed>(
        Expression<Func<TModel, TNestedModel?>> expression, 
        Func<PropertyParser<TNestedModel, TError>, TParsed> nestedParser)
        where TParsed : struct where TNestedModel : struct
    {
        var memberName = GetMemberName(expression);
        var memberValue = GetMemberValue(expression);

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
        parsedProperties.Add(prefix == null ? memberName : $"{prefix}.{memberName}");
        return memberName;
    }

    private TMember GetMemberValue<TMember>(Expression<Func<TModel, TMember>> expression)
    {
        var value = expression.Compile().Invoke(Model);
        
        return value is string stringValue
            ? (TMember)(object) stringValue.Trim()
            : value;
    }

    private T ReduceResult<T>(string propertyName, Result<T, TError> res)
    {
        return res.Reduce(
            x => x, 
            e =>
            {
                AddError(propertyName, e);
                return default!;
            });
    }
}

public static class PropertyParserExtensions
{
    public static Either<TModel1, TModel2> Either<TModel1, TModel2, TError>(
        this PropertyParser<Either<TModel1, TModel2>, TError> parser, 
        Func<TModel1, PropertyParser<TModel1, TError>, TModel1> parser1,
        Func<TModel2, PropertyParser<TModel2, TError>, TModel2> parser2
    )
    {
        return parser.Model.Map(
            x => parser1(x, parser.ReplaceModel(x)), 
            x => parser2(x, parser.ReplaceModel(x)));
    }
}
using Spinneret.Functional;

namespace Spinneret.Parsing;

public interface IModelParser<TError>
{
    Result<TParsed, IEnumerable<InvalidProperty<TError>>> Parse<TModel, TParsed>(
        TModel model, 
        Func<PropertyParser<TModel, TError>, TParsed> parseFn);
}

public class ModelParser<TError>(TError missingPropertyError) : IModelParser<TError>
{
    public Result<TParsed, IEnumerable<InvalidProperty<TError>>> Parse<TModel, TParsed>(
        TModel model,
        Func<PropertyParser<TModel, TError>, TParsed> parseFn)
    {
        var parsedProperties = new List<string>();
        
        var errors = new List<InvalidProperty<TError>>();

        var parser = new PropertyParser<TModel, TError>(model, parsedProperties, errors, missingPropertyError);

        var parsed = parseFn(parser);

        return errors.Count == 0 
            ? Result.Ok<TParsed, IEnumerable<InvalidProperty<TError>>>(parsed) 
            : Result.Error<TParsed, IEnumerable<InvalidProperty<TError>>>(errors);
    }
}

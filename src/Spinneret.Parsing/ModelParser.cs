using Spinneret.Functional;

namespace Spinneret.Parsing;

/// <summary>
/// Parses an input model into a typed result in one pass, producing either the parsed value
/// or the complete list of invalid properties. Implemented by the library — inject and call;
/// register with AddModelParser.
/// </summary>
public interface IModelParser<TError>
{
    Result<TParsed, IReadOnlyList<InvalidProperty<TError>>> Parse<TModel, TParsed>(
        TModel model,
        Func<PropertyParser<TModel, TError>, TParsed> parseFn);
}

public sealed class ModelParser<TError>(TError missingPropertyError) : IModelParser<TError>
{
    public Result<TParsed, IReadOnlyList<InvalidProperty<TError>>> Parse<TModel, TParsed>(
        TModel model,
        Func<PropertyParser<TModel, TError>, TParsed> parseFn)
    {
        var parsedProperties = new List<string>();

        var errors = new List<InvalidProperty<TError>>();

        var parser = new PropertyParser<TModel, TError>(model, parsedProperties, errors, missingPropertyError);

        var parsed = parseFn(parser);

        return errors.Count == 0
            ? Result.Ok<TParsed, IReadOnlyList<InvalidProperty<TError>>>(parsed)
            : Result.Error<TParsed, IReadOnlyList<InvalidProperty<TError>>>(errors);
    }
}

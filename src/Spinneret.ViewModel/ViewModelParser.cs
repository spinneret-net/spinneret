using Microsoft.Extensions.Localization;
using Spinneret.Parsing;

namespace Spinneret.ViewModel;

/// <summary>
/// The outcome of parsing a view model: whether it was valid, and the parsed value.
/// <see cref="Value"/> can be null for a valid view model whose parse function chose to
/// produce nothing — only <see cref="IsValid"/> distinguishes the two.
/// </summary>
public sealed record ViewModelParseResult<TParsed>
{
    public required bool IsValid { get; init; }
    public TParsed? Value { get; init; }
}

/// <summary>
/// Parses a view model with the same parse-don't-validate machinery as the HTTP boundary,
/// binding each error to the view-model property that caused it. Implemented by the library —
/// register with AddViewModelParser and inject.
/// </summary>
public interface IViewModelParser<TParseError> where TParseError : ILocalizable
{
    TParsed? Parse<TViewModel, TParsed>(
        TViewModel viewModel,
        ICollection<string>? propertiesToValidate,
        Func<PropertyParser<TViewModel, TParseError>, TParsed> parseFn)
        where TViewModel : IValidationStateProvider =>
        ParseChecked(viewModel, propertiesToValidate, parseFn).Value;

    /// <summary>
    /// Parses, and reports whether the view model held a value the parser rejected. A parse
    /// function is free to return <c>null</c> for a well-formed view model that has nothing to
    /// send — a null value therefore does not mean invalid, and only this overload can tell
    /// the two apart.
    /// </summary>
    ViewModelParseResult<TParsed> ParseChecked<TViewModel, TParsed>(
        TViewModel viewModel,
        ICollection<string>? propertiesToValidate,
        Func<PropertyParser<TViewModel, TParseError>, TParsed> parseFn)
        where TViewModel : IValidationStateProvider;
}

public sealed class ViewModelParser<TParseError>(IStringLocalizer localizer, TParseError missingPropertyError) : IViewModelParser<TParseError>
    where TParseError : ILocalizable
{
    public ViewModelParseResult<TParsed> ParseChecked<TViewModel, TParsed>(
        TViewModel viewModel,
        ICollection<string>? propertiesToValidate,
        Func<PropertyParser<TViewModel, TParseError>, TParsed> parseFn)
        where TViewModel : IValidationStateProvider
    {
        var parsedProperties = new List<string>();
        var errors = new List<InvalidProperty<TParseError>>();
        var parser = new PropertyParser<TViewModel, TParseError>(viewModel, parsedProperties, errors, missingPropertyError);
        var parsed = parseFn(parser);

        var relevantProperties = GetRelevantProperties(parsedProperties, propertiesToValidate);

        if (errors.Count == 0)
        {
            foreach (var property in relevantProperties)
            {
                viewModel.ValidationState.RemoveError(property);
            }

            return new ViewModelParseResult<TParsed> { IsValid = true, Value = parsed };
        }

        var invalidProperties = errors.Where(x => relevantProperties.Contains(x.PropertyName)).ToList();
        foreach (var invalidProperty in invalidProperties)
        {
            viewModel.ValidationState.AddError(invalidProperty.PropertyName, invalidProperty.Error.Localize(localizer));
        }

        var validProperties = relevantProperties.Except(invalidProperties.Select(x => x.PropertyName));
        foreach (var validProperty in validProperties)
        {
            viewModel.ValidationState.RemoveError(validProperty);
        }

        return new ViewModelParseResult<TParsed> { IsValid = false };
    }

    /// <summary>
    /// A parsed property is relevant when a changed property is it or a descendant of it, so that a
    /// change to e.g. "Text.ValueEn" revalidates the parsed "Text". This is found by expanding each
    /// changed property into its self-and-ancestor paths once and matching parsed properties against
    /// that set, giving O(1) lookups while respecting path-segment boundaries.
    /// </summary>
    private static ICollection<string> GetRelevantProperties(
        List<string> parsedProperties,
        ICollection<string>? propertiesToValidate)
    {
        if (propertiesToValidate == null)
        {
            return parsedProperties;
        }

        var changedAndAncestors = new HashSet<string>();
        foreach (var changed in propertiesToValidate)
        {
            foreach (var ancestor in SelfAndAncestors(changed))
            {
                changedAndAncestors.Add(ancestor);
            }
        }

        return parsedProperties.Where(changedAndAncestors.Contains).ToHashSet();
    }

    private static IEnumerable<string> SelfAndAncestors(string property)
    {
        for (var end = property.Length; end > 0; end = property.LastIndexOf('.', end - 1))
        {
            yield return property[..end];
        }
    }
}

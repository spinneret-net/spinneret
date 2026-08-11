using Microsoft.Extensions.Localization;
using Spinneret.Parsing;

namespace Spinneret.ViewModel;

public interface IViewModelParser<TParseError> where TParseError: ILocalizable
{
    TParsed? Parse<TViewModel, TParsed>(
        TViewModel viewModel,
        ICollection<string>? propertiesToValidate,
        Func<PropertyParser<TViewModel, TParseError>, TParsed> parseFn)
        where TViewModel : IValidationStateProvider =>
        Parse(viewModel, propertiesToValidate, parseFn, out _);

    /// <summary>
    /// Parses, and reports through <paramref name="isValid"/> whether the view model held a value the
    /// parser rejected. A parse function is free to return <c>null</c> for a well-formed view model that
    /// has nothing to send — a null result therefore does not mean invalid, and only this overload can
    /// tell the two apart.
    /// </summary>
    TParsed? Parse<TViewModel, TParsed>(
        TViewModel viewModel,
        ICollection<string>? propertiesToValidate,
        Func<PropertyParser<TViewModel, TParseError>, TParsed> parseFn,
        out bool isValid)
        where TViewModel : IValidationStateProvider;
}

public class ViewModelParser<TParseError>(IStringLocalizer localizer, TParseError missingPropertyError) : IViewModelParser<TParseError>
    where TParseError: ILocalizable
{
    public TParsed? Parse<TViewModel, TParsed>(
        TViewModel viewModel,
        ICollection<string>? propertiesToValidate,
        Func<PropertyParser<TViewModel, TParseError>, TParsed> parseFn,
        out bool isValid)
        where TViewModel : IValidationStateProvider
    {
        var parsedProperties = new List<string>();
        var errors = new List<InvalidProperty<TParseError>>();
        var parser = new PropertyParser<TViewModel, TParseError>(viewModel, parsedProperties, errors, missingPropertyError);
        var parsed = parseFn(parser);

        var relevantProperties = GetRelevantProperties(parsedProperties, propertiesToValidate);

        isValid = errors.Count == 0;

        if (errors.Count == 0)
        {
            foreach (var property in relevantProperties)
            {
                viewModel.ValidationState.RemoveError(property);
            }

            return parsed;
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

        return default;
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
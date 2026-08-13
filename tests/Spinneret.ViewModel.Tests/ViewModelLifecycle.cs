namespace Spinneret.ViewModel.Tests;

/// <summary>
/// ViewModelBase implements the lifecycle explicitly (only the view drives it, via
/// IViewModel); these helpers let tests keep calling it on concrete view models.
/// </summary>
internal static class ViewModelLifecycle
{
    public static Task InitializeAsync(this IViewModel viewModel, CancellationToken cancellationToken) =>
        viewModel.InitializeAsync(cancellationToken);

    public static Task UpdateAsync(this IViewModel viewModel, ICollection<string> changedProperties) =>
        viewModel.UpdateAsync(changedProperties);
}

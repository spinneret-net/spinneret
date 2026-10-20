namespace Spinneret.ViewModel.Tests;

// ViewModelBase implements the lifecycle explicitly; these let tests call it directly.
internal static class ViewModelLifecycle
{
    public static Task InitializeAsync(this IViewModel viewModel, CancellationToken cancellationToken) =>
        viewModel.InitializeAsync(cancellationToken);

    public static Task UpdateAsync(this IViewModel viewModel, ICollection<string> changedProperties) =>
        viewModel.UpdateAsync(changedProperties);
}

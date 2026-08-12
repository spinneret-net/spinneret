namespace Spinneret.ViewModel.Tests;

public class ViewModelFactoryTests
{
    [Test]
    public async Task Create_returns_the_registered_service_instance()
    {
        var viewModel = new SampleViewModel();
        var services = new FakeServiceProvider(new Dictionary<Type, object>
        {
            [typeof(SampleViewModel)] = viewModel
        });
        var sut = new ViewModelFactory(services);

        var created = sut.Create<SampleViewModel>();

        await Assert.That(ReferenceEquals(created, viewModel)).IsTrue();
    }

    [Test]
    public async Task Create_unregistered_type_throws_with_the_type_name()
    {
        var sut = new ViewModelFactory(new FakeServiceProvider([]));

        var exception = Assert.Throws<InvalidOperationException>(() => sut.Create<SampleViewModel>());

        await Assert.That(exception.Message).Contains($"model type {nameof(SampleViewModel)}");
    }

    private sealed class SampleViewModel : ViewModelBase;

    private sealed class FakeServiceProvider(Dictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
    }
}

namespace Spinneret.View.Tests;

public class ClientRenderContextTests
{
    [Test]
    public async Task IsClient_always_returns_true()
    {
        var context = new ClientRenderContext();

        await Assert.That(context.IsClient).IsTrue();
    }

    [Test]
    public async Task IsServer_always_returns_false()
    {
        var context = new ClientRenderContext();

        await Assert.That(context.IsServer).IsFalse();
    }

    [Test]
    public async Task IsPrerendering_always_returns_false()
    {
        var context = new ClientRenderContext();

        await Assert.That(context.IsPrerendering).IsFalse();
    }
}

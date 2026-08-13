using Spinneret.Functional;

namespace Spinneret.Functional.Tests;

public class UnitTests
{
    [Test]
    public async Task All_unit_values_are_equal()
    {
        await Assert.That(Unit.Value).IsEqualTo(default(Unit));
        await Assert.That(Unit.Value == default).IsTrue();
    }

    [Test]
    public async Task Unit_has_a_stable_hash_code()
    {
        await Assert.That(Unit.Value.GetHashCode()).IsEqualTo(default(Unit).GetHashCode());
    }
}

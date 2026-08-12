namespace Spinneret.Mediator.Tests;

public class CacheAttributeTests
{
    [Test]
    public async Task Constructor_with_enum_tags_exposes_duration_and_tags()
    {
        var attribute = new CacheAttribute(90, CacheTag.Alpha, CacheTag.Beta);

        await Assert.That(attribute.Duration).IsEqualTo(TimeSpan.FromSeconds(90));
        await Assert.That(attribute.Tags).IsEquivalentTo(new Enum[] { CacheTag.Alpha, CacheTag.Beta });
    }

    [Test]
    public async Task Constructor_with_no_tags_exposes_empty_tag_list()
    {
        var attribute = new CacheAttribute(30);

        await Assert.That(attribute.Tags.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Constructor_with_zero_seconds_throws_ArgumentOutOfRangeException()
    {
        await Assert.That(() => { _ = new CacheAttribute(0); })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_with_negative_seconds_throws_ArgumentOutOfRangeException()
    {
        await Assert.That(() => { _ = new CacheAttribute(-5, CacheTag.Alpha); })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_with_non_enum_tag_throws_ArgumentException()
    {
        await Assert.That(() => { _ = new CacheAttribute(60, "not an enum"); })
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_with_null_tag_throws_ArgumentException()
    {
        await Assert.That(() => { _ = new CacheAttribute(60, (object)null!); })
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task InvalidateCacheAttribute_with_enum_tags_exposes_tags()
    {
        var attribute = new InvalidateCacheAttribute(CacheTag.Gamma);

        await Assert.That(attribute.Tags).IsEquivalentTo(new Enum[] { CacheTag.Gamma });
    }

    [Test]
    public async Task InvalidateCacheAttribute_with_non_enum_tag_throws_ArgumentException()
    {
        await Assert.That(() => { _ = new InvalidateCacheAttribute(123); })
            .Throws<ArgumentException>();
    }
}

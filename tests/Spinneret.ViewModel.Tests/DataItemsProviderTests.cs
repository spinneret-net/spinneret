using System.Linq.Expressions;

namespace Spinneret.ViewModel.Tests;

/// <summary>Shim mapping the old positional call shape onto <see cref="DataItemsRequest{TItem}"/>.</summary>
file static class ProviderShim
{
    public static Task<DataItemsResult<TItem>> GetItems<TItem>(
        this IDataItemsProvider<TItem> provider,
        Expression<Func<TItem, object?>>? sortBy,
        SortDirection sortDirection,
        string? query,
        int offset,
        int count)
        => provider.GetItems(new DataItemsRequest<TItem>
        {
            SortBy = sortBy,
            SortDirection = sortDirection,
            Query = query,
            Offset = offset,
            Count = count,
        });
}

public class DataItemsProviderTests
{
    private static readonly string[] Fruits = ["banana", "apple", "cherry", "apricot"];

    [Test]
    public async Task GetItems_no_filter_or_sort_returns_all_items_in_order_with_total_count()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits);

        var result = await sut.GetItems(null, SortDirection.None, null, 0, 10);

        await Assert.That(string.Join(",", result.Items)).IsEqualTo("banana,apple,cherry,apricot");
        await Assert.That(result.TotalItemCount).IsEqualTo(4);
    }

    [Test]
    public async Task GetItems_applies_offset_and_count_for_paging()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits);

        var result = await sut.GetItems(null, SortDirection.None, null, 1, 2);

        await Assert.That(string.Join(",", result.Items)).IsEqualTo("apple,cherry");
        await Assert.That(result.TotalItemCount).IsEqualTo(4);
    }

    [Test]
    public async Task GetItems_offset_beyond_the_end_returns_an_empty_page_with_full_total()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits);

        var result = await sut.GetItems(null, SortDirection.None, null, 10, 5);

        await Assert.That(result.Items.ToList()).IsEmpty();
        await Assert.That(result.TotalItemCount).IsEqualTo(4);
    }

    [Test]
    public async Task GetItems_query_with_filter_filters_items_and_total_reflects_the_filter()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits, (item, query) => item.StartsWith(query));

        var result = await sut.GetItems(null, SortDirection.None, "ap", 0, 10);

        await Assert.That(result.Items.ToList()).IsEquivalentTo(["apple", "apricot"]);
        await Assert.That(result.TotalItemCount).IsEqualTo(2);
    }

    [Test]
    public async Task GetItems_null_query_skips_the_filter()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits, (_, _) => false);

        var result = await sut.GetItems(null, SortDirection.None, null, 0, 10);

        await Assert.That(result.TotalItemCount).IsEqualTo(4);
    }

    [Test]
    public async Task GetItems_query_without_a_filter_returns_all_items()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits);

        var result = await sut.GetItems(null, SortDirection.None, "ap", 0, 10);

        await Assert.That(result.TotalItemCount).IsEqualTo(4);
    }

    [Test]
    public async Task GetItems_sorts_ascending_by_the_sort_expression()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits);

        var result = await sut.GetItems(x => x, SortDirection.Ascending, null, 0, 10);

        await Assert.That(string.Join(",", result.Items)).IsEqualTo("apple,apricot,banana,cherry");
    }

    [Test]
    public async Task GetItems_sorts_descending_by_the_sort_expression()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits);

        var result = await sut.GetItems(x => x, SortDirection.Descending, null, 0, 10);

        await Assert.That(string.Join(",", result.Items)).IsEqualTo("cherry,banana,apricot,apple");
    }

    [Test]
    public async Task GetItems_sort_direction_none_ignores_the_sort_expression()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits);

        var result = await sut.GetItems(x => x, SortDirection.None, null, 0, 10);

        await Assert.That(result.Items.ToList()).IsEquivalentTo(Fruits);
    }

    [Test]
    public async Task GetItems_filters_then_sorts_then_pages()
    {
        var sut = new InMemoryDataItemsProvider<string>(Fruits, (item, query) => item.Contains(query));

        var result = await sut.GetItems(x => x, SortDirection.Ascending, "a", 1, 1);

        // Filter keeps banana, apple, apricot; sorted: apple, apricot, banana; page skips 1 takes 1.
        await Assert.That(result.Items.ToList()).IsEquivalentTo(["apricot"]);
        await Assert.That(result.TotalItemCount).IsEqualTo(3);
    }
}

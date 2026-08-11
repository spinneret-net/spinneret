using System.Linq.Expressions;

namespace Spinneret.ViewModel;

public enum SortDirection
{
    None,
    Ascending,
    Descending
}

public readonly record struct DataTableItemsResult<TItem>(IEnumerable<TItem> Items, int TotalItemCount);

public interface IDataItemsProvider<TItem>
{
    public Task<DataTableItemsResult<TItem>> GetItems(
        Expression<Func<TItem, object?>>? sortBy,
        SortDirection sortDirection,
        string? query,
        int offset,
        int count);
}

public readonly struct InMemoryDataItemsProvider<TItem>(
    IReadOnlyCollection<TItem> items,
    Func<TItem, string, bool>? filter = null)
    : IDataItemsProvider<TItem>
{
    public Task<DataTableItemsResult<TItem>> GetItems(
        Expression<Func<TItem, object?>>? sortBy,
        SortDirection sortDirection,
        string? query,
        int offset,
        int count)
    {
        IEnumerable<TItem> result = items;

        var filter1 = filter;
        if (query != null && filter1 != null)
            result = result.Where(x => filter1(x, query));

        if (sortBy != null && sortDirection != SortDirection.None)
        {
            var compiled = sortBy.Compile();
            result = sortDirection == SortDirection.Ascending
                ? result.OrderBy(compiled)
                : result.OrderByDescending(compiled);
        }

        var materialised = result.ToList();
        var totalCount = materialised.Count;
        var page = materialised.Skip(offset).Take(count);

        return Task.FromResult(new DataTableItemsResult<TItem>(page, totalCount));
    }
}

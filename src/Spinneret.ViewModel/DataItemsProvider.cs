using System.Linq.Expressions;

namespace Spinneret.ViewModel;

public enum SortDirection
{
    None = 0,
    Ascending = 1,
    Descending = 2,
}

/// <summary>
/// One page request against a data source. New capabilities (multi-column sort, grouping)
/// are added as optional init-only properties, so provider implementations keep compiling.
/// </summary>
public sealed record DataItemsRequest<TItem>
{
    /// <summary>Property selector to sort by; null for the source's natural order.</summary>
    public Expression<Func<TItem, object?>>? SortBy { get; init; }

    public SortDirection SortDirection { get; init; } = SortDirection.None;

    /// <summary>Free-text filter; null when the user has not searched.</summary>
    public string? Query { get; init; }

    /// <summary>Items to skip before the page starts.</summary>
    public int Offset { get; init; }

    /// <summary>Maximum items in the page.</summary>
    public required int Count { get; init; }
}

/// <summary>One page of items plus the total count after filtering, for paging UI.</summary>
public sealed record DataItemsResult<TItem>
{
    public required IEnumerable<TItem> Items { get; init; }
    public required int TotalItemCount { get; init; }
}

/// <summary>
/// Supplies pages of items to a data-bound table. Implemented by consumers — back it with a
/// database query, an API call, or <see cref="InMemoryDataItemsProvider{TItem}"/> for
/// already-materialized collections.
/// </summary>
public interface IDataItemsProvider<TItem>
{
    Task<DataItemsResult<TItem>> GetItems(DataItemsRequest<TItem> request, CancellationToken cancellationToken = default);
}

/// <summary>Pages, filters and sorts an in-memory collection.</summary>
public sealed class InMemoryDataItemsProvider<TItem>(
    IReadOnlyCollection<TItem> items,
    Func<TItem, string, bool>? filter = null)
    : IDataItemsProvider<TItem>
{
    public Task<DataItemsResult<TItem>> GetItems(DataItemsRequest<TItem> request, CancellationToken cancellationToken = default)
    {
        IEnumerable<TItem> result = items;

        if (request.Query != null && filter != null)
            result = result.Where(x => filter(x, request.Query));

        if (request.SortBy != null && request.SortDirection != SortDirection.None)
        {
            var compiled = request.SortBy.Compile();
            result = request.SortDirection == SortDirection.Ascending
                ? result.OrderBy(compiled)
                : result.OrderByDescending(compiled);
        }

        var materialised = result.ToList();
        var totalCount = materialised.Count;
        var page = materialised.Skip(request.Offset).Take(request.Count);

        return Task.FromResult(new DataItemsResult<TItem> { Items = page, TotalItemCount = totalCount });
    }
}

namespace WorkLog.Application.Common;

/// <summary>Página de resultados con los metadatos que necesita el panel para paginar.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public PagedResult<TOut> Map<TOut>(Func<T, TOut> selector) => new(Items.Select(selector).ToList(), Page, PageSize, TotalCount);
}

/// <summary>Parámetros de consulta comunes a todos los listados.</summary>
public sealed record PageRequest
{
    private const int MaxPageSize = 200;

    public PageRequest(int page = 1, int pageSize = 20, string? search = null)
    {
        Page = page < 1 ? 1 : page;
        PageSize = pageSize switch
        {
            < 1 => 20,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
    }

    public int Page { get; }
    public int PageSize { get; }
    public string? Search { get; }
    public int Skip => (Page - 1) * PageSize;
}

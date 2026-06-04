using Microsoft.AspNetCore.Mvc;

namespace EquipmentChecklist.Models;

/// <summary>
/// Server-side counterpart of the mobile <c>FilterBar.State</c>. Bound from
/// the query string by the default MVC model-binder; every consumer action
/// just takes <c>[FromQuery] ListFilter filter</c> and is done.
///
/// <para>Why this lives in <c>Models</c> rather than the controller layer:
/// the same shape is shared across History, Sign-Off Queue, Defects Index,
/// and Reports. Centralising it keeps the URL contract identical across
/// pages (so a user can bookmark a search), and gives us a single helper
/// to apply to any <see cref="System.Linq.IQueryable{T}"/>.</para>
/// </summary>
public class ListFilter
{
    /// <summary>Free-text search. Trimmed; null/whitespace = no filter.</summary>
    [FromQuery(Name = "q")]
    public string? Search { get; set; }

    /// <summary>Inclusive lower bound on the row's primary date.
    /// Null = unbounded.</summary>
    [FromQuery(Name = "from")]
    public DateTime? From { get; set; }

    /// <summary>Inclusive upper bound on the row's primary date. Widened to
    /// end-of-day by <see cref="EffectiveTo"/> so a single-day filter
    /// includes events later than midnight.</summary>
    [FromQuery(Name = "to")]
    public DateTime? To { get; set; }

    /// <summary>Named preset that drove From/To (today / 7d / 30d / month).
    /// Round-tripped so the view can highlight the active chip after a GET.</summary>
    [FromQuery(Name = "preset")]
    public string? Preset { get; set; }

    /// <summary>True when any filter is currently active.</summary>
    public bool IsActive =>
        !string.IsNullOrWhiteSpace(Search) || From.HasValue || To.HasValue;

    /// <summary>Trimmed, non-null search needle. Empty string when no search.</summary>
    public string SearchTrim => (Search ?? "").Trim();

    /// <summary>End-of-day-widened upper bound. Pre-computed so EF can
    /// translate the comparison cleanly (a method call inside the Where
    /// would not translate).</summary>
    public DateTime? EffectiveTo =>
        To.HasValue ? To.Value.Date.AddDays(1).AddTicks(-1) : (DateTime?)null;

    /// <summary>Round-trip the active filter back into a URL query so the
    /// pager and export buttons keep the user's narrow scope. Returns
    /// <c>""</c> when no filters are active.</summary>
    public string ToQueryString()
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(Search)) parts.Add($"q={Uri.EscapeDataString(SearchTrim)}");
        if (From.HasValue)                      parts.Add($"from={From.Value:yyyy-MM-dd}");
        if (To.HasValue)                        parts.Add($"to={To.Value:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(Preset)) parts.Add($"preset={Uri.EscapeDataString(Preset)}");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
}

/// <summary>
/// Translates a named preset (today / 7d / 30d / month) into concrete
/// From/To bounds on the <see cref="ListFilter"/>. Kept in one place so
/// every controller using the same partial agrees on what each chip means.
/// </summary>
public static class FilterPresets
{
    public static void Apply(ListFilter f)
    {
        if (string.IsNullOrWhiteSpace(f.Preset)) return;

        var today = DateTime.UtcNow.Date;
        switch (f.Preset)
        {
            case "today":
                f.From = today;
                f.To   = today;
                break;
            case "7d":
                f.From = today.AddDays(-6);
                f.To   = today;
                break;
            case "30d":
                f.From = today.AddDays(-29);
                f.To   = today;
                break;
            case "month":
                f.From = new DateTime(today.Year, today.Month, 1);
                f.To   = f.From.Value.AddMonths(1).AddDays(-1);
                break;
            default:
                // Unknown preset → drop the value so it doesn't round-trip
                // to a view that would then "highlight" a phantom chip.
                f.Preset = null;
                break;
        }
    }
}

/// <summary>
/// LINQ helpers for applying a <see cref="ListFilter"/> to a query.
///
/// <para>Each "Apply…" extension takes the predicate it needs (e.g. a
/// date selector for date-range filtering, or a set of string selectors
/// for the search). Keeping these as extensions rather than baking them
/// into <see cref="ListFilter"/> avoids dragging EF-specific
/// <see cref="System.Linq.Expressions.Expression"/> types into the model
/// layer.</para>
/// </summary>
public static class ListFilterExtensions
{
    /// <summary>
    /// Apply the from/to bounds against the given date selector. No-op if
    /// the filter has no date bounds set.
    /// </summary>
    public static IQueryable<T> ApplyDateRange<T>(
        this IQueryable<T> source,
        ListFilter filter,
        System.Linq.Expressions.Expression<Func<T, DateTime>> dateSelector)
    {
        if (!filter.From.HasValue && !filter.EffectiveTo.HasValue)
            return source;

        // Compile a small predicate at the call-site so the date selector
        // appears unmodified to EF's expression translator.
        if (filter.From.HasValue)
        {
            var from = filter.From.Value;
            var lower = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
                System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    dateSelector.Body,
                    System.Linq.Expressions.Expression.Constant(from, typeof(DateTime))),
                dateSelector.Parameters);
            source = source.Where(lower);
        }
        if (filter.EffectiveTo.HasValue)
        {
            var to = filter.EffectiveTo.Value;
            var upper = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
                System.Linq.Expressions.Expression.LessThanOrEqual(
                    dateSelector.Body,
                    System.Linq.Expressions.Expression.Constant(to, typeof(DateTime))),
                dateSelector.Parameters);
            source = source.Where(upper);
        }
        return source;
    }

    /// <summary>
    /// In-memory search: filters the materialised list by checking whether
    /// any of the haystack-selectors contains the search needle
    /// (case-insensitive). Used after the EF query has been pulled so we
    /// can search across navigation-property string fields without
    /// fighting the expression translator.
    /// </summary>
    public static List<T> ApplySearchInMemory<T>(
        this IEnumerable<T> source,
        ListFilter filter,
        params Func<T, string?>[] haystackSelectors)
    {
        if (string.IsNullOrWhiteSpace(filter.Search) || haystackSelectors.Length == 0)
            return source.ToList();

        var needle = filter.SearchTrim;
        return source.Where(item =>
            haystackSelectors.Any(sel =>
            {
                var h = sel(item);
                return !string.IsNullOrEmpty(h) &&
                       h.Contains(needle, StringComparison.OrdinalIgnoreCase);
            }))
            .ToList();
    }
}

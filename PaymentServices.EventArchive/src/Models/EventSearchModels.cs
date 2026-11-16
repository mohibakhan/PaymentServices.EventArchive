namespace PaymentServices.EventArchive.Models;

/// <summary>What a search request asks for. All filters are AND-ed.</summary>
public sealed class EventSearchCriteria
{
    /// <summary>Inclusive lower bound on the enqueued time (UTC).</summary>
    public DateTimeOffset From { get; init; }

    /// <summary>Exclusive upper bound on the enqueued time (UTC).</summary>
    public DateTimeOffset To { get; init; }

    // Each pattern is a case-insensitive exact match; '*' is a wildcard.
    public string? Subject { get; init; }
    public string? MessageType { get; init; }
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>Application property name → pattern.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    public int PageSize { get; init; }

    /// <summary>When true each item carries the full archived event, not just the summary.</summary>
    public bool IncludeEvent { get; init; }

    public SearchContinuation? Continuation { get; init; }
}

/// <summary>
/// Where the next page resumes: the date prefix being scanned, the storage
/// marker of the list page we stopped in, and the last blob name already
/// examined inside that page.
///
/// It also pins the date range of the search. A search with no explicit dates
/// means "the last 24 hours", which would otherwise shift between page
/// requests; carrying the range here keeps every page on the same window.
/// </summary>
public sealed record SearchContinuation(
    string Prefix,
    string? Marker,
    string? LastName,
    DateTimeOffset From,
    DateTimeOffset To);

public sealed class ArchivedEventSummary
{
    public string ArchiveId { get; set; } = string.Empty;
    public DateTimeOffset EnqueuedTimeUtc { get; set; }
    public string? Subject { get; set; }
    public string? MessageType { get; set; }
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>The full event — only when the request asked for <c>include=full</c>.</summary>
    public ArchivedEvent? Event { get; set; }
}

public sealed class EventSearchResult
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int Count { get; set; }
    public List<ArchivedEventSummary> Items { get; set; } = new();

    /// <summary>
    /// Non-null when there is more to scan — pass it back as
    /// <c>continuationToken</c> with the SAME filters. A page can hold fewer
    /// than <c>pageSize</c> items (even none) and still have a token, because a
    /// request stops scanning when its time budget runs out.
    /// </summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Blobs listed while producing this page (diagnostic).</summary>
    public long BlobsScanned { get; set; }
}

public sealed class ApiError
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

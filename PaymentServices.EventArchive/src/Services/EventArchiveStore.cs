using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Settings;

namespace PaymentServices.EventArchive.Services;

public interface IEventArchiveStore
{
    /// <summary>Archives the event. Returns false when it was already archived (redelivery).</summary>
    Task<bool> StoreAsync(ArchivedEvent archivedEvent, CancellationToken cancellationToken);

    /// <summary>The archived event, or null when the id is unknown or malformed.</summary>
    Task<ArchivedEvent?> GetAsync(string archiveId, CancellationToken cancellationToken);

    Task<EventSearchResult> SearchAsync(EventSearchCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>Thrown when a continuation token does not belong to the search it was sent with.</summary>
public sealed class InvalidContinuationTokenException : Exception
{
    public InvalidContinuationTokenException(string message) : base(message) { }
}

/// <summary>
/// Stores archived events as JSON blobs and searches them.
///
/// SEARCH walks the date prefixes that cover the requested range in
/// chronological order. For each listed blob it:
///   1. skips anything outside [from, to) using the timestamp in the name;
///   2. applies the subject / message type / message id / correlation id
///      filters from blob METADATA — no download;
///   3. downloads the blob only when it has to: an application-property
///      filter, <c>include=full</c>, or a field too long to fit in metadata.
/// It stops when the page is full, the range is exhausted, or the time budget
/// is spent, and returns a continuation that resumes exactly where it stopped.
/// </summary>
public sealed class EventArchiveStore : IEventArchiveStore
{
    private readonly IEventBlobContainer _container;
    private readonly EventArchiveSettings _settings;
    private readonly ILogger<EventArchiveStore> _logger;

    public EventArchiveStore(
        IEventBlobContainer container,
        IOptions<EventArchiveSettings> settings,
        ILogger<EventArchiveStore> logger)
    {
        _container = container;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Blobs requested per listing call.</summary>
    public int ListPageSize { get; init; } = 500;

    /// <summary>Blobs downloaded concurrently when a search has to read content.</summary>
    public int DownloadParallelism { get; init; } = 16;

    public async Task<bool> StoreAsync(ArchivedEvent archivedEvent, CancellationToken cancellationToken)
    {
        var blobName = EventBlobNaming.BlobNameFromArchiveId(archivedEvent.ArchiveId)
            ?? throw new InvalidOperationException($"'{archivedEvent.ArchiveId}' is not a valid archive id.");

        var content = BinaryData.FromObjectAsJson(archivedEvent, ArchiveJson.Options);
        var metadata = EventMetadata.Build(archivedEvent);

        return await _container.UploadIfNotExistsAsync(blobName, content, metadata, cancellationToken);
    }

    public async Task<ArchivedEvent?> GetAsync(string archiveId, CancellationToken cancellationToken)
    {
        var blobName = EventBlobNaming.BlobNameFromArchiveId(archiveId);
        if (blobName is null)
        {
            return null;
        }

        return await LoadAsync(blobName, cancellationToken);
    }

    public async Task<EventSearchResult> SearchAsync(
        EventSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var prefixes = EventBlobNaming.PlanPrefixes(criteria.From, criteria.To);
        var result = new EventSearchResult { From = criteria.From, To = criteria.To };

        var prefixIndex = 0;
        string? marker = null;
        string? lastName = null;

        if (criteria.Continuation is { } continuation)
        {
            prefixIndex = IndexOf(prefixes, continuation.Prefix);
            if (prefixIndex < 0)
            {
                throw new InvalidContinuationTokenException(
                    "The continuation token does not match this search's date range.");
            }

            marker = continuation.Marker;
            lastName = continuation.LastName;
        }

        SearchContinuation ResumeAt(string resumePrefix, string? resumeMarker, string? resumeAfter) =>
            new(resumePrefix, resumeMarker, resumeAfter, criteria.From, criteria.To);

        var pageSize = Math.Max(1, criteria.PageSize);
        var listPageSize = Math.Max(1, ListPageSize);
        var parallelism = Math.Max(1, DownloadParallelism);
        var budget = TimeSpan.FromSeconds(Math.Max(1, _settings.SEARCH_TIME_BUDGET_SECONDS));
        var stopwatch = Stopwatch.StartNew();

        while (prefixIndex < prefixes.Count)
        {
            var prefix = prefixes[prefixIndex];

            while (true)
            {
                // The marker that fetches THIS list page — what a continuation
                // must carry to come back to it.
                var pageMarker = marker;
                var page = await _container.ListPageAsync(prefix, pageMarker, listPageSize, cancellationToken);
                result.BlobsScanned += page.Items.Count;

                var candidates = new List<Candidate>();
                var pastEndOfRange = false;

                foreach (var entry in page.Items)
                {
                    // Already examined by the request that issued the continuation.
                    if (lastName is not null && string.CompareOrdinal(entry.Name, lastName) <= 0)
                    {
                        continue;
                    }

                    var timestamp = EventBlobNaming.TimestampFromBlobName(entry.Name);
                    if (timestamp is null || timestamp < criteria.From)
                    {
                        continue;
                    }

                    if (timestamp >= criteria.To)
                    {
                        // Names are chronological: nothing after this can be in range.
                        pastEndOfRange = true;
                        break;
                    }

                    var verdict = EvaluateMetadata(entry, criteria);
                    if (verdict == Verdict.NoMatch)
                    {
                        continue;
                    }

                    var needsContent = verdict == Verdict.Unknown ||
                                       criteria.Properties.Count > 0 ||
                                       criteria.IncludeEvent;

                    candidates.Add(new Candidate(entry, needsContent));
                }

                for (var offset = 0; offset < candidates.Count; offset += parallelism)
                {
                    var batch = candidates.GetRange(offset, Math.Min(parallelism, candidates.Count - offset));

                    var events = await Task.WhenAll(batch.Select(candidate =>
                        candidate.NeedsContent
                            ? LoadAsync(candidate.Entry.Name, cancellationToken)
                            : Task.FromResult<ArchivedEvent?>(null)));

                    for (var i = 0; i < batch.Count; i++)
                    {
                        var candidate = batch[i];
                        var archived = events[i];

                        if (candidate.NeedsContent && (archived is null || !MatchesEvent(archived, criteria)))
                        {
                            continue;
                        }

                        result.Items.Add(ToSummary(candidate.Entry, archived, criteria.IncludeEvent));

                        if (result.Items.Count >= pageSize)
                        {
                            return Finish(result, ResumeAt(prefix, pageMarker, candidate.Entry.Name));
                        }
                    }

                    var moreInThisPage = offset + parallelism < candidates.Count;
                    if (moreInThisPage && stopwatch.Elapsed > budget)
                    {
                        return Finish(result, ResumeAt(prefix, pageMarker, batch[^1].Entry.Name));
                    }
                }

                if (pastEndOfRange)
                {
                    return Finish(result, null);
                }

                if (page.NextMarker is null)
                {
                    break; // this prefix is exhausted
                }

                marker = page.NextMarker;
                lastName = null;

                if (stopwatch.Elapsed > budget)
                {
                    return Finish(result, ResumeAt(prefix, marker, null));
                }
            }

            prefixIndex++;
            marker = null;
            lastName = null;

            if (prefixIndex < prefixes.Count && stopwatch.Elapsed > budget)
            {
                return Finish(result, ResumeAt(prefixes[prefixIndex], null, null));
            }
        }

        return Finish(result, null);
    }

    private static EventSearchResult Finish(EventSearchResult result, SearchContinuation? continuation)
    {
        result.Count = result.Items.Count;
        result.ContinuationToken = continuation is null ? null : ContinuationTokenCodec.Encode(continuation);
        return result;
    }

    private async Task<ArchivedEvent?> LoadAsync(string blobName, CancellationToken cancellationToken)
    {
        var content = await _container.DownloadAsync(blobName, cancellationToken);
        if (content is null)
        {
            return null;
        }

        try
        {
            return content.ToObjectFromJson<ArchivedEvent>(ArchiveJson.Options);
        }
        catch (JsonException ex)
        {
            // One unreadable blob must not break a search over thousands.
            _logger.LogWarning(ex, "Archived blob {BlobName} is not a readable event; skipping.", blobName);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Matching
    // -------------------------------------------------------------------------

    private enum Verdict
    {
        Match,
        NoMatch,

        /// <summary>Metadata cannot decide — the blob content has to be read.</summary>
        Unknown
    }

    private sealed record Candidate(BlobEntry Entry, bool NeedsContent);

    private static Verdict EvaluateMetadata(BlobEntry entry, EventSearchCriteria criteria)
    {
        var unknown = false;

        foreach (var (key, pattern) in new[]
                 {
                     (EventMetadata.Subject, criteria.Subject),
                     (EventMetadata.MessageType, criteria.MessageType),
                     (EventMetadata.MessageId, criteria.MessageId),
                     (EventMetadata.CorrelationId, criteria.CorrelationId)
                 })
        {
            switch (EvaluateField(entry, key, pattern))
            {
                case Verdict.NoMatch:
                    return Verdict.NoMatch;
                case Verdict.Unknown:
                    unknown = true;
                    break;
            }
        }

        return unknown ? Verdict.Unknown : Verdict.Match;
    }

    private static Verdict EvaluateField(BlobEntry entry, string key, string? pattern)
    {
        if (pattern is null)
        {
            return Verdict.Match;
        }

        var value = EventMetadata.Read(entry.Metadata, key);
        if (value is not null)
        {
            return WildcardMatcher.IsMatch(value, pattern) ? Verdict.Match : Verdict.NoMatch;
        }

        // No metadata at all (not written by us, or stripped) or the value was
        // too long for metadata: only the content can answer.
        if (entry.Metadata.Count == 0 || EventMetadata.IsOverflow(entry.Metadata, key))
        {
            return Verdict.Unknown;
        }

        // The event simply has no value for this field.
        return Verdict.NoMatch;
    }

    private static bool MatchesEvent(ArchivedEvent archived, EventSearchCriteria criteria)
    {
        if (criteria.Subject is not null && !WildcardMatcher.IsMatch(archived.Subject, criteria.Subject))
        {
            return false;
        }

        if (criteria.MessageType is not null && !WildcardMatcher.IsMatch(archived.MessageType, criteria.MessageType))
        {
            return false;
        }

        if (criteria.MessageId is not null && !WildcardMatcher.IsMatch(archived.MessageId, criteria.MessageId))
        {
            return false;
        }

        if (criteria.CorrelationId is not null &&
            !WildcardMatcher.IsMatch(archived.CorrelationId, criteria.CorrelationId))
        {
            return false;
        }

        foreach (var (name, pattern) in criteria.Properties)
        {
            var matched = false;
            foreach (var property in archived.ApplicationProperties)
            {
                if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase) &&
                    WildcardMatcher.IsMatch(ToComparableString(property.Value), pattern))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The text an application property value is compared as. Values read back
    /// from a blob arrive as <see cref="JsonElement"/>.
    /// </summary>
    private static string? ToComparableString(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        JsonElement element => element.GetRawText(),
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static ArchivedEventSummary ToSummary(BlobEntry entry, ArchivedEvent? archived, bool includeEvent) =>
        new()
        {
            ArchiveId = EventBlobNaming.ArchiveIdFromBlobName(entry.Name),
            EnqueuedTimeUtc = archived?.EnqueuedTimeUtc
                              ?? EventMetadata.ReadEnqueuedTime(entry.Metadata)
                              ?? EventBlobNaming.TimestampFromBlobName(entry.Name)
                              ?? DateTimeOffset.MinValue,
            Subject = archived?.Subject ?? EventMetadata.Read(entry.Metadata, EventMetadata.Subject),
            MessageType = archived?.MessageType ?? EventMetadata.Read(entry.Metadata, EventMetadata.MessageType),
            MessageId = archived?.MessageId ?? EventMetadata.Read(entry.Metadata, EventMetadata.MessageId),
            CorrelationId = archived?.CorrelationId ?? EventMetadata.Read(entry.Metadata, EventMetadata.CorrelationId),
            SizeBytes = entry.SizeBytes,
            Event = includeEvent ? archived : null
        };

    private static int IndexOf(IReadOnlyList<string> prefixes, string prefix)
    {
        for (var i = 0; i < prefixes.Count; i++)
        {
            if (string.Equals(prefixes[i], prefix, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

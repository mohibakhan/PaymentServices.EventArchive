using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Options;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Settings;

namespace PaymentServices.EventArchive.Services;

/// <summary>
/// Turns the query string of <c>GET /events</c> into an
/// <see cref="EventSearchCriteria"/>.
///
///   date=2026-11-16                     one whole UTC day
///   from=…&amp;to=…                         ISO-8601 instants (no offset = UTC);
///                                       a bare date in <c>to</c> includes that day
///   subject= messageType= messageId= correlationId=
///   prop.{name}={value}                 application property filter (repeatable)
///   pageSize=  include=full  continuationToken=
///
/// With no date at all the search covers the last 24 hours. Unknown
/// parameters (such as the function key, <c>code</c>) are ignored.
/// </summary>
public sealed class EventSearchRequestParser
{
    private const string PropertyPrefix = "prop.";
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly EventArchiveSettings _settings;
    private readonly TimeProvider _timeProvider;

    public EventSearchRequestParser(IOptions<EventArchiveSettings> settings, TimeProvider timeProvider)
    {
        _settings = settings.Value;
        _timeProvider = timeProvider;
    }

    private TimeSpan MaxRange => TimeSpan.FromDays(Math.Max(1, _settings.SEARCH_MAX_RANGE_DAYS));

    public bool TryParse(
        IEnumerable<KeyValuePair<string, string?>> query,
        [NotNullWhen(true)] out EventSearchCriteria? criteria,
        [NotNullWhen(false)] out string? error)
    {
        criteria = null;
        error = null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (key.StartsWith(PropertyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = key[PropertyPrefix.Length..].Trim();
                if (name.Length == 0)
                {
                    error = "An application property filter needs a name: prop.{name}={value}.";
                    return false;
                }

                properties[name] = value;
            }
            else
            {
                values[key.Trim()] = value;
            }
        }

        // ---- Date range ------------------------------------------------------
        SearchContinuation? continuation = null;
        if (values.TryGetValue("continuationToken", out var tokenRaw) &&
            !ContinuationTokenCodec.TryDecode(tokenRaw, out continuation))
        {
            error = "'continuationToken' is not valid. Use the value returned by the previous page unchanged.";
            return false;
        }

        DateTimeOffset from;
        DateTimeOffset to;

        if (continuation is not null)
        {
            // Later pages stay on the exact window the first page searched,
            // even when that window was the moving default ("last 24 hours").
            from = continuation.From;
            to = continuation.To;

            if (to - from > MaxRange)
            {
                error = "'continuationToken' is not valid. Use the value returned by the previous page unchanged.";
                return false;
            }
        }
        else if (!TryResolveRange(values, out from, out to, out error))
        {
            return false;
        }

        // ---- Paging ----------------------------------------------------------
        var maxPageSize = Math.Max(1, _settings.SEARCH_MAX_PAGE_SIZE);
        var pageSize = Math.Clamp(_settings.SEARCH_DEFAULT_PAGE_SIZE, 1, maxPageSize);

        if (values.TryGetValue("pageSize", out var pageSizeRaw))
        {
            if (!int.TryParse(pageSizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) ||
                requested < 1)
            {
                error = "'pageSize' must be a positive whole number.";
                return false;
            }

            pageSize = Math.Min(requested, maxPageSize);
        }

        var includeEvent = false;
        if (values.TryGetValue("include", out var includeRaw))
        {
            if (string.Equals(includeRaw, "full", StringComparison.OrdinalIgnoreCase))
            {
                includeEvent = true;
            }
            else if (!string.Equals(includeRaw, "summary", StringComparison.OrdinalIgnoreCase))
            {
                error = "'include' must be 'summary' or 'full'.";
                return false;
            }
        }

        criteria = new EventSearchCriteria
        {
            From = from,
            To = to,
            Subject = values.GetValueOrDefault("subject"),
            MessageType = values.GetValueOrDefault("messageType"),
            MessageId = values.GetValueOrDefault("messageId"),
            CorrelationId = values.GetValueOrDefault("correlationId"),
            Properties = properties,
            PageSize = pageSize,
            IncludeEvent = includeEvent,
            Continuation = continuation
        };

        return true;
    }

    /// <summary>
    /// Works out [from, to) from the <c>date</c> / <c>from</c> / <c>to</c>
    /// parameters, defaulting to the last 24 hours.
    /// </summary>
    private bool TryResolveRange(
        Dictionary<string, string> values,
        out DateTimeOffset from,
        out DateTimeOffset to,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            return TryResolveRangeCore(values, out from, out to, out error);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Date arithmetic ran off the end of the calendar (year 1 / 9999).
            from = default;
            to = default;
            error = "The date range is outside the supported calendar.";
            return false;
        }
    }

    private bool TryResolveRangeCore(
        Dictionary<string, string> values,
        out DateTimeOffset from,
        out DateTimeOffset to,
        [NotNullWhen(false)] out string? error)
    {
        from = default;
        to = default;
        error = null;

        var now = _timeProvider.GetUtcNow();
        var hasDate = values.TryGetValue("date", out var dateRaw);
        var hasFrom = values.TryGetValue("from", out var fromRaw);
        var hasTo = values.TryGetValue("to", out var toRaw);

        if (hasDate)
        {
            if (hasFrom || hasTo)
            {
                error = "Use either 'date' or 'from'/'to', not both.";
                return false;
            }

            if (!TryParseInstant(dateRaw!, out from, out var isDateOnly) || !isDateOnly)
            {
                error = "'date' must be a calendar date in the form yyyy-MM-dd (UTC).";
                return false;
            }

            to = from.AddDays(1);
        }
        else
        {
            if (hasTo)
            {
                if (!TryParseInstant(toRaw!, out to, out var toIsDateOnly))
                {
                    error = "'to' is not a valid date/time. Use ISO-8601, e.g. 2026-11-16T14:30:00Z.";
                    return false;
                }

                if (toIsDateOnly)
                {
                    to = to.AddDays(1); // a bare date means "through the end of that day"
                }
            }
            else
            {
                to = now;
            }

            if (hasFrom)
            {
                if (!TryParseInstant(fromRaw!, out from, out _))
                {
                    error = "'from' is not a valid date/time. Use ISO-8601, e.g. 2026-11-16T14:30:00Z.";
                    return false;
                }
            }
            else
            {
                from = to - DefaultWindow;
            }
        }

        if (from >= to)
        {
            error = "'from' must be earlier than 'to'.";
            return false;
        }

        if (to - from > MaxRange)
        {
            error = $"The date range is too large. A single search can cover at most {MaxRange.TotalDays:0} days; " +
                    "narrow 'from'/'to' and search the periods separately.";
            return false;
        }

        // Nothing is archived in the future — don't list prefixes for it.
        var latest = now + ClockSkew;
        if (to > latest)
        {
            to = latest > from ? latest : from;
        }

        return true;
    }

    /// <summary>
    /// Parses a calendar date (<c>yyyy-MM-dd</c>, midnight UTC) or an ISO-8601
    /// instant. A value without an offset is taken as UTC.
    /// </summary>
    private static bool TryParseInstant(string raw, out DateTimeOffset value, out bool isDateOnly)
    {
        raw = raw.Trim();

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero);
            isDateOnly = true;
            return true;
        }

        isDateOnly = false;
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, styles, out value))
        {
            return true;
        }

        // A '+' in an un-encoded query string arrives as a space
        // (…T10:00:00+02:00 → "…T10:00:00 02:00"); give that form one more try.
        var lastSpace = raw.LastIndexOf(' ');
        return lastSpace > 0 &&
               DateTimeOffset.TryParse(
                   string.Concat(raw.AsSpan(0, lastSpace), "+", raw.AsSpan(lastSpace + 1)),
                   CultureInfo.InvariantCulture,
                   styles,
                   out value);
    }
}

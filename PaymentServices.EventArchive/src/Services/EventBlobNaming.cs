using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PaymentServices.EventArchive.Services;

/// <summary>
/// Blob layout for the archive:
///
///   {yyyy}/{MM}/{dd}/{HH}/{archiveId}.json
///   archiveId = {yyyyMMddTHHmmssfffZ}-{sequenceNumber}-{messageIdHash}
///
/// Everything is keyed on the broker's ENQUEUED time (UTC). Because the id
/// starts with that timestamp:
///   - blob names sort chronologically, so a date-range search is a prefix
///     listing that can stop as soon as it passes the end of the range;
///   - the path is derivable from the id, so get-by-id is a point read;
///   - the name is identical on every redelivery of the same message, so an
///     at-least-once redelivery can never create a second copy.
/// </summary>
public static partial class EventBlobNaming
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const int TimestampLength = 19;
    private const string Extension = ".json";

    [GeneratedRegex(@"^(?<y>\d{4})(?<M>\d{2})(?<d>\d{2})T(?<h>\d{2})\d{7}Z-\d{1,19}-[0-9a-f]{8}\z")]
    private static partial Regex ArchiveIdRegex();

    public static string BuildArchiveId(DateTimeOffset enqueuedTime, long sequenceNumber, string? messageId)
    {
        var timestamp = enqueuedTime.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId ?? string.Empty));
        var suffix = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        return $"{timestamp}-{sequenceNumber.ToString(CultureInfo.InvariantCulture)}-{suffix}";
    }

    public static bool IsValidArchiveId(string? archiveId) =>
        !string.IsNullOrEmpty(archiveId) && ArchiveIdRegex().IsMatch(archiveId);

    /// <summary>Blob name for an archive id, or null when the id is not well formed.</summary>
    public static string? BlobNameFromArchiveId(string? archiveId)
    {
        if (string.IsNullOrEmpty(archiveId))
        {
            return null;
        }

        var match = ArchiveIdRegex().Match(archiveId);
        if (!match.Success)
        {
            return null;
        }

        return $"{match.Groups["y"].Value}/{match.Groups["M"].Value}/{match.Groups["d"].Value}/" +
               $"{match.Groups["h"].Value}/{archiveId}{Extension}";
    }

    public static string ArchiveIdFromBlobName(string blobName)
    {
        var fileName = blobName[(blobName.LastIndexOf('/') + 1)..];
        return fileName.EndsWith(Extension, StringComparison.Ordinal)
            ? fileName[..^Extension.Length]
            : fileName;
    }

    /// <summary>
    /// The enqueued time encoded in a blob name (millisecond precision), or
    /// null when the blob was not written by the archiver.
    /// </summary>
    public static DateTimeOffset? TimestampFromBlobName(string blobName)
    {
        var fileName = blobName.AsSpan(blobName.LastIndexOf('/') + 1);
        if (fileName.Length < TimestampLength)
        {
            return null;
        }

        return DateTime.TryParseExact(
            fileName[..TimestampLength],
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    public static string DayPrefix(DateTime utc) =>
        utc.ToString("yyyy'/'MM'/'dd'/'", CultureInfo.InvariantCulture);

    public static string HourPrefix(DateTime utc) =>
        utc.ToString("yyyy'/'MM'/'dd'/'HH'/'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The listing prefixes that cover [from, to), in chronological order:
    /// one prefix per whole day, and one per hour for partly covered days.
    /// A prefix may reach slightly outside the range — callers still filter
    /// each blob on its own timestamp.
    /// </summary>
    public static IReadOnlyList<string> PlanPrefixes(DateTimeOffset from, DateTimeOffset toExclusive)
    {
        var prefixes = new List<string>();
        var fromUtc = from.UtcDateTime;
        var toUtc = toExclusive.UtcDateTime;

        var cursor = new DateTime(
            fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);

        while (cursor < toUtc)
        {
            if (cursor.Hour == 0 && cursor.AddDays(1) <= toUtc)
            {
                prefixes.Add(DayPrefix(cursor));
                cursor = cursor.AddDays(1);
            }
            else
            {
                prefixes.Add(HourPrefix(cursor));
                cursor = cursor.AddHours(1);
            }
        }

        return prefixes;
    }
}

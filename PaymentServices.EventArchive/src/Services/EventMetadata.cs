using System.Globalization;
using PaymentServices.EventArchive.Models;

namespace PaymentServices.EventArchive.Services;

/// <summary>
/// The searchable fields copied onto each blob as metadata, so a search can
/// filter on them from the listing alone — without downloading the blob.
///
/// Blob metadata must be ASCII and is size-limited, so values are
/// percent-encoded and a value that would be too long is left off and named in
/// the <c>overflow</c> entry instead; search then reads that blob's content to
/// decide. The blob body is always the source of truth.
/// </summary>
public static class EventMetadata
{
    public const string Subject = "subject";
    public const string MessageType = "messagetype";
    public const string MessageId = "messageid";
    public const string CorrelationId = "correlationid";
    public const string EnqueuedTimeUtc = "enqueuedtimeutc";
    public const string Overflow = "overflow";

    private const int MaxRawLength = 512;
    private const int MaxEncodedLength = 1024;

    public static Dictionary<string, string> Build(ArchivedEvent archivedEvent)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnqueuedTimeUtc] = archivedEvent.EnqueuedTimeUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };

        var overflow = new List<string>();
        Add(metadata, overflow, Subject, archivedEvent.Subject);
        Add(metadata, overflow, MessageType, archivedEvent.MessageType);
        Add(metadata, overflow, MessageId, archivedEvent.MessageId);
        Add(metadata, overflow, CorrelationId, archivedEvent.CorrelationId);

        if (overflow.Count > 0)
        {
            metadata[Overflow] = string.Join(",", overflow);
        }

        return metadata;
    }

    /// <summary>The decoded value of a field, or null when it is not in the metadata.</summary>
    public static string? Read(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var encoded) && !string.IsNullOrEmpty(encoded)
            ? Uri.UnescapeDataString(encoded)
            : null;

    /// <summary>True when the field exists on the event but was too long to store as metadata.</summary>
    public static bool IsOverflow(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(Overflow, out var overflow) &&
        overflow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(key, StringComparer.OrdinalIgnoreCase);

    public static DateTimeOffset? ReadEnqueuedTime(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue(EnqueuedTimeUtc, out var raw) &&
        DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static void Add(Dictionary<string, string> metadata, List<string> overflow, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.Length > MaxRawLength)
        {
            overflow.Add(key);
            return;
        }

        var encoded = Uri.EscapeDataString(value);
        if (encoded.Length > MaxEncodedLength)
        {
            overflow.Add(key);
            return;
        }

        metadata[key] = encoded;
    }
}

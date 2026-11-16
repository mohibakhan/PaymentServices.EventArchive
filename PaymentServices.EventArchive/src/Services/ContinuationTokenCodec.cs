using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaymentServices.EventArchive.Models;

namespace PaymentServices.EventArchive.Services;

/// <summary>
/// Encodes a <see cref="SearchContinuation"/> as the opaque, URL-safe
/// <c>continuationToken</c> clients pass back to get the next page.
/// </summary>
public static partial class ContinuationTokenCodec
{
    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2}/(\d{2}/)?\z")]
    private static partial Regex PrefixRegex();

    public static string Encode(SearchContinuation continuation)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new ContinuationPayload(
                continuation.Prefix,
                continuation.Marker,
                continuation.LastName,
                continuation.From.UtcTicks,
                continuation.To.UtcTicks),
            ArchiveJson.Options);

        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes a token; returns false when it is not one of ours.</summary>
    public static bool TryDecode(string? token, out SearchContinuation? continuation)
    {
        continuation = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var base64 = token.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

            var payload = JsonSerializer.Deserialize<ContinuationPayload>(
                Encoding.UTF8.GetString(Convert.FromBase64String(base64)),
                ArchiveJson.Options);

            if (payload?.P is null || !PrefixRegex().IsMatch(payload.P))
            {
                return false;
            }

            // Throws ArgumentOutOfRangeException (caught below) for ticks that
            // are not a real instant.
            var from = new DateTimeOffset(payload.F, TimeSpan.Zero);
            var to = new DateTimeOffset(payload.T, TimeSpan.Zero);
            if (from >= to)
            {
                return false;
            }

            continuation = new SearchContinuation(payload.P, payload.M, payload.N, from, to);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Wire shape of a continuation token: prefix, storage marker, last blob name,
/// and the search's from/to as UTC ticks.
/// </summary>
internal sealed record ContinuationPayload(string? P, string? M, string? N, long F, long T);

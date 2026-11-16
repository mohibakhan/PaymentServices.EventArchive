using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentServices.EventArchive.Services;

/// <summary>
/// The one set of JSON options used for archived blobs and API responses:
/// camelCase, nulls omitted, readable (non-escaped) text.
/// </summary>
public static class ArchiveJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString |
                         JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // A message body is kept as JSON up to the parser's default depth of
        // 64; the archive record wraps it, so allow more than that here.
        MaxDepth = 128
    };
}

using System.Text.Json;

namespace PaymentServices.EventArchive.Models;

/// <summary>
/// The archived form of one Service Bus message — everything the broker handed
/// us, stored as a single JSON blob.
/// </summary>
public sealed class ArchivedEvent
{
    /// <summary>
    /// Stable id of the archived event, e.g. <c>20261116T203745123Z-12345-a1b2c3d4</c>
    /// (enqueued time, sequence number, message-id hash). The blob path is
    /// derived from it, so a lookup by id is a point read.
    /// </summary>
    public string ArchiveId { get; set; } = string.Empty;

    public DateTimeOffset ArchivedAtUtc { get; set; }

    public ArchivedEventSource Source { get; set; } = new();

    // ---- Broker properties ---------------------------------------------------
    public string? MessageId { get; set; }
    public string? Subject { get; set; }

    /// <summary>Message type — a configured application property, else the Subject.</summary>
    public string? MessageType { get; set; }

    public string? CorrelationId { get; set; }
    public string? ContentType { get; set; }
    public string? SessionId { get; set; }
    public string? PartitionKey { get; set; }
    public string? TransactionPartitionKey { get; set; }
    public string? ReplyTo { get; set; }
    public string? ReplyToSessionId { get; set; }
    public string? To { get; set; }

    public DateTimeOffset EnqueuedTimeUtc { get; set; }
    public DateTimeOffset? ScheduledEnqueueTimeUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string? TimeToLive { get; set; }

    public long SequenceNumber { get; set; }
    public long EnqueuedSequenceNumber { get; set; }
    public int DeliveryCount { get; set; }
    public string? State { get; set; }

    public string? DeadLetterSource { get; set; }
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }

    // ---- Application properties ---------------------------------------------
    public Dictionary<string, object?> ApplicationProperties { get; set; } = new();

    // ---- Body ----------------------------------------------------------------

    /// <summary>
    /// How the body is stored: <c>json</c> (<see cref="Body"/>), <c>text</c>
    /// (<see cref="BodyText"/>), <c>base64</c> (<see cref="BodyBase64"/>),
    /// <c>amqp-value</c> / <c>amqp-sequence</c> (<see cref="BodyText"/>) or <c>empty</c>.
    /// </summary>
    public string BodyEncoding { get; set; } = BodyEncodings.Empty;

    public long BodySizeBytes { get; set; }

    /// <summary>The body as JSON, when the payload is valid JSON.</summary>
    public JsonElement? Body { get; set; }

    /// <summary>The body as text, when it is UTF-8 text that is not JSON.</summary>
    public string? BodyText { get; set; }

    /// <summary>The raw body bytes, when the payload is not valid UTF-8.</summary>
    public string? BodyBase64 { get; set; }
}

public sealed class ArchivedEventSource
{
    public string? Topic { get; set; }
    public string? Subscription { get; set; }
}

public static class BodyEncodings
{
    public const string Json = "json";
    public const string Text = "text";
    public const string Base64 = "base64";
    public const string AmqpValue = "amqp-value";
    public const string AmqpSequence = "amqp-sequence";
    public const string Empty = "empty";
}

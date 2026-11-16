using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Settings;

namespace PaymentServices.EventArchive.Services;

public interface IArchivedEventFactory
{
    ArchivedEvent Create(ServiceBusReceivedMessage message);
}

/// <summary>
/// Turns a received Service Bus message into the <see cref="ArchivedEvent"/>
/// that gets stored: every broker property, all application properties, and
/// the body in its most readable faithful form (JSON → text → base64).
/// </summary>
public sealed class ArchivedEventFactory : IArchivedEventFactory
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private readonly EventArchiveSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly string[] _messageTypePropertyNames;

    public ArchivedEventFactory(IOptions<EventArchiveSettings> settings, TimeProvider timeProvider)
    {
        _settings = settings.Value;
        _timeProvider = timeProvider;
        _messageTypePropertyNames = (_settings.MESSAGE_TYPE_PROPERTY_NAMES ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public ArchivedEvent Create(ServiceBusReceivedMessage message)
    {
        var archived = new ArchivedEvent
        {
            ArchiveId = EventBlobNaming.BuildArchiveId(
                message.EnqueuedTime, message.SequenceNumber, message.MessageId),
            ArchivedAtUtc = _timeProvider.GetUtcNow(),
            Source = new ArchivedEventSource
            {
                Topic = NullIfEmpty(_settings.SERVICE_BUS_TOPIC_NAME),
                Subscription = NullIfEmpty(_settings.SERVICE_BUS_SUBSCRIPTION_NAME)
            },

            MessageId = message.MessageId,
            Subject = message.Subject,
            CorrelationId = message.CorrelationId,
            ContentType = message.ContentType,
            SessionId = message.SessionId,
            PartitionKey = message.PartitionKey,
            TransactionPartitionKey = message.TransactionPartitionKey,
            ReplyTo = message.ReplyTo,
            ReplyToSessionId = message.ReplyToSessionId,
            To = message.To,

            EnqueuedTimeUtc = message.EnqueuedTime.ToUniversalTime(),
            ScheduledEnqueueTimeUtc = message.ScheduledEnqueueTime == default
                ? null
                : message.ScheduledEnqueueTime.ToUniversalTime(),
            ExpiresAtUtc = message.ExpiresAt == default ? null : message.ExpiresAt.ToUniversalTime(),
            TimeToLive = message.TimeToLive.ToString("c", CultureInfo.InvariantCulture),

            SequenceNumber = message.SequenceNumber,
            EnqueuedSequenceNumber = message.EnqueuedSequenceNumber,
            DeliveryCount = message.DeliveryCount,
            State = message.State.ToString(),

            DeadLetterSource = message.DeadLetterSource,
            DeadLetterReason = message.DeadLetterReason,
            DeadLetterErrorDescription = message.DeadLetterErrorDescription
        };

        foreach (var property in message.ApplicationProperties)
        {
            archived.ApplicationProperties[property.Key] = Normalize(property.Value);
        }

        archived.MessageType = ResolveMessageType(message);
        ReadBody(message, archived);

        return archived;
    }

    /// <summary>
    /// Message type: the first configured application property that carries a
    /// value, otherwise the Subject (label).
    /// </summary>
    private string? ResolveMessageType(ServiceBusReceivedMessage message)
    {
        foreach (var name in _messageTypePropertyNames)
        {
            foreach (var property in message.ApplicationProperties)
            {
                if (!string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = Convert.ToString(property.Value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return message.Subject;
    }

    private static void ReadBody(ServiceBusReceivedMessage message, ArchivedEvent archived)
    {
        ReadOnlyMemory<byte> bytes;
        try
        {
            bytes = message.Body.ToMemory();
        }
        catch (NotSupportedException)
        {
            // Not a binary (AMQP data) body — a sender used an AMQP value or
            // sequence body. Keep what it carried as text.
            ReadAmqpBody(message, archived);
            return;
        }

        archived.BodySizeBytes = bytes.Length;

        if (bytes.IsEmpty)
        {
            archived.BodyEncoding = BodyEncodings.Empty;
            return;
        }

        var content = bytes.Span.StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes;

        if (TryParseJson(content, out var json))
        {
            archived.BodyEncoding = BodyEncodings.Json;
            archived.Body = json;
            return;
        }

        try
        {
            archived.BodyText = StrictUtf8.GetString(content.Span);
            archived.BodyEncoding = BodyEncodings.Text;
        }
        catch (DecoderFallbackException)
        {
            archived.BodyBase64 = Convert.ToBase64String(bytes.Span);
            archived.BodyEncoding = BodyEncodings.Base64;
        }
    }

    private static void ReadAmqpBody(ServiceBusReceivedMessage message, ArchivedEvent archived)
    {
        var body = message.GetRawAmqpMessage().Body;

        if (body.TryGetValue(out var value))
        {
            archived.BodyEncoding = BodyEncodings.AmqpValue;
            archived.BodyText = value is byte[] binary
                ? Convert.ToBase64String(binary)
                : Convert.ToString(value, CultureInfo.InvariantCulture);
            return;
        }

        if (body.TryGetSequence(out var sequence))
        {
            var rows = sequence
                .Select(row => row.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).ToList())
                .ToList();

            archived.BodyEncoding = BodyEncodings.AmqpSequence;
            archived.BodyText = JsonSerializer.Serialize(rows, ArchiveJson.Options);
            return;
        }

        archived.BodyEncoding = BodyEncodings.Empty;
    }

    private static bool TryParseJson(ReadOnlyMemory<byte> content, out JsonElement json)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            json = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            json = default;
            return false;
        }
    }

    /// <summary>Makes an application property value safe to serialize as JSON.</summary>
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal => value,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
        byte[] binary => Convert.ToBase64String(binary),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

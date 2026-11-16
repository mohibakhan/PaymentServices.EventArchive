using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Services;
using PaymentServices.EventArchive.Settings;
using PaymentServices.EventArchive.UnitTests.TestSupport;

namespace PaymentServices.EventArchive.UnitTests;

public class ArchivedEventFactoryTests
{
    private static readonly DateTimeOffset Enqueued = new(2026, 11, 16, 20, 37, 45, 123, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 20, 37, 46, TimeSpan.Zero);

    private static ArchivedEventFactory CreateFactory(Action<EventArchiveSettings>? configure = null)
    {
        var settings = new EventArchiveSettings
        {
            SERVICE_BUS_TOPIC_NAME = "paymentservicestopic",
            SERVICE_BUS_SUBSCRIPTION_NAME = "eventarchive"
        };
        configure?.Invoke(settings);

        return new ArchivedEventFactory(Options.Create(settings), new FixedTimeProvider(Now));
    }

    private static ServiceBusReceivedMessage Message(
        BinaryData body,
        string? subject = "ReceiveCredit - Success",
        IDictionary<string, object>? properties = null) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: body,
            messageId: "message-1",
            correlationId: "correlation-1",
            subject: subject,
            contentType: "application/json",
            properties: properties,
            deliveryCount: 2,
            sequenceNumber: 12345,
            enqueuedTime: Enqueued);

    [Fact]
    public void Captures_the_broker_properties_and_where_the_event_came_from()
    {
        var archived = CreateFactory().Create(Message(BinaryData.FromString("{\"evolveId\":\"E1\"}")));

        Assert.Equal(EventBlobNaming.BuildArchiveId(Enqueued, 12345, "message-1"), archived.ArchiveId);
        Assert.Equal(Now, archived.ArchivedAtUtc);
        Assert.Equal("paymentservicestopic", archived.Source.Topic);
        Assert.Equal("eventarchive", archived.Source.Subscription);
        Assert.Equal("message-1", archived.MessageId);
        Assert.Equal("correlation-1", archived.CorrelationId);
        Assert.Equal("ReceiveCredit - Success", archived.Subject);
        Assert.Equal("application/json", archived.ContentType);
        Assert.Equal(Enqueued, archived.EnqueuedTimeUtc);
        Assert.Equal(12345, archived.SequenceNumber);
        Assert.Equal(2, archived.DeliveryCount);
    }

    [Fact]
    public void A_json_body_is_stored_as_json()
    {
        var archived = CreateFactory().Create(Message(BinaryData.FromString("{\"evolveId\":\"E1\",\"amount\":1.50}")));

        Assert.Equal(BodyEncodings.Json, archived.BodyEncoding);
        Assert.NotNull(archived.Body);
        Assert.Equal("E1", archived.Body!.Value.GetProperty("evolveId").GetString());
        Assert.Equal("1.50", archived.Body!.Value.GetProperty("amount").GetRawText());
        Assert.Null(archived.BodyText);
        Assert.Null(archived.BodyBase64);
    }

    [Fact]
    public void A_json_body_with_a_byte_order_mark_is_still_json()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}")).ToArray();

        var archived = CreateFactory().Create(Message(BinaryData.FromBytes(bytes)));

        Assert.Equal(BodyEncodings.Json, archived.BodyEncoding);
        Assert.Equal(bytes.Length, archived.BodySizeBytes);
    }

    [Fact]
    public void A_text_body_that_is_not_json_is_stored_as_text()
    {
        var archived = CreateFactory().Create(Message(BinaryData.FromString("payment received")));

        Assert.Equal(BodyEncodings.Text, archived.BodyEncoding);
        Assert.Equal("payment received", archived.BodyText);
        Assert.Null(archived.Body);
    }

    [Fact]
    public void A_binary_body_is_stored_as_base64()
    {
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0xC3 };

        var archived = CreateFactory().Create(Message(BinaryData.FromBytes(bytes)));

        Assert.Equal(BodyEncodings.Base64, archived.BodyEncoding);
        Assert.Equal(Convert.ToBase64String(bytes), archived.BodyBase64);
        Assert.Equal(4, archived.BodySizeBytes);
    }

    [Fact]
    public void An_empty_body_is_recorded_as_empty()
    {
        var archived = CreateFactory().Create(Message(BinaryData.FromBytes(Array.Empty<byte>())));

        Assert.Equal(BodyEncodings.Empty, archived.BodyEncoding);
        Assert.Equal(0, archived.BodySizeBytes);
    }

    [Fact]
    public void Application_properties_are_kept_in_a_json_safe_form()
    {
        var when = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var properties = new Dictionary<string, object>
        {
            ["State"] = "TransferCompleted",
            ["Attempt"] = 3,
            ["IsReturn"] = true,
            ["When"] = when,
            ["Elapsed"] = TimeSpan.FromSeconds(90)
        };

        var archived = CreateFactory().Create(Message(BinaryData.FromString("{}"), properties: properties));

        Assert.Equal("TransferCompleted", archived.ApplicationProperties["State"]);
        Assert.Equal(3, archived.ApplicationProperties["Attempt"]);
        Assert.True((bool)archived.ApplicationProperties["IsReturn"]!);
        Assert.Equal(when.ToString("O"), archived.ApplicationProperties["When"]);
        Assert.Equal("00:01:30", archived.ApplicationProperties["Elapsed"]);

        // The whole record must survive a JSON round trip.
        var json = JsonSerializer.Serialize(archived, ArchiveJson.Options);
        var restored = JsonSerializer.Deserialize<ArchivedEvent>(json, ArchiveJson.Options)!;
        Assert.Equal(archived.ArchiveId, restored.ArchiveId);
        Assert.Equal(5, restored.ApplicationProperties.Count);
    }

    [Fact]
    public void Message_type_is_the_subject_when_no_type_property_is_present()
    {
        var archived = CreateFactory().Create(Message(BinaryData.FromString("{}")));

        Assert.Equal("ReceiveCredit - Success", archived.MessageType);
    }

    [Fact]
    public void Message_type_comes_from_a_configured_application_property_when_present()
    {
        var properties = new Dictionary<string, object> { ["MessageType"] = "PaymentCompleted" };

        var archived = CreateFactory().Create(Message(BinaryData.FromString("{}"), properties: properties));

        Assert.Equal("PaymentCompleted", archived.MessageType);
        Assert.Equal("ReceiveCredit - Success", archived.Subject);
    }

    [Fact]
    public void The_type_property_names_are_configurable()
    {
        var properties = new Dictionary<string, object> { ["State"] = "TransferCompleted" };
        var factory = CreateFactory(settings => settings.MESSAGE_TYPE_PROPERTY_NAMES = "State");

        var archived = factory.Create(Message(BinaryData.FromString("{}"), properties: properties));

        Assert.Equal("TransferCompleted", archived.MessageType);
    }
}

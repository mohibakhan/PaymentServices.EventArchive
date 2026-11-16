using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Services;

namespace PaymentServices.EventArchive.Functions;

/// <summary>
/// Service Bus Trigger — subscribed to an UNFILTERED subscription (default
/// 1=1 rule), so it receives every message sent to the topic and archives each
/// one to blob storage as a single JSON blob.
///
/// MANUAL SETTLE (AutoCompleteMessages = false) — a message is completed only
/// AFTER its blob is safely written, so nothing can be acknowledged and lost:
///   - stored (or already stored by an earlier delivery) → Complete;
///   - storage failure → the error is logged and the message is left
///     UNSETTLED: its lock expires, the broker redelivers it, and after the
///     subscription's max delivery count it lands in the dead-letter queue.
///     The exception is deliberately NOT rethrown — a failed invocation makes
///     the Service Bus processor abandon the message at once, and the
///     immediate redeliveries would burn the whole delivery count within
///     seconds of a storage outage. Waiting out the lock spaces the retries;
///   - a message that cannot be turned into an archive record at all →
///     dead-lettered immediately with the reason, never dropped.
///
/// Watch the subscription's dead-letter queue and this function's error logs:
/// those are the two signals that events are not reaching the archive.
///
/// Archiving is idempotent: the blob name comes from the message's enqueued
/// time and sequence number, so a redelivery maps to the same blob and is a
/// no-op.
/// </summary>
public sealed class ArchiveEvent
{
    private readonly IArchivedEventFactory _factory;
    private readonly IEventArchiveStore _store;
    private readonly ILogger<ArchiveEvent> _logger;

    public ArchiveEvent(
        IArchivedEventFactory factory,
        IEventArchiveStore store,
        ILogger<ArchiveEvent> logger)
    {
        _factory = factory;
        _store = store;
        _logger = logger;
    }

    [Function(nameof(ArchiveEvent))]
    public async Task Run(
        [ServiceBusTrigger(
            topicName: "%eventArchive:AppSettings:SERVICE_BUS_TOPIC_NAME%",
            subscriptionName: "%eventArchive:AppSettings:SERVICE_BUS_SUBSCRIPTION_NAME%",
            Connection = "eventArchive:AppSettings:SERVICE_BUS_CONNSTRING",
            AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        ArchivedEvent archived;
        try
        {
            archived = _factory.Create(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cannot build an archive record for message {MessageId} (sequence {SequenceNumber}); dead-lettering.",
                message.MessageId, message.SequenceNumber);

            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason: "ArchiveRecordFailed",
                deadLetterErrorDescription: Truncate(ex.Message, 1024),
                cancellationToken: cancellationToken);
            return;
        }

        bool created;
        try
        {
            created = await _store.StoreAsync(archived, cancellationToken);
        }
        catch (Exception ex)
        {
            // Leave the message unsettled (see the class remarks): it comes
            // back when its lock expires, and dead-letters if this keeps failing.
            _logger.LogError(ex,
                "Failed to archive event {ArchiveId}. MessageId={MessageId} DeliveryCount={DeliveryCount}. " +
                "The message is left for redelivery.",
                archived.ArchiveId, message.MessageId, message.DeliveryCount);
            return;
        }

        if (created)
        {
            _logger.LogInformation(
                "Archived event {ArchiveId}. MessageId={MessageId} Subject={Subject} MessageType={MessageType}",
                archived.ArchiveId, archived.MessageId, archived.Subject, archived.MessageType);
        }
        else
        {
            _logger.LogInformation(
                "Event {ArchiveId} was already archived (redelivery, DeliveryCount={DeliveryCount}); completing.",
                archived.ArchiveId, message.DeliveryCount);
        }

        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

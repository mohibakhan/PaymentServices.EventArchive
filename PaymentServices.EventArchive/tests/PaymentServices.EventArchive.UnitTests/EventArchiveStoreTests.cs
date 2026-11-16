using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Services;
using PaymentServices.EventArchive.Settings;
using PaymentServices.EventArchive.UnitTests.TestSupport;

namespace PaymentServices.EventArchive.UnitTests;

public class EventArchiveStoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryEventBlobContainer _container = new();

    private EventArchiveStore CreateStore(int listPageSize = 500) =>
        new(_container, Options.Create(new EventArchiveSettings()), NullLogger<EventArchiveStore>.Instance)
        {
            ListPageSize = listPageSize
        };

    private static ArchivedEvent Event(
        DateTimeOffset enqueued,
        long sequenceNumber,
        string subject = "ReceiveCredit - Success",
        string? correlationId = null)
    {
        var messageId = $"message-{sequenceNumber}";
        return new ArchivedEvent
        {
            ArchiveId = EventBlobNaming.BuildArchiveId(enqueued, sequenceNumber, messageId),
            ArchivedAtUtc = enqueued,
            MessageId = messageId,
            Subject = subject,
            MessageType = subject,
            CorrelationId = correlationId,
            EnqueuedTimeUtc = enqueued,
            SequenceNumber = sequenceNumber,
            BodyEncoding = BodyEncodings.Text,
            BodyText = "body"
        };
    }

    private static EventSearchCriteria Criteria(
        DateTimeOffset from,
        DateTimeOffset to,
        int pageSize = 50,
        string? subject = null,
        string? messageType = null,
        string? messageId = null,
        string? correlationId = null,
        Dictionary<string, string>? properties = null,
        bool includeEvent = false,
        SearchContinuation? continuation = null) =>
        new()
        {
            From = from,
            To = to,
            PageSize = pageSize,
            Subject = subject,
            MessageType = messageType,
            MessageId = messageId,
            CorrelationId = correlationId,
            Properties = properties ?? new Dictionary<string, string>(),
            IncludeEvent = includeEvent,
            Continuation = continuation
        };

    private async Task StoreAllAsync(EventArchiveStore store, params ArchivedEvent[] events)
    {
        foreach (var archivedEvent in events)
        {
            Assert.True(await store.StoreAsync(archivedEvent, CancellationToken.None));
        }
    }

    // ---- Store / get ---------------------------------------------------------

    [Fact]
    public async Task A_stored_event_can_be_read_back_by_its_id()
    {
        var store = CreateStore();
        var original = Event(Day.AddHours(10), 1, correlationId: "correlation-1");
        original.ApplicationProperties["State"] = "TransferCompleted";

        await StoreAllAsync(store, original);
        var restored = await store.GetAsync(original.ArchiveId, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(original.ArchiveId, restored!.ArchiveId);
        Assert.Equal("message-1", restored.MessageId);
        Assert.Equal("ReceiveCredit - Success", restored.Subject);
        Assert.Equal("correlation-1", restored.CorrelationId);
        Assert.Equal(original.EnqueuedTimeUtc, restored.EnqueuedTimeUtc);
        Assert.Equal("body", restored.BodyText);
        Assert.Equal("TransferCompleted", ((JsonElement)restored.ApplicationProperties["State"]!).GetString());
    }

    [Fact]
    public async Task The_blob_lands_in_the_date_and_hour_folder_of_its_enqueued_time()
    {
        var store = CreateStore();
        var archived = Event(Day.AddHours(10).AddMinutes(30), 1);

        await StoreAllAsync(store, archived);

        Assert.Equal(new[] { $"2026/11/16/10/{archived.ArchiveId}.json" }, _container.Names);
    }

    [Fact]
    public async Task Storing_the_same_event_again_is_a_no_op()
    {
        var store = CreateStore();
        var archived = Event(Day.AddHours(10), 1);

        Assert.True(await store.StoreAsync(archived, CancellationToken.None));
        Assert.False(await store.StoreAsync(archived, CancellationToken.None));

        Assert.Single(_container.Names);
    }

    [Theory]
    [InlineData("20261116T100000000Z-999-00000000")]
    [InlineData("not-an-id")]
    [InlineData("../../etc")]
    public async Task An_unknown_or_malformed_id_returns_nothing(string archiveId)
    {
        Assert.Null(await CreateStore().GetAsync(archiveId, CancellationToken.None));
    }

    // ---- Search --------------------------------------------------------------

    [Fact]
    public async Task Subject_is_filtered_from_metadata_without_downloading_any_blob()
    {
        var store = CreateStore();
        var failure = Event(Day.AddHours(10).AddMinutes(5), 2, subject: "ReceiveCredit - Failure");
        await StoreAllAsync(
            store,
            Event(Day.AddHours(10), 1),
            failure,
            Event(Day.AddHours(10).AddMinutes(10), 3, subject: "ReturnPayment - Success"));

        var result = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), subject: "ReceiveCredit - Failure"), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(failure.ArchiveId, item.ArchiveId);
        Assert.Equal("ReceiveCredit - Failure", item.Subject);
        Assert.Equal("ReceiveCredit - Failure", item.MessageType);
        Assert.Equal("message-2", item.MessageId);
        Assert.Equal(failure.EnqueuedTimeUtc, item.EnqueuedTimeUtc);
        Assert.Null(item.Event);
        Assert.Null(result.ContinuationToken);
        Assert.Equal(1, result.Count);
        Assert.Equal(0, _container.DownloadCalls);
    }

    [Fact]
    public async Task Filters_accept_wildcards_and_ignore_case()
    {
        var store = CreateStore();
        await StoreAllAsync(
            store,
            Event(Day.AddHours(10), 1, subject: "ReceiveCredit - Success"),
            Event(Day.AddHours(11), 2, subject: "ReceiveCredit - Failure"),
            Event(Day.AddHours(12), 3, subject: "ReturnPayment - Success"));

        var result = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), subject: "receivecredit*"), CancellationToken.None);

        Assert.Equal(new[] { "message-1", "message-2" }, result.Items.Select(item => item.MessageId));
    }

    [Fact]
    public async Task Message_id_and_correlation_id_are_searchable()
    {
        var store = CreateStore();
        await StoreAllAsync(
            store,
            Event(Day.AddHours(10), 1, correlationId: "payment-A"),
            Event(Day.AddHours(11), 2, correlationId: "payment-B"),
            Event(Day.AddHours(12), 3));

        var byCorrelation = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), correlationId: "payment-B"), CancellationToken.None);
        var byMessageId = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), messageId: "message-3"), CancellationToken.None);

        Assert.Equal("message-2", Assert.Single(byCorrelation.Items).MessageId);
        Assert.Equal("message-3", Assert.Single(byMessageId.Items).MessageId);
    }

    [Fact]
    public async Task Application_properties_are_matched_against_the_stored_event()
    {
        var store = CreateStore();
        var completed = Event(Day.AddHours(10), 1);
        completed.ApplicationProperties["State"] = "TransferCompleted";
        completed.ApplicationProperties["Attempt"] = 3;
        completed.ApplicationProperties["IsReturn"] = true;
        var failed = Event(Day.AddHours(11), 2);
        failed.ApplicationProperties["State"] = "TransferFailed";
        failed.ApplicationProperties["Attempt"] = 3;
        var noProperties = Event(Day.AddHours(12), 3);
        await StoreAllAsync(store, completed, failed, noProperties);

        var byState = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), properties: new() { ["state"] = "transfercompleted" }),
            CancellationToken.None);
        var byNumberAndFlag = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), properties: new() { ["Attempt"] = "3", ["IsReturn"] = "true" }),
            CancellationToken.None);
        var byWildcard = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), properties: new() { ["State"] = "Transfer*" }),
            CancellationToken.None);

        Assert.Equal("message-1", Assert.Single(byState.Items).MessageId);
        Assert.Equal("message-1", Assert.Single(byNumberAndFlag.Items).MessageId);
        Assert.Equal(new[] { "message-1", "message-2" }, byWildcard.Items.Select(item => item.MessageId));
    }

    [Fact]
    public async Task Include_full_returns_the_whole_event_with_each_item()
    {
        var store = CreateStore();
        await StoreAllAsync(store, Event(Day.AddHours(10), 1));

        var result = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), includeEvent: true), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.NotNull(item.Event);
        Assert.Equal("body", item.Event!.BodyText);
    }

    [Fact]
    public async Task Only_events_enqueued_inside_the_range_are_returned()
    {
        var store = CreateStore();
        await StoreAllAsync(
            store,
            Event(Day.AddHours(10), 1),
            Event(Day.AddHours(11).AddMinutes(30), 2),
            Event(Day.AddHours(13), 3));

        var middle = await store.SearchAsync(
            Criteria(Day.AddHours(10).AddMinutes(30), Day.AddHours(11).AddMinutes(45)), CancellationToken.None);
        var early = await store.SearchAsync(
            Criteria(Day.AddHours(10), Day.AddHours(11).AddMinutes(15)), CancellationToken.None);
        var upperBoundIsExclusive = await store.SearchAsync(
            Criteria(Day.AddHours(10), Day.AddHours(13)), CancellationToken.None);

        Assert.Equal("message-2", Assert.Single(middle.Items).MessageId);
        Assert.Equal("message-1", Assert.Single(early.Items).MessageId);
        Assert.Equal(new[] { "message-1", "message-2" }, upperBoundIsExclusive.Items.Select(item => item.MessageId));
    }

    [Fact]
    public async Task Paging_walks_every_match_once_in_time_order_across_hours_and_days()
    {
        // Listing pages of 2 and result pages of 3, so a result page ends in
        // the middle of a listing page and resumes from there.
        var store = CreateStore(listPageSize: 2);
        var start = new DateTimeOffset(2026, 11, 14, 22, 0, 0, TimeSpan.Zero);
        await StoreAllAsync(
            store,
            Event(start.AddMinutes(10), 1),
            Event(start.AddMinutes(20), 2),
            Event(start.AddMinutes(65), 3),
            Event(start.AddHours(2).AddMinutes(15), 4),
            Event(start.AddHours(2).AddMinutes(45), 5),
            Event(start.AddHours(11), 6),
            Event(start.AddHours(27), 7));

        var from = start;
        var to = new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);
        var seen = new List<string?>();
        SearchContinuation? continuation = null;
        var requests = 0;

        do
        {
            var page = await store.SearchAsync(
                Criteria(from, to, pageSize: 3, continuation: continuation), CancellationToken.None);

            Assert.InRange(page.Items.Count, 0, 3);
            seen.AddRange(page.Items.Select(item => item.MessageId));

            continuation = null;
            if (page.ContinuationToken is not null)
            {
                Assert.True(ContinuationTokenCodec.TryDecode(page.ContinuationToken, out continuation));
            }

            Assert.True(++requests < 20, "Paging did not terminate.");
        }
        while (continuation is not null);

        Assert.Equal(
            new[] { "message-1", "message-2", "message-3", "message-4", "message-5", "message-6", "message-7" },
            seen);
    }

    [Fact]
    public async Task A_blob_without_metadata_is_still_found_by_reading_its_content()
    {
        var store = CreateStore();
        var archived = Event(Day.AddHours(10), 1, subject: "ReceiveCredit - Failure");
        await StoreAllAsync(store, archived, Event(Day.AddHours(11), 2));
        _container.StripMetadata($"2026/11/16/10/{archived.ArchiveId}.json");

        var result = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), subject: "ReceiveCredit - Failure"), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("message-1", item.MessageId);
        Assert.Equal("ReceiveCredit - Failure", item.Subject);
    }

    [Fact]
    public async Task A_subject_too_long_for_metadata_is_matched_from_the_content()
    {
        var store = CreateStore();
        var longSubject = new string('S', 600) + "-tail";
        await StoreAllAsync(
            store,
            Event(Day.AddHours(10), 1, subject: longSubject),
            Event(Day.AddHours(11), 2));

        var result = await store.SearchAsync(
            Criteria(Day, Day.AddDays(1), subject: "*-tail"), CancellationToken.None);

        Assert.Equal("message-1", Assert.Single(result.Items).MessageId);
    }

    [Fact]
    public async Task A_continuation_from_a_different_date_range_is_refused()
    {
        var store = CreateStore();
        var foreign = new SearchContinuation("2020/01/01/", null, null, Day, Day.AddDays(1));

        await Assert.ThrowsAsync<InvalidContinuationTokenException>(() =>
            store.SearchAsync(Criteria(Day, Day.AddDays(1), continuation: foreign), CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_range_returns_no_items_and_no_token()
    {
        var store = CreateStore();
        await StoreAllAsync(store, Event(Day.AddHours(10), 1));

        var result = await store.SearchAsync(
            Criteria(Day.AddDays(2), Day.AddDays(3)), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Null(result.ContinuationToken);
    }
}

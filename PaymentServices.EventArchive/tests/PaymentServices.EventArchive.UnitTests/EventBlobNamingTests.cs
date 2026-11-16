using PaymentServices.EventArchive.Services;

namespace PaymentServices.EventArchive.UnitTests;

public class EventBlobNamingTests
{
    private static readonly DateTimeOffset Enqueued = new(2026, 11, 16, 20, 37, 45, 123, TimeSpan.Zero);

    [Fact]
    public void Archive_id_encodes_time_sequence_and_a_message_id_hash()
    {
        var id = EventBlobNaming.BuildArchiveId(Enqueued, 12345, "message-1");

        Assert.StartsWith("20261116T203745123Z-12345-", id);
        Assert.True(EventBlobNaming.IsValidArchiveId(id));
    }

    [Fact]
    public void Archive_id_is_the_same_on_every_delivery_of_a_message()
    {
        Assert.Equal(
            EventBlobNaming.BuildArchiveId(Enqueued, 12345, "message-1"),
            EventBlobNaming.BuildArchiveId(Enqueued, 12345, "message-1"));
    }

    [Fact]
    public void Different_messages_with_the_same_time_and_sequence_get_different_ids()
    {
        Assert.NotEqual(
            EventBlobNaming.BuildArchiveId(Enqueued, 12345, "message-1"),
            EventBlobNaming.BuildArchiveId(Enqueued, 12345, "message-2"));
    }

    [Fact]
    public void Archive_id_uses_utc_whatever_offset_the_time_carries()
    {
        var eastern = new DateTimeOffset(2026, 11, 16, 15, 37, 45, 123, TimeSpan.FromHours(-5));

        Assert.Equal(
            EventBlobNaming.BuildArchiveId(Enqueued, 1, "m"),
            EventBlobNaming.BuildArchiveId(eastern, 1, "m"));
    }

    [Fact]
    public void Blob_name_is_partitioned_by_utc_date_and_hour()
    {
        var id = EventBlobNaming.BuildArchiveId(Enqueued, 12345, "message-1");

        var blobName = EventBlobNaming.BlobNameFromArchiveId(id);

        Assert.Equal($"2026/11/16/20/{id}.json", blobName);
        Assert.Equal(id, EventBlobNaming.ArchiveIdFromBlobName(blobName!));
        Assert.Equal(Enqueued, EventBlobNaming.TimestampFromBlobName(blobName!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("../../secrets")]
    [InlineData("20261116T203745123Z-12345")]
    [InlineData("20261116T203745123Z-12345-XYZ")]
    [InlineData("20261116T203745123Z-12345-a1b2c3d4/../x")]
    [InlineData("20261116T203745123Z-12345-a1b2c3d4\n")]
    public void Malformed_archive_ids_are_rejected(string? archiveId)
    {
        Assert.False(EventBlobNaming.IsValidArchiveId(archiveId));
        Assert.Null(EventBlobNaming.BlobNameFromArchiveId(archiveId));
    }

    [Fact]
    public void Blobs_not_written_by_the_archiver_have_no_timestamp()
    {
        Assert.Null(EventBlobNaming.TimestampFromBlobName("2026/11/16/20/readme.txt"));
    }

    [Fact]
    public void A_range_inside_one_hour_plans_that_hour()
    {
        var prefixes = EventBlobNaming.PlanPrefixes(
            new DateTimeOffset(2026, 11, 16, 20, 10, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 16, 20, 50, 0, TimeSpan.Zero));

        Assert.Equal(new[] { "2026/11/16/20/" }, prefixes);
    }

    [Fact]
    public void A_whole_day_plans_a_single_day_prefix()
    {
        var prefixes = EventBlobNaming.PlanPrefixes(
            new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { "2026/11/16/" }, prefixes);
    }

    [Fact]
    public void A_range_across_days_uses_hours_at_the_edges_and_days_in_the_middle()
    {
        var prefixes = EventBlobNaming.PlanPrefixes(
            new DateTimeOffset(2026, 11, 15, 22, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 18, 1, 15, 0, TimeSpan.Zero));

        Assert.Equal(
            new[]
            {
                "2026/11/15/22/", "2026/11/15/23/",
                "2026/11/16/", "2026/11/17/",
                "2026/11/18/00/", "2026/11/18/01/"
            },
            prefixes);
    }

    [Fact]
    public void An_empty_range_plans_nothing()
    {
        var instant = new DateTimeOffset(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

        Assert.Empty(EventBlobNaming.PlanPrefixes(instant, instant));
    }
}

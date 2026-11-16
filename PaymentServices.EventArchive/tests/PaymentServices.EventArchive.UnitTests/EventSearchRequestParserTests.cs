using Microsoft.Extensions.Options;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Services;
using PaymentServices.EventArchive.Settings;
using PaymentServices.EventArchive.UnitTests.TestSupport;

namespace PaymentServices.EventArchive.UnitTests;

public class EventSearchRequestParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private static EventSearchRequestParser CreateParser() =>
        new(Options.Create(new EventArchiveSettings()), new FixedTimeProvider(Now));

    private static Dictionary<string, string?> Query(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);

    private static EventSearchCriteria Parse(params (string Key, string Value)[] pairs)
    {
        Assert.True(CreateParser().TryParse(Query(pairs), out var criteria, out var error), error);
        return criteria!;
    }

    private static string ParseError(params (string Key, string Value)[] pairs)
    {
        Assert.False(CreateParser().TryParse(Query(pairs), out _, out var error));
        return error!;
    }

    [Fact]
    public void With_no_dates_the_search_covers_the_last_24_hours()
    {
        var criteria = Parse();

        Assert.Equal(Now.AddHours(-24), criteria.From);
        Assert.Equal(Now, criteria.To);
        Assert.Equal(50, criteria.PageSize);
        Assert.False(criteria.IncludeEvent);
        Assert.Empty(criteria.Properties);
    }

    [Fact]
    public void Date_selects_one_whole_utc_day()
    {
        var criteria = Parse(("date", "2026-11-10"));

        Assert.Equal(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), criteria.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), criteria.To);
    }

    [Fact]
    public void From_and_to_accept_instants_and_treat_a_missing_offset_as_utc()
    {
        var criteria = Parse(("from", "2026-11-10T08:00:00"), ("to", "2026-11-10T10:30:00-05:00"));

        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), criteria.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 15, 30, 0, TimeSpan.Zero), criteria.To);
    }

    [Fact]
    public void A_plus_sign_that_arrived_as_a_space_is_still_understood()
    {
        var criteria = Parse(("from", "2026-11-10T08:00:00 02:00"), ("to", "2026-11-10T12:00:00Z"));

        Assert.Equal(new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero), criteria.From);
    }

    [Fact]
    public void A_bare_date_in_to_includes_that_whole_day()
    {
        var criteria = Parse(("from", "2026-11-09"), ("to", "2026-11-10"));

        Assert.Equal(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), criteria.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), criteria.To);
    }

    [Fact]
    public void A_range_reaching_into_the_future_is_cut_off_at_now()
    {
        var criteria = Parse(("date", "2026-11-16"));

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), criteria.From);
        Assert.Equal(Now.AddMinutes(1), criteria.To);
    }

    [Fact]
    public void Filters_and_application_properties_are_picked_up()
    {
        var criteria = Parse(
            ("subject", "ReceiveCredit*"),
            ("messageType", "ReceiveCredit - Success"),
            ("messageId", "message-1"),
            ("correlationId", "correlation-1"),
            ("prop.State", "TransferCompleted"),
            ("PROP.fintechId", "F1"),
            ("include", "full"),
            ("pageSize", "25"));

        Assert.Equal("ReceiveCredit*", criteria.Subject);
        Assert.Equal("ReceiveCredit - Success", criteria.MessageType);
        Assert.Equal("message-1", criteria.MessageId);
        Assert.Equal("correlation-1", criteria.CorrelationId);
        Assert.Equal("TransferCompleted", criteria.Properties["State"]);
        Assert.Equal("F1", criteria.Properties["fintechId"]);
        Assert.True(criteria.IncludeEvent);
        Assert.Equal(25, criteria.PageSize);
    }

    [Fact]
    public void Unknown_parameters_such_as_the_function_key_are_ignored()
    {
        var criteria = Parse(("code", "secret-function-key"), ("subject", "x"));

        Assert.Equal("x", criteria.Subject);
        Assert.Empty(criteria.Properties);
    }

    [Fact]
    public void Page_size_is_capped_at_the_configured_maximum()
    {
        Assert.Equal(500, Parse(("pageSize", "100000")).PageSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void A_bad_page_size_is_rejected(string pageSize)
    {
        Assert.Contains("pageSize", ParseError(("pageSize", pageSize)));
    }

    [Fact]
    public void Date_cannot_be_combined_with_from_or_to()
    {
        Assert.Contains("either", ParseError(("date", "2026-11-10"), ("from", "2026-11-09")));
    }

    [Fact]
    public void An_unreadable_date_is_rejected()
    {
        Assert.Contains("'from'", ParseError(("from", "yesterday")));
        Assert.Contains("'to'", ParseError(("to", "32/13/2026")));
        Assert.Contains("'date'", ParseError(("date", "2026-11-10T10:00:00Z")));
    }

    [Fact]
    public void From_must_be_before_to()
    {
        Assert.Contains("earlier", ParseError(("from", "2026-11-10T10:00:00Z"), ("to", "2026-11-10T09:00:00Z")));
    }

    [Fact]
    public void A_range_longer_than_the_maximum_is_rejected()
    {
        Assert.Contains("31 days", ParseError(("from", "2026-09-01"), ("to", "2026-11-01")));
    }

    [Fact]
    public void A_date_at_the_end_of_the_calendar_is_rejected_rather_than_crashing()
    {
        Assert.Contains("calendar", ParseError(("date", "9999-12-31")));
    }

    [Fact]
    public void A_property_filter_needs_a_name()
    {
        Assert.Contains("prop.{name}", ParseError(("prop.", "x")));
    }

    [Fact]
    public void An_unknown_include_value_is_rejected()
    {
        Assert.Contains("include", ParseError(("include", "everything")));
    }

    [Fact]
    public void A_continuation_token_round_trips_and_pins_the_date_range()
    {
        var from = new DateTimeOffset(2026, 11, 14, 3, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 15, 3, 0, 0, TimeSpan.Zero);
        var token = ContinuationTokenCodec.Encode(
            new SearchContinuation("2026/11/14/05/", "marker+/=", "2026/11/14/05/x.json", from, to));

        // No dates on the request: without the token this would be "the last 24 hours".
        var criteria = Parse(("continuationToken", token), ("subject", "x"));

        Assert.NotNull(criteria.Continuation);
        Assert.Equal("2026/11/14/05/", criteria.Continuation!.Prefix);
        Assert.Equal("marker+/=", criteria.Continuation.Marker);
        Assert.Equal("2026/11/14/05/x.json", criteria.Continuation.LastName);
        Assert.Equal(from, criteria.From);
        Assert.Equal(to, criteria.To);
    }

    [Fact]
    public void A_continuation_token_covering_more_than_the_maximum_range_is_rejected()
    {
        var token = ContinuationTokenCodec.Encode(new SearchContinuation(
            "2020/01/01/",
            null,
            null,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Contains("continuationToken", ParseError(("continuationToken", token)));
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("e30")] // {}
    [InlineData("eyJwIjoiLi4vLi4vIn0")] // {"p":"../../"}
    public void A_forged_or_damaged_continuation_token_is_rejected(string token)
    {
        Assert.Contains("continuationToken", ParseError(("continuationToken", token)));
    }
}

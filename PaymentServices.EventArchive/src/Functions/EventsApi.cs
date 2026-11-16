using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PaymentServices.EventArchive.Models;
using PaymentServices.EventArchive.Services;

namespace PaymentServices.EventArchive.Functions;

/// <summary>
/// Read API over the archive.
///
///   GET /api/events                 search (see <see cref="EventSearchRequestParser"/>)
///   GET /api/events/{archiveId}     one archived event in full
/// </summary>
public sealed class EventsApi
{
    private readonly IEventArchiveStore _store;
    private readonly EventSearchRequestParser _parser;
    private readonly ILogger<EventsApi> _logger;

    public EventsApi(
        IEventArchiveStore store,
        EventSearchRequestParser parser,
        ILogger<EventsApi> logger)
    {
        _store = store;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>
    /// Search by date range, subject, message type, message id, correlation id
    /// and application properties. All filters are AND-ed; values match
    /// case-insensitively and may use '*' as a wildcard.
    /// </summary>
    [Function("SearchEvents")]
    public async Task<IActionResult> Search(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "events")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var query = req.Query.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value.ToString()));

        if (!_parser.TryParse(query, out var criteria, out var error))
        {
            return Json(StatusCodes.Status400BadRequest, new ApiError { Error = "Invalid request", Message = error });
        }

        try
        {
            var result = await _store.SearchAsync(criteria, cancellationToken);

            _logger.LogInformation(
                "Event search returned {Count} item(s) after scanning {BlobsScanned} blob(s). " +
                "From={From:o} To={To:o} MoreAvailable={MoreAvailable}",
                result.Count, result.BlobsScanned, result.From, result.To, result.ContinuationToken is not null);

            return Json(StatusCodes.Status200OK, result);
        }
        catch (InvalidContinuationTokenException ex)
        {
            return Json(StatusCodes.Status400BadRequest, new ApiError { Error = "Invalid request", Message = ex.Message });
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status400BadRequest && criteria.Continuation is not null)
        {
            // Storage rejected the listing marker inside the token.
            _logger.LogWarning(ex, "Storage rejected a continuation token.");
            return Json(StatusCodes.Status400BadRequest, new ApiError
            {
                Error = "Invalid request",
                Message = "'continuationToken' is no longer valid. Start the search again without it."
            });
        }
    }

    [Function("GetEvent")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "events/{archiveId}")] HttpRequest req,
        string archiveId,
        CancellationToken cancellationToken)
    {
        if (!EventBlobNaming.IsValidArchiveId(archiveId))
        {
            return Json(StatusCodes.Status400BadRequest, new ApiError
            {
                Error = "Invalid request",
                Message = "The archive id is not well formed. Use the 'archiveId' value returned by a search."
            });
        }

        var archived = await _store.GetAsync(archiveId, cancellationToken);
        if (archived is null)
        {
            return Json(StatusCodes.Status404NotFound, new ApiError
            {
                Error = "Not found",
                Message = $"No archived event with id '{archiveId}'."
            });
        }

        return Json(StatusCodes.Status200OK, archived);
    }

    /// <summary>
    /// Serializes with the archive's own JSON options so responses look exactly
    /// like the stored blobs, whatever the host's MVC JSON settings are.
    /// </summary>
    private static ContentResult Json(int statusCode, object value) => new()
    {
        StatusCode = statusCode,
        ContentType = "application/json; charset=utf-8",
        Content = JsonSerializer.Serialize(value, value.GetType(), ArchiveJson.Options)
    };
}

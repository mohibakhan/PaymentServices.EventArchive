using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PaymentServices.EventArchive.Services;

namespace PaymentServices.EventArchive.Functions;

/// <summary>
/// GET /api/health — 200 when the archive's blob storage can be reached, 503
/// otherwise. Anonymous so probes and the release smoke test can call it; it
/// returns no detail about the failure (that goes to the log).
/// </summary>
public sealed class HealthCheck
{
    private readonly IEventBlobContainer _container;
    private readonly ILogger<HealthCheck> _logger;

    public HealthCheck(IEventBlobContainer container, ILogger<HealthCheck> logger)
    {
        _container = container;
        _logger = logger;
    }

    [Function(nameof(HealthCheck))]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        try
        {
            await _container.PingAsync(cancellationToken);
            return new ContentResult
            {
                StatusCode = StatusCodes.Status200OK,
                ContentType = "application/json; charset=utf-8",
                Content = "{\"status\":\"Healthy\"}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Health check failed: blob storage is not reachable.");
            return new ContentResult
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
                ContentType = "application/json; charset=utf-8",
                Content = "{\"status\":\"Unhealthy\"}"
            };
        }
    }
}

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace PaymentServices.EventArchive.Services;

/// <summary>Azure Blob Storage implementation of <see cref="IEventBlobContainer"/>.</summary>
public sealed class AzureEventBlobContainer : IEventBlobContainer
{
    private static readonly IReadOnlyDictionary<string, string> NoMetadata =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public AzureEventBlobContainer(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task<bool> UploadIfNotExistsAsync(
        string name,
        BinaryData content,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);

        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            Metadata = metadata,

            // Create-only: never overwrite an event that is already archived.
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
        };

        try
        {
            await _container.GetBlobClient(name).UploadAsync(content, options, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // BlobAlreadyExists / ConditionNotMet — a redelivery of a message
            // that was archived before its completion reached the broker.
            return false;
        }
    }

    public async Task<BlobListPage> ListPageAsync(
        string prefix,
        string? marker,
        int pageSizeHint,
        CancellationToken cancellationToken)
    {
        var pages = _container
            .GetBlobsAsync(
                traits: BlobTraits.Metadata,
                states: BlobStates.None,
                prefix: prefix,
                cancellationToken: cancellationToken)
            .AsPages(marker, pageSizeHint);

        try
        {
            await foreach (var page in pages)
            {
                var items = new List<BlobEntry>(page.Values.Count);
                foreach (var blob in page.Values)
                {
                    IReadOnlyDictionary<string, string> metadata = blob.Metadata is { Count: > 0 }
                        ? new Dictionary<string, string>(blob.Metadata, StringComparer.OrdinalIgnoreCase)
                        : NoMetadata;

                    items.Add(new BlobEntry(blob.Name, metadata, blob.Properties.ContentLength ?? 0));
                }

                // Only the first page — the caller drives paging with the marker.
                return new BlobListPage(
                    items,
                    string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The container is created on the first archived event; until
            // then there is simply nothing to list.
        }

        return new BlobListPage(Array.Empty<BlobEntry>(), null);
    }

    public async Task<BinaryData?> DownloadAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.GetBlobClient(name).DownloadContentAsync(cancellationToken);
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await _container.ExistsAsync(cancellationToken);
    }

    /// <summary>Creates the container on first write, once per process.</summary>
    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }
}

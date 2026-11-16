using PaymentServices.EventArchive.Services;

namespace PaymentServices.EventArchive.UnitTests.TestSupport;

/// <summary>
/// In-memory stand-in for blob storage: ordinal name order, prefix listing
/// with markers, create-only uploads — the behaviours the archive relies on.
/// </summary>
public sealed class InMemoryEventBlobContainer : IEventBlobContainer
{
    private readonly SortedDictionary<string, (BinaryData Content, Dictionary<string, string> Metadata)> _blobs =
        new(StringComparer.Ordinal);

    public int ListCalls { get; private set; }
    public int DownloadCalls { get; private set; }
    public IReadOnlyCollection<string> Names => _blobs.Keys;

    public Task<bool> UploadIfNotExistsAsync(
        string name,
        BinaryData content,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        lock (_blobs)
        {
            if (_blobs.ContainsKey(name))
            {
                return Task.FromResult(false);
            }

            _blobs[name] = (content, new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase));
            return Task.FromResult(true);
        }
    }

    public Task<BlobListPage> ListPageAsync(
        string prefix,
        string? marker,
        int pageSizeHint,
        CancellationToken cancellationToken)
    {
        lock (_blobs)
        {
            ListCalls++;

            // The marker is the first name of the page it fetches.
            var remaining = _blobs
                .Where(blob => blob.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(blob => marker is null || string.CompareOrdinal(blob.Key, marker) >= 0)
                .ToList();

            var items = remaining
                .Take(pageSizeHint)
                .Select(blob => new BlobEntry(blob.Key, blob.Value.Metadata, blob.Value.Content.ToMemory().Length))
                .ToList();

            var nextMarker = remaining.Count > pageSizeHint ? remaining[pageSizeHint].Key : null;
            return Task.FromResult(new BlobListPage(items, nextMarker));
        }
    }

    public Task<BinaryData?> DownloadAsync(string name, CancellationToken cancellationToken)
    {
        lock (_blobs)
        {
            DownloadCalls++;
            return Task.FromResult<BinaryData?>(_blobs.TryGetValue(name, out var blob) ? blob.Content : null);
        }
    }

    public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Removes all metadata from a blob, as if it had been written by another tool.</summary>
    public void StripMetadata(string name)
    {
        lock (_blobs)
        {
            _blobs[name] = (_blobs[name].Content, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}

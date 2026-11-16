namespace PaymentServices.EventArchive.Services;

/// <summary>One blob as returned by a listing: name, metadata and size — no content.</summary>
public sealed record BlobEntry(string Name, IReadOnlyDictionary<string, string> Metadata, long SizeBytes);

/// <summary>One page of a listing. <see cref="NextMarker"/> is null on the last page.</summary>
public sealed record BlobListPage(IReadOnlyList<BlobEntry> Items, string? NextMarker);

/// <summary>
/// The few blob operations the archive needs. Kept as a seam so the archive
/// and search logic can be unit tested without a storage account.
/// </summary>
public interface IEventBlobContainer
{
    /// <summary>Creates the blob. Returns false (and writes nothing) when it already exists.</summary>
    Task<bool> UploadIfNotExistsAsync(
        string name,
        BinaryData content,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken);

    /// <summary>Lists blobs under a prefix in name order, including their metadata.</summary>
    Task<BlobListPage> ListPageAsync(
        string prefix,
        string? marker,
        int pageSizeHint,
        CancellationToken cancellationToken);

    /// <summary>The blob's content, or null when it does not exist.</summary>
    Task<BinaryData?> DownloadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Round-trips to storage; throws when it cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}

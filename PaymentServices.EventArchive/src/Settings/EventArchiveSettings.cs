namespace PaymentServices.EventArchive.Settings;

/// <summary>
/// EventArchive configuration. Bound from the <c>eventArchive:AppSettings</c>
/// section, which the release pipeline pushes to Azure App Configuration from
/// deploy_parameters.
///
/// NOTE — the Service Bus trigger binding is resolved by the Functions HOST,
/// which cannot see App Configuration, so its values are plain Function App
/// application settings:
///   <c>SERVICE_BUS_CONNSTRING</c> — added to the Function App by hand;
///   <c>SERVICE_BUS_TOPIC_NAME</c>, <c>SERVICE_BUS_SUBSCRIPTION_NAME</c> — set
///   by the release pipeline.
/// The storage connection string is likewise added by hand, as
/// <c>STORAGE_CONNSTRING</c>. No pipeline writes either connection string.
/// </summary>
public sealed class EventArchiveSettings
{
    public const string SectionName = "eventArchive:AppSettings";

    // -------------------------------------------------------------------------
    // Service Bus — filled from the root application settings of the same
    // name (see Program.cs); recorded on each archived event as its source.
    // -------------------------------------------------------------------------
    public string SERVICE_BUS_TOPIC_NAME { get; set; } = string.Empty;
    public string SERVICE_BUS_SUBSCRIPTION_NAME { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Blob storage — set EITHER a connection string OR an account URL
    // (https://{account}.blob.core.windows.net) to use managed identity.
    // STORAGE_CONNSTRING is also read from the plain application setting of
    // the same name (see Program.cs).
    // -------------------------------------------------------------------------
    public string? STORAGE_CONNSTRING { get; set; }
    public string? STORAGE_ACCOUNT_URL { get; set; }
    public string STORAGE_CONTAINER_NAME { get; set; } = "events";

    /// <summary>
    /// Comma-separated application property names checked (in order) for the
    /// message type. When none is present the Subject/Label is the message type.
    /// </summary>
    public string MESSAGE_TYPE_PROPERTY_NAMES { get; set; } = "messageType,eventType";

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------
    public int SEARCH_DEFAULT_PAGE_SIZE { get; set; } = 50;
    public int SEARCH_MAX_PAGE_SIZE { get; set; } = 500;

    /// <summary>Largest from/to window a single search request may cover.</summary>
    public int SEARCH_MAX_RANGE_DAYS { get; set; } = 31;

    /// <summary>
    /// How long one search request may keep scanning before it returns what it
    /// has plus a continuation token. Keeps selective searches over busy
    /// periods inside the HTTP timeout.
    /// </summary>
    public int SEARCH_TIME_BUDGET_SECONDS { get; set; } = 20;
}

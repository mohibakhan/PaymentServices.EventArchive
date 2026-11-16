namespace PaymentServices.EventArchive.Settings;

/// <summary>
/// EventArchive configuration. Bound from the <c>eventArchive:AppSettings</c> section.
///
/// NOTE — the Service Bus trigger binding (topic, subscription, connection) is
/// resolved by the Functions HOST, not this worker, so those three values must
/// exist as Function App application settings. The release pipeline writes every
/// deploy_parameters key as an application setting for that reason.
/// </summary>
public sealed class EventArchiveSettings
{
    public const string SectionName = "eventArchive:AppSettings";

    // -------------------------------------------------------------------------
    // Service Bus (also read by the trigger binding)
    // -------------------------------------------------------------------------
    public string SERVICE_BUS_TOPIC_NAME { get; set; } = string.Empty;
    public string SERVICE_BUS_SUBSCRIPTION_NAME { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Blob storage — set EITHER a connection string OR an account URL
    // (https://{account}.blob.core.windows.net) to use managed identity.
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

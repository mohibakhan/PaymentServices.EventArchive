using System.Diagnostics.CodeAnalysis;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentServices.EventArchive.Services;
using PaymentServices.EventArchive.Settings;

namespace PaymentServices.EventArchive;

[ExcludeFromCodeCoverage]
public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = new HostBuilder()
            .ConfigureAppConfiguration(SetupAppConfiguration)
            .ConfigureFunctionsWebApplication()
            .ConfigureServices((context, services) =>
            {
                var config = context.Configuration;

                // Application Insights
                services.AddApplicationInsightsTelemetryWorkerService();
                services.ConfigureFunctionsApplicationInsights();

                // Settings
                services.AddOptions<EventArchiveSettings>()
                    .Configure<IConfiguration>((settings, cfg) =>
                    {
                        cfg.GetSection(EventArchiveSettings.SectionName).Bind(settings);

                        // The trigger's topic and subscription are host-level
                        // application settings (not App Configuration keys).
                        settings.SERVICE_BUS_TOPIC_NAME =
                            cfg["SERVICE_BUS_TOPIC_NAME"] ?? settings.SERVICE_BUS_TOPIC_NAME;
                        settings.SERVICE_BUS_SUBSCRIPTION_NAME =
                            cfg["SERVICE_BUS_SUBSCRIPTION_NAME"] ?? settings.SERVICE_BUS_SUBSCRIPTION_NAME;

                        // The storage connection string is added to the Function
                        // App by hand as a plain STORAGE_CONNSTRING setting.
                        if (string.IsNullOrWhiteSpace(settings.STORAGE_CONNSTRING))
                        {
                            settings.STORAGE_CONNSTRING = cfg["STORAGE_CONNSTRING"];
                        }
                    });

                services.AddSingleton(TimeProvider.System);

                // Blob storage
                services.AddSingleton(sp => CreateContainerClient(
                    sp.GetRequiredService<IOptions<EventArchiveSettings>>().Value, config));
                services.AddSingleton<IEventBlobContainer, AzureEventBlobContainer>();

                // Archive + search
                services.AddSingleton<IArchivedEventFactory, ArchivedEventFactory>();
                services.AddSingleton<IEventArchiveStore, EventArchiveStore>();
                services.AddSingleton<EventSearchRequestParser>();
            })
            .ConfigureLogging(logging =>
            {
                // Application Insights adds a Warning-and-above filter by
                // default; remove it so Information logs are kept.
                logging.Services.Configure<LoggerFilterOptions>(options =>
                {
                    var defaultRule = options.Rules.FirstOrDefault(rule =>
                        rule.ProviderName ==
                        "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
                    if (defaultRule is not null)
                    {
                        options.Rules.Remove(defaultRule);
                    }
                });
            })
            .Build();

        await host.RunAsync();
    }

    /// <summary>
    /// Environment variables (the Function App's application settings) first,
    /// then — when AppConfig:Endpoint and AZURE_CLIENT_ID are set — Azure App
    /// Configuration with Key Vault references, same as the other payment
    /// services.
    /// </summary>
    private static void SetupAppConfiguration(IConfigurationBuilder builder)
    {
        builder.AddEnvironmentVariables();
        var settings = builder.Build();

        var appConfigUrl = settings["AppConfig:Endpoint"];
        var azureClientId = settings["AZURE_CLIENT_ID"];

        if (!string.IsNullOrWhiteSpace(appConfigUrl) && !string.IsNullOrWhiteSpace(azureClientId))
        {
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = azureClientId
            });

            builder.AddAzureAppConfiguration(options =>
            {
                options
                    .Connect(new Uri(appConfigUrl), credential)
                    .Select("eventArchive:*")
                    .ConfigureKeyVault(kv => kv.SetCredential(credential));
            });
        }
    }

    /// <summary>
    /// Connection string when one is configured, otherwise the account URL
    /// with the app's managed identity.
    /// </summary>
    private static BlobContainerClient CreateContainerClient(EventArchiveSettings settings, IConfiguration config)
    {
        var containerName = string.IsNullOrWhiteSpace(settings.STORAGE_CONTAINER_NAME)
            ? "events"
            : settings.STORAGE_CONTAINER_NAME;

        if (!string.IsNullOrWhiteSpace(settings.STORAGE_CONNSTRING))
        {
            return new BlobContainerClient(settings.STORAGE_CONNSTRING, containerName);
        }

        if (!string.IsNullOrWhiteSpace(settings.STORAGE_ACCOUNT_URL))
        {
            var credentialOptions = new DefaultAzureCredentialOptions();
            var azureClientId = config["AZURE_CLIENT_ID"];
            if (!string.IsNullOrWhiteSpace(azureClientId))
            {
                credentialOptions.ManagedIdentityClientId = azureClientId;
            }

            var containerUri = new Uri($"{settings.STORAGE_ACCOUNT_URL.TrimEnd('/')}/{containerName}");
            return new BlobContainerClient(containerUri, new DefaultAzureCredential(credentialOptions));
        }

        throw new InvalidOperationException(
            "Blob storage is not configured. Add a STORAGE_CONNSTRING application setting, " +
            $"or set {EventArchiveSettings.SectionName}:STORAGE_ACCOUNT_URL to use managed identity.");
    }
}

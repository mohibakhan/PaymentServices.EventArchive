# PaymentServices.EventArchive

A .NET 10 isolated Azure Functions app that keeps a permanent copy of every message sent to a Service Bus topic and lets you search that archive over HTTP.

It listens on a subscription that has **no filter**, so it receives everything published to the topic. Each message becomes one JSON blob holding the full event: every broker property, all application properties, and the body.

## How it works

`ArchiveEvent` (Service Bus trigger) turns each message into an archive record and writes it to blob storage, then completes the message. The message is completed only after the blob is written, so an event is never acknowledged and lost. If storage is unavailable the message is redelivered and, after the subscription's max delivery count, dead-lettered. Archiving is idempotent: a redelivered message maps to the same blob and is skipped.

Blobs are laid out by the message's enqueued time (UTC):

```
{container}/{yyyy}/{MM}/{dd}/{HH}/{archiveId}.json
archiveId = {yyyyMMddTHHmmssfffZ}-{sequenceNumber}-{messageIdHash}
```

Subject, message type, message id, correlation id and enqueued time are also written as blob metadata. A search lists the date folders that cover the requested range and filters on that metadata without opening the blobs; it only downloads a blob when it has to (an application-property filter, `include=full`, or a value too long for metadata).

**Message type** is the Subject (label), unless the message carries one of the application properties named in `MESSAGE_TYPE_PROPERTY_NAMES`, in which case that property's value is used.

## API

All endpoints are `GET`. `events` endpoints need a function key (`?code=` or the `x-functions-key` header); `health` is anonymous.

| Endpoint | Purpose |
| --- | --- |
| `/api/events` | Search the archive |
| `/api/events/{archiveId}` | One archived event in full |
| `/api/health` | 200 when blob storage is reachable, otherwise 503 |

### Search parameters

| Parameter | Meaning |
| --- | --- |
| `date` | One whole UTC day, `yyyy-MM-dd`. Cannot be combined with `from`/`to`. |
| `from`, `to` | ISO-8601 instants (`2026-11-16T14:30:00Z`); no offset means UTC. `from` is inclusive, `to` exclusive. A bare date in `to` includes that whole day. |
| `subject` | Service Bus Subject / label |
| `messageType` | Message type (see above) |
| `messageId`, `correlationId` | Broker ids |
| `prop.{name}` | Application property filter, e.g. `prop.State=TransferCompleted`. Repeatable. |
| `include` | `summary` (default) or `full` to return the whole event with each item |
| `pageSize` | Default 50, maximum 500 |
| `continuationToken` | The token from the previous page |

With no date parameters the search covers the last 24 hours. A single search can span at most 31 days. All filters are AND-ed, match case-insensitively, and accept `*` as a wildcard (`subject=ReceiveCredit*`, `subject=*Failure`).

Results come back oldest first. When `continuationToken` in the response is not null there is more to scan: call again with the same filters plus that token. A page can contain fewer than `pageSize` items, or none, and still carry a token, because each request stops scanning after `SEARCH_TIME_BUDGET_SECONDS`. Keep paging until the token is null.

```
GET /api/events?date=2026-11-16&subject=ReceiveCredit%20-%20Failure
GET /api/events?from=2026-11-10&to=2026-11-16&messageType=ReturnPayment*
GET /api/events?from=2026-11-16T13:00:00Z&to=2026-11-16T14:00:00Z&prop.State=TransferFailed&include=full
GET /api/events/20261116T203745123Z-12345-a1b2c3d4
```

```json
{
  "from": "2026-11-16T00:00:00+00:00",
  "to": "2026-11-17T00:00:00+00:00",
  "count": 1,
  "items": [
    {
      "archiveId": "20261116T203745123Z-12345-a1b2c3d4",
      "enqueuedTimeUtc": "2026-11-16T20:37:45.123+00:00",
      "subject": "ReceiveCredit - Failure",
      "messageType": "ReceiveCredit - Failure",
      "messageId": "0f8fad5b-d9cb-469f-a165-70867728950e",
      "sizeBytes": 2148
    }
  ],
  "blobsScanned": 412
}
```

## Configuration

Settings live under `eventArchive:AppSettings`. Per-environment values are in `deploy_parameters/`; the release pipeline writes them to the Function App as application settings.

| Setting | Default | Purpose |
| --- | --- | --- |
| `SERVICE_BUS_CONNSTRING` | | Service Bus connection string. For managed identity set `SERVICE_BUS_CONNSTRING:fullyQualifiedNamespace` instead. |
| `SERVICE_BUS_TOPIC_NAME` | | Topic to archive |
| `SERVICE_BUS_SUBSCRIPTION_NAME` | | The unfiltered subscription |
| `STORAGE_CONNSTRING` | | Storage connection string, **or** |
| `STORAGE_ACCOUNT_URL` | | `https://{account}.blob.core.windows.net` to use managed identity |
| `STORAGE_CONTAINER_NAME` | `events` | Created on first write if missing |
| `MESSAGE_TYPE_PROPERTY_NAMES` | `messageType,eventType` | Application properties that carry the message type |
| `SEARCH_DEFAULT_PAGE_SIZE` | `50` | |
| `SEARCH_MAX_PAGE_SIZE` | `500` | |
| `SEARCH_MAX_RANGE_DAYS` | `31` | Largest window one search may cover |
| `SEARCH_TIME_BUDGET_SECONDS` | `20` | How long one request scans before returning a continuation token |

To run locally, copy `src/local.settings.example.json` to `src/local.settings.json`, fill in the Service Bus connection string, and start Azurite for storage.

## Azure prerequisites

The pipelines deploy code and settings; they do not create infrastructure. Each environment needs:

A Function App on the .NET 10 isolated worker (Functions v4), with a managed identity that can read the Key Vault secrets referenced in `*_ea_keyvault.parameters.json`.

A subscription on the topic with the default rule (no filter):

```
az servicebus topic subscription create \
  --resource-group <rg> --namespace-name <namespace> \
  --topic-name paymentservicestopic --name eventarchive \
  --max-delivery-count 10 --lock-duration PT1M
```

A storage account for the archive. For long-term retention, a lifecycle rule that moves blobs to the Cool or Cold tier keeps cost down and leaves them searchable. Avoid the Archive tier: archived blobs cannot be read without rehydration, so searches that need blob content would skip them.

## Pipelines

`pipelines/build.yml` restores, builds, runs the unit tests and publishes the `drop` artifact (the function app zip plus `deploy_parameters`). `pipelines/release.yml` runs when that build completes on `main` and deploys DEV → QA → PROD through `pipelines/templates/deploy-stage.yml`: apply settings, zip-deploy, then call `/api/health`.

Before the first release, replace the `<placeholders>` in `release.yml` (build pipeline name, service connections, resource groups) and in the Key Vault parameter files. The settings step refuses to deploy while a placeholder remains. Add approvals to the QA and PROD environments in Azure DevOps to gate promotion.

# Custom Fork: Single-Database Configuration & In-Memory Schema Hot Reload

> **Applies to:** this custom DAB fork (branch `feature/mssql-schema-hot-reload` and later).
> Not part of upstream Data API builder.

## Problem statement

DAB can be configured to target **multiple databases** (via `data-source-files`). In that
scenario, DAB's built-in hot reload re-parses the whole configuration and **rebuilds the
metadata, query executors, and engines for every database**, even when only one database's
configuration changed. That means:

- every unchanged database suffers a full metadata re-initialization (database roundtrips),
- in-flight behavior for unchanged endpoints can be disturbed,
- the reload is effectively "all-or-nothing".

## Solution overview

This fork adds a **scoped hot reload engine** (`HotReloadEngine`) that:

1. Re-parses the configuration file and validates it **without contacting any database**
   (JSON schema + config-only validators).
2. Computes, at **data-source granularity**, which databases actually changed.
3. Rebuilds **only the changed data sources**:
   - in-memory metadata caches (`ISqlMetadataProvider`),
   - the logical connection-pool layer for the changed database type (query builder /
     executor / exception parser, including per-data-source connection-string builders),
   - the query/mutation engine entries for the changed database type.
4. Evicts the Hot Chocolate request executor so the GraphQL schema is lazily re-stitched
   from the new configuration on the next request (unchanged databases' metadata stays
   cached — no roundtrips).
5. Invokes `ITenantSchemaRegistryService.ReloadRegistryAsync()` when the (future) tenant
   schema registry feature is registered, so `dbo.sys_TenantCustomFields` mappings re-sync
   in the same reload.
6. Applies **last-known-good (LKG) protection**: invalid JSON, schema violations, or
   metadata/mapping errors keep the previous in-memory state active, log a warning, and
   never crash the process.

There are **two triggers** (a dual-trigger signal abstraction, `IHotReloadSignal`):

| Trigger | When it is active | How it is raised |
| --- | --- | --- |
| **File watcher** (`FileWatcherHotReloadSignal`) | Development mode only (`host.mode: development` / `DAB_ENVIRONMENT=Development`), non-hosted | DAB's existing `ConfigFileWatcher` detects a change to `dab-config.json`; the custom signal adapts DAB's existing config-changed events (500 ms debounce) |
| **Admin endpoint** (`AdminEndpointHotReloadSignal`) | Any mode, once an API key is configured | `POST /admin/hot-reload` with the API key — for production containers, webhooks, CI/CD |

No file watcher is duplicated: the custom engine rides DAB's existing configuration change
pipeline and only adds the per-database scoping, the admin trigger, and the registry hook.

## Enabling the feature

The engine is registered in host setup (`Startup.ConfigureServices`) via
`services.AddCustomHotReloadEngine();` — no configuration file switch is required. Both
triggers are then available subject to the conditions in the table above.

### Environment variables

| Variable | Required | Purpose |
| --- | --- | --- |
| `DAB_HOT_RELOAD_API_KEY` | Optional | Enables `POST /admin/hot-reload`. When unset the endpoint exists but returns **403 Forbidden**. When set, the request must present the key in the `X-DAB-HOT-RELOAD-KEY` header (constant-time comparison). Deliberately **not** stored in `dab-config.json` — it is deployment configuration, mirroring `DAB_CONFIG_AUTH_TOKEN` for `POST /configuration`. |

Development-mode file watching needs no additional configuration: it is active whenever the
host runs in Development mode (see the table above) and the configuration is file-based.

## Admin endpoint reference

### `POST /admin/hot-reload`

Programmatically triggers the same scoped reload as a local file change, without touching
files on disk. The engine re-reads the configuration source, validates, diffs, and applies
the scoped refresh.

**Request**

```http
POST /admin/hot-reload HTTP/1.1
Host: <dab-host>
X-DAB-HOT-RELOAD-KEY: <your-api-key>
```

No request body is required or accepted.

**Response codes**

| Code | Meaning |
| --- | --- |
| `202 Accepted` | Reload requested and applied. Unchanged databases were not affected. |
| `401 Unauthorized` | API key missing or incorrect. |
| `403 Forbidden` | Endpoint disabled (no `DAB_HOT_RELOAD_API_KEY` configured). |
| `503 Service Unavailable` | Reload failed; the last-known-good configuration and schema state remain active (see the engine logs). |

**Example**

```bash
curl -X POST "https://<dab-host>/admin/hot-reload" \
  -H "X-DAB-HOT-RELOAD-KEY: $(cat /run/secrets/dab-hot-reload-key)" \
  -w "\nHTTP %{http_code}\n"
```

```json
{ "message": "Hot reload accepted and applied. Unchanged databases were not affected." }
```

### Behavior details

- **Scoping.** Only data sources whose slice changed (data source properties + the entities
  and autoentities mapped to them) are rebuilt. A reload that changes nothing (e.g. an
  identical re-save) is a no-op.
- **Global, cheap refreshes still run.** The authorization resolver, OpenAPI document, and
  GraphQL schema creator/eviction refresh globally from the new configuration — these are
  in-memory operations with no database roundtrips.
- **No restarts.** The ASP.NET Core host, routing, and middleware pipelines are never torn
  down. In-flight requests to unchanged databases keep serving throughout.
- **Single-database configs** (a lone `data-source` section) are supported too; identical
  reloads produce no rebuilds.
- **Tenant schema registry.** When a future feature registers an
  `ITenantSchemaRegistryService`, the engine calls `ReloadRegistryAsync()` as part of every
  reload. Until then the hook is a no-op.
- **Endpoint routing.** The literal `/admin/hot-reload` route is mapped ahead of
  `RestController`'s catch-all and is exempted from the client-role header requirement
  (`X-MS-API-ROLE`), because it authenticates via its own API key.

### Security notes

- Keep `DAB_HOT_RELOAD_API_KEY` secret: in Azure Container Apps use a
  [secret](https://learn.microsoft.com/azure/container-apps/manage-secrets) or Key Vault,
  in Kubernetes a Secret, in CI/CD a protected variable.
- The key is compared with `CryptographicOperations.FixedTimeEquals` (timing-attack safe).
- There is **no loopback restriction** — cloud CI/CD and webhook callers arrive from
  arbitrary addresses; the key is the single source of authorization.
- Serve the endpoint over HTTPS in production.

## Sample configurations

Ready-to-use samples live in [`samples/admin-hot-reload/`](../../samples/admin-hot-reload/).

### Single database (MSSQL)

```json
{
  "$schema": "https://github.com/Azure/data-api-builder/releases/download/vmajor.minor.patch/dab.draft.schema.json",
  "data-source": {
    "database-type": "mssql",
    "connection-string": "Server=localhost;Database=library;User ID=sa;Password=@env('DAB_DB_PASSWORD');TrustServerCertificate=True;"
  },
  "runtime": {
    "rest": { "enabled": true, "path": "/api" },
    "graphql": { "enabled": true, "path": "/graphql" },
    "host": {
      "mode": "development",
      "authentication": { "provider": "AppService" }
    }
  },
  "entities": {
    "Book": {
      "source": { "object": "dbo.Books", "type": "table" },
      "permissions": [
        { "role": "anonymous", "actions": [ "read" ] }
      ]
    }
  }
}
```

With this file, editing `dab-config.json` while DAB runs in Development mode triggers a
scoped reload automatically (debounced). In Production mode, call the admin endpoint.

### Multiple databases (`data-source-files`)

Root config (`multidb-dab-config.json`):

```json
{
  "$schema": "https://github.com/Azure/data-api-builder/releases/download/vmajor.minor.patch/dab.draft.schema.json",
  "data-source-files": [
    "multidb-sql.dab-config.json",
    "multidb-cosmos.dab-config.json"
  ],
  "runtime": {
    "rest": { "enabled": true, "path": "/api" },
    "graphql": { "enabled": true, "path": "/graphql" },
    "host": {
      "mode": "production",
      "authentication": { "provider": "AppService" }
    }
  }
}
```

Child (`multidb-sql.dab-config.json`, MSSQL database):

```json
{
  "$schema": "https://github.com/Azure/data-api-builder/releases/download/vmajor.minor.patch/dab.draft.schema.json",
  "data-source": {
    "database-type": "mssql",
    "connection-string": "Server=sql.internal;Database=library;User ID=sa;Password=@env('DAB_DB_PASSWORD');TrustServerCertificate=True;"
  },
  "entities": {
    "Book": {
      "source": { "object": "dbo.Books", "type": "table" },
      "permissions": [ { "role": "anonymous", "actions": [ "read" ] } ]
    }
  }
}
```

Child (`multidb-cosmos.dab-config.json`, Cosmos DB database):

```json
{
  "$schema": "https://github.com/Azure/data-api-builder/releases/download/vmajor.minor.patch/dab.draft.schema.json",
  "data-source": {
    "database-type": "cosmosdb_nosql",
    "connection-string": "AccountEndpoint=@env('DAB_COSMOS_ENDPOINT');AccountKey=@env('DAB_COSMOS_KEY');",
    "options": {
      "database": "graphqldb",
      "container": "planet",
      "schema": "schema.gql"
    }
  },
  "entities": {
    "Planet": {
      "source": { "object": "graphqldb.planet", "type": "table" },
      "permissions": [ { "role": "anonymous", "actions": [ "read" ] } ]
    }
  }
}
```

When only `multidb-sql.dab-config.json` changes, the reload rebuilds **only the MSSQL database's**
metadata and connection-pool state; the Cosmos database is untouched (no roundtrip, no
rebuild).

## Triggering from CI/CD

```yaml
# GitHub Actions example
- name: Hot-reload DAB configuration
  run: |
    curl -X POST "${{ secrets.DAB_URL }}/admin/hot-reload" \
      -H "X-DAB-HOT-RELOAD-KEY: ${{ secrets.DAB_HOT_RELOAD_API_KEY }}" \
      -f
```

## Triggering from Azure Container Apps (webhook / portal)

1. Add `DAB_HOT_RELOAD_API_KEY` as an ACA secret and reference it in the container
   environment (e.g. `DAB_HOT_RELOAD_API_KEY` -> `secretref:dab-hot-reload-key`).
2. Call the endpoint from a workflow, an Azure Function webhook, or a database admin portal:

```bash
curl -X POST "https://<your-app>.azurecontainerapps.io/admin/hot-reload" \
  -H "X-DAB-HOT-RELOAD-KEY: $KEY"
```

## Observability

On success the engine logs (Information):

```
Admin hot reload succeeded. Changed data sources: <names>.
```

On failure it logs a Warning with the exception and restores the last-known-good state:

```
Admin hot reload failed. Restoring the last-known-good configuration and rolling back the affected data sources.
```

The admin endpoint returns `503` in that case; the process keeps serving.

## Architecture notes (for contributors)

- All custom code lives under `src/Core/Custom/HotReload/`; core DAB files carry only
  single-line hooks:
  - `FileSystemRuntimeConfigLoader.SignalConfigReloaded()` — programmatic signal wrapper,
  - one-line guards in `MetadataProviderFactory` / `QueryManagerFactory` /
    `QueryEngineFactory` / `MutationEngineFactory` `OnConfigChanged` handlers that consult
    the ambient `HotReloadScope` (no scope → upstream rebuild-everything behavior is kept).
- `HotReloadEngine` is an `IHostedService`; its factory dependencies are injected as lazy
  `Func<>` delegates so startup keeps upstream's deferred service construction.
- Unit tests: `src/Service.Tests/UnitTests/HotReload*UnitTests.cs`.

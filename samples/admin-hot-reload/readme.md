# Single-Database Hot Reload — Samples

Sample configurations for the custom fork's single-database hot reload feature. See the
full documentation in
[`docs/design/custom-single-database-hot-reload.md`](../../docs/design/custom-single-database-hot-reload.md).

## Files

| File | Description |
| --- | --- |
| `dab-config.json` | Single MSSQL database. Runs in **Development mode** — editing this file while DAB runs triggers a scoped hot reload automatically (500 ms debounce). |
| `multidb-dab-config.json` | Multi-database root config (`data-source-files`) pointing at the two child files below. Runs in **Production mode** — reloads are triggered explicitly via the admin endpoint. |
| `multidb-sql.dab-config.json` | Child config: MSSQL database (`Book` entity). |
| `multidb-cosmos.dab-config.json` | Child config: Cosmos DB database (`Planet` entity) with a `schema.gql` GraphQL schema. |
| `schema.gql` | Minimal Cosmos DB GraphQL schema referenced by the Cosmos child config. |

## Enabling the admin endpoint (Production / cloud)

Set the API key as an environment variable — it must **not** be stored in the config file:

```bash
export DAB_HOT_RELOAD_API_KEY="$(openssl rand -hex 32)"
```

In Azure Container Apps, configure it as a [secret](https://learn.microsoft.com/azure/container-apps/manage-secrets)
and reference it from the container environment (`DAB_HOT_RELOAD_API_KEY` ->
`secretref:dab-hot-reload-key`).

## Triggering a reload

Development mode (local file watching is automatic — no call needed):

```bash
# Just edit dab-config.json; the change is applied after a 500 ms debounce.
```

Production / cloud (authenticated admin endpoint):

```bash
curl -X POST "https://<dab-host>/admin/hot-reload" \
  -H "X-DAB-HOT-RELOAD-KEY: $DAB_HOT_RELOAD_API_KEY" \
  -w "\nHTTP %{http_code}\n"
```

Expected responses: `202 Accepted` on success, `401` for a missing/wrong key, `403` when no
key is configured, `503` when the reload failed and the last-known-good state remains active.

## What a reload refreshes

Only the **changed** databases are rebuilt (metadata caches, connection-pool layer, GraphQL
schema re-stitch); unchanged databases keep serving untouched. The tenant schema registry
(`ITenantSchemaRegistryService`) is re-synced when that future feature is registered.

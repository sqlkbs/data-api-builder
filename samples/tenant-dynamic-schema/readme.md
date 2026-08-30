# Tenant-Aware Dynamic Schema sample

Two tenants served by one DAB process, backed by one shared physical table, each exposing its own
custom business attributes. See `docs/design/custom-tenant-dynamic-schema.md` for the design.

## Files

| File | Purpose |
|---|---|
| `sys_TenantSchemaFields.sql` | DDL for the tenant schema metadata table. |
| `dab-config.json` | Parent config listing one child config per tenant. |
| `tenant-001.dab-config.json` | TENANT-001's data source and entities. |
| `tenant-002.dab-config.json` | TENANT-002's data source and entities. |

## How a tenant is bound

Each tenant is its own DAB **data source**, declaring itself with the `tenant-id` data source option:

```json
"data-source": {
  "database-type": "mssql",
  "connection-string": "...",
  "options": { "tenant-id": "TENANT-001" }
}
```

Metadata providers are created per data source, so each tenant's schema is already isolated and the
engine never has to inspect a request to work out which tenant it is serving. Note that both sample
tenants point at the same `dbo.Assay` table yet map the alias `recoveryRate` to **different** JSON
paths — that is exactly why tenant resolution is bound to the data source.

Entity names must be unique across the whole configuration, hence the `Tenant001*` / `Tenant002*`
prefixes. Per-tenant `rest.path` and `graphql.singular/plural` keep the exposed surface clean.

## Registering fields

`FieldSourceType` selects how a field is stored:

| Value | Meaning | Exposed by |
|---|---|---|
| `1` | `StandardColumn` — a real physical column | the native dab-config `mappings` your generator emits |
| `2` | `JsonAttribute` — a JSON scalar in a container column | the engine, via `JSON_VALUE` / `JSON_MODIFY` |
| `3` | `ForeignLookup` — reserved | not projected yet |

```sql
-- TENANT-001: two decimal attributes inside the CustomAttributesJson container column.
INSERT INTO dbo.sys_TenantSchemaFields
    (TenantId, EntityName, FieldSourceType, PhysicalColumnName, JsonPath, ApiAlias, DataType, DisplayName, IsVisible, IsReadOnly)
VALUES
    ('TENANT-001', 'Tenant001Assay', 2, 'CustomAttributesJson', '$.RecoveryRate', 'recoveryRate', 'decimal(18,6)', 'Recovery Rate (%)',  1, 0),
    ('TENANT-001', 'Tenant001Assay', 2, 'CustomAttributesJson', '$.GoldGrade',    'goldGrade',    'decimal(18,6)', 'Gold Grade (g/t)',   1, 0);

-- TENANT-002: the SAME alias mapped to a different path, plus a datetime attribute.
INSERT INTO dbo.sys_TenantSchemaFields
    (TenantId, EntityName, FieldSourceType, PhysicalColumnName, JsonPath, ApiAlias, DataType, DisplayName, IsVisible, IsReadOnly)
VALUES
    ('TENANT-002', 'Tenant002Assay', 2, 'CustomAttributesJson', '$.RecRate',   'recoveryRate', 'decimal(9,4)', 'Recovery %',  1, 0),
    ('TENANT-002', 'Tenant002Assay', 2, 'CustomAttributesJson', '$.AssayedOn', 'assayedOn',    'datetime2',    'Assayed On',  1, 0);
```

`EntityName` must match the entity name in dab-config.json, and the container column named by
`PhysicalColumnName` must exist on the table. Fields that cannot be represented safely — an alias
colliding with a real column, a missing container column, a blank `JsonPath`, an alias that is not a
plain identifier, or an unsupported `DataType` such as `geometry` — are skipped with a startup
warning rather than failing the engine.

`IsVisible` is a **front-end form hint only** and does not hide a field from the API. `IsReadOnly`
**is** enforced: writes to such a field are rejected.

## Permissions

Wildcard field permissions (`"include": ["*"]`) expand from the entity's column set, so newly
registered attributes are covered automatically. If your generator emits **explicit** field lists
instead, it must include each attribute's `ApiAlias`, or field-level authorization will silently
reject it.

## Using it

```bash
export DAB_DB_PASSWORD='...'
export DAB_HOT_RELOAD_API_KEY='<strong-random-key>'   # enables POST /admin/hot-reload
dab start --config samples/tenant-dynamic-schema/dab-config.json
```

Read and write custom attributes exactly like physical columns:

```bash
# Read
curl "http://localhost:5000/api/tenant-001/assay?\$select=sampleCode,recoveryRate,goldGrade"

# Filter and sort on a JSON attribute
curl "http://localhost:5000/api/tenant-001/assay?\$filter=recoveryRate gt 90&\$orderby=goldGrade desc"

# Update one attribute. Attributes you omit are left untouched.
curl -X PATCH "http://localhost:5000/api/tenant-001/assay/assayId/42" \
     -H 'Content-Type: application/json' \
     -d '{ "recoveryRate": 91.5 }'
```

`PATCH` is the recommended verb for custom attributes. Both `PATCH` and `PUT` only *set* the keys
present in the body — an omitted attribute is never removed — which deliberately differs from DAB's
`PUT` replace semantics for physical columns. Sending an explicit `null` removes that key, and when a
write leaves the document with no attributes at all the container column is stored as SQL `NULL`
instead of an empty `{}`, so the column's invariant is `NULL` ⇔ no custom attributes currently stored.
API reads return `null` for a missing key, a JSON null and a NULL container alike. Deleting the row
removes the container column with it.

## Applying a schema change

After inserting or updating rows in `dbo.sys_TenantSchemaFields`, regenerate the tenant's
dab-config.json (your admin worker does this) and trigger a reload:

```bash
curl -X POST http://localhost:5000/admin/hot-reload -H 'X-DAB-HOT-RELOAD-KEY: <key>'
```

The reload re-hydrates the registry, and when the dynamic schema actually changed it rebuilds the
affected tenants' entity metadata and evicts the GraphQL schema, so the new attributes appear without
a restart. This works even when dab-config.json itself is unchanged. If
`dbo.sys_TenantSchemaFields` cannot be read, the previous schema snapshot keeps serving and the
configuration reload still succeeds.

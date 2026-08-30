# Custom fork: Tenant-Aware Dynamic Schema

Lets each tenant expose its own custom business attributes through DAB's native REST and GraphQL
surface, backed either by physical columns or by JSON scalars inside a system-managed container
column, with full read, insert and update support.

Companion to `docs/design/custom-single-database-hot-reload.md`, whose dual-trigger engine re-hydrates
this feature's cache.

## 1. Problem

A multi-tenant backend stores every tenant's records in shared physical tables. Tenants need
different, user-defined attributes on those tables without DDL per tenant. Attributes are declared in
`dbo.sys_TenantSchemaFields` and stored as JSON scalars inside a container column such as
`CustomAttributesJson`.

The requirement is that such an attribute behaves like a real field: it appears in the GraphQL schema
and OpenAPI document, is selectable, filterable and sortable through REST and GraphQL, and is
writable through `POST`/`PATCH`/`PUT` — with no per-request database roundtrip to resolve mappings.

## 2. Why a projection interceptor alone is not enough

The obvious approach — rewrite the `SELECT` projection in `MsSqlQueryBuilder` — cannot work, because a
field with no physical column is rejected long before the query builder runs. Four independent gates
enumerate `SourceDefinition.Columns`, which is populated purely from live database introspection:

| Gate | Location |
|---|---|
| GraphQL object type fields | `SchemaConverter.CreateObjectTypeDefinitionForTableOrView` |
| REST `$select` validation | `RequestValidator.ValidateRequestContext` (requires `Columns.ContainsKey`) |
| Default projection | `SqlQueryStructure` |
| Wildcard field authorization | `AuthorizationResolver.ResolveEntityDefinitionColumns` |

dab-config `mappings` and `fields` cannot close the gap either: they only **rename** a field, never
create one. A mapping whose backing name is absent from `SourceDefinition.Columns` resolves through
`TryGetBackingColumn` but still fails REST validation and never reaches the GraphQL schema. And
`FieldMetadata` has nowhere to record a container column, JSON path or data type.

**Therefore the attribute must exist in `SourceDefinition.Columns`.** Everything else follows from
that one decision.

## 3. Design

### 3.1 Tenant binding lives on the data source

Every tenant is its own DAB data source, declaring itself through a free-form data source option:

```json
"data-source": { "database-type": "mssql", "connection-string": "...",
                 "options": { "tenant-id": "TENANT-001" } }
```

This requires no change to DAB's configuration object model or JSON schema. Because
`MetadataProviderFactory` creates one metadata provider — and therefore one `SourceDefinition` per
entity — per data source, each tenant's schema is *already* isolated. Two tenants sharing one physical
table can map the same alias to different JSON paths without colliding.

Crucially this keeps tenant resolution off the request path entirely: no `HttpContext` claim
inspection, no per-request lookup, and no need to inject anything into `MsSqlQueryBuilder` (which
`QueryManagerFactory` constructs with `new`).

### 3.2 Registry with snapshot semantics

`TenantSchemaRegistryService` caches `dbo.sys_TenantSchemaFields` in a `ConcurrentDictionary` keyed by
`(TenantId, EntityName)`.

- A reload **never mutates the published dictionary**. It builds a complete replacement off to the
  side and installs it with `Interlocked.Exchange`; readers use `Volatile.Read`. An in-flight request
  therefore observes either the entire old snapshot or the entire new one, never a half-applied change.
- `SnapshotVersion` advances only when the content actually differs, computed from an FNV-1a signature
  over every column of every row. This is what tells the hot reload engine whether a metadata rebuild
  is needed.
- `ReloadRegistryAsync` **never throws**. A missing table, permission error or transient failure keeps
  the last-known-good snapshot published, logs a warning and returns `false`.
- Only tenants this process actually declares are queried, and tenants sharing a connection string are
  collapsed into a single roundtrip.
- Reads use DAB's own `IQueryExecutor`, inheriting connection-string resolution, managed identity,
  retry policy and logging. (Dapper is not a dependency of this repository.)

Hydration is lazy and happens at most once, driven from schema inference rather than a hosted service,
which sidesteps startup ordering entirely: `Startup.Configure` runs before any `IHostedService`.

### 3.3 Virtual columns

`VirtualColumnDefinition : ColumnDefinition` carries the container column, JSON path, read cast and
write expression. `ColumnDefinition` is an unsealed public class and `SourceDefinition.Columns` is a
mutable dictionary, so instances are simply inserted — no generic class is modified. DAB's generic code
treats them exactly like physical columns; only the MS SQL query builder inspects the concrete type.

Injection happens through two methods `MsSqlMetadataProvider` **already overrides**, so no generic
cross-engine file is touched:

1. `PopulateTriggerMetadataForTable` — the last hook that is both asynchronous and aware of the entity
   name. Hydrates the registry and captures the injection target.
2. `PopulateColumnDefinitionWithHasDefaultAndDbType` — runs after every physical column is known, so a
   collision is detectable and the **physical column always wins**.

Both run inside `PopulateObjectDefinitionForEntities()`, which precedes
`GenerateExposedToBackingColumnMapsForEntities()`, so the new columns participate in DAB's
exposed-to-backing maps, GraphQL schema, OpenAPI document and authorization metadata.

A field is skipped with a warning — never an exception — when its alias collides with a physical
column, its container column is missing, its `JsonPath` is blank, its alias is not a plain identifier,
or its `DataType` is unsupported. A malformed row degrades one field; it cannot stop the engine.

### 3.4 Reads: one override covers everything

`BaseSqlQueryBuilder.Build(Column)` is `protected virtual` and is the single chokepoint through which
projections (`Build(LabelledColumn)`), filter predicates (`Build(Predicate)`), `ORDER BY`
(`Build(OrderByColumn)`), `GROUP BY` and `HAVING` all render a column reference. Overriding it once in
`MsSqlQueryBuilder` therefore delivers the entire read surface:

```sql
CAST(JSON_VALUE([q1].[CustomAttributesJson], '$.RecoveryRate') AS DECIMAL(18,6)) AS [recoveryRate]
```

The cast is not cosmetic: `JSON_VALUE` returns `nvarchar(4000)`, so without it every attribute would
serialize through `FOR JSON PATH` as a JSON string and numeric or temporal GraphQL fields would fail to
resolve.

`Build(Column)` receives only schema/table/alias/column strings and no entity context, so the missing
context is supplied by `TenantVirtualColumnScope` — a `[ThreadStatic]` stack of frames pushed by each
`Build(...)` entry point, carrying the structure's `SourceDefinition`. Query building is entirely
synchronous string construction, so a thread-static stack is both correct and allocation-free; frames
nest naturally for `OUTER APPLY` subqueries. Because the captured definition is per data source, it is
inherently per tenant — something a process-wide schema/table/column catalog could not guarantee.

The existing spatial support is untouched: geometry still projects via `STAsText()`, and both appear in
the same query.

### 3.5 Writes: collapse into one JSON_MODIFY chain

N API fields map to **one** physical column, so writes cannot be a per-parameter transform like the
spatial feature. The insert column/value lists and the update `SET` list are collapsed instead:

```sql
-- UPDATE / UPSERT
SET [CustomAttributesJson] = JSON_MODIFY(JSON_MODIFY(
      ISNULL([CustomAttributesJson],'{}'),
      '$.RecoveryRate', CAST(@param1 AS DECIMAL(18,6))),
      '$.GoldGrade',    CAST(@param2 AS DECIMAL(18,6)))

-- INSERT
INSERT INTO [dbo].[assay] ([id], [name], [CustomAttributesJson])
VALUES (@param0, @param1, JSON_MODIFY('{}', '$.RecoveryRate', CAST(@param2 AS DECIMAL(18,6))))
```

Design points:

- The collapse **replaces** generic predicate building rather than post-processing it, because the
  left-hand side of an assignment must not be rewritten into a `JSON_VALUE` expression.
- `ISNULL(container, '{}')` initializes a NULL document.
- **Omitted attributes never appear in the chain**, so a write can add or overwrite a tenant's
  attributes but can never drop the ones it did not mention.
- An **explicitly null attribute removes its key** via lax-mode `JSON_MODIFY(doc, path, NULL)`, which
  is null-safe (no error when the path does not exist). API clients cannot tell a missing key from a
  JSON null — `JSON_VALUE` yields SQL `NULL` either way — but the distinction between "explicitly
  cleared" and "never set" is intentionally not preserved in the stored document.
- **An empty document is stored as SQL `NULL`**: the chain is wrapped in `NULLIF(..., N'{}')` on both
  the INSERT and UPDATE paths, so the container column carries the invariant `NULL` ⇔ no custom
  attributes currently stored. This removes empty-object shells and the storage they would occupy.
- Writing the container column directly alongside its attributes would emit two assignments for one
  column; this is rejected with a `400` instead of invalid SQL.
- `IsReadOnly` surfaces as `ColumnDefinition.IsReadOnly`, so DAB's existing enforcement rejects writes
  with no new code.

### 3.6 Declared type mapping

| `DataType` | CLR | Read cast | Write expression |
|---|---|---|---|
| `nvarchar`, `varchar`, `char`, `text` | `string` | none | `@p` |
| `int`, `bigint`, `smallint`, `tinyint` | integral | same | `CAST(@p AS ...)` |
| `bit` | `bool` | `BIT` | `CAST(@p AS BIT)` |
| `decimal(p,s)`, `numeric`, `money` | `decimal` | `DECIMAL(p,s)`, default `DECIMAL(38,10)` | `CAST(@p AS DECIMAL(p,s))` |
| `float`, `real` | `double` / `float` | same | `CAST(@p AS ...)` |
| `date`, `datetime`, `datetime2` | `DateTime` | `DATE` / `DATETIME2` | `CONVERT(NVARCHAR(33), @p, 127)` |
| `datetimeoffset` | `DateTimeOffset` | `DATETIMEOFFSET` | `CONVERT(NVARCHAR(33), @p, 127)` |
| `time` | `TimeOnly` | `TIME` | `CONVERT(NVARCHAR(16), @p, 114)` |
| `uniqueidentifier` | `Guid` | `UNIQUEIDENTIFIER` | `CAST(@p AS NVARCHAR(36))` |
| `geometry`, `geography` | — | **rejected** | — |

Spatial types are rejected because a CLR spatial value cannot be a JSON scalar; physical
geometry/geography columns remain handled by the spatial feature.

### 3.7 Hot reload integration

The existing engine already invoked `ITenantSchemaRegistryService.ReloadRegistryAsync` on both
triggers. Three gaps had to be closed:

1. **Ordering.** The registry reload ran *after* the metadata rebuild, so a rebuild would have seen the
   stale snapshot. It now runs after the configuration is swapped in — so a newly added tenant data
   source is visible — and before the factory events.
2. **Registry-only changes were invisible.** `RuntimeConfigDiffer` returns an empty diff for an
   unchanged config, and the scoped rebuild then iterates zero data sources, rebuilding nothing. When
   `SnapshotVersion` advances, the change set is now widened to the tenant data sources, so the
   existing scoped-rebuild and GraphQL-eviction machinery refreshes virtual columns, re-expands
   wildcard field permissions and re-stitches the schema. In practice the admin worker also rewrites
   dab-config.json, which triggers the normal path; the widening covers direct table edits.
3. **Failures were fatal.** A registry failure reached the outer handler, which restored the
   last-known-good configuration and rolled back data sources. A registry read error is now non-fatal:
   the registry keeps its own snapshot and the configuration reload still succeeds.

### 3.8 Admin endpoint

`POST /admin/hot-reload` previously mapped its route unconditionally and answered `403` when
`DAB_HOT_RELOAD_API_KEY` was unset, disclosing that the endpoint exists. The route is now mapped only
when a key is configured, so an unconfigured deployment answers **404**. The `403`/`401` responses
remain as defence in depth.

## 4. Touchpoints

| File | Change |
|---|---|
| `src/Core/Custom/TenantSchema/*` | All feature logic (new). |
| `src/Core/Services/MetadataProviders/MsSqlMetadataProvider.cs` | Two one-line hooks in methods it already overrides, plus one field. |
| `src/Core/Resolvers/MsSqlQueryBuilder.cs` | `Build(Column)` override, `BuildUpdateOperations` delegation, five scope pushes, insert/output call sites. |
| `src/Core/Custom/HotReload/HotReloadEngine.cs` | Reload ordering, change-set widening, non-fatal registry failure. |
| `src/Core/Custom/HotReload/AdminHotReloadEndpointExtensions.cs` | Conditional route mapping. |
| `src/Core/Custom/MsSqlSpatialExtensions.cs` | One-word visibility change so the tenant extension can delegate physical columns to it. |
| `src/Service/Startup.cs` | Two registration/publication lines. |

**No generic cross-engine class is modified** — `SqlMetadataProvider`, `BaseSqlQueryBuilder`,
`SourceDefinition`/`ColumnDefinition`, `RequestValidator`, `SchemaConverter`, `AuthorizationResolver`
and the configuration object model are all untouched, and the dab-config JSON schema is unchanged.

## 5. Limitations

- Virtual columns apply to **table** entities only; the injection hook is table-guarded, so views and
  stored procedures are unsupported.
- `JSON_VALUE` truncates at 4000 characters, so very long string attributes are out of scope.
- `bit` attributes round-trip as JSON `0`/`1` rather than `true`/`false`; reads cast back to `BIT`, so
  both forms are accepted.
- `FieldSourceType = 3` (`ForeignLookup`) rows are loaded into the registry but not projected.
- Nested/multiple-create GraphQL mutations are not covered.
- `PATCH` and `PUT` both merge JSON attributes rather than replacing them, which deliberately differs
  from DAB's `PUT` replace semantics for physical columns.
- Row-level tenant isolation remains a dab-config permissions/database-policy concern; the registry
  adds no row filters.

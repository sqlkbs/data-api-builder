-- Custom fork: Tenant-Aware Dynamic Schema.
-- Licensed under the MIT License.
--
-- Unified tenant schema abstraction layer governing both physical SQL columns and
-- dynamic JSON attributes.
--
-- Dual-layer strategy:
--   FieldSourceType = 1 (StandardColumn) -- backed by a real physical column. The API surface
--       is produced by the native DAB entity mappings the admin worker generates into the
--       tenant's dab-config.json. Rows of this type are informational for the engine.
--   FieldSourceType = 2 (JsonAttribute)  -- backed by a JSON scalar inside the internal
--       system-managed container column named by PhysicalColumnName (e.g. CustomAttributesJson)
--       and addressed by JsonPath. The engine projects these via JSON_VALUE and writes them
--       via JSON_MODIFY.
--   FieldSourceType = 3 (ForeignLookup)  -- reserved. Rows are loaded into the registry but the
--       engine performs no projection for them yet (see docs/design/custom-tenant-dynamic-schema.md).
--
-- ApiAlias is the machine-facing REST/GraphQL property key. DisplayName is a human UI label and
-- is never used to build SQL or shape API responses.
--
-- Note: this table intentionally has no IsActive/IsDeleted column. Every row is an active
-- mapping; remove a row to retire a field. IsVisible/IsReadOnly are field-level metadata
-- (IsVisible drives front-end form generation only and does NOT hide a field from the API;
-- IsReadOnly is enforced by the engine and rejects writes to the field).

CREATE TABLE dbo.sys_TenantSchemaFields
(
    FieldId INT IDENTITY(1,1) PRIMARY KEY,
    TenantId NVARCHAR(50) NOT NULL,
    EntityName NVARCHAR(100) NOT NULL,

    -- Field Classification: 1 = StandardColumn, 2 = JsonAttribute, 3 = ForeignLookup
    FieldSourceType TINYINT NOT NULL DEFAULT 1,

    PhysicalColumnName NVARCHAR(128) NOT NULL,
    JsonPath NVARCHAR(250) NULL,               -- e.g., '$.RecoveryRate' (Populated if FieldSourceType = 2)

    ApiAlias NVARCHAR(128) NOT NULL,           -- Exposed camelCase/PascalCase JSON property key
    DataType NVARCHAR(50) NOT NULL,            -- 'decimal', 'nvarchar', 'datetime', 'geometry', etc.

    -- UI & Form Metadata
    DisplayName NVARCHAR(100) NOT NULL,        -- Human label (e.g., "Gold Grade (g/t)")
    IsVisible BIT NOT NULL DEFAULT 1,
    IsReadOnly BIT NOT NULL DEFAULT 0,

    CONSTRAINT CK_FieldSourceType CHECK (FieldSourceType IN (1, 2, 3))
);

-- Index 1: Prevent duplicate physical column + path mapping for a tenant entity.
-- SQL Server treats NULLs as equal in a UNIQUE index, so this also guarantees at most one
-- StandardColumn row (JsonPath IS NULL) per physical column of a tenant entity.
CREATE UNIQUE NONCLUSTERED INDEX UX_TenantSchemaFields_PhysicalAndPath
ON dbo.sys_TenantSchemaFields (TenantId, EntityName, PhysicalColumnName, JsonPath);

-- Index 2: Prevent duplicate API property aliases for a tenant entity.
CREATE UNIQUE NONCLUSTERED INDEX UX_TenantSchemaFields_ApiAlias
ON dbo.sys_TenantSchemaFields (TenantId, EntityName, ApiAlias);

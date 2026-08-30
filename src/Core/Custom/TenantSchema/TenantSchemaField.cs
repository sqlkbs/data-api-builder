// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Classification of a tenant schema field. Mirrors the <c>FieldSourceType TINYINT</c> column of
/// <c>dbo.sys_TenantSchemaFields</c> (constrained by <c>CK_FieldSourceType</c> to 1, 2 or 3), so the
/// enum is explicitly backed by <see cref="byte"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1028:Enum Storage should be Int32", Justification = "The underlying type intentionally mirrors the TINYINT storage of dbo.sys_TenantSchemaFields.FieldSourceType, so values read via DbDataReader.GetByte map across without a widening conversion.")]
public enum TenantFieldSourceType : byte
{
    /// <summary>
    /// Backed by a real physical column. The API surface for these fields is produced by the
    /// native DAB entity mappings the admin worker generates into the tenant's dab-config.json,
    /// so the engine performs no projection rewriting for them.
    /// </summary>
    StandardColumn = 1,

    /// <summary>
    /// Backed by a JSON scalar stored inside the internal system-managed container column named by
    /// <see cref="TenantSchemaField.PhysicalColumnName"/> and addressed by
    /// <see cref="TenantSchemaField.JsonPath"/>. Projected via <c>JSON_VALUE</c> and written via
    /// <c>JSON_MODIFY</c>.
    /// </summary>
    JsonAttribute = 2,

    /// <summary>
    /// Reserved for physical-to-business key translation (see AGENTS.md section 3C). Rows of this
    /// type are loaded into the registry but the engine performs no projection for them yet.
    /// </summary>
    ForeignLookup = 3
}

/// <summary>
/// A single tenant schema field mapping, materialized from one row of
/// <c>dbo.sys_TenantSchemaFields</c>.
///
/// <para>
/// <see cref="ApiAlias"/> is strictly the machine-facing REST/GraphQL JSON property key.
/// <see cref="DisplayName"/> is a human UI label consumed by front-end form generators and is
/// never used to build SQL or shape API responses.
/// </para>
/// </summary>
public sealed record TenantSchemaField
{
    /// <summary>Identity of the backing row (<c>FieldId</c>).</summary>
    public int FieldId { get; init; }

    /// <summary>Owning tenant. Correlated with the <c>tenant-id</c> data source option.</summary>
    public required string TenantId { get; init; }

    /// <summary>DAB entity name this field belongs to.</summary>
    public required string EntityName { get; init; }

    /// <summary>How the field is physically stored.</summary>
    public required TenantFieldSourceType FieldSourceType { get; init; }

    /// <summary>
    /// Physical column backing the field. For <see cref="TenantFieldSourceType.JsonAttribute"/>
    /// this is the internal system-managed JSON container column (e.g. <c>CustomAttributesJson</c>),
    /// not a column the API exposes directly.
    /// </summary>
    public required string PhysicalColumnName { get; init; }

    /// <summary>
    /// JSON path addressing the scalar inside <see cref="PhysicalColumnName"/> (e.g.
    /// <c>$.RecoveryRate</c>). Populated only for <see cref="TenantFieldSourceType.JsonAttribute"/>.
    /// </summary>
    public string? JsonPath { get; init; }

    /// <summary>Exposed camelCase/PascalCase JSON property key used in REST/GraphQL payloads.</summary>
    public required string ApiAlias { get; init; }

    /// <summary>
    /// Declared SQL data type of the field (e.g. <c>decimal</c>, <c>nvarchar</c>, <c>datetime</c>).
    /// Drives both the CLR type advertised to the GraphQL/REST layer and the <c>CAST</c> applied
    /// around <c>JSON_VALUE</c>/<c>JSON_MODIFY</c>.
    /// </summary>
    public required string DataType { get; init; }

    /// <summary>Human UI label (e.g. "Gold Grade (g/t)"). Never used to build SQL.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Front-end form visibility hint. Deliberately NOT used to filter the API surface: a field
    /// hidden in the UI is still addressable through REST/GraphQL.
    /// </summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>
    /// When true the engine rejects writes to this field. Surfaced through
    /// <c>ColumnDefinition.IsReadOnly</c> so DAB's existing read-only enforcement applies.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// True when this field is a JSON attribute carrying the container column and path required to
    /// build a <c>JSON_VALUE</c>/<c>JSON_MODIFY</c> expression.
    /// </summary>
    public bool IsProjectableJsonAttribute =>
        FieldSourceType == TenantFieldSourceType.JsonAttribute
        && !string.IsNullOrWhiteSpace(PhysicalColumnName)
        && !string.IsNullOrWhiteSpace(JsonPath);
}

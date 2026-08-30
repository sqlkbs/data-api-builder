// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Config.DatabasePrimitives;

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// A column definition for a tenant JSON attribute (<see cref="TenantFieldSourceType.JsonAttribute"/>)
/// that has no physical column of its own: its value lives as a JSON scalar inside the internal
/// system-managed container column named by <see cref="ContainerColumnName"/>, addressed by
/// <see cref="JsonPath"/>.
///
/// <para>
/// Instances are injected into <c>SourceDefinition.Columns</c> during MS SQL schema inference. That
/// single act is what makes a dynamic attribute a first-class field everywhere in DAB — GraphQL
/// object types, GraphQL create/update input types, REST <c>$select</c> validation, the default
/// projection, the OData filter model, and wildcard field-level authorization all enumerate
/// <c>SourceDefinition.Columns</c> and therefore pick the attribute up with no further changes.
/// </para>
///
/// <para>
/// Because the type derives from <see cref="ColumnDefinition"/>, DAB's generic code paths treat it
/// exactly like a physical column; only the MS SQL query builder inspects the concrete type, to
/// rewrite the column reference into a <c>JSON_VALUE</c> read or a <c>JSON_MODIFY</c> write. No core
/// generic class is modified to carry this metadata.
/// </para>
/// </summary>
public sealed class VirtualColumnDefinition : ColumnDefinition
{
    /// <summary>
    /// Exposed REST/GraphQL property key for this attribute, retained for diagnostics and for the
    /// projection alias.
    /// </summary>
    public required string ApiAlias { get; init; }

    /// <summary>
    /// Physical JSON container column the attribute is stored in (e.g. <c>CustomAttributesJson</c>).
    /// </summary>
    public required string ContainerColumnName { get; init; }

    /// <summary>JSON path of the scalar inside the container column, e.g. <c>$.RecoveryRate</c>.</summary>
    public required string JsonPath { get; init; }

    /// <summary>
    /// SQL type the <c>JSON_VALUE</c> result is cast to on read, or <c>null</c> for text attributes
    /// where <c>JSON_VALUE</c>'s native <c>nvarchar</c> result already matches the declared type.
    /// Without this cast every attribute would surface as a JSON string and a numeric or temporal
    /// GraphQL field would fail to resolve.
    /// </summary>
    public string? SqlReadCastType { get; init; }

    /// <summary>
    /// Template for the value expression handed to <c>JSON_MODIFY</c> on write, where <c>{0}</c> is
    /// the parameter placeholder — for example <c>CAST({0} AS DECIMAL(18,6))</c>. The expression's
    /// SQL type determines whether the value lands in the document as a JSON number, boolean or
    /// string, so it is declared per data type rather than inferred.
    /// </summary>
    public required string SqlWriteValueTemplate { get; init; }

    /// <summary>Renders the write value expression for the given parameter name.</summary>
    /// <param name="parameterName">Parameterized value name, e.g. <c>@param0</c>.</param>
    public string ToWriteValueExpression(string parameterName)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, SqlWriteValueTemplate, parameterName);
}

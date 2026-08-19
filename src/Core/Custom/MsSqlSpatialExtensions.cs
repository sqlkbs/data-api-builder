// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Data;
using System.Diagnostics.CodeAnalysis;
using Azure.DataApiBuilder.Config.DatabasePrimitives;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Services;
using Microsoft.Data.SqlClient;

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Custom fork extensions implementing MS SQL Server spatial (geometry/geography) support.
///
/// Spatial CLR UDTs do not have a corresponding SqlDbType enum value, so they are mapped to
/// <c>typeof(string)</c> at metadata inference time, and the SQL AST is adjusted so the CLR
/// spatial values round-trip as plain strings (Well-Known Text) at the REST/GraphQL boundary:
/// - Reads project spatial columns via <c>[Column].STAsText() AS [Column]</c>.
/// - Mutation OUTPUT clauses project spatial columns via
///   <c>Inserted.[Column].STAsText() AS [Column]</c>.
/// - Writes wrap string parameters via <c>geometry::STGeomFromText(@param, 4326)</c> or
///   <c>geography::STGeomFromText(@param, 4326)</c>.
///
/// Per the fork's Code Isolation Protocol, all spatial SQL formatting lives in this file and
/// core DAB classes only contain single-line extension method invocations.
/// </summary>
public static class MsSqlSpatialExtensions
{
    /// <summary>SQL Server data type name for the geometry CLR UDT.</summary>
    public const string GEOMETRY_TYPE_NAME = "geometry";

    /// <summary>SQL Server data type name for the geography CLR UDT.</summary>
    public const string GEOGRAPHY_TYPE_NAME = "geography";

    /// <summary>Spatial reference identifier (SRID) assumed when parsing WKT input parameters.</summary>
    public const int DEFAULT_SRID = 4326;

    private static readonly SqlCommandBuilder _identifierQuoter = new();

    /// <summary>
    /// Determines whether the given SQL Server type name is a spatial type handled by this extension.
    /// Comparison is case-insensitive to accommodate the casing of the DATA_TYPE schema collection.
    /// </summary>
    /// <param name="sqlTypeName">Raw SQL Server data type name (e.g. 'geometry').</param>
    /// <returns>True when the type name is 'geometry' or 'geography', otherwise false.</returns>
    public static bool IsSpatialType(this string? sqlTypeName)
    {
        return string.Equals(sqlTypeName, GEOMETRY_TYPE_NAME, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sqlTypeName, GEOGRAPHY_TYPE_NAME, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the given SQL Server type name is the 'geometry' spatial type.
    /// </summary>
    /// <param name="sqlTypeName">Raw SQL Server data type name.</param>
    /// <returns>True when the type name is 'geometry', otherwise false.</returns>
    public static bool IsGeometryType(this string? sqlTypeName)
    {
        return string.Equals(sqlTypeName, GEOMETRY_TYPE_NAME, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// System type mapping hook for the MS SQL type mapping. 'geometry' and 'geography' map to
    /// <c>typeof(string)</c> so spatial values flow through the engine as Well-Known Text. All other
    /// SQL Server type names are resolved by the core TypeHelper mapping.
    /// </summary>
    /// <param name="sqlTypeName">Raw SQL Server data type name sourced from the DATA_TYPE schema column.</param>
    /// <returns>The CLR type the SQL type name maps to.</returns>
    public static Type ToSpatialSystemTypeOrDefault(this string sqlTypeName)
    {
        return sqlTypeName.IsSpatialType() ? typeof(string) : TypeHelper.GetSystemTypeFromSqlDbType(sqlTypeName);
    }

    /// <summary>
    /// Applies the spatial type mapping to a table column definition: retains the raw SQL data type
    /// name (needed later by the query AST to distinguish geometry from geography) and remaps
    /// geometry/geography to <c>typeof(string)</c> with <see cref="DbType.String"/> so spatial
    /// values are serialized/deserialized as strings. Non-spatial columns are left untouched.
    /// </summary>
    /// <param name="columnDefinition">Column definition being populated from database metadata.</param>
    /// <param name="sqlTypeName">Raw SQL Server data type name (e.g. 'geometry').</param>
    public static void ApplySpatialTypeMapping(this ColumnDefinition columnDefinition, string sqlTypeName)
    {
        columnDefinition.SqlTypeName = sqlTypeName;
        if (sqlTypeName.IsSpatialType())
        {
            columnDefinition.SystemType = typeof(string);
            columnDefinition.DbType = DbType.String;
        }
    }

    /// <summary>
    /// Looks up the spatial SQL type name ('geometry' or 'geography') for a column of the given
    /// source definition. Used by the query AST to decide whether a column needs spatial formatting.
    /// </summary>
    /// <param name="sourceDefinition">Source definition of the entity being queried.</param>
    /// <param name="columnName">Backing column name.</param>
    /// <param name="spatialTypeName">The raw spatial type name when the column is spatial, otherwise null.</param>
    /// <returns>True when the column is a supported spatial type, otherwise false.</returns>
    public static bool TryGetSpatialTypeName(
        this SourceDefinition sourceDefinition,
        string columnName,
        [NotNullWhen(true)] out string? spatialTypeName)
    {
        spatialTypeName = null;
        if (sourceDefinition.Columns.TryGetValue(columnName, out ColumnDefinition? columnDefinition)
            && columnDefinition.SqlTypeName is string sqlTypeName
            && sqlTypeName.IsSpatialType())
        {
            spatialTypeName = sqlTypeName;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a SELECT column projection for a spatial type using Well-Known Text (WKT).
    /// The format is type-independent (both geometry and geography expose STAsText()).
    /// </summary>
    /// <param name="sqlTypeName">Raw SQL Server spatial type name ('geometry' or 'geography').</param>
    /// <param name="columnReference">Already quoted column reference, e.g. [dbo].[places].[shape].</param>
    /// <param name="label">Optional alias to attach to the projection; when provided the projection is emitted as ... AS [label].</param>
    /// <returns>SQL fragment such as [dbo].[places].[shape].STAsText() AS [shape].</returns>
    public static string ToSpatialProjection(this string sqlTypeName, string columnReference, string? label = null)
    {
        string projection = $"{columnReference}.STAsText()";
        return string.IsNullOrEmpty(label) ? projection : $"{projection} AS {QuoteIdentifier(label)}";
    }

    /// <summary>
    /// Formats the columns returned by a mutation (the OUTPUT clause, or the trigger-fallback
    /// subsequent SELECT) so spatial columns are projected as Well-Known Text, e.g.
    /// Inserted.[shape].STAsText() AS [shape]. Non-spatial columns keep the standard
    /// {prefix}.[Column] AS [Label] format. The format of the spatial projection is
    /// type-independent (both geometry and geography expose STAsText()).
    /// </summary>
    /// <param name="columns">Labelled columns the mutation returns.</param>
    /// <param name="columnPrefix">Qualifier applied to each column reference (e.g. 'Inserted', or the quoted table name), or empty for an unqualified reference.</param>
    /// <param name="sourceDefinition">Source definition of the entity being mutated.</param>
    /// <returns>Comma-separated list of formatted output column projections.</returns>
    public static string FormatSpatialOutputColumns(this List<LabelledColumn> columns, string columnPrefix, SourceDefinition sourceDefinition)
    {
        return string.Join(", ", columns.Select(column => FormatSpatialOutputColumn(column, columnPrefix, sourceDefinition)));
    }

    /// <summary>
    /// Formats a single mutation output column: spatial columns are projected as
    /// {prefix}.[Column].STAsText() AS [Label] (e.g. Inserted.[shape].STAsText() AS [shape]).
    /// </summary>
    private static string FormatSpatialOutputColumn(LabelledColumn column, string columnPrefix, SourceDefinition sourceDefinition)
    {
        string columnReference = string.IsNullOrEmpty(columnPrefix)
            ? QuoteIdentifier(column.ColumnName)
            : $"{columnPrefix}.{QuoteIdentifier(column.ColumnName)}";

        return sourceDefinition.TryGetSpatialTypeName(column.ColumnName, out string? spatialTypeName)
            ? spatialTypeName.ToSpatialProjection(columnReference, column.Label)
            : $"{columnReference} AS {QuoteIdentifier(column.Label)}";
    }

    /// <summary>
    /// Formats an INSERT/UPDATE parameter for a spatial type:
    /// geometry::STGeomFromText(@paramName, 4326) or geography::STGeomFromText(@paramName, 4326).
    /// </summary>
    /// <param name="sqlTypeName">Raw SQL Server spatial type name ('geometry' or 'geography').</param>
    /// <param name="paramName">Parameterized value name, e.g. @param0.</param>
    /// <returns>SQL expression converting the WKT string parameter into the CLR spatial value.</returns>
    /// <exception cref="ArgumentException">Thrown when the type name is not a supported spatial type.</exception>
    public static string ToSpatialParameter(this string sqlTypeName, string paramName)
    {
        if (!sqlTypeName.IsSpatialType())
        {
            throw new ArgumentException($"'{sqlTypeName}' is not a supported spatial data type.", nameof(sqlTypeName));
        }

        string spatialTypeName = sqlTypeName.IsGeometryType() ? GEOMETRY_TYPE_NAME : GEOGRAPHY_TYPE_NAME;
        return $"{spatialTypeName}::STGeomFromText({paramName}, {DEFAULT_SRID})";
    }

    /// <summary>
    /// Formats the given parameter name for the given column, wrapping it with STGeomFromText only
    /// when the column is a supported spatial type. Non-spatial parameters are returned unchanged.
    /// </summary>
    /// <param name="paramName">Parameterized value name, e.g. @param0.</param>
    /// <param name="sourceDefinition">Source definition of the entity being mutated.</param>
    /// <param name="columnName">Backing column name the parameter maps to.</param>
    /// <returns>The spatial-wrapped parameter or the original parameter name.</returns>
    public static string ToSpatialParameterOrDefault(this string paramName, SourceDefinition sourceDefinition, string columnName)
    {
        if (sourceDefinition.TryGetSpatialTypeName(columnName, out string? spatialTypeName))
        {
            return spatialTypeName.ToSpatialParameter(paramName);
        }

        return paramName;
    }

    /// <summary>
    /// Quotes an identifier using the SQL Server quoting rules, e.g. shape -> [shape].
    /// </summary>
    private static string QuoteIdentifier(string identifier)
    {
        return _identifierQuoter.QuoteIdentifier(identifier);
    }
}

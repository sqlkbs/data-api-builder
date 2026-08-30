// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using Azure.DataApiBuilder.Config.DatabasePrimitives;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Service.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Custom fork extensions implementing the MS SQL half of the Tenant-Aware Dynamic Schema.
///
/// <para>
/// Tenant JSON attributes (<see cref="TenantFieldSourceType.JsonAttribute"/>) are registered as
/// <see cref="VirtualColumnDefinition"/> entries in <c>SourceDefinition.Columns</c> during schema
/// inference, which is what makes them first-class fields across DAB's GraphQL schema, REST
/// validation, default projection, filter model and wildcard authorization. This file then supplies
/// the SQL rewriting those virtual columns require:
/// </para>
///
/// <list type="bullet">
/// <item>Reads project as <c>CAST(JSON_VALUE([container], '$.path') AS type) AS [apiAlias]</c>.</item>
/// <item>Writes collapse every attribute of one container column into a single nested
/// <c>JSON_MODIFY</c> assignment, so N API fields become one SQL assignment.</item>
/// <item>Attributes absent from a request are never touched. An attribute explicitly set to null
/// removes its key, and when a write leaves no attributes the container column is stored as SQL
/// <c>NULL</c> — so the container's invariant is <c>NULL</c> ⇔ no custom attributes.</item>
/// </list>
///
/// <para>
/// Per the fork's Code Isolation Protocol all of this logic lives here; the MS SQL query builder and
/// metadata provider contain only single-line invocations, and no generic cross-engine class is
/// modified.
/// </para>
/// </summary>
public static class MsSqlTenantSchemaExtensions
{
    private static readonly SqlCommandBuilder _identifierQuoter = new();

    #region Declared data type mapping

    /// <summary>
    /// Resolves the <c>DataType</c> declared in <c>dbo.sys_TenantSchemaFields</c> into everything the
    /// engine needs: the CLR type advertised to REST/GraphQL, the ADO.NET parameter types, the SQL
    /// type <c>JSON_VALUE</c> results are cast to on read, and the value expression used on write.
    ///
    /// <para>
    /// An explicit precision is honored when supplied (e.g. <c>decimal(18,6)</c>); otherwise a wide
    /// default is used. Spatial types are intentionally rejected: a CLR spatial value cannot be
    /// meaningfully stored as a JSON scalar, and the existing spatial support already handles
    /// physical geometry/geography columns.
    /// </para>
    /// </summary>
    /// <param name="dataType">Declared SQL data type, optionally including precision.</param>
    /// <param name="mapping">The resolved mapping when the type is supported.</param>
    /// <returns>True when the declared type is supported for a JSON attribute.</returns>
    public static bool TryMapTenantDataType(string? dataType, [NotNullWhen(true)] out TenantDataTypeMapping? mapping)
    {
        mapping = null;

        if (string.IsNullOrWhiteSpace(dataType))
        {
            return false;
        }

        string declared = dataType.Trim();

        // Split 'decimal(18,6)' into the base type name and its parenthesized arguments.
        string baseType = declared;
        string? arguments = null;
        int parenthesis = declared.IndexOf('(', StringComparison.Ordinal);
        if (parenthesis > 0 && declared.EndsWith(')'))
        {
            baseType = declared[..parenthesis].Trim();
            arguments = declared[(parenthesis + 1)..^1].Trim();
        }

        switch (baseType.ToLowerInvariant())
        {
            case "nvarchar":
            case "varchar":
            case "nchar":
            case "char":
            case "text":
            case "ntext":
                // JSON_VALUE already returns nvarchar, so no read cast is needed. On write the raw
                // string parameter lands in the document as a JSON string.
                mapping = new TenantDataTypeMapping(typeof(string), DbType.String, SqlDbType.NVarChar, null, "{0}");
                return true;

            case "int":
            case "integer":
                mapping = new TenantDataTypeMapping(typeof(int), DbType.Int32, SqlDbType.Int, "INT", "CAST({0} AS INT)");
                return true;

            case "bigint":
                mapping = new TenantDataTypeMapping(typeof(long), DbType.Int64, SqlDbType.BigInt, "BIGINT", "CAST({0} AS BIGINT)");
                return true;

            case "smallint":
                mapping = new TenantDataTypeMapping(typeof(short), DbType.Int16, SqlDbType.SmallInt, "SMALLINT", "CAST({0} AS SMALLINT)");
                return true;

            case "tinyint":
                mapping = new TenantDataTypeMapping(typeof(byte), DbType.Byte, SqlDbType.TinyInt, "TINYINT", "CAST({0} AS TINYINT)");
                return true;

            case "bit":
            case "boolean":
            case "bool":
                // Stored as a JSON number (0/1) because JSON_MODIFY has no boolean coercion; reads
                // cast back to BIT so both 0/1 and true/false round-trip.
                mapping = new TenantDataTypeMapping(typeof(bool), DbType.Boolean, SqlDbType.Bit, "BIT", "CAST({0} AS BIT)");
                return true;

            case "decimal":
            case "numeric":
            case "money":
            case "smallmoney":
            {
                string precision = IsValidPrecision(arguments) ? arguments! : "38,10";
                mapping = new TenantDataTypeMapping(
                    typeof(decimal),
                    DbType.Decimal,
                    SqlDbType.Decimal,
                    $"DECIMAL({precision})",
                    $"CAST({{0}} AS DECIMAL({precision}))");
                return true;
            }

            case "float":
            case "double":
                mapping = new TenantDataTypeMapping(typeof(double), DbType.Double, SqlDbType.Float, "FLOAT", "CAST({0} AS FLOAT)");
                return true;

            case "real":
                mapping = new TenantDataTypeMapping(typeof(float), DbType.Single, SqlDbType.Real, "REAL", "CAST({0} AS REAL)");
                return true;

            case "date":
                mapping = new TenantDataTypeMapping(typeof(DateTime), DbType.Date, SqlDbType.Date, "DATE", "CONVERT(NVARCHAR(10), {0}, 23)");
                return true;

            case "datetime":
            case "datetime2":
            case "smalldatetime":
                // ISO 8601 (style 127) keeps the JSON text sortable and unambiguous, and casts back
                // to DATETIME2 losslessly on read.
                mapping = new TenantDataTypeMapping(typeof(DateTime), DbType.DateTime2, SqlDbType.DateTime2, "DATETIME2", "CONVERT(NVARCHAR(33), {0}, 127)");
                return true;

            case "datetimeoffset":
                mapping = new TenantDataTypeMapping(typeof(DateTimeOffset), DbType.DateTimeOffset, SqlDbType.DateTimeOffset, "DATETIMEOFFSET", "CONVERT(NVARCHAR(33), {0}, 127)");
                return true;

            case "time":
                mapping = new TenantDataTypeMapping(typeof(TimeOnly), DbType.Time, SqlDbType.Time, "TIME", "CONVERT(NVARCHAR(16), {0}, 114)");
                return true;

            case "uniqueidentifier":
            case "guid":
                mapping = new TenantDataTypeMapping(typeof(Guid), DbType.Guid, SqlDbType.UniqueIdentifier, "UNIQUEIDENTIFIER", "CAST({0} AS NVARCHAR(36))");
                return true;

            default:
                return false;
        }
    }

    /// <summary>Validates a parenthesized precision such as <c>18,6</c> or <c>18</c>.</summary>
    private static bool IsValidPrecision(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        foreach (string part in arguments.Split(','))
        {
            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Virtual column injection

    /// <summary>
    /// Injects the tenant's JSON attributes into an entity's <see cref="SourceDefinition"/> as
    /// <see cref="VirtualColumnDefinition"/> entries. Invoked during MS SQL schema inference, after
    /// the physical columns have been discovered and before DAB builds its exposed-to-backing name
    /// maps, so the new columns participate in every downstream mapping.
    ///
    /// <para>
    /// A field is skipped with a warning — never an exception — when it cannot be represented
    /// safely, so a malformed tenant schema row can degrade one field but can never prevent the
    /// engine from starting.
    /// </para>
    /// </summary>
    /// <param name="sourceDefinition">Entity source definition being populated.</param>
    /// <param name="entityName">Entity name, for diagnostics and config reconciliation.</param>
    /// <param name="entity">Entity configuration, used to honor a generated dab-config mapping/alias.</param>
    /// <param name="jsonAttributes">Projectable JSON attributes registered for this tenant entity.</param>
    /// <param name="logger">Optional logger for skip diagnostics.</param>
    /// <returns>The number of virtual columns injected.</returns>
    public static int ApplyTenantVirtualColumns(
        this SourceDefinition sourceDefinition,
        string entityName,
        Entity? entity,
        IReadOnlyList<TenantSchemaField> jsonAttributes,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceDefinition);

        if (jsonAttributes.Count == 0)
        {
            return 0;
        }

        int injected = 0;

        foreach (TenantSchemaField field in jsonAttributes)
        {
            if (!field.IsProjectableJsonAttribute)
            {
                logger?.LogWarning(
                    "Tenant schema field '{ApiAlias}' on entity '{EntityName}' is marked as a JSON attribute but is missing a container column or JSON path; it is not exposed.",
                    field.ApiAlias,
                    entityName);
                continue;
            }

            if (!IsSafeIdentifier(field.ApiAlias))
            {
                logger?.LogWarning(
                    "Tenant schema field alias '{ApiAlias}' on entity '{EntityName}' is not a valid GraphQL/OData identifier; it is not exposed.",
                    field.ApiAlias,
                    entityName);
                continue;
            }

            if (!TryMapTenantDataType(field.DataType, out TenantDataTypeMapping? mapping))
            {
                logger?.LogWarning(
                    "Tenant schema field '{ApiAlias}' on entity '{EntityName}' declares unsupported DataType '{DataType}'; it is not exposed. Spatial types cannot be stored as JSON attributes.",
                    field.ApiAlias,
                    entityName,
                    field.DataType);
                continue;
            }

            // The container column must be a real column of this table, otherwise the generated
            // JSON_VALUE/JSON_MODIFY expressions would reference a non-existent column.
            if (!sourceDefinition.Columns.TryGetValue(field.PhysicalColumnName, out ColumnDefinition? containerColumn)
                || containerColumn is VirtualColumnDefinition)
            {
                logger?.LogWarning(
                    "Tenant schema field '{ApiAlias}' on entity '{EntityName}' references JSON container column '{ContainerColumn}', which is not a physical column of the entity; it is not exposed.",
                    field.ApiAlias,
                    entityName,
                    field.PhysicalColumnName);
                continue;
            }

            string backingName = ResolveBackingName(entity, field.ApiAlias);

            // A physical column always wins: the tenant schema may not shadow real table columns.
            if (sourceDefinition.Columns.ContainsKey(backingName))
            {
                logger?.LogWarning(
                    "Tenant schema field '{ApiAlias}' on entity '{EntityName}' resolves to backing name '{BackingName}', which already exists as a physical column; the physical column takes precedence and the JSON attribute is not exposed.",
                    field.ApiAlias,
                    entityName,
                    backingName);
                continue;
            }

            sourceDefinition.Columns.Add(backingName, new VirtualColumnDefinition
            {
                ApiAlias = field.ApiAlias,
                ContainerColumnName = field.PhysicalColumnName,
                JsonPath = field.JsonPath!,
                SqlReadCastType = mapping.SqlReadCastType,
                SqlWriteValueTemplate = mapping.SqlWriteValueTemplate,
                SystemType = mapping.SystemType,
                DbType = mapping.DbType,
                SqlDbType = mapping.SqlDbType,
                SqlTypeName = field.DataType,
                // A JSON attribute is always optional: the key may simply be absent from the document.
                IsNullable = true,
                IsReadOnly = field.IsReadOnly,
                HasDefault = false,
                IsAutoGenerated = false
            });

            injected++;
        }

        if (injected > 0)
        {
            logger?.LogInformation(
                "Injected {InjectedCount} tenant JSON attribute(s) as virtual columns on entity '{EntityName}'.",
                injected,
                entityName);
        }

        return injected;
    }

    /// <summary>
    /// Resolves the name a virtual column should be keyed by in <c>SourceDefinition.Columns</c>.
    ///
    /// <para>
    /// The tenant schema registry is authoritative, but the config generator may additionally emit a
    /// documentation mapping or field alias for the same attribute. When it does, that mapping
    /// already defines the exposed-to-backing relationship DAB will use, so the virtual column must
    /// be keyed by the mapping's backing name — otherwise <c>TryGetBackingColumn</c> and
    /// <c>SourceDefinition.Columns</c> would disagree and REST would reject the field. When no
    /// mapping exists the alias itself is used, producing an identity mapping.
    /// </para>
    /// </summary>
    private static string ResolveBackingName(Entity? entity, string apiAlias)
    {
        if (entity?.Fields is not null)
        {
            foreach (FieldMetadata fieldMetadata in entity.Fields)
            {
                string exposed = string.IsNullOrWhiteSpace(fieldMetadata.Alias) ? fieldMetadata.Name : fieldMetadata.Alias!;
                if (string.Equals(exposed, apiAlias, StringComparison.OrdinalIgnoreCase))
                {
                    return fieldMetadata.Name;
                }
            }
        }

        if (entity?.Mappings is not null)
        {
            foreach (KeyValuePair<string, string> mapping in entity.Mappings)
            {
                if (string.Equals(mapping.Value, apiAlias, StringComparison.OrdinalIgnoreCase))
                {
                    return mapping.Key;
                }
            }
        }

        return apiAlias;
    }

    /// <summary>
    /// Ensures an alias is a plain identifier. Guards both the GraphQL schema (where an invalid name
    /// would break schema construction) and the generated SQL.
    /// </summary>
    private static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Metadata provider hooks

    /// <summary>
    /// First half of the metadata provider hook, invoked while trigger metadata is collected for a
    /// table — the last point in MS SQL schema inference that is both asynchronous and aware of the
    /// entity name.
    ///
    /// <para>
    /// Hydrates the registry (lazily, at most once per process) and returns the injection target to
    /// be consumed once the physical columns are known. Returns <c>null</c> — making the whole
    /// feature a no-op — when the registry is not registered or the data source declares no tenant.
    /// </para>
    /// </summary>
    /// <param name="runtimeConfigProvider">Provider used to resolve the data source's tenant.</param>
    /// <param name="dataSourceName">Data source the metadata provider belongs to.</param>
    /// <param name="entityName">Entity whose metadata is being populated.</param>
    /// <param name="sourceDefinition">Source definition being populated.</param>
    public static async Task<TenantVirtualColumnTarget?> PrepareTenantVirtualColumnsAsync(
        RuntimeConfigProvider runtimeConfigProvider,
        string dataSourceName,
        string entityName,
        SourceDefinition sourceDefinition)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfigProvider);

        ITenantSchemaRegistryService? registry = TenantSchemaRegistryAccessor.Current;
        if (registry is null)
        {
            return null;
        }

        if (!runtimeConfigProvider.TryGetLoadedConfig(out RuntimeConfig? runtimeConfig)
            || !runtimeConfig.CheckDataSourceExists(dataSourceName)
            || !runtimeConfig.GetDataSourceFromDataSourceName(dataSourceName).TryGetTenantId(out string? tenantId))
        {
            return null;
        }

        await registry.EnsureHydratedAsync();

        return new TenantVirtualColumnTarget(sourceDefinition, entityName, tenantId);
    }

    /// <summary>
    /// Second half of the metadata provider hook, invoked once every physical column of the table has
    /// been discovered. Injects the tenant's JSON attributes and consumes the target.
    ///
    /// <para>
    /// Running here rather than earlier is deliberate: a JSON attribute whose alias collides with a
    /// real column must lose, which is only detectable once the physical columns exist. The target is
    /// verified against the supplied source definition so that entities without trigger metadata —
    /// views, for instance — never pick up a previous table's attributes.
    /// </para>
    /// </summary>
    /// <param name="target">Target captured by <see cref="PrepareTenantVirtualColumnsAsync"/>.</param>
    /// <param name="runtimeConfigProvider">Provider used to resolve the entity configuration.</param>
    /// <param name="sourceDefinition">Source definition whose columns were just populated.</param>
    /// <param name="logger">Optional logger for skip diagnostics.</param>
    /// <returns>Always <c>null</c>, so the caller's captured target is cleared.</returns>
    public static TenantVirtualColumnTarget? InjectTenantVirtualColumns(
        this TenantVirtualColumnTarget? target,
        RuntimeConfigProvider runtimeConfigProvider,
        SourceDefinition sourceDefinition,
        ILogger? logger = null)
    {
        if (target is null || !ReferenceEquals(target.SourceDefinition, sourceDefinition))
        {
            return null;
        }

        ITenantSchemaRegistryService? registry = TenantSchemaRegistryAccessor.Current;
        if (registry is null)
        {
            return null;
        }

        IReadOnlyList<TenantSchemaField> jsonAttributes = registry.GetJsonAttributes(target.TenantId, target.EntityName);
        if (jsonAttributes.Count == 0)
        {
            return null;
        }

        Entity? entity = null;
        if (runtimeConfigProvider.TryGetLoadedConfig(out RuntimeConfig? runtimeConfig))
        {
            runtimeConfig.Entities.TryGetValue(target.EntityName, out entity);
        }

        sourceDefinition.ApplyTenantVirtualColumns(target.EntityName, entity, jsonAttributes, logger);
        return null;
    }

    #endregion

    #region Read projection

    /// <summary>
    /// Builds the read expression for a virtual column, given an already-qualified reference to its
    /// JSON container column (e.g. <c>[q1].[CustomAttributesJson]</c> or
    /// <c>Inserted.[CustomAttributesJson]</c>).
    ///
    /// <para>
    /// The cast is what preserves the declared type through DAB's <c>FOR JSON PATH</c> serialization:
    /// without it every attribute would surface as a JSON string and numeric or temporal GraphQL
    /// fields would fail to resolve.
    /// </para>
    /// </summary>
    /// <param name="virtualColumn">Virtual column being projected.</param>
    /// <param name="containerReference">Qualified reference to the container column.</param>
    public static string ToJsonValueExpression(this VirtualColumnDefinition virtualColumn, string containerReference)
    {
        ArgumentNullException.ThrowIfNull(virtualColumn);

        string jsonValue = $"JSON_VALUE({containerReference}, '{EscapeSqlLiteral(virtualColumn.JsonPath)}')";
        return virtualColumn.SqlReadCastType is null
            ? jsonValue
            : $"CAST({jsonValue} AS {virtualColumn.SqlReadCastType})";
    }

    #endregion

    #region Write collapsing

    /// <summary>
    /// Builds the <c>SET</c> assignment list of an UPDATE/UPSERT, collapsing every tenant JSON
    /// attribute targeting the same container column into one nested <c>JSON_MODIFY</c> assignment.
    ///
    /// <para>
    /// This must run instead of — not after — generic predicate building, because the left-hand side
    /// of an assignment cannot be rewritten into a <c>JSON_VALUE</c> expression.
    /// </para>
    /// </summary>
    /// <param name="updateOperations">Assignments produced by the query structure.</param>
    /// <param name="sourceDefinition">Source definition of the entity being mutated.</param>
    /// <param name="parameters">Query parameters, used to detect explicitly null values.</param>
    /// <param name="buildPhysicalAssignment">Callback rendering a physical column assignment using DAB's own predicate builder.</param>
    public static string BuildUpdateOperations(
        List<Predicate> updateOperations,
        SourceDefinition sourceDefinition,
        IReadOnlyDictionary<string, DbConnectionParam> parameters,
        Func<Predicate, string> buildPhysicalAssignment)
    {
        ArgumentNullException.ThrowIfNull(updateOperations);
        ArgumentNullException.ThrowIfNull(sourceDefinition);
        ArgumentNullException.ThrowIfNull(buildPhysicalAssignment);

        List<string> assignments = new();
        Dictionary<string, List<(VirtualColumnDefinition Column, string ParameterExpression)>> byContainer =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> physicalTargets = new(StringComparer.OrdinalIgnoreCase);

        foreach (Predicate operation in updateOperations)
        {
            Column? target = operation.Left?.AsColumn();
            if (target is not null
                && sourceDefinition.Columns.TryGetValue(target.ColumnName, out ColumnDefinition? columnDefinition)
                && columnDefinition is VirtualColumnDefinition virtualColumn)
            {
                if (!byContainer.TryGetValue(virtualColumn.ContainerColumnName, out var attributes))
                {
                    attributes = new List<(VirtualColumnDefinition, string)>();
                    byContainer.Add(virtualColumn.ContainerColumnName, attributes);
                }

                attributes.Add((virtualColumn, operation.Right.AsString() ?? string.Empty));
                continue;
            }

            if (target is not null)
            {
                physicalTargets.Add(target.ColumnName);
            }

            assignments.Add(buildPhysicalAssignment(operation));
        }

        foreach ((string containerColumn, var attributes) in byContainer)
        {
            // Writing the container column directly and through its attributes in one statement would
            // emit two assignments for the same column, which SQL Server rejects.
            if (physicalTargets.Contains(containerColumn))
            {
                throw new DataApiBuilderException(
                    message: $"The JSON container column '{containerColumn}' cannot be modified directly in the same request that modifies its tenant custom attributes.",
                    statusCode: HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
            }

            string quotedContainer = QuoteIdentifier(containerColumn);
            string document = $"ISNULL({quotedContainer}, '{{}}')";
            assignments.Add($"{quotedContainer} = {BuildJsonModifyChain(document, attributes, parameters)}");
        }

        return string.Join(", ", assignments);
    }

    /// <summary>
    /// Collapses an INSERT's column and value lists so tenant JSON attributes become a single
    /// <c>JSON_MODIFY</c> expression assigned to their container column.
    /// </summary>
    /// <param name="insertColumns">Backing column names being inserted.</param>
    /// <param name="values">Value expressions, positionally paired with <paramref name="insertColumns"/>.</param>
    /// <param name="sourceDefinition">Source definition of the entity being inserted into.</param>
    /// <param name="parameters">Query parameters, used to detect explicitly null values.</param>
    /// <returns>The quoted column list and matching value list.</returns>
    public static (string ColumnList, string ValueList) CollapseInsertColumnsAndValues(
        List<string> insertColumns,
        List<string> values,
        SourceDefinition sourceDefinition,
        IReadOnlyDictionary<string, DbConnectionParam> parameters)
    {
        ArgumentNullException.ThrowIfNull(insertColumns);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(sourceDefinition);

        List<string> columnList = new();
        List<string> valueList = new();
        Dictionary<string, List<(VirtualColumnDefinition Column, string ParameterExpression)>> byContainer =
            new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < insertColumns.Count; i++)
        {
            string columnName = insertColumns[i];
            string value = i < values.Count ? values[i] : string.Empty;

            if (sourceDefinition.Columns.TryGetValue(columnName, out ColumnDefinition? columnDefinition)
                && columnDefinition is VirtualColumnDefinition virtualColumn)
            {
                if (!byContainer.TryGetValue(virtualColumn.ContainerColumnName, out var attributes))
                {
                    attributes = new List<(VirtualColumnDefinition, string)>();
                    byContainer.Add(virtualColumn.ContainerColumnName, attributes);
                }

                attributes.Add((virtualColumn, value));
                continue;
            }

            columnList.Add(QuoteIdentifier(columnName));
            valueList.Add(value);
        }

        foreach ((string containerColumn, var attributes) in byContainer)
        {
            if (insertColumns.Any(column => string.Equals(column, containerColumn, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DataApiBuilderException(
                    message: $"The JSON container column '{containerColumn}' cannot be written directly in the same request that writes its tenant custom attributes.",
                    statusCode: HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
            }

            columnList.Add(QuoteIdentifier(containerColumn));
            valueList.Add(BuildJsonModifyChain("'{}'", attributes, parameters));
        }

        return (string.Join(", ", columnList), string.Join(", ", valueList));
    }

    /// <summary>
    /// Nests one <c>JSON_MODIFY</c> call per attribute around the given JSON document expression.
    /// Only the supplied attributes appear, so keys absent from the request are left untouched —
    /// a write can add or overwrite a tenant's attributes but never drop the ones it did not mention.
    ///
    /// <para>
    /// An explicitly null attribute removes its key: <c>JSON_MODIFY(doc, path, NULL)</c> in lax mode is
    /// SQL Server's native delete, and it is null-safe (no error when the path does not exist). When
    /// the chain leaves the document holding no values, the whole expression collapses to SQL
    /// <c>NULL</c> via <c>NULLIF(..., N'{}')</c>, so the container column has the invariant
    /// <c>NULL</c> ⇔ no custom attributes stored — <c>JSON_VALUE</c> reads a NULL container, a missing
    /// key and a JSON null identically, so the API surface is unchanged.
    /// </para>
    /// </summary>
    private static string BuildJsonModifyChain(
        string documentExpression,
        List<(VirtualColumnDefinition Column, string ParameterExpression)> attributes,
        IReadOnlyDictionary<string, DbConnectionParam> parameters)
    {
        StringBuilder document = new(documentExpression);

        foreach ((VirtualColumnDefinition virtualColumn, string parameterExpression) in attributes)
        {
            string path = EscapeSqlLiteral(virtualColumn.JsonPath);

            if (IsExplicitNull(parameterExpression, parameters))
            {
                document = new StringBuilder($"JSON_MODIFY({document}, '{path}', NULL)");
                continue;
            }

            document = new StringBuilder(
                $"JSON_MODIFY({document}, '{path}', {virtualColumn.ToWriteValueExpression(parameterExpression)})");
        }

        // An empty document holds no attributes, so it is stored as SQL NULL rather than as an empty
        // object. Both the insert chain (base '{}') and the update chain (base ISNULL(col,'{}')) are
        // wrapped, which keeps the "NULL means no custom attributes" invariant uniform across paths.
        return $"NULLIF({document}, N'{{}}')";
    }

    /// <summary>
    /// Determines whether a value expression is a bare parameter bound to null. Wrapped expressions
    /// (for example the spatial <c>STGeomFromText</c> form) are never treated as null.
    /// </summary>
    private static bool IsExplicitNull(string parameterExpression, IReadOnlyDictionary<string, DbConnectionParam> parameters)
    {
        return parameters.TryGetValue(parameterExpression, out DbConnectionParam? parameter) && parameter.Value is null;
    }

    #endregion

    #region Mutation output columns

    /// <summary>
    /// Formats the columns a mutation returns, projecting tenant JSON attributes through
    /// <c>JSON_VALUE</c> and delegating every physical column — including spatial ones — to the
    /// existing spatial formatter.
    /// </summary>
    /// <param name="columns">Labelled columns the mutation returns.</param>
    /// <param name="columnPrefix">Qualifier applied to each column reference (e.g. <c>Inserted</c>), or empty.</param>
    /// <param name="sourceDefinition">Source definition of the entity being mutated.</param>
    public static string FormatOutputColumns(
        this List<LabelledColumn> columns,
        string columnPrefix,
        SourceDefinition sourceDefinition)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(sourceDefinition);

        return string.Join(", ", columns.Select(column =>
        {
            if (sourceDefinition.Columns.TryGetValue(column.ColumnName, out ColumnDefinition? columnDefinition)
                && columnDefinition is VirtualColumnDefinition virtualColumn)
            {
                string containerReference = string.IsNullOrEmpty(columnPrefix)
                    ? QuoteIdentifier(virtualColumn.ContainerColumnName)
                    : $"{columnPrefix}.{QuoteIdentifier(virtualColumn.ContainerColumnName)}";

                return $"{virtualColumn.ToJsonValueExpression(containerReference)} AS {QuoteIdentifier(column.Label)}";
            }

            return MsSqlSpatialExtensions.FormatSpatialOutputColumn(column, columnPrefix, sourceDefinition);
        }));
    }

    #endregion

    private static string EscapeSqlLiteral(string literal) => literal.Replace("'", "''", StringComparison.Ordinal);

    private static string QuoteIdentifier(string identifier) => _identifierQuoter.QuoteIdentifier(identifier);
}

/// <summary>
/// Everything the engine derives from a tenant field's declared <c>DataType</c>.
/// </summary>
/// <param name="SystemType">CLR type advertised to REST/GraphQL.</param>
/// <param name="DbType">ADO.NET parameter type.</param>
/// <param name="SqlDbType">SQL Server parameter type.</param>
/// <param name="SqlReadCastType">SQL type the <c>JSON_VALUE</c> result is cast to, or null for text.</param>
/// <param name="SqlWriteValueTemplate">Value expression template for <c>JSON_MODIFY</c>, with <c>{0}</c> as the parameter.</param>
public sealed record TenantDataTypeMapping(
    Type SystemType,
    DbType DbType,
    SqlDbType SqlDbType,
    string? SqlReadCastType,
    string SqlWriteValueTemplate);

/// <summary>
/// Pending virtual column injection for one entity, carried between the two MS SQL metadata provider
/// hooks. <see cref="SourceDefinition"/> is retained so the second hook can verify it is completing
/// the entity the first hook captured.
/// </summary>
/// <param name="SourceDefinition">Source definition being populated.</param>
/// <param name="EntityName">Entity whose JSON attributes should be injected.</param>
/// <param name="TenantId">Tenant declared by the owning data source.</param>
public sealed record TenantVirtualColumnTarget(
    SourceDefinition SourceDefinition,
    string EntityName,
    string TenantId);

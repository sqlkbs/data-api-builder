// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using Azure.DataApiBuilder.Config.DatabasePrimitives;
using Azure.DataApiBuilder.Core.Models;

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Ambient, per-query scope that lets the MS SQL query builder recognize tenant virtual columns
/// while rendering an individual column reference.
///
/// <para>
/// <b>Why an ambient scope.</b> <c>MsSqlQueryBuilder</c> is constructed with <c>new</c> by
/// <c>QueryManagerFactory</c>, so it has no dependency injection, no <c>HttpContext</c>, and no
/// registry reference. <c>BaseSqlQueryBuilder.Build(Column)</c> — the single chokepoint through which
/// projections, filter predicates, ORDER BY, GROUP BY and HAVING all render a column — receives only
/// schema/table/alias/column strings and no entity context. This scope supplies the missing context
/// by capturing the <see cref="SourceDefinition"/> of the structure currently being built, which is
/// where the injected <see cref="VirtualColumnDefinition"/> instances live.
/// </para>
///
/// <para>
/// <b>Why it is tenant-safe.</b> Because every tenant is its own data source, and metadata providers
/// (and therefore <see cref="SourceDefinition"/> instances) are created per data source, the captured
/// definition is already scoped to one tenant. Two tenants that share the same physical table but
/// declare different aliases for it can never be confused, which a process-wide
/// schema/table/column catalog could not guarantee.
/// </para>
///
/// <para>
/// <b>Why <see cref="ThreadStaticAttribute"/> is sufficient.</b> Query building is entirely
/// synchronous string construction with no await points, so the whole push/render/pop cycle runs on
/// one thread. Frames form a stack because <c>Build(SqlQueryStructure)</c> recurses into
/// <c>OUTER APPLY</c> subqueries, each of which pushes its own frame.
/// </para>
/// </summary>
internal static class TenantVirtualColumnScope
{
    [ThreadStatic]
    private static List<Frame>? _frames;

    /// <summary>
    /// Pushes the given structure's source definition onto the ambient stack. Always returns a
    /// scope, so callers can use a single unconditional <c>using</c> statement.
    /// </summary>
    /// <param name="sourceDefinition">Source definition of the structure being built.</param>
    /// <param name="schemaName">Schema of the underlying database object.</param>
    /// <param name="tableName">Name of the underlying database object.</param>
    /// <param name="sourceAlias">Alias the structure assigns to the source, when it uses one.</param>
    internal static Scope Push(
        SourceDefinition sourceDefinition,
        string schemaName,
        string tableName,
        string? sourceAlias)
    {
        List<Frame> frames = _frames ??= new List<Frame>(capacity: 4);
        frames.Add(new Frame(
            sourceDefinition: sourceDefinition,
            schemaName: schemaName,
            tableName: tableName,
            sourceAlias: sourceAlias,
            hasVirtualColumns: ContainsVirtualColumn(sourceDefinition)));

        return default;
    }

    /// <summary>
    /// Resolves a column reference to its tenant virtual column definition, when the column belongs
    /// to a source on the ambient stack and is a JSON attribute rather than a physical column.
    /// </summary>
    internal static bool TryResolve(Column column, [NotNullWhen(true)] out VirtualColumnDefinition? virtualColumn)
    {
        virtualColumn = null;

        List<Frame>? frames = _frames;
        if (frames is null || frames.Count == 0)
        {
            return false;
        }

        // Innermost frame first: a nested subquery's source shadows the outer one.
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            Frame frame = frames[i];
            if (!frame.Matches(column))
            {
                continue;
            }

            if (frame.HasVirtualColumns
                && frame.SourceDefinition.Columns.TryGetValue(column.ColumnName, out ColumnDefinition? columnDefinition)
                && columnDefinition is VirtualColumnDefinition resolved)
            {
                virtualColumn = resolved;
                return true;
            }

            // The owning source was found and the column is physical; stop searching outward.
            return false;
        }

        return false;
    }

    /// <summary>
    /// True when any column of the source definition is a tenant JSON attribute. Evaluated once per
    /// pushed frame so that per-column resolution stays a single dictionary lookup.
    /// </summary>
    internal static bool ContainsVirtualColumn(SourceDefinition sourceDefinition)
    {
        foreach (ColumnDefinition columnDefinition in sourceDefinition.Columns.Values)
        {
            if (columnDefinition is VirtualColumnDefinition)
            {
                return true;
            }
        }

        return false;
    }

    private static void Pop()
    {
        List<Frame>? frames = _frames;
        if (frames is not null && frames.Count > 0)
        {
            frames.RemoveAt(frames.Count - 1);
        }
    }

    /// <summary>
    /// Disposable handle popping the frame pushed by <see cref="Push"/>. A struct, so an
    /// unconditional <c>using</c> in the query builder allocates nothing.
    /// </summary>
    internal readonly struct Scope : IDisposable
    {
        public void Dispose() => Pop();
    }

    private readonly struct Frame
    {
        public Frame(
            SourceDefinition sourceDefinition,
            string schemaName,
            string tableName,
            string? sourceAlias,
            bool hasVirtualColumns)
        {
            SourceDefinition = sourceDefinition;
            SchemaName = schemaName;
            TableName = tableName;
            SourceAlias = sourceAlias;
            HasVirtualColumns = hasVirtualColumns;
        }

        public SourceDefinition SourceDefinition { get; }

        public string SchemaName { get; }

        public string TableName { get; }

        public string? SourceAlias { get; }

        public bool HasVirtualColumns { get; }

        /// <summary>
        /// Determines whether a column reference belongs to this frame's source. Read structures
        /// alias their source, while mutation structures reference the qualified table directly, so
        /// both forms are matched. Columns of a joined, unrelated table match neither and are
        /// therefore never rewritten.
        /// </summary>
        public bool Matches(Column column)
        {
            if (!string.IsNullOrEmpty(column.TableAlias))
            {
                return string.Equals(column.TableAlias, SourceAlias, StringComparison.Ordinal);
            }

            return string.Equals(column.TableName, TableName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(column.TableSchema, SchemaName, StringComparison.OrdinalIgnoreCase);
        }
    }
}

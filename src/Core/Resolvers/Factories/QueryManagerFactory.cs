// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom.HotReload;
using Azure.DataApiBuilder.Service.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static Azure.DataApiBuilder.Config.DabConfigEvents;

namespace Azure.DataApiBuilder.Core.Resolvers.Factories
{
    /// <summary>
    /// QueryManagerFactory. Implements IQueryManagerFactory
    /// Used to get the appropriate query builder, query executor and exception parser and  based on the database type.
    /// </summary>
    public class QueryManagerFactory : IAbstractQueryManagerFactory
    {
        // Internally mutated during Hot-Reload
        private IDictionary<DatabaseType, IQueryBuilder> _queryBuilders;
        private IDictionary<DatabaseType, IQueryExecutor> _queryExecutors;
        private IDictionary<DatabaseType, DbExceptionParser> _dbExceptionsParsers;
        private readonly RuntimeConfigProvider _runtimeConfigProvider;
        private readonly ILogger<IQueryExecutor> _logger;
        private readonly IHttpContextAccessor _contextAccessor;
        private readonly HotReloadEventHandler<HotReloadEventArgs>? _handler;
        private readonly IOboTokenProvider? _oboTokenProvider;

        /// <summary>
        /// Initiates an instance of QueryManagerFactory
        /// </summary>
        /// <param name="runtimeConfigProvider">runtimeconfigprovider.</param>
        /// <param name="logger">logger.</param>
        /// <param name="contextAccessor">httpcontextaccessor.</param>
        /// <param name="oboTokenProvider">Optional OBO token provider for user-delegated authentication.</param>
        public QueryManagerFactory(
            RuntimeConfigProvider runtimeConfigProvider,
            ILogger<IQueryExecutor> logger,
            IHttpContextAccessor contextAccessor,
            HotReloadEventHandler<HotReloadEventArgs>? handler,
            IOboTokenProvider? oboTokenProvider = null)
        {
            handler?.Subscribe(QUERY_MANAGER_FACTORY_ON_CONFIG_CHANGED, OnConfigChanged);
            _handler = handler;
            _runtimeConfigProvider = runtimeConfigProvider;
            _logger = logger;
            _contextAccessor = contextAccessor;
            _oboTokenProvider = oboTokenProvider;
            _queryBuilders = new Dictionary<DatabaseType, IQueryBuilder>();
            _queryExecutors = new Dictionary<DatabaseType, IQueryExecutor>();
            _dbExceptionsParsers = new Dictionary<DatabaseType, DbExceptionParser>();

            ConfigureQueryManagerFactory();
        }

        private void ConfigureQueryManagerFactory()
        {

            foreach (DataSource dataSource in _runtimeConfigProvider.GetConfig().ListAllDataSources())
            {
                if (_queryBuilders.ContainsKey(dataSource.DatabaseType))
                {
                    // we have already created the builder, parser and executor for this database type.No need to create again.
                    continue;
                }

                if (TryCreateQueryComponents(dataSource, out IQueryBuilder? queryBuilder, out IQueryExecutor? queryExecutor, out DbExceptionParser? exceptionParser))
                {
                    _queryBuilders.TryAdd(dataSource.DatabaseType, queryBuilder!);
                    _queryExecutors.TryAdd(dataSource.DatabaseType, queryExecutor!);
                    _dbExceptionsParsers.TryAdd(dataSource.DatabaseType, exceptionParser!);
                }
            }
        }

        /// <summary>
        /// Creates the query builder, executor, and exception parser for a data source's database
        /// type. Returns true when the database type has components to register (all types do;
        /// Cosmos deliberately registers null components because the Cosmos query engine does not
        /// consume the SQL query-manager layer).
        /// </summary>
        private bool TryCreateQueryComponents(
            DataSource dataSource,
            out IQueryBuilder? queryBuilder,
            out IQueryExecutor? queryExecutor,
            out DbExceptionParser? exceptionParser)
        {
            queryBuilder = null;
            queryExecutor = null;
            exceptionParser = null;

            switch (dataSource.DatabaseType)
            {
                case DatabaseType.CosmosDB_NoSQL:
                    break;
                case DatabaseType.MSSQL:
                    queryBuilder = new MsSqlQueryBuilder();
                    exceptionParser = new MsSqlDbExceptionParser(_runtimeConfigProvider);
                    queryExecutor = new MsSqlQueryExecutor(_runtimeConfigProvider, exceptionParser, _logger, _contextAccessor, _handler, _oboTokenProvider);
                    break;
                case DatabaseType.MySQL:
                    queryBuilder = new MySqlQueryBuilder();
                    exceptionParser = new MySqlDbExceptionParser(_runtimeConfigProvider);
                    queryExecutor = new MySqlQueryExecutor(_runtimeConfigProvider, exceptionParser, _logger, _contextAccessor, _handler);
                    break;
                case DatabaseType.PostgreSQL:
                    queryBuilder = new PostgresQueryBuilder();
                    exceptionParser = new PostgreSqlDbExceptionParser(_runtimeConfigProvider);
                    queryExecutor = new PostgreSqlQueryExecutor(_runtimeConfigProvider, exceptionParser, _logger, _contextAccessor, _handler);
                    break;
                case DatabaseType.DWSQL:
                    queryBuilder = new DwSqlQueryBuilder(enableNto1JoinOpt: _runtimeConfigProvider.GetConfig().EnableDwNto1JoinOpt);
                    exceptionParser = new MsSqlDbExceptionParser(_runtimeConfigProvider);
                    queryExecutor = new MsSqlQueryExecutor(_runtimeConfigProvider, exceptionParser, _logger, _contextAccessor, _handler, _oboTokenProvider);
                    break;
                default:
                    throw new NotSupportedException(dataSource.DatabaseTypeNotSupportedMessage);
            }

            return true;
        }

        /// <summary>
        /// Custom fork: rebuilds the logical connection-pool layer (query builder, executor, and
        /// exception parser — including per-data-source connection-string builders) for a single
        /// database type from the current runtime configuration. Used by the scoped hot reload
        /// engine; see <c>Azure.DataApiBuilder.Core.Custom.HotReload</c>.
        /// </summary>
        internal void RebuildQueryManager(DatabaseType databaseType)
        {
            _queryBuilders.Remove(databaseType);
            _queryExecutors.Remove(databaseType);
            _dbExceptionsParsers.Remove(databaseType);

            foreach (DataSource dataSource in _runtimeConfigProvider.GetConfig().ListAllDataSources())
            {
                if (dataSource.DatabaseType != databaseType)
                {
                    continue;
                }

                if (TryCreateQueryComponents(dataSource, out IQueryBuilder? queryBuilder, out IQueryExecutor? queryExecutor, out DbExceptionParser? exceptionParser))
                {
                    _queryBuilders.TryAdd(databaseType, queryBuilder!);
                    _queryExecutors.TryAdd(databaseType, queryExecutor!);
                    _dbExceptionsParsers.TryAdd(databaseType, exceptionParser!);
                }

                return;
            }
        }

        public void OnConfigChanged(object? sender, HotReloadEventArgs args)
        {
            // Custom fork: single-database hot reload. When the engine installed a scoped
            // change set, rebuild only the changed database types and skip the full rebuild.
            if (this.TryApplyScopedQueryManagerRebuild(args))
            {
                return;
            }

            _queryBuilders = new Dictionary<DatabaseType, IQueryBuilder>();
            _queryExecutors = new Dictionary<DatabaseType, IQueryExecutor>();
            _dbExceptionsParsers = new Dictionary<DatabaseType, DbExceptionParser>();
            ConfigureQueryManagerFactory();
        }

        /// <inheritdoc />
        public IQueryBuilder GetQueryBuilder(DatabaseType databaseType)
        {
            if (!_queryBuilders.TryGetValue(databaseType, out IQueryBuilder? queryBuilder))
            {
                throw new DataApiBuilderException(
                    $"{nameof(DatabaseType)}:{databaseType} could not be found within the config",
                    HttpStatusCode.BadRequest,
                    DataApiBuilderException.SubStatusCodes.DataSourceNotFound);
            }

            return queryBuilder;
        }

        /// <inheritdoc />
        public IQueryExecutor GetQueryExecutor(DatabaseType databaseType)
        {
            if (!_queryExecutors.TryGetValue(databaseType, out IQueryExecutor? queryExecutor))
            {
                throw new DataApiBuilderException(
                    $"{nameof(databaseType)}:{databaseType} could not be found within the config",
                    HttpStatusCode.BadRequest,
                    DataApiBuilderException.SubStatusCodes.DataSourceNotFound);
            }

            return queryExecutor;
        }

        /// <inheritdoc />
        public DbExceptionParser GetDbExceptionParser(DatabaseType databaseType)
        {
            if (!_dbExceptionsParsers.TryGetValue(databaseType, out DbExceptionParser? exceptionParser))
            {
                throw new DataApiBuilderException(
                    $"{nameof(databaseType)}:{databaseType} could not be found within the config",
                    HttpStatusCode.BadRequest,
                    DataApiBuilderException.SubStatusCodes.DataSourceNotFound);
            }

            return exceptionParser;
        }

    }
}

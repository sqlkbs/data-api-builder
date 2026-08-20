// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Azure.DataApiBuilder.Auth;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom.HotReload;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Services.Cache;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Azure.DataApiBuilder.Service.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static Azure.DataApiBuilder.Config.DabConfigEvents;

namespace Azure.DataApiBuilder.Core.Resolvers.Factories
{
    /// <summary>
    /// QueryEngineFactory class.
    /// Used to get the appropriate queryEngine based on database type.
    /// </summary>
    public class QueryEngineFactory : IQueryEngineFactory
    {
        // Internally mutated during Hot-Reload
        private Dictionary<DatabaseType, IQueryEngine> _queryEngines;
        private readonly RuntimeConfigProvider _runtimeConfigProvider;
        private readonly IAbstractQueryManagerFactory _queryManagerFactory;
        private readonly IMetadataProviderFactory _metadataProviderFactory;
        private readonly CosmosClientProvider _cosmosClientProvider;
        private readonly IHttpContextAccessor _contextAccessor;
        private readonly IAuthorizationResolver _authorizationResolver;
        private readonly GQLFilterParser _gQLFilterParser;
        private readonly DabCacheService _cache;
        private readonly ILogger<IQueryEngine> _logger;

        /// <inheritdoc/>
        public QueryEngineFactory(RuntimeConfigProvider runtimeConfigProvider,
            IAbstractQueryManagerFactory queryManagerFactory,
            IMetadataProviderFactory metadataProviderFactory,
            CosmosClientProvider cosmosClientProvider,
            IHttpContextAccessor contextAccessor,
            IAuthorizationResolver authorizationResolver,
            GQLFilterParser gQLFilterParser,
            ILogger<IQueryEngine> logger,
            DabCacheService cache,
            HotReloadEventHandler<HotReloadEventArgs>? handler)
        {
            handler?.Subscribe(QUERY_ENGINE_FACTORY_ON_CONFIG_CHANGED, OnConfigChanged);
            _queryEngines = new Dictionary<DatabaseType, IQueryEngine>();
            _runtimeConfigProvider = runtimeConfigProvider;
            _queryManagerFactory = queryManagerFactory;
            _metadataProviderFactory = metadataProviderFactory;
            _cosmosClientProvider = cosmosClientProvider;
            _contextAccessor = contextAccessor;
            _authorizationResolver = authorizationResolver;
            _gQLFilterParser = gQLFilterParser;
            _cache = cache;
            _logger = logger;

            ConfigureQueryEngines();
        }

        public void ConfigureQueryEngines()
        {
            RuntimeConfig config = _runtimeConfigProvider.GetConfig();

            if (config.SqlDataSourceUsed)
            {
                IQueryEngine queryEngine = CreateSqlQueryEngine();
                foreach (DatabaseType sqlDatabaseType in SqlDatabaseTypes)
                {
                    _queryEngines.Add(sqlDatabaseType, queryEngine);
                }
            }

            if (config.CosmosDataSourceUsed)
            {
                _queryEngines.Add(DatabaseType.CosmosDB_NoSQL, CreateCosmosQueryEngine());
            }
        }

        /// <summary>The SQL database types that share the single SQL query engine instance.</summary>
        private static readonly DatabaseType[] SqlDatabaseTypes =
        {
            DatabaseType.MSSQL,
            DatabaseType.MySQL,
            DatabaseType.PostgreSQL,
            DatabaseType.DWSQL
        };

        private IQueryEngine CreateSqlQueryEngine()
        {
            return new SqlQueryEngine(
                _queryManagerFactory,
                _metadataProviderFactory,
                _contextAccessor,
                _authorizationResolver,
                _gQLFilterParser,
                _logger,
                _runtimeConfigProvider,
                _cache);
        }

        private IQueryEngine CreateCosmosQueryEngine()
        {
            return new CosmosQueryEngine(_cosmosClientProvider, _metadataProviderFactory, _authorizationResolver, _gQLFilterParser, _runtimeConfigProvider, _cache);
        }

        /// <summary>
        /// Custom fork: rebuilds the query engine entries for a single database type from the
        /// current runtime configuration. SQL database types share one engine instance, so all
        /// SQL keys are replaced together. Used by the scoped hot reload engine; see
        /// <c>Azure.DataApiBuilder.Core.Custom.HotReload</c>.
        /// </summary>
        internal void RebuildQueryEngine(DatabaseType databaseType)
        {
            RuntimeConfig config = _runtimeConfigProvider.GetConfig();

            if (databaseType == DatabaseType.CosmosDB_NoSQL)
            {
                _queryEngines.Remove(DatabaseType.CosmosDB_NoSQL);
                if (config.CosmosDataSourceUsed)
                {
                    _queryEngines.Add(DatabaseType.CosmosDB_NoSQL, CreateCosmosQueryEngine());
                }

                return;
            }

            foreach (DatabaseType sqlDatabaseType in SqlDatabaseTypes)
            {
                _queryEngines.Remove(sqlDatabaseType);
            }

            if (config.SqlDataSourceUsed)
            {
                IQueryEngine queryEngine = CreateSqlQueryEngine();
                foreach (DatabaseType sqlDatabaseType in SqlDatabaseTypes)
                {
                    _queryEngines.Add(sqlDatabaseType, queryEngine);
                }
            }
        }

        public void OnConfigChanged(object? sender, HotReloadEventArgs args)
        {
            // Custom fork: single-database hot reload. When the engine installed a scoped
            // change set, rebuild only the changed database types and skip the full rebuild.
            if (this.TryApplyScopedQueryEngineRebuild(args))
            {
                return;
            }

            _queryEngines = new Dictionary<DatabaseType, IQueryEngine>();
            ConfigureQueryEngines();
        }

        /// <inheritdoc/>
        public IQueryEngine GetQueryEngine(DatabaseType databaseType)
        {
            if (!_queryEngines.TryGetValue(databaseType, out IQueryEngine? queryEngine))
            {
                throw new DataApiBuilderException(
                    $"{nameof(databaseType)}:{databaseType} could not be found within the config",
                    HttpStatusCode.BadRequest,
                    DataApiBuilderException.SubStatusCodes.DataSourceNotFound);
            }

            return queryEngine;
        }
    }
}

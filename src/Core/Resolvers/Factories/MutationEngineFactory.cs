// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Azure.DataApiBuilder.Auth;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom.HotReload;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Azure.DataApiBuilder.Service.Exceptions;
using Microsoft.AspNetCore.Http;
using static Azure.DataApiBuilder.Config.DabConfigEvents;

namespace Azure.DataApiBuilder.Core.Resolvers.Factories
{
    /// <summary>
    /// MutationEngineFactory class.
    /// Used to get the IMutationEngine based on database type.
    /// </summary>
    public class MutationEngineFactory : IMutationEngineFactory
    {
        private Dictionary<DatabaseType, IMutationEngine> _mutationEngines;
        private readonly RuntimeConfigProvider _runtimeConfigProvider;
        private readonly IAbstractQueryManagerFactory _queryManagerFactory;
        private readonly IMetadataProviderFactory _metadataProviderFactory;
        private readonly CosmosClientProvider _cosmosClientProvider;
        private readonly IQueryEngineFactory _queryEngineFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationResolver _authorizationResolver;
        private readonly GQLFilterParser _gQLFilterParser;

        /// <summary>
        /// Initializes a new instance of the <see cref="MutationEngineFactory"/> class.
        /// </summary>
        /// <param name="runtimeConfigProvider">runtimeConfigProvider.</param>
        /// <param name="queryManagerFactory">queryManagerFactory</param>
        /// <param name="metadataProviderFactory">metadataProviderFactory.</param>
        /// <param name="cosmosClientProvider">cosmosClientProvider</param>
        /// <param name="queryEngineFactory">queryEngineFactory.</param>
        /// <param name="httpContextAccessor">httpContextAccessor.</param>
        /// <param name="authorizationResolver">authorizationResolver.</param>
        /// <param name="gQLFilterParser">GqlFilterParser.</param>
        public MutationEngineFactory(RuntimeConfigProvider runtimeConfigProvider,
            IAbstractQueryManagerFactory queryManagerFactory,
            IMetadataProviderFactory metadataProviderFactory,
            CosmosClientProvider cosmosClientProvider,
            IQueryEngineFactory queryEngineFactory,
            IHttpContextAccessor httpContextAccessor,
            IAuthorizationResolver authorizationResolver,
            GQLFilterParser gQLFilterParser,
            HotReloadEventHandler<HotReloadEventArgs>? handler)

        {
            handler?.Subscribe(MUTATION_ENGINE_FACTORY_ON_CONFIG_CHANGED, OnConfigChanged);
            _cosmosClientProvider = cosmosClientProvider;
            _queryManagerFactory = queryManagerFactory;
            _metadataProviderFactory = metadataProviderFactory;
            _httpContextAccessor = httpContextAccessor;
            _authorizationResolver = authorizationResolver;
            _queryEngineFactory = queryEngineFactory;
            _runtimeConfigProvider = runtimeConfigProvider;
            _gQLFilterParser = gQLFilterParser;
            _mutationEngines = new Dictionary<DatabaseType, IMutationEngine>();
            ConfigureMutationEngines();
        }

        private void ConfigureMutationEngines()
        {
            RuntimeConfig config = _runtimeConfigProvider.GetConfig();

            if (config.SqlDataSourceUsed)
            {
                IMutationEngine mutationEngine = CreateSqlMutationEngine();
                foreach (DatabaseType sqlDatabaseType in SqlDatabaseTypes)
                {
                    _mutationEngines.Add(sqlDatabaseType, mutationEngine);
                }
            }

            if (config.CosmosDataSourceUsed)
            {
                _mutationEngines.Add(DatabaseType.CosmosDB_NoSQL, CreateCosmosMutationEngine());
            }
        }

        /// <summary>The SQL database types that share the single SQL mutation engine instance.</summary>
        private static readonly DatabaseType[] SqlDatabaseTypes =
        {
            DatabaseType.MySQL,
            DatabaseType.MSSQL,
            DatabaseType.PostgreSQL,
            DatabaseType.DWSQL
        };

        private IMutationEngine CreateSqlMutationEngine()
        {
            return new SqlMutationEngine(
                _queryManagerFactory,
                _metadataProviderFactory,
                _queryEngineFactory,
                _authorizationResolver,
                _gQLFilterParser,
                _httpContextAccessor,
                _runtimeConfigProvider);
        }

        private IMutationEngine CreateCosmosMutationEngine()
        {
            return new CosmosMutationEngine(_cosmosClientProvider, _metadataProviderFactory, _authorizationResolver);
        }

        /// <summary>
        /// Custom fork: rebuilds the mutation engine entries for a single database type from the
        /// current runtime configuration. SQL database types share one engine instance, so all
        /// SQL keys are replaced together. Used by the scoped hot reload engine; see
        /// <c>Azure.DataApiBuilder.Core.Custom.HotReload</c>.
        /// </summary>
        internal void RebuildMutationEngine(DatabaseType databaseType)
        {
            RuntimeConfig config = _runtimeConfigProvider.GetConfig();

            if (databaseType == DatabaseType.CosmosDB_NoSQL)
            {
                _mutationEngines.Remove(DatabaseType.CosmosDB_NoSQL);
                if (config.CosmosDataSourceUsed)
                {
                    _mutationEngines.Add(DatabaseType.CosmosDB_NoSQL, CreateCosmosMutationEngine());
                }

                return;
            }

            foreach (DatabaseType sqlDatabaseType in SqlDatabaseTypes)
            {
                _mutationEngines.Remove(sqlDatabaseType);
            }

            if (config.SqlDataSourceUsed)
            {
                IMutationEngine mutationEngine = CreateSqlMutationEngine();
                foreach (DatabaseType sqlDatabaseType in SqlDatabaseTypes)
                {
                    _mutationEngines.Add(sqlDatabaseType, mutationEngine);
                }
            }
        }

        public void OnConfigChanged(object? sender, HotReloadEventArgs args)
        {
            // Custom fork: single-database hot reload. When the engine installed a scoped
            // change set, rebuild only the changed database types and skip the full rebuild.
            if (this.TryApplyScopedMutationEngineRebuild(args))
            {
                return;
            }

            _mutationEngines = new Dictionary<DatabaseType, IMutationEngine>();
            ConfigureMutationEngines();
        }

        /// <inheritdoc/>
        public IMutationEngine GetMutationEngine(DatabaseType databaseType)
        {
            if (!_mutationEngines.TryGetValue(databaseType, out IMutationEngine? mutationEngine))
            {
                throw new DataApiBuilderException(
                    $"{nameof(databaseType)}:{databaseType} could not be found within the config",
                    HttpStatusCode.BadRequest,
                    DataApiBuilderException.SubStatusCodes.DataSourceNotFound);
            }

            return mutationEngine;
        }
    }
}

using Microsoft.EntityFrameworkCore;
using SER.Models;
using System;
using System.Collections.Generic;

namespace SER.Graphql.Reflection.NetCore.Builder
{
    /// <summary>
    /// Provides various settings needed to configure
    /// the Graphql auto reflection integration.
    /// </summary>
    public class SERGraphQlOptions
    {

        /// <summary>
        /// Gets or sets the concrete type of the <see cref="DbContext"/> used by the
        /// Graphql auto reflection stores. If this property is not populated,
        /// an exception is thrown at runtime when trying to use the stores.
        /// </summary>
        public Type DbContextType { get; set; }
        public Type UserType { get; set; }
        public Type RoleType { get; set; }
        public Type UserRoleType { get; set; }
        public string ConnectionString { get; set; }
        public string SecurityKey { get; set; }
        public string SigningKey { get; set; }
        public string TokenIssuer { get; set; }
        public bool EnableCustomFilter { get; set; }
        public string NameCustomFilter { get; set; }
        public string NameClaimCustomFilter { get; set; }
        public bool EnableStatusMutation { get; set; }
        public bool EnableAudit { get; set; }

        /// <summary>
        /// Path to the JSON file listing entity class names and/or table names that GraphQL must never
        /// process (no query, mutation, input, nested field or introspection). Resolved relative to the
        /// content root, like <c>permissions.graphql.json</c>. When set, the file must exist and be
        /// valid or startup fails (fail closed). Default: <c>excluded.graphql.json</c>.
        /// </summary>
        public string ExcludedTypesPath { get; set; } = "excluded.graphql.json";

        /// <summary>
        /// The entity whose primary key <em>is</em> the tenant key (the company row itself). For a
        /// caller scoped to a company, this type is filtered by <c>id == company_id-claim</c> instead of
        /// by a <c>company_id</c> column it does not have. Leave null when there is no such type.
        /// </summary>
        public Type TenantRootType { get; set; }

        /// <summary>
        /// Entity class names that are shared reference data (e.g. cities, taxes, measure units) and may
        /// be read across tenants. For a company-scoped caller, types NOT in this list and with no
        /// resolvable <c>company_id</c> are filtered to empty (fail closed) rather than returned whole.
        /// </summary>
        public IEnumerable<string> GlobalSharedTypeNames { get; set; } = new List<string>();

        /// <summary>
        /// Upper bound on how many rows a single list field may return (the <c>first</c> argument is
        /// clamped to this). Stops one request from pulling a whole table. 0 disables the cap.
        /// Default: 1000.
        /// </summary>
        public int MaxPageSize { get; set; } = 1000;

        public Action<GraphStatusRequest> CallbackStatus { get; set; }
    }
}

using GraphQL.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Newtonsoft.Json.Linq;
using SER.Graphql.Reflection.NetCore.Utilities;
using Microsoft.Extensions.Options;
using SER.Graphql.Reflection.NetCore.Builder;
using System.ComponentModel.DataAnnotations;
using SER.Models;
using SER.Models.SERAudit;
using System.Collections;
using SER.Graphql.Reflection.NetCore.Models;
using Microsoft.AspNetCore.Hosting;

namespace SER.Graphql.Reflection.NetCore.Generic
{
    public interface IDatabaseMetadata
    {
        void ReloadMetadata();
        IEnumerable<TableMetadata> GetTableMetadatas();
    }

    public class DatabaseMetadata<TContext> : IDatabaseMetadata where TContext : DbContext
    {
        private readonly ITableNameLookup _tableNameLookup;
        private readonly IConfiguration _config;
        private IEnumerable<TableMetadata> _tables;
        private readonly IOptionsMonitor<SERGraphQlOptions> _optionsDelegate;
        private readonly IWebHostEnvironment _env;

        // Inherited IdentityUser/IdentityRole members that must never be projected, even without an
        // attribute (the framework declares them, so they cannot carry [GraphQLIgnore]).
        private static readonly HashSet<string> IdentitySecretMembers = new HashSet<string>
        {
            "PasswordHash", "SecurityStamp", "ConcurrencyStamp"
        };

        public DatabaseMetadata(
            ITableNameLookup tableNameLookup,
            IConfiguration config,
            IWebHostEnvironment env,
            IOptionsMonitor<SERGraphQlOptions> optionsDelegate)
        {
            _env = env;
            _config = config;
            _tableNameLookup = tableNameLookup;
            _optionsDelegate = optionsDelegate;
            if (_tables == null || !_tables.Any())
                ReloadMetadata();
        }
        public IEnumerable<TableMetadata> GetTableMetadatas()
        {
            if (_tables == null || !_tables.Any())
            {
                _tables = FetchTableMetaData();
                return _tables;
            }
            return _tables;
        }

        public void ReloadMetadata()
        {
            _tables = FetchTableMetaData();
        }

        private IReadOnlyList<TableMetadata> FetchTableMetaData()
        {
            var metaTables = new List<TableMetadata>();

            string SqlConnectionStr = !string.IsNullOrEmpty(_optionsDelegate.CurrentValue.ConnectionString) ?
                _optionsDelegate.CurrentValue.ConnectionString : !string.IsNullOrEmpty(_config.GetConnectionString("DefaultConnection")) ?
                    _config.GetConnectionString("DefaultConnection") :
                    _config.GetValue<string>($"{_env.EnvironmentName}:ConnectionStrings:DefaultConnection");
            var optionsBuilder = new DbContextOptionsBuilder<TContext>();
            optionsBuilder.UseNpgsql(SqlConnectionStr, o => o.UseNetTopologySuite());
            using DbContext _dbContext = (DbContext)Activator.CreateInstance(typeof(TContext), new object[] { optionsBuilder.Options });
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == _dbContext.GetType().Assembly.GetName().Name);


            var jsonModelTypes = assembly.GetTypes().Where(x => !x.IsAbstract && typeof(JsonBaseModel).IsAssignableFrom(x)).ToList();

            foreach (var entityType in _dbContext.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (Constants.SystemTablesSnakeCase.Contains(tableName))
                {
                    continue;
                }
                var elementType = assembly.GetTypes().Where(x => !x.IsAbstract && typeof(IBaseModel).IsAssignableFrom(x))
                    .FirstOrDefault(x => x == entityType.ClrType);

                if (elementType == null)
                {
                    elementType = assembly.GetTypes().Where(x => !x.IsAbstract).FirstOrDefault(x =>
                        x == entityType.ClrType && (_optionsDelegate.CurrentValue.UserType.Name == entityType.Name.Split(".").Last()
                        || _optionsDelegate.CurrentValue.RoleType.Name == entityType.Name.Split(".").Last()
                        || _optionsDelegate.CurrentValue.UserRoleType.Name == entityType.Name.Split(".").Last()));

                    if (elementType == null) continue;
                    // Console.WriteLine($"tabla evaluada Name {entityType.Name.Split(".").Last()} elementType {elementType}");
                }

                if (elementType.IsDefined(typeof(GraphQLIgnoreAttribute), inherit: false))
                    continue;

                var namePk = entityType.FindPrimaryKey()?.Properties
                     .Select(x => x.Name).FirstOrDefault();
                if (namePk == null) continue;
                // Type elementType = Type.GetType(entityType.Name);
                //Console.WriteLine($"tabla evaluada Name {entityType.Name} elementType {elementType} {entityType.ClrType} ");

                metaTables.Add(new TableMetadata
                {
                    TableName = tableName,
                    AssemblyFullName = entityType.ClrType.FullName,
                    Columns = GetColumnsMetadata(entityType, elementType),
                    Type = elementType ?? entityType.ClrType,
                    NamePK = namePk
                });
                _tableNameLookup.InsertKeyName(elementType.Name.ToSnakeCase());

            }

            foreach (var entityType in jsonModelTypes)
            {
                var tableName = entityType.Name;

                if (entityType.IsDefined(typeof(GraphQLIgnoreAttribute), inherit: false))
                    continue;

                metaTables.Add(new TableMetadata
                {
                    TableName = tableName,
                    AssemblyFullName = entityType.FullName,
                    Columns = GetColumnsMetadata(null, entityType),
                    Type = entityType,
                    NamePK = null
                });
                _tableNameLookup.InsertKeyName(entityType.Name.ToSnakeCase());
            }

            if (_optionsDelegate.CurrentValue.EnableAudit)
            {
                metaTables.Add(new TableMetadata
                {
                    TableName = nameof(Audit),
                    AssemblyFullName = typeof(Audit).FullName,
                    Columns = GetColumnsMetadata(null, typeof(Audit)),
                    Type = typeof(Audit),
                    NamePK = nameof(Audit.id)
                });
                _tableNameLookup.InsertKeyName(typeof(Audit).Name.ToSnakeCase());
            }

            return metaTables;
        }

        private IReadOnlyList<ColumnMetadata> GetColumnsMetadata(IEntityType entityType, Type type)
        {
            var tableColumns = new List<ColumnMetadata>();

            if (type != null)
            {
                foreach (var propertyType in type.GetProperties())
                {
                    var field = propertyType.PropertyType;
                    if (field.IsGenericType && field.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        field = field.GetGenericArguments()[0];
                    }
                    //Console.WriteLine($"Type: {propertyType.GetType()} type3: {propertyType.Name} {field?.Name}");
                    var isList = !propertyType.PropertyType.IsArray && typeof(ICollection).IsAssignableFrom(propertyType.PropertyType); // propertyType.PropertyType.Name.Contains("List");

                    if (isList)
                        field = propertyType.PropertyType.GetGenericArguments().Count() > 0 ? propertyType.PropertyType.GetGenericArguments()[0] : propertyType.PropertyType;

                    var isJson = propertyType.GetCustomAttributes(true)
                        .Where(x => x.GetType() == typeof(ColumnAttribute) && ((ColumnAttribute)x).TypeName == "jsonb")
                        .FirstOrDefault();

                    if (propertyType.GetCustomAttributes(true)
                           .Any(x => x.GetType() == typeof(NotMappedAttribute))) continue;

                    // Never project a column marked [GraphQLIgnore], an inherited Identity secret, or a
                    // navigation to an excluded type. Dropping it here removes it from the output field,
                    // the input field, the filter arguments and introspection in one place.
                    if (propertyType.GetCustomAttributes(true).Any(x => x is GraphQLIgnoreAttribute))
                        continue;
                    if (IdentitySecretMembers.Contains(propertyType.Name)
                        && (typeof(Microsoft.AspNetCore.Identity.IdentityUser).IsAssignableFrom(type)
                            || typeof(Microsoft.AspNetCore.Identity.IdentityRole).IsAssignableFrom(type)))
                        continue;
                    if (field != null && field.IsDefined(typeof(GraphQLIgnoreAttribute), inherit: false))
                        continue;

                    tableColumns.Add(new ColumnMetadata
                    {
                        ColumnName = propertyType.Name,
                        DataType = propertyType.Name == "id" ? "uniqueidentifier" : field.Name,
                        IsNull = field != null,
                        Type = field ?? propertyType.GetType(),
                        IsList = isList,
                        IsJson = isJson != null
                    });
                }
            }
            else
            {
                var tableIdentifier = StoreObjectIdentifier.Table(entityType.GetTableName().ToSnakeCase(), entityType.GetSchema());

                foreach (var propertyType in entityType.GetProperties())
                {
                    var columnMetadata = new ColumnMetadata
                    {
                        ColumnName = propertyType.GetColumnName(tableIdentifier),
                        DataType = propertyType.GetRelationalTypeMapping().ClrType.Name,
                        IsNull = false,
                        Type = propertyType.GetRelationalTypeMapping().ClrType,
                        IsList = false,
                        IsJson = false
                    };
                    tableColumns.Add(columnMetadata);
                    //Console.WriteLine($"columnMetadata info {JObject.FromObject(columnMetadata)}");
                }
            }
            return tableColumns;
        }
    }
}

﻿using GraphQL.Types;
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

        public DatabaseMetadata(
            ITableNameLookup tableNameLookup,
            IConfiguration config,
            IOptionsMonitor<SERGraphQlOptions> optionsDelegate)
        {
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

            string SqlConnectionStr = _optionsDelegate.CurrentValue.ConnectionString;
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

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Linq.Dynamic.Core;
using GraphQL.Types;
using GraphQL.DataLoader;
using GraphQL;
using Microsoft.EntityFrameworkCore.DynamicLinq;
using System.ComponentModel.DataAnnotations;
using SER.Graphql.Reflection.NetCore.Utilities;
using SER.Graphql.Reflection.NetCore.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.Extensions.Options;
using SER.Graphql.Reflection.NetCore.Builder;
using SER.Models;
using SER.Models.SERAudit;
using System.Collections;
using System.Text.Json;
using System.Buffers;
using SER.Graphql.Reflection.NetCore.Models;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using SER.Graphql.Reflection.NetCore.WebSocket;
using Microsoft.AspNetCore.Hosting;
using GraphQLParser.AST;
using GraphQLParser;
using SER.Graphql.Reflection.NetCore.Parser;

namespace SER.Graphql.Reflection.NetCore
{
    public class BasicGenericGraphRepository<T, TContext> : IGraphRepository<T>
            where T : class
            where TContext : DbContext
    {
        //private readonly TContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly FillDataExtensions _fillDataExtensions;
        private readonly IWebHostEnvironment _env;

        private IConfiguration _config;
        private IMemoryCache _cache;
        private readonly ILogger _logger;
        public string model;
        public string nameModel;
        private readonly AuditManager _cRepositoryLog;
        private readonly IHandleMsg<T> _handleMsg;
        public static readonly ILoggerFactory MyLoggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddFilter((category, level) => category == DbLoggerCategory.Database.Command.Name
                        && level == LogLevel.Information)
                .AddConsole();
        });
        private readonly IOptionsMonitor<SERGraphQlOptions> _optionsDelegate;

        public BasicGenericGraphRepository(
            IHttpContextAccessor httpContextAccessor,
            FillDataExtensions fillDataExtensions,
            IWebHostEnvironment env,
            IConfiguration config,
            IOptionsMonitor<SERGraphQlOptions> optionsDelegate)
        {
            //_context = db;
            _config = config;
            _httpContextAccessor = httpContextAccessor;
            model = typeof(T).Name;
            nameModel = typeof(T).Name.ToSnakeCase().ToLower();
            _cache = _httpContextAccessor.HttpContext.RequestServices.GetService<IMemoryCache>();
            _handleMsg = _httpContextAccessor.HttpContext.RequestServices.GetService<IHandleMsg<T>>();
            _logger = _httpContextAccessor.HttpContext.RequestServices.GetService<ILogger<BasicGenericGraphRepository<T, TContext>>>();
            _fillDataExtensions = fillDataExtensions;
            _optionsDelegate = optionsDelegate;
            _cRepositoryLog = httpContextAccessor.HttpContext.RequestServices.GetService<AuditManager>();
            _env = env;
        }

        public string GetCompanyIdUser()
        {
            return _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(x =>
                x.Type == _optionsDelegate.CurrentValue.NameClaimCustomFilter)?.Value;
        }

        public string GetCurrentUser()
        {
            return _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(x =>
                x.Type == Claims.Subject)?.Value;
        }

        public string GetCurrenUserName()
        {
            return _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(x => x.Type == Claims.Name)?.Value;
        }

        public List<string> GetRolesUser()
        {
            return _httpContextAccessor.HttpContext.User.Claims.Where(x =>
                x.Type == Claims.Role).Select(x => x.Value).ToList();
        }

        public async Task<T> GetFirstAsync(IResolveFieldContext context, string alias, List<string> includeExpressions = null,
            string whereArgs = "", Dictionary<string, object> customfilters = null, params object[] args)
        {
            var entity = await GetQuery(context, alias, includeExpressions: includeExpressions,
               first: 1, whereArgs: whereArgs, customfilters: customfilters, args: args)
               .AsNoTracking().FirstOrDefaultAsync();
            if (entity == null) return null;
            return entity;
        }

        public async Task<IEnumerable<T>> GetAllAsync(IResolveFieldContext context, string alias, List<string> includeExpressions = null,
            string orderBy = "", string whereArgs = "", int? take = null, int? offset = null, Dictionary<string, object> customfilters = null, params object[] args)
        {
            return await GetQuery(context, alias, includeExpressions: includeExpressions, orderBy: orderBy,
                first: take, offset: offset, whereArgs: whereArgs, customfilters: customfilters, args: args)
                .AsNoTracking().ToListAsync();
        }

        public IQueryable<T> GetQuery(IResolveFieldContext context, string alias, List<string> includeExpressions = null,
            string orderBy = "", string whereArgs = "", int? first = null, int? offset = null, Dictionary<string, object> customfilters = null,
            params object[] args)
        {
            var scope = context.RequestServices.CreateScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<TContext>();

            IQueryable<T> query = GetModel(dbContext);

            if (includeExpressions != null && includeExpressions.Count > 0)
            {
                foreach (var include in includeExpressions)
                    query = query.Include(include);
            }
            if (!string.IsNullOrEmpty(whereArgs) && args.Length > 0)
                query = query.Where(whereArgs, args);

            if (_optionsDelegate.CurrentValue.EnableCustomFilter && GetRolesUser().Any(x => x != "Super-User"))
                query = FilterQueryByCustomFilter(query, out _);

            if (customfilters != null)
                query = FilterWithCustomParams(query, customfilters);

            if (!string.IsNullOrEmpty(orderBy))
                query = query.OrderBy(orderBy);

            if (offset != null && first != null)
            {
                var result = new PagedResultBase();

                result.current_page = offset.Value;
                result.page_size = first.Value;
                var allowCache = args.Length == 0;

                int? rowCount = null;
                if (allowCache)
                    rowCount = CacheGetOrCreate(query);

                result.row_count = rowCount ?? query.CountAsync().Result;

                var pageCount = (double)result.row_count / first.Value;
                result.page_count = (int)Math.Ceiling(pageCount);

                _fillDataExtensions.Add(alias, result);
                query = query.Skip((offset.Value - 1) * first.Value);
            }
            if (first != null)
            {
                query = query.Take(first.Value);
            }

            return query;
        }

        private IQueryable<T> FilterWithCustomParams(IQueryable<T> query, Dictionary<string, object> customfilters)
        {
            var index = 0;
            foreach (var filter in customfilters)
            {
                if (filter.Value.GetType().IsArray)
                {
                    var expToFilter = $"@{index}.Contains(string(object({filter.Key})))";
                    query = query.Where(expToFilter, filter.Value);
                }
                else
                    query = query.Where($"{filter.Key} = @{index}", filter.Value);
                index++;
            }
            return query;
        }

        public int GetCountQuery(IResolveFieldContext context, List<string> includeExpressions = null,
           string whereArgs = "", Dictionary<string, object> customfilters = null, params object[] args)
        {
            var scope = context.RequestServices.CreateScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<TContext>();

            IQueryable<T> query = GetModel(dbContext);

            if (includeExpressions != null && includeExpressions.Count > 0)
            {
                foreach (var include in includeExpressions)
                    query = query.Include(include);
            }
            if (!string.IsNullOrEmpty(whereArgs) && args.Length > 0)
                query = query.Where(whereArgs, args);

            if (customfilters != null)
                query = FilterWithCustomParams(query, customfilters);

            if (_optionsDelegate.CurrentValue.EnableCustomFilter && GetRolesUser().Any(x => x != "Super-User"))
                query = FilterQueryByCustomFilter(query, out _);
            return query.Count();
        }

        public SumObjectResponse<T> GetSumQuery(IResolveFieldContext context, string param, List<string> includeExpressions = null,
           string whereArgs = "", Dictionary<string, object> customfilters = null, params object[] args)
        {
            var scope = context.RequestServices.CreateScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<TContext>();

            IQueryable<T> query = dbContext.Set<T>();

            if (includeExpressions != null && includeExpressions.Count > 0)
            {
                foreach (var include in includeExpressions)
                    query = query.Include(include);
            }
            if (!string.IsNullOrEmpty(whereArgs) && args.Length > 0)
                query = query.Where(whereArgs, args);

            if (customfilters != null)
                query = FilterWithCustomParams(query, customfilters);

            if (_optionsDelegate.CurrentValue.EnableCustomFilter && GetRolesUser().Any(x => x != "Super-User"))
                query = FilterQueryByCustomFilter(query, out _);

            return new SumObjectResponse<T>
            {
                response_sum = query.Sum(param)
            };
        }

        private IQueryable<T> FilterQueryByCustomFilter(IQueryable<T> query, out bool find, Type parentType = null, string columnName = "")
        {
            string nameField = _optionsDelegate.CurrentValue.NameCustomFilter;
            find = false;
            string companyId = null;
            var types = new Dictionary<string, Type>();
            var typeToEvaluate = typeof(T);
            if (parentType != null) typeToEvaluate = parentType;

            //Console.WriteLine($" *************** nameField {nameField} Name {_httpContextAccessor.HttpContext.User.Identity.Name} IsAuthenticated {_httpContextAccessor.HttpContext.User.Identity.IsAuthenticated}" +
            //    $" GetCompanyIdUser() { GetCompanyIdUser()}");
            foreach (var propertyInfo in typeToEvaluate.GetProperties())
            {
                if (propertyInfo.GetCustomAttributes(true).Any(x => x.GetType() == typeof(NotMappedAttribute))
                   || propertyInfo.GetCustomAttributes(true).Where(x => x.GetType() == typeof(ColumnAttribute)).Any(attr => ((ColumnAttribute)attr).TypeName == "geography"
                       || ((ColumnAttribute)attr).TypeName == "jsonb")
                    || (propertyInfo.PropertyType.IsArray)
                    || (propertyInfo.PropertyType.IsGenericType && propertyInfo.PropertyType.GetGenericTypeDefinition() == typeof(IList<>))
                    || (propertyInfo.PropertyType.IsGenericType && propertyInfo.PropertyType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    || (propertyInfo.PropertyType.IsGenericType && propertyInfo.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>))
                    || (typeof(ICollection).IsAssignableFrom(propertyInfo.PropertyType))
                   )
                {
                    continue;
                }
                var field = propertyInfo.PropertyType;
                if (field.IsGenericType && field.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    field = field.GetGenericArguments()[0];
                }
                var childType = field ?? propertyInfo.GetType();
                var attrName = "";
                foreach (ForeignKeyAttribute attr in propertyInfo.GetCustomAttributes(true).Where(x => x.GetType() == typeof(ForeignKeyAttribute)))
                {
                    attrName = attr.Name;
                }

                if (attrName != "" && typeof(IBaseModel).IsAssignableFrom(childType))
                {
                    types.Add(propertyInfo.Name, childType);
                }
                //Console.WriteLine($"---------------Name: {propertyInfo.Name.ToSnakeCase()} nameField {nameField} find {find}  childType {childType} ");
                if (propertyInfo.Name.ToSnakeCase() == nameField)
                {
                    find = true;
                    if (_httpContextAccessor.HttpContext.User.Identity.IsAuthenticated && !string.IsNullOrEmpty(_httpContextAccessor.HttpContext.User.Identity.Name))
                        companyId = GetCompanyIdUser();
                    else
                        companyId = _httpContextAccessor.HttpContext.Session?.GetInt32(nameField)?.ToString();

                    // if (typeof(TUser) == typeof(T) || typeToEvaluate == typeof(TUser)) query = query.Where($"{columnName}{propertyInfo.Name}  = @0 ", companyId);
                    query = query.Where($"{columnName}{propertyInfo.Name}  = @0 ", companyId);
                    break;
                }
            }

            if (!find)
            {
                foreach (var dict in types.OrderByDescending(x => x.Key))
                {
                    //Console.WriteLine($"---------------dict: {dict.Key}");
                    query = FilterQueryByCustomFilter(query, out bool finded, dict.Value, $"{dict.Key}.");
                    if (finded) break;
                }
            }

            return query;
        }

        public async Task<ILookup<Tkey, T>> GetItemsByIds<Tkey>(IEnumerable<Tkey> ids, IResolveFieldContext context, string param,
            bool isString = false)
        // where Tkey : struct
        {
            var whereArgs = new StringBuilder();
            var args = new List<object>();
            var orderBy = context.GetArgument<string>("orderBy");
            var take = context.GetArgument<int?>("first");

            string SqlConnectionStr = !string.IsNullOrEmpty(_optionsDelegate.CurrentValue.ConnectionString) ?
                _optionsDelegate.CurrentValue.ConnectionString : !string.IsNullOrEmpty(_config.GetConnectionString("DefaultConnection")) ?
                    _config.GetConnectionString("DefaultConnection") :
                    _config.GetValue<string>($"{_env.EnvironmentName}:ConnectionStrings:DefaultConnection");
            var optionsBuilder = new DbContextOptionsBuilder<TContext>();
            optionsBuilder.UseNpgsql(SqlConnectionStr, o => o.UseNetTopologySuite());
            optionsBuilder.EnableSensitiveDataLogging();
            optionsBuilder.UseLoggerFactory(MyLoggerFactory);

            using DbContext _dbContext = (DbContext)Activator.CreateInstance(typeof(TContext), new object[] { optionsBuilder.Options });
            // using var _db = new DbContext(optionsBuilder.Options);
            IQueryable<T> query = _dbContext.Set<T>();

            List<string> includeExpressions = new();
            GraphUtils.DetectChild<FakeUser, FakeRole, FakeUserRole>(context.FieldAst.SelectionSet.Selections.Where(x => x is GraphQLField)
                        .Select(x => x as GraphQLField).ToList(), includeExpressions,
                   ((dynamic)context.FieldDefinition.ResolvedType).ResolvedType, args, whereArgs,
                   arguments: context.Arguments, mainType: typeof(T));

            if (whereArgs.Length > 0)
                whereArgs.Append(" and ");

            if (isString) whereArgs.Append($"@{args.Count}.Contains(string(object({param})))");
            else
                whereArgs.Append($"@{args.Count}.Contains(int({param}))");


            if (ids.Any() && ids.First().GetType() == typeof(Guid))
            {
                args.Add(ids.Select(x => x.ToString()));
            }
            else
            {
                args.Add(ids);
            }


            if (includeExpressions != null && includeExpressions.Count > 0)
            {
                foreach (var include in includeExpressions)
                    query = query.Include(include);
            }

            _logger.LogWarning($"whereArgs: {whereArgs}");
            query = query.Where(whereArgs.ToString(), args.ToArray());

            if (_optionsDelegate.CurrentValue.EnableCustomFilter && GetRolesUser().Any(x => x != "Super-User"))
                query = FilterQueryByCustomFilter(query, out _);

            if (!string.IsNullOrEmpty(orderBy))
                query = query.OrderBy(orderBy);

            if (take != null)
                query = query.Take(take.Value);

            await query.AsNoTracking().ToListAsync();
            var items = await query.AsNoTracking().ToListAsync();
            var pi = typeof(T).GetProperty(param);
            //if (typeof(Tkey) == typeof(int))
            return items.ToLookup(x => (Tkey)pi.GetValue(x, null));
        }


        public async Task<T> GetByIdAsync(IResolveFieldContext context, string alias, int id, List<string> includeExpressions = null,
          string whereArgs = "", Dictionary<string, object> customfilters = null, params object[] args)
        {
            if (id == 0) return null;
            var entity = await GetQuery(context, alias, includeExpressions: includeExpressions,
                first: 1, whereArgs: whereArgs, customfilters: customfilters, args: args)
                .AsNoTracking().FirstOrDefaultAsync();

            //var entity = await GetModel.FindAsync(id);
            if (entity == null) return null;
            return entity;
        }

        public async Task<T> GetByIdAsync(IResolveFieldContext context, string alias, string id, List<string> includeExpressions = null,
         string whereArgs = "", Dictionary<string, object> customfilters = null, params object[] args)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var entity = await GetQuery(context, alias, includeExpressions: includeExpressions,
                first: 1, whereArgs: whereArgs, customfilters: customfilters, args: args)
                .AsNoTracking().FirstOrDefaultAsync();
            if (entity == null) return null;
            return entity;
        }

        public async Task<T> GetByIdAsync(IResolveFieldContext context, string alias, Guid? id, List<string> includeExpressions = null,
         string whereArgs = "", Dictionary<string, object> customfilters = null, params object[] args)
        {
            if (id == null) return null;
            var entity = await GetQuery(context, alias, includeExpressions: includeExpressions,
                first: 1, whereArgs: whereArgs, customfilters: customfilters, args: args)
                .AsNoTracking().FirstOrDefaultAsync();
            if (entity == null) return null;
            return entity;
        }

        private DbSet<T> GetModel(TContext dbContext) => dbContext.Set<T>();

        /// <summary>
        /// crea un instanica tipo T en la base de datos
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="alias"></param>
        /// <param name="sendObjFirebase"></param>
        /// <param name="includeExpressions"></param>
        /// <returns></returns>
        public async Task<T> Create(IResolveFieldContext context, T entity, string alias = "", bool sendObjFirebase = true, List<string> includeExpressions = null)
        {
            //var objstr = JsonSerializer.Serialize(entity);
            //_logger.LogInformation($"----------------------------objstr {objstr}");

            var scope = context.RequestServices.CreateScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<TContext>();

            var cacheKeySize = string.Format("_{0}_size", model);
            _cache.Remove(cacheKeySize);
            nameModel = $"create_{nameModel}";
            try
            {
                entity.GetType().GetProperty("created_by_id")?.SetValue(entity, GetCurrentUser(), null);

                var obj = dbContext.Add(entity);
                dbContext.SaveChanges();

                if (_optionsDelegate.CurrentValue.EnableAudit)
                {
                    await _cRepositoryLog.AddLog(dbContext, new AuditBinding()
                    {
                        action = AudiState.CREATE,
                        objeto = typeof(T).Name,
                    }, id: GetKey(dbContext, entity), commit: true);
                }

                includeExpressions?.ForEach(x => dbContext.Entry(obj.Entity).Reference(x).Load());

                if (sendObjFirebase) SendStatus(GraphGrpcStatus.CREATE, GetKey(dbContext, entity));
                if (!string.IsNullOrEmpty(GetCurrentUser()))
                    _handleMsg.GetStream().OnNext(entity);

                return obj.Entity;
            }
            catch (ValidationException exc)
            {
                _logger.LogError(exc, $"{nameof(Create)} validation exception: {exc?.Message}");
                _fillDataExtensions.Add($"{(string.IsNullOrEmpty(alias) ? nameModel : alias)}", $"{exc?.Message}");
                dbContext.Entry(entity).State = EntityState.Detached;

            }
            catch (DbUpdateException e)
            {
                _logger.LogError(e, $"{nameof(Create)} db update error: {e?.InnerException?.Message}");
                _fillDataExtensions.Add($"{(string.IsNullOrEmpty(alias) ? nameModel : alias)}", $"{e.InnerException?.Message}");
                dbContext.Entry(entity).State = EntityState.Detached;
            }
            return entity;
        }

        /// <summary>
        /// creacion de la entidad a traves de un diccionario
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="alias"></param>
        /// <param name="sendObjFirebase"></param>
        /// <param name="includeExpressions"></param>
        /// <returns></returns>
        public async Task<T> Create(IResolveFieldContext context, Dictionary<string, object> dict, string alias = "", bool sendObjFirebase = true, List<string> includeExpressions = null)
        {
            dynamic entity = Activator.CreateInstance(typeof(T));
            foreach (var values in dict)
            {
                var propertyInfo = typeof(T).GetProperty(values.Key);
                if (propertyInfo.Name == "id") continue;
                propertyInfo.SetValue(entity, values.Value.GetRealValue(), null);
            }
            return await Create(context, entity, alias: alias, sendObjFirebase: sendObjFirebase, includeExpressions: includeExpressions);


        }

        /// <summary>
        /// obtiene el valor del primary key cuando se crea un objeto
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public virtual string GetKey(TContext dbContext, T entity)
        {
            var keyName = dbContext.Model.FindEntityType(typeof(T)).FindPrimaryKey()?.Properties
                .Select(x => x.Name).FirstOrDefault(); // .Single();
            return entity.GetType().GetProperty(keyName).GetValue(entity, null).ToString();
        }

        public Task<T> Update(IResolveFieldContext context, object id, T entity, string alias = "", bool sendObjFirebase = true, List<string> includeExpressions = null)
        {
            throw new NotImplementedException();
        }

        public async Task<T> Update(IResolveFieldContext context, object id, Dictionary<string, object> entity, Dictionary<string, object> dict, string alias = "", bool sendObjFirebase = true, List<string> includeExpressions = null)
        {
            var obj = entity.DictToObject<T>();

            return await Update(context, id, obj, dict, alias: alias, sendObjFirebase: sendObjFirebase, includeExpressions: includeExpressions);
        }

        /// <summary>
        /// actualizacion de una entidad
        /// </summary>
        /// <param name="id"></param>
        /// <param name="entity"></param>
        /// <param name="dict"></param>
        /// <param name="alias"></param>
        /// <param name="sendObjFirebase"></param>
        /// <param name="includeExpressions"></param>
        /// <returns></returns>
        public async Task<T> Update(IResolveFieldContext context, object id, T entity, Dictionary<string, object> dict, string alias = "", bool sendObjFirebase = true, List<string> includeExpressions = null)
        {
            //var obj = GetModel.Find(id);
            var scope = context.RequestServices.CreateScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<TContext>();

            T obj = null;
            if (id is string)
            {
                if (Guid.TryParse(id.ToString(), out Guid @guid))
                {
                    var keyName = dbContext.Model.FindEntityType(typeof(T)).FindPrimaryKey()?.Properties
                        .Select(x => x.Name).FirstOrDefault();
                    var pi = typeof(T).GetProperty(keyName);
                    var expToEvaluate = EqualPredicate<T>(typeof(T), keyName, @guid, pi.PropertyType);
                    obj = GetModel(dbContext).FirstOrDefault(expToEvaluate);
                }
                else
                {
                    obj = GetModel(dbContext).Find(id);
                }
            }
            else
            {
                obj = GetModel(dbContext).Find(id);
            }

            if (obj != null)
            {
                nameModel = $"update_{nameModel}";
                try
                {
                    foreach (var values in dict)
                    {
                        try
                        {
                            //Console.WriteLine($"___________ key: {values.Key} values {values.Value} {values.Value.GetType()}");
                            var propertyInfo = typeof(T).GetProperty(values.Key);
                            if (propertyInfo.Name == "id") continue;

                            var oldValue = propertyInfo.GetValue(obj);
                            dynamic newValue = propertyInfo.GetValue(entity);

                            // if (newValue == null && oldValue != null) continue;
                            // if (newValue == oldValue) continue;
                            Type type = null;
                            var isList = !propertyInfo.PropertyType.IsArray && typeof(ICollection).IsAssignableFrom(propertyInfo.PropertyType);
                            if (isList)
                                type = propertyInfo.PropertyType.GetGenericArguments().Count() > 0 ?
                                    propertyInfo.PropertyType.GetGenericArguments()[0] : propertyInfo.PropertyType;

                            if (isList && type.BaseType == typeof(object) && newValue != null)
                                DeleteRelationsM2M(dbContext, type, id);

                            //Console.WriteLine($"___________TRACEEEEEEEEEEEEEEEEE____________: key: {propertyInfo.Name} {oldValue} {newValue} isList {isList} BaseType {type.BaseType}");
                            if (isList)
                                if (type.BaseType == typeof(object) && newValue != null) propertyInfo.SetValue(obj, newValue, null);
                                else UpdateList(dbContext, type, id, values.Value, newValue);
                            else
                                propertyInfo.SetValue(obj, newValue, null);

                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e.Message);
                        }
                    }

                    if (_optionsDelegate.CurrentValue.EnableAudit)
                    {
                        var modified = await _cRepositoryLog.AddLog(dbContext, new AuditBinding()
                        {
                            action = AudiState.UPDATE,
                            objeto = typeof(T).Name,
                        }, id: id.ToString());
                        var propertyInfo = typeof(T).GetProperties().FirstOrDefault(x => x.Name == "last_movement");
                        if (propertyInfo != null)
                            obj.GetType().GetProperty(propertyInfo.Name)?.SetValue(obj, modified, null);

                        obj.GetType().GetProperty("update_date")?.SetValue(obj, DateTime.UtcNow, null);
                        obj.GetType().GetProperty("updated_by_id")?.SetValue(obj, GetCurrentUser(), null);
                    }

                    //_context.Entry(obj).State = EntityState.Modified;
                    await dbContext.SaveChangesAsync();
                    includeExpressions?.ForEach(x => dbContext.Entry(obj).Reference(x).Load());

                    if (sendObjFirebase) SendStatus(GraphGrpcStatus.UPDATE, id.ToString());
                    if (!string.IsNullOrEmpty(GetCurrentUser()))
                        _handleMsg.GetStream().OnNext(entity);

                }
                catch (ValidationException exc)
                {
                    _logger.LogError(exc, $"{nameof(Update)} validation exception: {exc?.Message}");
                    _fillDataExtensions.Add($"{(string.IsNullOrEmpty(alias) ? nameModel : alias)}", @$"{id} => {exc?.Message}");
                    dbContext.Entry(obj).State = EntityState.Detached;

                }
                catch (DbUpdateException e)
                {
                    dbContext.Entry(obj).State = EntityState.Detached;
                    _fillDataExtensions.Add($"{(string.IsNullOrEmpty(alias) ? nameModel : alias)}", $"{id} => {e.InnerException?.Message}");
                }
            }
            return obj;
        }

        private void UpdateList(TContext dbContext, Type type, object parentId, dynamic values, dynamic entities)
        {
            try
            {
                GetType()
                    .GetMethod("UpdateListAsync")
                    .MakeGenericMethod(type)
                    .Invoke(this, parameters: new object[] { dbContext, parentId, values, entities });
            }
            catch (Exception e)
            {
                _logger.LogError($"ERROR {e} type {type}");
            }
        }

        public void UpdateListAsync<M>(TContext dbContext, object parentId, ICollection<object> values, List<M> entities) where M : class
        {
            var iQueryable = dbContext.Set<M>();
            var keyProperty = typeof(M).GetProperties();
            var keyName = dbContext.Model.FindEntityType(typeof(M)).FindPrimaryKey()?.Properties
                        .Select(x => x.Name).FirstOrDefault();
            var paramFK = "";

            Type valueType = null;

            foreach (var prop in typeof(M).GetProperties())
            {
                var field = prop.PropertyType;
                if (field.IsGenericType && field.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    field = field.GetGenericArguments()[0];
                }

                if (field == typeof(T))
                {
                    paramFK = prop.GetCustomAttributes(true)
                        .Where(x => x.GetType() == typeof(ForeignKeyAttribute))
                        .Select(attr => ((ForeignKeyAttribute)attr).Name)
                        .FirstOrDefault();
                    valueType = field;
                    break;
                }
            }


            if (!string.IsNullOrEmpty(paramFK))
            {
                valueType = typeof(M).GetProperty(paramFK).PropertyType;
                Expression<Func<M, bool>> expToEvaluate = null;
                if (Guid.TryParse(parentId.ToString(), out Guid @guid))
                {
                    expToEvaluate = EqualPredicate<M>(typeof(M), paramFK, @guid, valueType);
                }
                else
                {
                    expToEvaluate = EqualPredicate<M>(typeof(M), paramFK, parentId, valueType);
                }
                var dataDb = iQueryable.Where(expToEvaluate).ToList();

                var stringJson = JsonSerializer.Serialize(values);
                var jsonElement = ToJsonDocument(stringJson);

                var array = jsonElement.EnumerateArray();

                while (array.MoveNext())
                {
                    var objectElement = array.Current;
                    var props = objectElement.EnumerateObject();

                    if (!props.Select(x => x.Name).Contains(keyName))
                    {
                        iQueryable.Add(JsonSerializer.Deserialize<M>(objectElement.ToString()));
                        continue;
                    }
                    var propId = props.FirstOrDefault(x => x.Name == keyName);

                    //Console.WriteLine($"  ***************** propId {propId} {propId.Equals(null)} ");

                    bool isEqual(M a) => propId.Value.GetInt32() == int.Parse(keyProperty.First(x => x.Name == keyName).GetValue(a).ToString());
                    var obj = dataDb.FirstOrDefault(isEqual);
                    var entity = entities.FirstOrDefault(isEqual);

                    if (obj == null)
                    {
                        iQueryable.Add(entity);
                        continue;
                    }

                    while (props.MoveNext())
                    {
                        var pair = props.Current;
                        string propertyName = pair.Name;
                        JsonElement propertyValue = pair.Value;
                        if (propertyName == "id") continue;

                        var propertyInfo = keyProperty.FirstOrDefault(x => x.Name == propertyName);
                        var oldValue = propertyInfo.GetValue(obj);
                        var newValue = propertyInfo.GetValue(entity);
                        //Console.WriteLine($"  ***************** oldValue {oldValue} newValue {newValue} PropertyType {propertyInfo.PropertyType} propertyValue {propertyValue} ");
                        //dynamic value = typeof(JsonExtensions)
                        //    .GetMethod("ElementToObject")
                        //    .MakeGenericMethod(propertyInfo.PropertyType)
                        //    .Invoke(null, parameters: new object[] { propertyValue });

                        propertyInfo.SetValue(obj, newValue, null);

                    }
                }
                // _context.SaveChanges();
            }
        }


        private JsonElement ToJsonDocument(string response)
        {
            var documentOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip
            };
            return JsonDocument.Parse(response, documentOptions).RootElement;
        }

        private void DeleteRelationsM2M(TContext dbContext, Type type, object parentId)
        {
            try
            {
                GetType()
                    .GetMethod("DeleteRelations")
                    .MakeGenericMethod(type)
                    .Invoke(this, parameters: new object[] { dbContext, parentId });
            }
            catch (Exception e)
            {
                _logger.LogError($"ERROR {e} type {nameof(type)} parentId {parentId}");
            }
        }

        public void DeleteRelations<M>(TContext dbContext, object parentId) where M : class
        {
            var iQueryable = dbContext.Set<M>();
            var keyProperty = typeof(M).GetProperties();
            var paramFK = "";
            Type valueType = null;

            foreach (var prop in typeof(M).GetProperties())
            {
                var field = prop.PropertyType;
                if (field.IsGenericType && field.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    field = field.GetGenericArguments()[0];
                }

                if (field == typeof(T))
                {
                    paramFK = prop.GetCustomAttributes(true)
                        .Where(x => x.GetType() == typeof(ForeignKeyAttribute))
                        .Select(attr => ((ForeignKeyAttribute)attr).Name)
                        .FirstOrDefault();
                    valueType = field;
                    break;
                }
            }

            //var paramToEvaluate = typeof(M).GetProperties().FirstOrDefault(x => x.PropertyType.Name == paramFK).PropertyType;
            //if (paramToEvaluate.IsGenericType && paramToEvaluate.GetGenericTypeDefinition() == typeof(Nullable<>))
            //{
            //    paramToEvaluate = paramToEvaluate.GetGenericArguments()[0];
            //    paramFK = paramToEvaluate.Name;
            //}
            if (!string.IsNullOrEmpty(paramFK))
            {
                valueType = typeof(M).GetProperty(paramFK).PropertyType;
                Expression<Func<M, bool>> expToEvaluate = null;
                if (Guid.TryParse(parentId.ToString(), out Guid @guid))
                {
                    expToEvaluate = EqualPredicate<M>(typeof(M), paramFK, @guid, valueType);
                }
                else
                {
                    expToEvaluate = EqualPredicate<M>(typeof(M), paramFK, parentId, valueType);
                }
                iQueryable.RemoveRange(iQueryable.Where(expToEvaluate));
                // _context.SaveChanges();
            }
        }

        private Expression<Func<M, bool>> EqualPredicate<M>(Type type, string propertyName, object value, Type valueType) where M : class
        {
            var parameter = Expression.Parameter(type, "x");
            // x => x.id == value
            //     |___|
            var property = Expression.Property(parameter, propertyName);

            // x => x.id == value
            //             |__|
            var numberValue = Expression.Convert(Expression.Constant(value), valueType); // Expression.Constant(value);

            // x => x.id == value
            //|________________|
            var exp = Expression.Equal(property, numberValue);
            return Expression.Lambda<Func<M, bool>>(exp, parameter);
        }

        public async Task<T> Delete(IResolveFieldContext context, object id, string alias = "", bool sendObjFirebase = true)
        {
            var scope = context.RequestServices.CreateScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<TContext>();

            T obj = null;
            if (id is string)
            {
                if (Guid.TryParse(id.ToString(), out Guid @guid))
                {
                    var keyName = dbContext.Model.FindEntityType(typeof(T)).FindPrimaryKey()?.Properties
                        .Select(x => x.Name).FirstOrDefault();
                    var pi = typeof(T).GetProperty(keyName);
                    var expToEvaluate = EqualPredicate<T>(typeof(T), keyName, @guid, pi.PropertyType);
                    obj = GetModel(dbContext).FirstOrDefault(expToEvaluate);
                }
                else
                {
                    obj = GetModel(dbContext).Find(id);
                }
            }
            else
            {
                obj = GetModel(dbContext).Find(id);
            }

            if (obj != null)
            {
                nameModel = $"delete_{nameModel}";
                try
                {
                    //GetModel.Remove(obj);
                    dbContext.Entry(obj).State = EntityState.Deleted;
                    dbContext.SaveChanges();

                    if (_optionsDelegate.CurrentValue.EnableAudit)
                    {
                        await _cRepositoryLog.AddLog(dbContext, new AuditBinding()
                        {
                            action = AudiState.DELETE,
                            objeto = typeof(T).Name,
                        }, id: id.ToString(), commit: true);
                    }
                }
                catch (ValidationException exc)
                {
                    _logger.LogError(exc, $"{nameof(Update)} validation exception: {exc?.Message}");
                    dbContext.Entry(obj).State = EntityState.Detached;
                    _fillDataExtensions.Add($"{(string.IsNullOrEmpty(alias) ? nameModel : alias)}", @$"{id} => {exc?.Message}");
                }
                catch (DbUpdateException e)
                {
                    dbContext.Entry(obj).State = EntityState.Detached;
                    _fillDataExtensions.Add($"{(string.IsNullOrEmpty(alias) ? nameModel : alias)}", $"{id} => {e.InnerException?.Message}");
                }

                var cacheKeySize = string.Format("_{0}_size", model);
                _cache.Remove(cacheKeySize);
                if (sendObjFirebase) SendStatus(GraphGrpcStatus.DELETE, id.ToString());
                if (!string.IsNullOrEmpty(GetCurrentUser()))
                    _handleMsg.GetStream().OnNext(obj);
            }
            return obj;
        }

        #region helpers
        enum GraphGrpcStatus
        {
            CREATE, UPDATE, DELETE
        }

        private void SendStatus(GraphGrpcStatus action, string id)
        {
            if (_optionsDelegate.CurrentValue.EnableStatusMutation)
            {
                try
                {
                    _optionsDelegate.CurrentValue.CallbackStatus?.Invoke(new GraphStatusRequest
                    {
                        ClassName = typeof(T).Name,
                        Action = (int)action,
                        Id = id,
                        CompanyId = GetCompanyIdUser()
                    });
                }
                catch (Exception e)
                {
                    _logger.LogError($"error Callback : {e}");
                    throw;
                }
            }
        }
        #endregion

        #region utilities
        public int CacheGetOrCreate<E>(IQueryable<E> query)
             where E : class
        {
            var cacheKeySize = string.Format("_{0}_size", typeof(T).Name);
            var cacheEntry = _cache.GetOrCreate(cacheKeySize, entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromDays(1);
                entry.Size = 1000;
                return query.Count();
            });

            return cacheEntry;
        }



        #endregion
    }

}

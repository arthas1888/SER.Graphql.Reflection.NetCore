using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Server;
using GraphQL.Types;
using GraphQL.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SER.Graphql.Reflection.NetCore.Generic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SER.Graphql.Reflection.NetCore.Builder;
using Microsoft.EntityFrameworkCore;
using SER.Models.SERAudit;
using SER.Models;
using SER.Graphql.Reflection.NetCore.WebSocket;
using SER.Graphql.Reflection.NetCore.Permissions;
using GraphQL.SystemTextJson;
using GraphQL.DI;
using GraphQL.Authorization;
using GraphQL.MicrosoftDI;
using GraphQL.Resolvers;
using SER.Graphql.Reflection.NetCore.Models;

namespace SER.Graphql.Reflection.NetCore.Custom
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddConfigGraphQl<TContext>(this IServiceCollection services, Action<ExecutionOptions> options)
          where TContext : DbContext
           => AddConfigGraphQl<TContext, object, object, object>(services, options);

        public static IServiceCollection AddConfigGraphQl<TContext, TUser>(this IServiceCollection services, Action<ExecutionOptions> options)
          where TContext : DbContext
          where TUser : class
           => AddConfigGraphQl<TContext, TUser, object, object>(services, options);

        public static IServiceCollection AddConfigGraphQl<TContext, TUser, TRole>(this IServiceCollection services, Action<ExecutionOptions> options)
           where TContext : DbContext
           where TUser : class
           where TRole : class
            => AddConfigGraphQl<TContext, TUser, TRole, object>(services, options);

        public static IServiceCollection AddBasicConfigGraphQl<TContext>(this IServiceCollection services, Action<ExecutionOptions> options)

        where TContext : DbContext
        {
            services.AddHttpContextAccessor();

            //services.AddSingleton<IDocumentExecuter, MyDocumentExecuter>();
            services.AddSingleton<ITableNameLookup, TableNameLookup>();
            services.AddSingleton<TableMetadata>();
            services.AddSingleton<IDatabaseMetadata, DatabaseMetadata<TContext>>();

            services.AddSingleton<GraphQLQuery<FakeUser, FakeRole, FakeUserRole>>();
            services.AddScoped<IGraphRepository<Audit>, GenericGraphRepository<Audit, TContext, FakeUser, FakeRole, FakeUserRole>>();

            services.AddScoped<FillDataExtensions>();
            services.AddSingleton<ISchema, AppSchema<FakeUser, FakeRole, FakeUserRole>>();
            services.AddSingleton<ISERFieldResolver<FakeUser, FakeRole, FakeUserRole>, MyFieldResolver<FakeUser, FakeRole, FakeUserRole>>();
            services.AddSingleton<IFieldResolver, CUDResolver>();

            services.AddLogging(builder => builder.AddConsole());

            // v5
            services.AddGraphQL(builder => builder
                .ConfigureExecutionOptions(options)
                .AddSystemTextJson()
                .AddDataLoader() // Add required services for DataLoader support
                .AddUserContextBuilder(ctx => new GraphQLUserContext(ctx.User))
                .AddGraphTypes()
            );


            var config = new GraphQLConfiguration(services);
            config.UseDbContext<TContext>();

            // services.AddScoped<IGraphRepository<IdentityRoleClaim<string>>, BasicGenericGraphRepository<IdentityRoleClaim<string>, TContext>>();


            services.Configure<SERGraphQlOptions>(options =>
            {
                options.UserType = typeof(FakeUser);
                options.RoleType = typeof(FakeRole);
                options.UserRoleType = typeof(FakeUserRole);
            });

            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == typeof(TContext).Assembly.GetName().Name);
            // var assembly = Assembly.GetCallingAssembly();

            foreach (var type in assembly.GetTypes()
                .Where(x => !x.IsAbstract && typeof(IBaseModel).IsAssignableFrom(x)))
            {
                var interfaceType = typeof(IGraphRepository<>).MakeGenericType(new Type[] { type });
                var inherateType = typeof(GenericGraphRepository<,,,,>).MakeGenericType(new Type[] { type, typeof(TContext), typeof(FakeUser), typeof(FakeRole), typeof(FakeUserRole) });
                var serviceLifetime = Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped;
                Console.WriteLine($"Dependencia IGraphRepository registrada type {type.Name}");
                services.TryAdd(new ServiceDescriptor(interfaceType, inherateType, serviceLifetime));

                var interfaceHandleType = typeof(IHandleMsg<>).MakeGenericType(new Type[] { type });
                var inherateHandleType = typeof(HandleMsg<>).MakeGenericType(new Type[] { type });
                services.TryAdd(new ServiceDescriptor(interfaceHandleType, inherateHandleType, Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton));
            }


            return services;
        }

        public static IServiceCollection AddConfigGraphQl<TContext, TUser, TRole, TUserRole>(this IServiceCollection services, Action<ExecutionOptions> options)

        where TContext : DbContext
        where TUser : class
        where TRole : class
        where TUserRole : class
        {
            services.AddHttpContextAccessor();

            //services.AddSingleton<IDocumentExecuter, MyDocumentExecuter>();
            services.AddSingleton<ITableNameLookup, TableNameLookup>();
            services.AddSingleton<TableMetadata>();
            services.AddSingleton<IDatabaseMetadata, DatabaseMetadata<TContext>>();

            services.AddSingleton<GraphQLQuery<TUser, TRole, TUserRole>>();
            services.AddScoped<IGraphRepository<Audit>, GenericGraphRepository<Audit, TContext, TUser, TRole, TUserRole>>();

            services.AddScoped<FillDataExtensions>();
            services.AddSingleton<ISchema, AppSchema<TUser, TRole, TUserRole>>();
            services.AddSingleton<ISERFieldResolver<TUser, TRole, TUserRole>, MyFieldResolver<TUser, TRole, TUserRole>>();
            services.AddSingleton<IFieldResolver, CUDResolver>();

            services.AddLogging(builder => builder.AddConsole());

            // v5
            services.AddGraphQL(builder => builder
                .ConfigureExecutionOptions(options)
                .AddSystemTextJson()
                .AddDataLoader() // Add required services for DataLoader support
                .AddUserContextBuilder(ctx => new GraphQLUserContext(ctx.User))
                .AddGraphTypes()
            );


            var config = new GraphQLConfiguration(services);
            config.UseDbContext<TContext>();

            if (typeof(IdentityUser).IsAssignableFrom(typeof(TUser)))
            {
                services.AddScoped<IGraphRepository<TUser>, GenericGraphRepository<TUser, TContext, TUser, TRole, TUserRole>>();
                config.UseUser<TUser>();
            }

            if (typeof(IdentityRole).IsAssignableFrom(typeof(TRole)))
            {
                services.AddScoped<IGraphRepository<TRole>, GenericGraphRepository<TRole, TContext, TUser, TRole, TUserRole>>();
                config.UseRole<TRole>();
            }

            if (typeof(IdentityUserRole<string>).IsAssignableFrom(typeof(TUserRole)))
            {
                services.AddScoped<IGraphRepository<TUserRole>, GenericGraphRepository<TUserRole, TContext, TUser, TRole, TUserRole>>();
                config.UseUserRole<TUserRole>();
            }

            services.AddScoped<IGraphRepository<IdentityRoleClaim<string>>, GenericGraphRepository<IdentityRoleClaim<string>, TContext, TUser, TRole, TUserRole>>();
            AddScopedModelsDynamic<TContext, TUser, TRole, TUserRole>(services);


            return services;
        }

        /// <summary>
        /// Add required services for GraphQL authorization
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static void AddRequiredAuthorization(this IServiceCollection services)
        {
            //permissions
            services.TryAddSingleton<IAuthorizationEvaluator, AuthorizationEvaluator>();

            // extension method defined in this project
            services.AddTransient(s =>
            {
                var authSettings = new AuthorizationSettings();
                // authSettings.AddPolicy("AdminPolicy", _ => _.RequireClaim("role", "Admin"));
                authSettings.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
                return authSettings;
            });
            services.AddTransient<IValidationRule, CustomAuthorizationValidationRule>();
        }

        public static void AddScopedModelsDynamic<TContext, TUser, TRole, TUserRole>(this IServiceCollection services)
             where TContext : DbContext
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == typeof(TContext).Assembly.GetName().Name);
            // var assembly = Assembly.GetCallingAssembly();

            foreach (var type in assembly.GetTypes()
                .Where(x => !x.IsAbstract && typeof(IBaseModel).IsAssignableFrom(x)))
            {
                var interfaceType = typeof(IGraphRepository<>).MakeGenericType(new Type[] { type });
                var inherateType = typeof(GenericGraphRepository<,,,,>).MakeGenericType(new Type[] { type, typeof(TContext),
                    typeof(TUser),typeof(TRole), typeof(TUserRole)});
                var serviceLifetime = Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped;
                // Console.WriteLine($"Dependencia IGraphRepository registrada type {type.Name}");
                services.TryAdd(new ServiceDescriptor(interfaceType, inherateType, serviceLifetime));

                var interfaceHandleType = typeof(IHandleMsg<>).MakeGenericType(new Type[] { type });
                var inherateHandleType = typeof(HandleMsg<>).MakeGenericType(new Type[] { type });
                services.TryAdd(new ServiceDescriptor(interfaceHandleType, inherateHandleType, Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton));
            }
        }
    }
    //internal sealed class GraphQLBuilder : IGraphQLBuilder
    //{
    //    public IServiceCollection Services { get; }

    //    public GraphQLBuilder(IServiceCollection services)
    //    {
    //        Services = services;
    //    }
    //}
}

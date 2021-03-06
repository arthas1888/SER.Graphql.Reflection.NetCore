﻿using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;

namespace SER.Graphql.Reflection.NetCore.Builder
{
    public class GraphQLConfiguration
    {
        /// <summary>
        /// Initializes a new instance
        /// </summary>
        /// <param name="services">The services collection.</param>
        public GraphQLConfiguration(IServiceCollection services)
            => Services = services ?? throw new ArgumentNullException(nameof(services));

        /// <summary>
        /// Configures the GraphQL.Reflection Entity Framework Core stores to use the specified database context type.
        /// </summary>
        /// <typeparam name="TContext">The type of the <see cref="DbContext"/> used by GraphQL.Reflection.</typeparam>
        /// <returns>The <see cref="GraphQLConfiguration"/>.</returns>
        public GraphQLConfiguration UseDbContext<TContext>()
            where TContext : DbContext
            => UseDbContext(typeof(TContext));

        /// <summary>
        /// Configures the GraphQL.Reflection Entity Framework Core stores to use the specified database context type.
        /// </summary>
        /// <param name="type">The type of the <see cref="DbContext"/> used by GraphQL.Reflection.</param>
        /// <returns>The <see cref="GraphQLConfiguration"/>.</returns>
        public GraphQLConfiguration UseDbContext(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (!typeof(DbContext).IsAssignableFrom(type))
            {
                throw new ArgumentException("configure dbcontext", nameof(type));
            }

            return Configure(options => options.DbContextType = type);
        }

        public GraphQLConfiguration UseUser<TUser>()
            where TUser : class
            => UseUser(typeof(TUser));
        public GraphQLConfiguration UseUser(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (!typeof(IdentityUser).IsAssignableFrom(type))
            {
                throw new ArgumentException("configure IdentityUser", nameof(type));
            }

            return Configure(options => options.UserType = type);
        }

        public GraphQLConfiguration UseRole<TRole>()
           where TRole : class
           => UseRole(typeof(TRole));
        public GraphQLConfiguration UseRole(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (!typeof(IdentityRole).IsAssignableFrom(type))
            {
                throw new ArgumentException("configure IdentityRole", nameof(type));
            }

            return Configure(options => options.RoleType = type);
        }

        public GraphQLConfiguration UseUserRole<TUserRole>()
           where TUserRole : class
           => UseUserRole(typeof(TUserRole));
        public GraphQLConfiguration UseUserRole(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            return Configure(options => options.UserRoleType = type);
        }

        /// <summary>
        /// Gets the services collection.
        /// </summary>
        public IServiceCollection Services { get; }

        /// <summary>
        /// Amends the default GraphQL.Reflection Entity Framework Core configuration.
        /// </summary>
        /// <param name="configuration">The delegate used to configure the GraphQL.Reflection options.</param>
        /// <remarks>This extension can be safely called multiple times.</remarks>
        /// <returns>The <see cref="GraphQLConfiguration"/>.</returns>
        public GraphQLConfiguration Configure(Action<SERGraphQlOptions> configuration)
        {
            if (configuration is null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            Services.Configure(configuration);

            return this;
        }


    }
}

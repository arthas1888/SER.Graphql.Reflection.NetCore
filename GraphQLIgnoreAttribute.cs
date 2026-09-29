using System;

namespace SER.Graphql.Reflection.NetCore
{
    /// <summary>
    /// Excludes something from the reflection GraphQL schema entirely.
    /// <para>
    /// On a <b>property</b>: the column is never projected — no output field, no input field, no filter
    /// argument, nothing in introspection. Use it for values that must never leave the server (secrets,
    /// tokens, password hashes, private keys, certificate passwords).
    /// </para>
    /// <para>
    /// On a <b>class</b> (entity): the whole type is skipped — no root query, mutation, input or entity
    /// type, and it is removed as a nested field/navigation of other types, so it does not exist even for
    /// introspection. Use it for tables GraphQL must never process (credentials, logs, backoffice-only or
    /// billing-internal entities).
    /// </para>
    /// Unlike <c>[JsonIgnore]</c> — which the schema builder does not honor and which only affects REST
    /// serialization — this attribute removes the target from the GraphQL surface. Not inherited, so an
    /// exclusion never leaks onto a derived type by accident.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class GraphQLIgnoreAttribute : Attribute
    {
    }
}

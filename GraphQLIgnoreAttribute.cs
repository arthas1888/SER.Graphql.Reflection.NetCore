using System;

namespace SER.Graphql.Reflection.NetCore
{
    /// <summary>
    /// Marks a model property so the reflection schema never projects it: no output field, no input
    /// field, no filter argument, and therefore nothing in introspection. Use it for columns that
    /// must never leave the server (secrets, tokens, password hashes, private keys, certificate
    /// passwords). Unlike <c>[JsonIgnore]</c> — which the schema builder does not honor and which only
    /// affects REST serialization — this attribute removes the column from the GraphQL surface entirely.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class GraphQLIgnoreAttribute : Attribute
    {
    }
}

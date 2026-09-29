using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SER.Graphql.Reflection.NetCore.Builder
{
    /// <summary>
    /// Loads and holds the set of entity class names / table names that must never be processed by the
    /// reflection schema. Loaded once from the configured JSON file. Fail closed: if a path is configured
    /// but the file is missing or invalid, loading throws — the caller (schema metadata build, at
    /// startup) lets that abort the process rather than exposing everything.
    /// </summary>
    public static class ExcludedGraphTypes
    {
        private static HashSet<string> _names;
        private static readonly object _lock = new();

        /// <summary>
        /// Ensures the set is loaded from <paramref name="path"/> and returns it. An empty/blank path
        /// means the feature is off (empty set). A configured-but-unreadable file throws.
        /// </summary>
        public static HashSet<string> Load(string path)
        {
            if (_names != null) return _names;
            lock (_lock)
            {
                if (_names != null) return _names;
                _names = Read(path);
                return _names;
            }
        }

        private static HashSet<string> Read(string path)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path))
                return set;

            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"GraphQL excluded-types file '{path}' is configured but was not found. " +
                    "Startup fails closed rather than exposing every table.");

            List<string> names;
            try
            {
                names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"GraphQL excluded-types file '{path}' is not a valid JSON array of names.", e);
            }

            if (names == null)
                throw new InvalidOperationException(
                    $"GraphQL excluded-types file '{path}' deserialized to null; expected a JSON array of names.");

            foreach (var n in names)
                if (!string.IsNullOrWhiteSpace(n))
                    set.Add(n.Trim());

            return set;
        }

        /// <summary>Matches by entity class name or by table name, case-insensitively.</summary>
        public static bool IsExcluded(string className, string tableName)
            => _names != null
               && ((className != null && _names.Contains(className))
                   || (tableName != null && _names.Contains(tableName)));

        /// <summary>Test seam: forces a reload on the next <see cref="Load"/>.</summary>
        public static void Reset()
        {
            lock (_lock) { _names = null; }
        }
    }
}

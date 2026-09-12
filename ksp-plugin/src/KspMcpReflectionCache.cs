using System;
using System.Collections.Generic;
using System.Reflection;

namespace KspMcp
{
    // Cache metadata, never vessel/module values. A missing member is cached
    // too: compatibility probes otherwise enumerate every field every frame.
    internal static class KspMcpReflectionCache
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly object Sync = new object();
        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> Fields = new Dictionary<Type, Dictionary<string, FieldInfo>>();
        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> Properties = new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        public static FieldInfo FindField(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            lock (Sync)
            {
                Dictionary<string, FieldInfo> members;
                if (!Fields.TryGetValue(type, out members))
                {
                    members = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
                    Fields[type] = members;
                }
                FieldInfo field;
                if (members.TryGetValue(name, out field)) return field;
                field = type.GetField(name, Flags);
                if (field == null)
                    foreach (FieldInfo candidate in type.GetFields(Flags))
                        if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) { field = candidate; break; }
                members[name] = field;
                return field;
            }
        }

        public static PropertyInfo FindProperty(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            lock (Sync)
            {
                Dictionary<string, PropertyInfo> members;
                if (!Properties.TryGetValue(type, out members))
                {
                    members = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
                    Properties[type] = members;
                }
                PropertyInfo property;
                if (members.TryGetValue(name, out property)) return property;
                property = type.GetProperty(name, Flags);
                if (property == null)
                    foreach (PropertyInfo candidate in type.GetProperties(Flags))
                        if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) { property = candidate; break; }
                members[name] = property;
                return property;
            }
        }
    }
}

using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GTFO.LobbyExpansion.Util;

public static class ReflectionUtil
{
    public static void SetStaticField(Type type, string fieldName, object value)
    {
        // The compatibility patch uses this only for named fields in the loaded PacksHelper plugin.
#pragma warning disable S3011
        var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException($"Could not find static field '{fieldName}' in type '{type.FullName}'.");
#pragma warning restore S3011

        field.SetValue(null, value);
    }

    [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
    public static Type GetRequiredTypeByName(string name, Assembly assembly)
    {
        var type = assembly.GetTypes()
            .FirstOrDefault(type => type.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        return type ?? throw new ArgumentException(
            $"Could not find type '{name}' in assembly '{assembly.FullName ?? "unnamed"}'.");
    }
}

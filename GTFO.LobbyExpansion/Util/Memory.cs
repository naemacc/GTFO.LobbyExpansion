// Credit to https://github.com/BepInEx/Il2CppInterop/blob/master/Il2CppInterop.Runtime/MemoryUtils.cs for FindSignatureInBlock

using Il2CppInterop.Common;
using Il2CppInterop.Runtime.InteropTypes;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BindingFlags = System.Reflection.BindingFlags;

namespace GTFO.LobbyExpansion.Util;

public static class Memory
{
    private const long DefaultBlockScanSize = 3000;

    private sealed record Signature(byte[] Bytes, char[] Mask);

    public static nint GetIl2CppConstructorAddress(Type type, params Type[] parameterTypes)
    {
        var constructorInfo = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, parameterTypes);

        if (constructorInfo is null)
        {
            var formattedParameterTypeNames = parameterTypes.Select(x => x.FullName?.ToString() ?? "null");
            throw new MissingMethodException($"Could not find constructor for {type.FullName} with {parameterTypes.Length} parameters ({formattedParameterTypeNames}).");
        }

        return GetIl2CppMethodAddress(type, constructorInfo);
    }

    public static nint GetIl2CppConstructorAddress<T>(params Type[] parameterTypes) where T : Il2CppObjectBase
    {
        return GetIl2CppConstructorAddress(typeof(T), parameterTypes);
    }

    public static nint GetIl2CppMethodAddress(Type type, string methodName)
    {
        return GetIl2CppMethodAddress(type, methodName, []);
    }

    public static nint GetIl2CppMethodAddress(Type type, string methodName, Type[] parameterTypes)
    {
        var methodInfo = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        if (methodInfo is null)
        {
            var extraInfo = "";

            if (parameterTypes.Length > 0)
                extraInfo = $" with {parameterTypes.Length} parameters (${string.Join(", ", parameterTypes.Select(x => x.FullName?.ToString() ?? x.Name))})";

            throw new MissingMethodException($"Could not find method {methodName} in type {type.FullName}{extraInfo}.");
        }

        return GetIl2CppMethodAddress(type, methodInfo);
    }

    public static nint GetIl2CppMethodAddress<T>(string methodName) where T : Il2CppObjectBase
    {
        return GetIl2CppMethodAddress<T>(methodName, []);
    }

    public static nint GetIl2CppMethodAddress<T>(string methodName, Type[] parameterTypes) where T : Il2CppObjectBase
    {
        return GetIl2CppMethodAddress(typeof(T), methodName, parameterTypes);
    }

    public static nint GetIl2CppMethodAddress(Type type, MethodBase methodBase)
    {
        var methodInfoPtrField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(methodBase) ?? throw new MissingFieldException($"Could not find IL2CPP method info pointer field for method {methodBase} of {type.FullName ?? type.Name}.");

        var methodInfoPtr = methodInfoPtrField.GetValue(null) ?? throw new InvalidOperationException($"Method info pointer field for method {methodBase} of {type.FullName ?? type.Name} was null.");

        if (methodInfoPtr is not IntPtr methodInfoPointer)
            throw new InvalidOperationException($"Method info pointer field for method {methodBase} of {type.FullName ?? type.Name} did not contain an IntPtr.");

        return Marshal.ReadIntPtr(methodInfoPointer);
    }

    public static nint GetIl2CppMethodAddress<T>(MethodBase methodBase) where T : Il2CppObjectBase
    {
        return GetIl2CppMethodAddress(typeof(T), methodBase);
    }

    public static nint FindSignatureInIl2CppMethod(nint methodPointer, string signature, long? blockSize)
    {
        var sig = ConvertAobToSignature(signature) ?? throw new ArgumentException($"{nameof(signature)} was an invalid signature.");
        return FindSignatureInBlock(methodPointer, blockSize ?? DefaultBlockScanSize, sig.Bytes, sig.Mask);
    }

    public static nint FindSignatureInIl2CppMethod<T>(string methodName, string signature, long blockSize = DefaultBlockScanSize)
        where T : Il2CppObjectBase
    {
        var methodPointer = GetIl2CppMethodAddress<T>(methodName);
        return FindSignatureInIl2CppMethod(methodPointer, signature, blockSize);
    }

    public static void PatchMemory(IntPtr address, byte[] bytes)
    {
        if (address == IntPtr.Zero)
            throw new ArgumentException($"{nameof(address)} cannot be zero.");

        // TODO: Bother with VirtualQuery first?
        var appliedProtection = Win32.VirtualProtect(
            address,
            new IntPtr(bytes.Length),
            Win32.MemoryProtections.ExecuteReadWrite,
            out var oldProtection);

        if (!appliedProtection)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"VirtualProtect failed for address {address}.");

        var restoredProtection = false;

        try
        {
#if DEBUG
            L.Verbose($"Patching {address:X} with {string.Join(", ", bytes.Select(x => x.ToString("X")))}");
#endif

            // TODO: Good idea?
            for (var i = 0; i < bytes.Length; i++)
                Marshal.WriteByte(address, i, bytes[i]);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            try
            {
                restoredProtection = Win32.VirtualProtect(
                    address,
                    new IntPtr(bytes.Length),
                    oldProtection,
                    out _);
            }
            catch
            {
                L.Error($"VirtualProtect failed to restore protection for address {address}.");
            }
        }

        if (!restoredProtection)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"VirtualProtect failed to restore protection for address {address}.");
    }

    private static nint FindSignatureInBlock(nint block, long blockSize, byte[] pattern, char[] mask)
    {
        for (long address = 0; address < blockSize; address++)
        {
            var found = true;
            for (uint offset = 0; offset < mask.Length; offset++)
            {
                var byteAddress = block + (nint)address + (nint)offset;
                if (Marshal.ReadByte(byteAddress) != pattern[offset] && mask[offset] != '?')
                {
                    found = false;
                    break;
                }
            }

            if (found)
                return block + (nint)address;
        }

        return 0;
    }

    private static Signature? ConvertAobToSignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return default;

        var entries = signature.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (entries.Length == 0)
            return default;

        var patternBytes = new byte[entries.Length];
        var mask = new char[entries.Length];

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];

            if (entry is "?" or "??")
            {
                patternBytes[i] = 0;
                mask[i] = '?';
            }
            else
            {
                patternBytes[i] = byte.Parse(entry, NumberStyles.HexNumber);
                mask[i] = 'x';
            }
        }

        return new Signature(patternBytes, mask);
    }
}

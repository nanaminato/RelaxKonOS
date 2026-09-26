using System.Runtime.InteropServices;
using System.Text;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>
/// 一个命名凭据槽：平台安全存储里的独立条目。每个槽有自己的 service/schema 身份，
/// 因此 SSH 凭据不会与 RelaxKonOS 登录凭据互相读写，即使两者同属一个操作系统用户。
/// </summary>
internal sealed record PlatformSecretSlot(
    string ServiceName,
    string AccountName,
    string SchemaName,
    string SchemaAttributeName,
    string SchemaAttributeValue,
    string Label)
{
    /// <summary>macOS Keychain 通用密码条目。</summary>
    public static PlatformSecretSlot MacKeychain(string serviceName, string accountName = "default") =>
        new(serviceName, accountName, serviceName, "application", serviceName, serviceName);

    /// <summary>Linux Secret Service 条目。<paramref name="label"/> 只用于密钥环界面显示，不参与匹配。</summary>
    public static PlatformSecretSlot LinuxSecret(
        string schemaName, string attributeName, string attributeValue, string label) =>
        new(schemaName, attributeName, schemaName, attributeName, attributeValue, label);
}

/// <summary>平台安全存储的读写结果。返回 false 表示平台存储不可用，而不是「没有值」。</summary>
internal interface IPlatformSecretStore
{
    /// <summary>读取槽内载荷。成功返回 true，<paramref name="value"/> 为 null 表示槽为空。</summary>
    bool TryRead(out string? value);

    bool TryWrite(string value);

    bool TryClear();
}

/// <summary>
/// macOS Keychain 通用密码。用 Security.framework 的通用密码接口，不落任何明文文件。
/// </summary>
internal sealed class MacKeychainStore(PlatformSecretSlot slot) : IPlatformSecretStore
{
    private const int Success = 0;
    private const int ItemNotFound = -25300;

    private readonly byte[] _service = Encoding.UTF8.GetBytes(slot.ServiceName);
    private readonly byte[] _account = Encoding.UTF8.GetBytes(slot.AccountName);

    public bool TryRead(out string? value)
    {
        value = null;
        IntPtr data = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)_service.Length, _service, (uint)_account.Length, _account,
                out var length, out data, out item);
            if (status == ItemNotFound) return true;
            if (status != Success) return false;

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public bool TryWrite(string value)
    {
        var password = Encoding.UTF8.GetBytes(value);
        IntPtr data = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)_service.Length, _service, (uint)_account.Length, _account,
                out _, out data, out item);
            if (status == Success)
                return SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password) == Success;
            if (status != ItemNotFound) return false;

            return SecKeychainAddGenericPassword(
                IntPtr.Zero, (uint)_service.Length, _service, (uint)_account.Length, _account,
                (uint)password.Length, password, out item) == Success;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public bool TryClear()
    {
        IntPtr data = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)_service.Length, _service, (uint)_account.Length, _account,
                out _, out data, out item);
            return status == ItemNotFound || (status == Success && SecKeychainItemDelete(item) == Success);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength,
        out IntPtr passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength,
        byte[] passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}

/// <summary>
/// Linux Secret Service（libsecret）。GNOME Keyring / KWallet 通过它提供加密存储；
/// 库缺失时报告不可用，绝不退化为明文文件。
/// </summary>
internal sealed class LinuxSecretServiceStore : IPlatformSecretStore
{
    private static readonly GlibHashFunction HashFunction = Hash;
    private static readonly GlibEqualFunction EqualFunction = Equal;
    private static readonly IntPtr HashFunctionPointer = Marshal.GetFunctionPointerForDelegate(HashFunction);
    private static readonly IntPtr EqualFunctionPointer = Marshal.GetFunctionPointerForDelegate(EqualFunction);

    private readonly PlatformSecretSlot _slot;
    // libsecret takes the schema by reference on every call, so this struct field cannot be readonly.
    private SecretSchema _schema;

    public LinuxSecretServiceStore(PlatformSecretSlot slot)
    {
        _slot = slot;
        _schema = new SecretSchema
        {
            Name = slot.SchemaName,
            Flags = 0,
            Attributes = CreateAttributes(slot.SchemaAttributeName)
        };
    }

    public bool TryRead(out string? value)
    {
        value = null;
        try
        {
            using var attributes = new SecretAttributes(_slot.SchemaAttributeName, _slot.SchemaAttributeValue);
            var password = secret_password_lookupv_sync(ref _schema, attributes.Handle, IntPtr.Zero, out var error);
            try
            {
                if (error != IntPtr.Zero) return false;
                if (password == IntPtr.Zero) return true;
                value = Marshal.PtrToStringUTF8(password);
                return true;
            }
            finally
            {
                if (password != IntPtr.Zero) secret_password_free(password);
                FreeError(error);
            }
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public bool TryWrite(string value)
    {
        try
        {
            using var attributes = new SecretAttributes(_slot.SchemaAttributeName, _slot.SchemaAttributeValue);
            if (attributes.Handle == IntPtr.Zero) return false;
            var saved = secret_password_storev_sync(
                ref _schema, attributes.Handle, IntPtr.Zero, _slot.Label, value,
                IntPtr.Zero, out var error);
            var hasError = error != IntPtr.Zero;
            FreeError(error);
            return saved && !hasError;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public bool TryClear()
    {
        try
        {
            using var attributes = new SecretAttributes(_slot.SchemaAttributeName, _slot.SchemaAttributeValue);
            var cleared = secret_password_clearv_sync(ref _schema, attributes.Handle, IntPtr.Zero, out var error);
            var hasError = error != IntPtr.Zero;
            FreeError(error);
            return cleared && !hasError;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static void FreeError(IntPtr error)
    {
        if (error != IntPtr.Zero) g_error_free(error);
    }

    private static SecretSchemaAttribute[] CreateAttributes(string attributeName)
    {
        var attributes = new SecretSchemaAttribute[32];
        attributes[0] = new SecretSchemaAttribute { Name = attributeName, Type = 0 };
        return attributes;
    }

    private sealed class SecretAttributes : IDisposable
    {
        private readonly IntPtr _key;
        private readonly IntPtr _value;
        public IntPtr Handle { get; }

        public SecretAttributes(string attributeName, string attributeValue)
        {
            Handle = g_hash_table_new(HashFunctionPointer, EqualFunctionPointer);
            _key = Marshal.StringToCoTaskMemUTF8(attributeName);
            _value = Marshal.StringToCoTaskMemUTF8(attributeValue);
            g_hash_table_insert(Handle, _key, _value);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) g_hash_table_destroy(Handle);
            Marshal.FreeCoTaskMem(_key);
            Marshal.FreeCoTaskMem(_value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchema
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Name;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public SecretSchemaAttribute[] Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchemaAttribute
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Name;
        public int Type;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GlibHashFunction(IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool GlibEqualFunction(IntPtr first, IntPtr second);

    private static uint Hash(IntPtr value)
    {
        uint hash = 5381;
        for (var index = 0; ; index++)
        {
            var current = Marshal.ReadByte(value, index);
            if (current == 0) return hash;
            hash = (hash << 5) + hash + current;
        }
    }

    private static bool Equal(IntPtr first, IntPtr second)
    {
        var index = 0;
        while (true)
        {
            var left = Marshal.ReadByte(first, index);
            var right = Marshal.ReadByte(second, index);
            if (left != right) return false;
            if (left == 0) return true;
            index++;
        }
    }

    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_password_lookupv_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    // The whole `*v_sync` family takes the attribute table directly after the schema:
    // (schema, attributes, collection, label, password, cancellable, error).  It is NOT the
    // varargs `secret_password_store_sync` order, which has no attributes parameter at all
    // (schema, collection, label, password, cancellable, error, ...attributes).  Passing
    // collection first therefore hands libsecret a NULL attributes table, which trips its
    // `g_return_val_if_fail (attributes != NULL)` guard: a GLib CRITICAL on stderr reading
    // `secret_password_storev_sync: assertion 'attributes != NULL' failed`, and a FALSE return
    // that surfaces as "secure storage unavailable" for every single Linux write.
    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_storev_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr collection, string label,
        string password, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_clearv_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool g_hash_table_insert(IntPtr hashTable, IntPtr key, IntPtr value);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_hash_table_destroy(IntPtr hashTable);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);
}
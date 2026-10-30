using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MathGPT.Core;

// Credentials never appear in process arguments, environment, logs, or plaintext files.
internal static class MacKeychain
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static byte[] Service(string root) => Encoding.UTF8.GetBytes("xyz.mathgpt.desktop." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))));
    private static void Check(int status) { if (status != 0) throw new UserException("钥匙串访问未完成（" + status + "）。请允许 MathGPT 访问自己的凭据后重试。"); }
    public static string? Get(string root, string id)
    {
        var service = Service(root); var account = Encoding.UTF8.GetBytes(UrlPolicy.SafeId(id));
        var status = Find(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, out var item);
        if (status == -25300) return null;
        Check(status);
        try { var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, checked((int)length)); try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); } }
        finally { Free(IntPtr.Zero, data); if (item != IntPtr.Zero) Release(item); }
    }
    public static void Put(string root, string id, string secret)
    {
        var service = Service(root); var account = Encoding.UTF8.GetBytes(UrlPolicy.SafeId(id)); var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = Find(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out _, out var data, out var item);
            if (status == -25300) { Check(Add(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)bytes.Length, bytes, out item)); if (item != IntPtr.Zero) Release(item); }
            else { Check(status); try { Check(Modify(item, IntPtr.Zero, (uint)bytes.Length, bytes)); } finally { Free(IntPtr.Zero, data); if (item != IntPtr.Zero) Release(item); } }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void Remove(string root, string id)
    {
        var service = Service(root); var account = Encoding.UTF8.GetBytes(UrlPolicy.SafeId(id));
        var status = Find(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out _, out var data, out var item);
        if (status == -25300) return;
        Check(status);
        try { Check(Delete(item)); } finally { Free(IntPtr.Zero, data); if (item != IntPtr.Zero) Release(item); }
    }
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")] private static extern int Find(IntPtr keychains, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint length, out IntPtr data, out IntPtr item);
    [DllImport(Security, EntryPoint = "SecKeychainAddGenericPassword")] private static extern int Add(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint length, byte[] data, out IntPtr item);
    [DllImport(Security, EntryPoint = "SecKeychainItemModifyAttributesAndData")] private static extern int Modify(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security, EntryPoint = "SecKeychainItemDelete")] private static extern int Delete(IntPtr item);
    [DllImport(Security, EntryPoint = "SecKeychainItemFreeContent")] private static extern int Free(IntPtr attributes, IntPtr data);
    [DllImport(Foundation, EntryPoint = "CFRelease")] private static extern void Release(IntPtr value);
}

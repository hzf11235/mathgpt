using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MathGPT.Core;

public static partial class UrlPolicy
{
    public static Uri Base(string input)
    {
        if (!Uri.TryCreate(input.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))))
            throw new UserException("请输入 HTTPS API 地址；本机服务可使用 http://127.0.0.1。地址不能含密码、查询或片段。");
        return uri;
    }
    public static bool IsMathGpt(string input) => Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Equals("mathgpt.xyz", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;
    public static Uri Endpoint(string input, string suffix)
    {
        var uri = Base(input);
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/" + suffix.TrimStart('/'));
    }
    public static string Origin(string input)
    {
        var uri = Base(input);
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static void OfficialDownload(string value)
    {
        var uri = Base(value);
        if (uri.Scheme != "https" || !uri.IsDefaultPort || !new[] {
            "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com",
            "downloads.claude.ai", "download.deepseek.com", "persistent.oaistatic.com", "claude.ai" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new UserException("下载地址不属于已核验的官方分发源。");
    }
    public static string SafeId(string id)
    {
        if (!SafeIdPattern().IsMatch(id)) throw new UserException("配置标识无效。");
        return id;
    }
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,90}$")]
    private static partial Regex SafeIdPattern();
}

public sealed class UserException(string message) : Exception(message);

public static partial class Privacy
{
    public static string Clean(string? text) => TokenPattern().Replace(text ?? "", "[已隐藏凭据]");
    [GeneratedRegex(@"(?i)(?:sk-[a-zA-Z0-9_-]{8,}|Bearer\s+[a-zA-Z0-9._~+/\-=]{8,}|eyJ[a-zA-Z0-9_.-]{16,})")]
    private static partial Regex TokenPattern();
    public static string Friendly(Exception ex) => ex switch
    {
        OperationCanceledException => "已停止。已发出的请求可能仍由服务端结算，请核对网站账单。",
        UserException => Clean(ex.Message),
        HttpRequestException => "网络连接失败。请检查地址、代理或网络，再重试。",
        UnauthorizedAccessException => "这个目录没有写入权限。请选择当前用户可写的位置。",
        IOException => "文件正被占用，或磁盘不可写。请关闭相关程序后重试。",
        CryptographicException => OperatingSystem.IsMacOS() ? "无法访问钥匙串凭据，请在当前 Mac 用户下重新连接。" : "无法解密本机凭据。请在此 Windows 账号下重新连接。",
        _ => "操作没有完成。请检查设置后重试；详细原因可在帮助页查看。"
    };
}

public static class SafeFiles
{
    public static string DirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new UserException("请选择目录。");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 4 ||
            full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new UserException("请选择本机普通子目录，不能使用磁盘根目录或网络共享。");
        NoLinks(full);
        return full;
    }
    public static void NoLinks(string path)
    {
        for (var at = Path.GetFullPath(path); !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new UserException("此路径包含链接或重解析点，请选择普通本机目录。");
    }
    public static string Child(string root, string relative)
    {
        var prefix = DirectoryPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var value = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UserException("文件路径超出任务目录。");
        NoLinks(value);
        return value;
    }
    public static void AtomicWrite(string path, string text)
    {
        NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false, true));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Writable(string path)
    {
        var safe = DirectoryPath(path);
        Directory.CreateDirectory(safe);
        var probe = Path.Combine(safe, ".mathgpt-write-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
    }
}

// Windows DPAPI, current-user scope. The OS owns cryptography and credential isolation.
public sealed class SecretVault
{
    private readonly string root;
    public SecretVault(string dataRoot)
    {
        root = SafeFiles.Child(dataRoot, "credentials");
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsMacOS()) { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return; }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secret storage requires Windows DPAPI or macOS Keychain.");
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        foreach (var identity in new[] { sid, new System.Security.Principal.SecurityIdentifier("S-1-5-18") })
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
    }
    private string PathFor(string id) => SafeFiles.Child(root, UrlPolicy.SafeId(id) + ".dpapi");
    public bool Contains(string id) => OperatingSystem.IsMacOS() ? MacKeychain.Get(root, id) is not null : File.Exists(PathFor(id));
    public void Put(string id, string secret)
    {
        if (OperatingSystem.IsMacOS()) { MacKeychain.Put(root, id, secret); return; }
        var input = Encoding.UTF8.GetBytes(secret);
        try
        {
            var encrypted = Transform(input, true);
            var target = PathFor(id);
            var temporary = target + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, target, true);
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }
    public string? Get(string id)
    {
        if (OperatingSystem.IsMacOS()) return MacKeychain.Get(root, id);
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        var plain = Transform(File.ReadAllBytes(path), false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Remove(string id) { if (OperatingSystem.IsMacOS()) { MacKeychain.Remove(root, id); return; } var path = PathFor(id); if (File.Exists(path)) File.Delete(path); }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
                Marshal.FreeHGlobal(input.Data);
            }
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

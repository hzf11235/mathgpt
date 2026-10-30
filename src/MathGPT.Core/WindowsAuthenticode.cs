using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MathGPT.Core;

// Use the Windows trust provider directly: signature checks do not require a
// hidden PowerShell process, an extracted script, or an execution-policy change.
public static class WindowsAuthenticode
{
    public static string TrustedSigner(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        SafeFiles.NoLinks(path);
        var fullPath = Path.GetFullPath(path);
        using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pathPointer = Marshal.StringToCoTaskMemUni(fullPath);
        var filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustFile>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            UiChoice = 2, // WTD_UI_NONE: report failure to the application.
            // Keep Windows' default Authenticode trust/revocation policy, as
            // Get-AuthenticodeSignature did. Do not disable policy or revocation.
            RevocationChecks = 0,
            UnionChoice = 1, // WTD_CHOICE_FILE.
            File = filePointer,
            StateAction = 1, // WTD_STATEACTION_VERIFY.
            ProviderFlags = 0x2000, // WTD_DISABLE_MD2_MD4.
            UiContext = 1 // WTD_UICONTEXT_INSTALL.
        };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            Marshal.StructureToPtr(new TrustFile
            {
                Size = (uint)Marshal.SizeOf<TrustFile>(),
                Path = pathPointer,
                Handle = file.SafeFileHandle.DangerousGetHandle()
            }, filePointer, false);
            // WinVerifyTrust returns a LONG, not an HRESULT. Only zero succeeds.
            if (Native.WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new UserException("安装包签名未通过 Windows 验证，已阻止安装。");
            var provider = Native.WTHelperProvDataFromStateData(data.StateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero :
                Native.WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var certificatePointer = signer == IntPtr.Zero ? IntPtr.Zero :
                Native.WTHelperGetProvCertFromChain(signer, 0);
            if (certificatePointer == IntPtr.Zero)
                throw new UserException("安装包缺少可信发布者信息，已阻止安装。");
            var certificate = Marshal.PtrToStructure<ProviderCertificate>(certificatePointer);
            if (certificate.Context == IntPtr.Zero)
                throw new UserException("安装包缺少可信发布者信息，已阻止安装。");
            using var signerCertificate = new X509Certificate2(certificate.Context);
            return signerCertificate.Subject;
        }
        finally
        {
            // Release provider state even when trust or publisher extraction fails.
            try
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE.
                Native.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally
            {
                Marshal.FreeCoTaskMem(filePointer);
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        public uint Size;
        public IntPtr Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback;
        public IntPtr SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    // Only read the documented prefix; the remaining chain fields stay native.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public IntPtr Context;
    }

    private static class Native
    {
        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signerIndex,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificateIndex);
    }
}

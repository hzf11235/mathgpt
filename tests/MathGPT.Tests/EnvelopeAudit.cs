using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MathGPT.Core;
using Microsoft.Win32.SafeHandles;

namespace MathGPT.Tests;

internal static class EnvelopeAudit
{
    public static Task Run()
    {
        using var rsa = new RSACng(2048);
        var password = string.Concat(Enumerable.Repeat("长密码测试", 24));
        var value = PasswordCipher.Encrypt(password, rsa.ExportSubjectPublicKeyInfoPem(), "fixture-key-id");
        var parts = value.Split('.');
        if (parts.Length != 4 || parts[0] != "v2") throw new InvalidOperationException("no long-password envelope");
        var input = Convert.FromBase64String(parts[1]);
        var label = Encoding.UTF8.GetBytes("password-v2");
        var pin = GCHandle.Alloc(label, GCHandleType.Pinned);
        try
        {
            var padding = new Oaep { Algorithm = "SHA256", Label = pin.AddrOfPinnedObject(), LabelLength = label.Length };
            var key = new byte[256];
            var status = NCryptDecrypt(rsa.Key.Handle, input, input.Length, ref padding, key, key.Length, out var length, 4);
            if (status != 0 || length != 32) throw new CryptographicException(status);
            var cipher = Convert.FromBase64String(parts[3]);
            var plaintext = new byte[cipher.Length - 16];
            using var aes = new AesGcm(key.AsSpan(0, length).ToArray(), 16);
            aes.Decrypt(Convert.FromBase64String(parts[2]), cipher.AsSpan(0, cipher.Length - 16), cipher.AsSpan(cipher.Length - 16),
                plaintext, Encoding.UTF8.GetBytes("password-v2:fixture-key-id"));
            if (Encoding.UTF8.GetString(plaintext) != password) throw new InvalidOperationException("Unicode password changed");
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext);
        }
        finally { pin.Free(); }
        return Task.CompletedTask;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Oaep
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Algorithm;
        public IntPtr Label;
        public int LabelLength;
    }
    [DllImport("ncrypt.dll")]
    private static extern int NCryptDecrypt(SafeNCryptKeyHandle key, byte[] input, int inputLength,
        ref Oaep padding, byte[] output, int outputLength, out int resultLength, int flags);
}

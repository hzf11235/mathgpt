using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

namespace MathGPT.Core;

public static class PasswordCipher
{
    // Server-defined v1 RSA-OAEP/SHA256 and v2 RSA-OAEP + AES-256-GCM envelope.
    // Native .NET RSA for v1; Bouncy Castle supports the server's v2 OAEP label
    // on every desktop platform. No CNG-only code or custom OAEP implementation.
    public static string Encrypt(string password, string publicKey, string keyId)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        var plain = Encoding.UTF8.GetBytes(password);
        try
        {
            if (plain.Length <= rsa.KeySize / 8 - 66)
                return Convert.ToBase64String(rsa.Encrypt(plain, RSAEncryptionPadding.OaepSHA256));
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length]; var tag = new byte[16];
                using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes("password-v2:" + keyId));
                var wrapped = Wrap(rsa.ExportParameters(false), key);
                return "v2." + Convert.ToBase64String(wrapped) + "." + Convert.ToBase64String(nonce) + "." +
                    Convert.ToBase64String(cipher.Concat(tag).ToArray());
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] Wrap(RSAParameters key, byte[] bytes)
    {
        var cipher = new OaepEncoding(new RsaEngine(), new Sha256Digest(), Encoding.UTF8.GetBytes("password-v2"));
        cipher.Init(true, new RsaKeyParameters(false, new BigInteger(1,key.Modulus!), new BigInteger(1,key.Exponent!)));
        return cipher.ProcessBlock(bytes,0,bytes.Length);
    }
}

using System.Security.Cryptography;

namespace TidalSharp;

internal class Decryption
{
    public static (byte[] key, byte[] nonce) DecryptSecurityToken(string securityToken)
    {
        // Do not change this
        string masterKeyBase64 = "UIlTTEMmmLfGowo/UC60x2H45W6MdGgTRfo/umg4754=";

        // Decode the base64 strings to byte arrays
        byte[] masterKey = Convert.FromBase64String(masterKeyBase64);
        byte[] securityTokenBytes = Convert.FromBase64String(securityToken);

        // Get the IV from the first 16 bytes of the securityToken
        byte[] iv = new byte[16];
        Array.Copy(securityTokenBytes, 0, iv, 0, 16);
        byte[] encryptedSt = new byte[securityTokenBytes.Length - 16];
        Array.Copy(securityTokenBytes, 16, encryptedSt, 0, encryptedSt.Length);

        // Initialize decryptor
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = masterKey;
        aes.IV = iv;

        using ICryptoTransform decryptor = aes.CreateDecryptor();
        // Decrypt the security token
        byte[] decryptedSt = decryptor.TransformFinalBlock(encryptedSt, 0, encryptedSt.Length);

        // Get the audio stream decryption key and nonce from the decrypted security token
        byte[] key = new byte[16];
        Array.Copy(decryptedSt, 0, key, 0, 16);
        byte[] nonce = new byte[8];
        Array.Copy(decryptedSt, 16, nonce, 0, 8);

        return (key, nonce);
    }

    public static void DecryptFile(string pathFileEncrypted, string pathFileDestination, byte[] key, byte[] nonce)
    {
        using FileStream fsSrc = new(pathFileEncrypted, FileMode.Open, FileAccess.Read);
        using FileStream fsDst = new(pathFileDestination, FileMode.Create, FileAccess.Write);
        DecryptStream(fsSrc, fsDst, key, nonce);
    }

    public static void DecryptStream(Stream encryptedStream, Stream outStream, byte[] key, byte[] nonce)
    {
        byte[] counter = new byte[16];
        Array.Copy(nonce, 0, counter, 0, nonce.Length);

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Key = key;
        aes.IV = counter;

        byte[] buffer = new byte[4096];
        int bytesRead;

        using ICryptoTransform decryptor = aes.CreateDecryptor();
        while ((bytesRead = encryptedStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            byte[] decrypted = decryptor.TransformFinalBlock(buffer, 0, bytesRead);
            outStream.Write(decrypted, 0, decrypted.Length);
        }
    }
}
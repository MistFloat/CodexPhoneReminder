using System.Security.Cryptography;
using System.Text;

namespace CodexPhoneReminder.Agent;

/// <summary>
/// Encrypts only the phone/Agent payload, never relay credentials.  The relay
/// sees the envelope but cannot inspect the request or response plaintext.
/// </summary>
public static class RelayEnvelopeCrypto
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static RelayEnvelope Encrypt(string base64Key, string associatedData, string plaintext)
    {
        var key = DecodeKey(base64Key);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var clear = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[clear.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, clear, cipher, tag, Encoding.UTF8.GetBytes(associatedData));

        var combined = new byte[cipher.Length + tag.Length];
        Buffer.BlockCopy(cipher, 0, combined, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, cipher.Length, tag.Length);
        return new RelayEnvelope(Convert.ToBase64String(nonce), Convert.ToBase64String(combined));
    }

    public static bool TryDecrypt(string base64Key, string associatedData, RelayEnvelope envelope, out string plaintext)
    {
        plaintext = string.Empty;
        try
        {
            var key = DecodeKey(base64Key);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var combined = Convert.FromBase64String(envelope.Ciphertext);
            if (nonce.Length != NonceLength || combined.Length < TagLength) return false;

            var cipherLength = combined.Length - TagLength;
            var clear = new byte[cipherLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, combined.AsSpan(0, cipherLength), combined.AsSpan(cipherLength, TagLength), clear,
                Encoding.UTF8.GetBytes(associatedData));
            plaintext = Encoding.UTF8.GetString(clear);
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static byte[] DecodeKey(string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        if (key.Length != 32) throw new CryptographicException("Relay key must be 256 bits.");
        return key;
    }
}

public sealed record RelayEnvelope(string Nonce, string Ciphertext);
public sealed record RelayApiRequest(string Method, string Path, string? Body);

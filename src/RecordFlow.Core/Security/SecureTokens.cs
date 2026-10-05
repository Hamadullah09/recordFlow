using System.Security.Cryptography;
using System.Text;

namespace RecordFlow.Core.Security;

/// <summary>Cryptographically random identifiers, tokens and human-readable reference numbers.</summary>
public static class SecureTokens
{
    // Crockford base32 without I, L, O, U – easy to read aloud over the phone.
    private const string ReadableAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>URL-safe random token. 32 bytes = 256 bits of entropy.</summary>
    public static string NewToken(int bytes = 32) =>
        Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string NewKey() => NewToken(12);

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string NewOrderNumber(DateTime utcNow) =>
        $"RF-{utcNow:yyMMdd}-{Readable(6)}";

    public static string NewConfirmationNumber() => $"CNF-{Readable(4)}-{Readable(4)}";

    public static string Readable(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = ReadableAlphabet[RandomNumberGenerator.GetInt32(ReadableAlphabet.Length)];
        return new string(chars);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>True when the string only contains characters a token produced by <see cref="NewToken"/> can contain.</summary>
    public static bool LooksLikeToken(string? value, int minLength = 16, int maxLength = 128) =>
        value is not null && value.Length >= minLength && value.Length <= maxLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

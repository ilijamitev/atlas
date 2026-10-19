using System.Security.Cryptography;
using System.Text;
using Atlas.Shared.Markets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atlas.Shared.Encryption;

public sealed class EncryptionOptions
{
    public Dictionary<string, MarketKeyring> Markets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A market's keys. New values use the active key; older values name the key they were written with.</summary>
public sealed class MarketKeyring
{
    public string ActiveKeyId { get; set; } = "";

    /// <summary>Key ID → base64 AES-256 key. Locally from config; in production from a key vault in the market.</summary>
    public Dictionary<string, string> Keys { get; set; } = new();
}

/// <summary>Encrypts document images and identifiers at rest, with a key per market.</summary>
public interface IFieldCipher
{
    byte[] Encrypt(string market, ReadOnlySpan<byte> plaintext);

    byte[] Decrypt(string market, ReadOnlySpan<byte> ciphertext);
}

/// <summary>
/// AES-256-GCM. Layout: version (1 byte) | key ID length (1) | key ID | nonce (12) | tag (16) | ciphertext. The key ID
/// lets keys rotate without re-encrypting old values. The market code is authenticated data, so a value encrypted
/// for one market can't be decrypted as another's.
/// </summary>
internal sealed class FieldCipher : IFieldCipher
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<string, (string ActiveKeyId, Dictionary<string, byte[]> Keys)> _keyrings;

    public FieldCipher(IOptions<EncryptionOptions> options) =>
        _keyrings = options.Value.Markets.ToDictionary(
            m => m.Key.ToUpperInvariant(),
            m => (m.Value.ActiveKeyId, m.Value.Keys.ToDictionary(k => k.Key, k => Convert.FromBase64String(k.Value))));

    public byte[] Encrypt(string market, ReadOnlySpan<byte> plaintext)
    {
        var (activeKeyId, keys) = Keyring(market);
        var keyId = Encoding.ASCII.GetBytes(activeKeyId);
        var headerSize = 2 + keyId.Length;
        var output = new byte[headerSize + NonceSize + TagSize + plaintext.Length];
        output[0] = FormatVersion;
        output[1] = (byte)keyId.Length;
        keyId.CopyTo(output, 2);

        var nonce = output.AsSpan(headerSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(keys[activeKeyId], TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(headerSize + NonceSize + TagSize),
            output.AsSpan(headerSize + NonceSize, TagSize),
            AssociatedData(market));
        return output;
    }

    public byte[] Decrypt(string market, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < 2 + NonceSize + TagSize || ciphertext[0] != FormatVersion)
        {
            throw new CryptographicException("Unknown ciphertext format.");
        }

        var keyIdLength = ciphertext[1];
        var keyId = Encoding.ASCII.GetString(ciphertext.Slice(2, keyIdLength));
        var (_, keys) = Keyring(market);
        if (!keys.TryGetValue(keyId, out var key))
        {
            throw new CryptographicException($"Market {market} has no key '{keyId}'.");
        }

        var offset = 2 + keyIdLength;
        var body = ciphertext[(offset + NonceSize + TagSize)..];
        var plaintext = new byte[body.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            ciphertext.Slice(offset, NonceSize),
            body,
            ciphertext.Slice(offset + NonceSize, TagSize),
            plaintext,
            AssociatedData(market));
        return plaintext;
    }

    private (string ActiveKeyId, Dictionary<string, byte[]> Keys) Keyring(string market) =>
        _keyrings.TryGetValue(market.ToUpperInvariant(), out var keyring)
            ? keyring
            : throw new CryptographicException($"Market {market} has no encryption keys.");

    private static byte[] AssociatedData(string market) => Encoding.ASCII.GetBytes(market.ToUpperInvariant());
}

/// <summary>Every market this deployment serves needs an active 256-bit key, or the service doesn't start.</summary>
internal sealed class EncryptionOptionsValidator(IOptions<MarketsOptions> markets) : IValidateOptions<EncryptionOptions>
{
    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        var failures = new List<string>();
        foreach (var market in markets.Value.EnabledCodes())
        {
            var keyring = options.Markets.FirstOrDefault(m => string.Equals(m.Key, market, StringComparison.OrdinalIgnoreCase)).Value;
            if (keyring is null)
            {
                failures.Add($"{market}: no encryption keys configured.");
                continue;
            }

            if (!keyring.Keys.TryGetValue(keyring.ActiveKeyId, out var key) || !Is256BitKey(key))
            {
                failures.Add($"{market}: ActiveKeyId must name a base64 256-bit key.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool Is256BitKey(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class FieldEncryptionExtensions
{
    public static IServiceCollection AddFieldEncryption(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EncryptionOptions>().Bind(configuration.GetSection("Atlas:Encryption")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EncryptionOptions>, EncryptionOptionsValidator>();
        services.AddSingleton<IFieldCipher, FieldCipher>();
        return services;
    }

    public static byte[] EncryptString(this IFieldCipher cipher, string market, string value) =>
        cipher.Encrypt(market, Encoding.UTF8.GetBytes(value));

    public static string DecryptString(this IFieldCipher cipher, string market, byte[] value) =>
        Encoding.UTF8.GetString(cipher.Decrypt(market, value));
}

using System.Security.Cryptography;
using System.Text;
using Atlas.Shared.Encryption;
using Microsoft.Extensions.Options;

namespace Atlas.UnitTests.Shared;

public sealed class FieldCipherTests
{
    private static readonly string KeyOne = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string KeyTwo = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Round_trips_and_hides_the_plaintext()
    {
        var cipher = Cipher(("MB", "k1", [("k1", KeyOne)]));
        var plaintext = Encoding.UTF8.GetBytes("0000000000000");

        var encrypted = cipher.Encrypt("MB", plaintext);

        Assert.Equal(plaintext, cipher.Decrypt("MB", encrypted));
        Assert.DoesNotContain("0000000000000", Encoding.UTF8.GetString(encrypted));
    }

    [Fact]
    public void Uses_a_fresh_nonce_every_time()
    {
        var cipher = Cipher(("MB", "k1", [("k1", KeyOne)]));

        Assert.NotEqual(cipher.Encrypt("MB", [1, 2, 3]), cipher.Encrypt("MB", [1, 2, 3]));
    }

    [Fact]
    public void Old_values_still_decrypt_after_the_key_rotates()
    {
        var before = Cipher(("MB", "k1", [("k1", KeyOne)]));
        var after = Cipher(("MB", "k2", [("k1", KeyOne), ("k2", KeyTwo)]));
        var old = before.Encrypt("MB", [42]);

        Assert.Equal([42], after.Decrypt("MB", old));
        Assert.Contains("k2", Encoding.ASCII.GetString(after.Encrypt("MB", [42])));
    }

    [Fact]
    public void A_value_encrypted_for_one_market_cant_be_read_as_another()
    {
        // Same key in both markets: only the market binding (authenticated data) tells them apart.
        var cipher = Cipher(("MA", "k1", [("k1", KeyOne)]), ("MB", "k1", [("k1", KeyOne)]));
        var forMa = cipher.Encrypt("MA", [1, 2, 3]);

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt("MB", forMa));
    }

    [Fact]
    public void Tampering_is_detected()
    {
        var cipher = Cipher(("MB", "k1", [("k1", KeyOne)]));
        var encrypted = cipher.Encrypt("MB", [1, 2, 3]);
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt("MB", encrypted));
    }

    internal static FieldCipher Cipher(params (string Market, string Active, (string Id, string Key)[] Keys)[] markets)
    {
        var options = new EncryptionOptions();
        foreach (var (market, active, keys) in markets)
        {
            options.Markets[market] = new MarketKeyring
            {
                ActiveKeyId = active,
                Keys = keys.ToDictionary(k => k.Id, k => k.Key),
            };
        }

        return new FieldCipher(Options.Create(options));
    }
}

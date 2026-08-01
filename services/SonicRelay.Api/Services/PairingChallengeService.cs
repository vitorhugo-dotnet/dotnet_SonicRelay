using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SonicRelay.Api.Services;

public sealed class PairingChallengeService(IOptions<DeviceIdentityOptions> options, TimeProvider time)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public int MaxAttempts => options.Value.PairingMaxAttempts;

    public string GenerateCode()
    {
        Span<char> span = stackalloc char[8];
        for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(span);
    }

    public string HashCode(string code)
    {
        var key = DeviceCredentialService.RequireKey(
            options.Value.PairingCodeHmacKey, nameof(DeviceIdentityOptions.PairingCodeHmacKey));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(code)));
    }

    public DateTimeOffset NewExpiry() => time.GetUtcNow().AddMinutes(options.Value.PairingCodeTtlMinutes);
}

using System.Security.Cryptography;
using DietTime.Application;

namespace DietTime.Infrastructure;

public sealed class GuestTokenGenerator(
    GuestProfileOptions options,
    TimeProvider clock) : IGuestTokenGenerator
{
    public GuestSessionTokenResult Generate()
    {
        if (options.TokenLengthBytes < 32 || options.TokenLengthBytes > 128)
            throw new InvalidOperationException("Guest token length must be between 32 and 128 bytes.");
        if (options.TokenExpiryDays < 1)
            throw new InvalidOperationException("Guest token expiry must be at least one day.");

        var bytes = RandomNumberGenerator.GetBytes(options.TokenLengthBytes);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new(token, clock.GetUtcNow().AddDays(options.TokenExpiryDays));
    }
}

public sealed class GuestTokenHasher(
    GuestProfileOptions options) : IGuestTokenHasher
{
    public bool IsValidFormat(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 256)
            return false;

        Span<byte> decoded = stackalloc byte[128];
        return TryDecode(rawToken, decoded, out var bytesWritten) &&
               bytesWritten == options.TokenLengthBytes;
    }

    public string Hash(string rawToken)
    {
        if (!IsValidFormat(rawToken))
            throw new ArgumentException("Guest token format is invalid.", nameof(rawToken));
        EnsureSupportedAlgorithm();
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(rawToken)))
            .ToLowerInvariant();
    }

    public bool Verify(string rawToken, string storedHash)
    {
        if (!IsValidFormat(rawToken) ||
            storedHash.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        EnsureSupportedAlgorithm();
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(rawToken), actual);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private void EnsureSupportedAlgorithm()
    {
        if (!options.HashAlgorithm.Equals("SHA256", StringComparison.OrdinalIgnoreCase) &&
            !options.HashAlgorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only SHA256 guest-token hashing is supported.");
        }
    }

    private static bool TryDecode(
        string token,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (token.Any(character =>
                !(character is >= 'A' and <= 'Z' or
                  >= 'a' and <= 'z' or
                  >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        var padding = (4 - token.Length % 4) % 4;
        var base64 = token.Replace('-', '+').Replace('_', '/') + new string('=', padding);
        return Convert.TryFromBase64String(base64, destination, out bytesWritten);
    }
}

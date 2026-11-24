using System.Security.Cryptography;
using System.Text;

namespace MonitorDashboard.Mcp;

public static class McpBearerAuthentication
{
    public static byte[] HashConfiguredToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 512 ||
            token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(
                "MCP is enabled but Dashboard__Mcp__BearerToken is missing or invalid " +
                "(use a randomly generated token of 32-512 characters, without whitespace).");

        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public static bool Matches(string? authorization, ReadOnlySpan<byte> expectedHash)
    {
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var token = authorization[7..];
        if (token.Length is < 32 or > 512 || token.Any(char.IsWhiteSpace))
            return false;

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}

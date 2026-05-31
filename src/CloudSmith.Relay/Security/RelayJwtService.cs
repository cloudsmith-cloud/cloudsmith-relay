// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudSmith.Relay.Security;

/// <summary>
/// Issues and validates per-agent JWTs signed with the relay's RSA-2048 private key (RS256).
///
/// Token claims:
///   sub  — agentId
///   sid  — siteId (may be null)
///   iat  — issued-at (Unix seconds)
///   exp  — expiry (Unix seconds, <see cref="TokenLifetime"/> from iat)
///
/// Tokens are long-lived (<see cref="TokenLifetime"/>) because agents rely on them
/// for every LAN request and there is no refresh flow yet. The relay validates the
/// signature and expiry on every request — the private key never leaves the relay
/// process and is never stored in the JWT itself.
/// </summary>
public sealed class RelayJwtService
{
    /// <summary>Token validity window — 30 days, matching typical deployment cycles.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    private readonly RSA _privateKey;
    private readonly RSA _publicKey;

    /// <summary>
    /// Load the relay's private key from a PEM file at <paramref name="privateKeyPath"/>.
    /// </summary>
    public static RelayJwtService FromPrivateKeyFile(string privateKeyPath)
    {
        var pem = File.ReadAllText(privateKeyPath);
        return FromPrivateKeyPem(pem);
    }

    /// <summary>
    /// Load the relay's private key from a PEM string.
    /// </summary>
    public static RelayJwtService FromPrivateKeyPem(string privateKeyPem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return new RelayJwtService(rsa);
    }

    private RelayJwtService(RSA key)
    {
        _privateKey = key;
        // Export the public key into a separate RSA instance so we never hand
        // out a reference that could be used to sign.
        _publicKey = RSA.Create();
        _publicKey.ImportSubjectPublicKeyInfo(_privateKey.ExportSubjectPublicKeyInfo(), out _);
    }

    /// <summary>
    /// Issue a signed JWT for <paramref name="agentId"/>.
    /// The token is returned as a compact base64url-encoded string (header.payload.signature).
    /// </summary>
    public string IssueToken(string agentId, string? siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = now + (long)TokenLifetime.TotalSeconds;

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(
            """{"alg":"RS256","typ":"JWT"}"""));

        var payloadObj = siteId is not null
            ? new { sub = agentId, sid = siteId, iat = now, exp }
            : (object)new { sub = agentId, iat = now, exp };

        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(payloadObj)));

        var signingInput = Encoding.UTF8.GetBytes($"{header}.{payload}");
        var sig = Base64UrlEncode(_privateKey.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        return $"{header}.{payload}.{sig}";
    }

    /// <summary>
    /// Validate a token previously issued by <see cref="IssueToken"/>.
    /// Returns the <c>sub</c> (agentId) claim on success, or null if the token is
    /// invalid, expired, or has been tampered with.
    /// </summary>
    public string? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            // Verify signature.
            var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
            var sig = Base64UrlDecode(parts[2]);

            if (!_publicKey.VerifyData(
                    signingInput,
                    sig,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                return null;
            }

            // Decode payload.
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            // Check expiry.
            if (root.TryGetProperty("exp", out var expProp))
            {
                var exp = expProp.GetInt64();
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
                    return null; // expired
            }

            // Extract subject.
            if (!root.TryGetProperty("sub", out var subProp)) return null;
            return subProp.GetString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sign an arbitrary byte payload with the relay private key (RS256).
    /// Used by the WebSocket challenge-response handshake.
    /// </summary>
    public byte[] Sign(byte[] data) =>
        _privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        var pad = t.Length % 4 switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(t + pad);
    }
}

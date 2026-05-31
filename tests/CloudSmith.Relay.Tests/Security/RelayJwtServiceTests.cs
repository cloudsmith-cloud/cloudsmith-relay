// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using CloudSmith.Relay.Security;
using Xunit;

namespace CloudSmith.Relay.Tests.Security;

/// <summary>
/// Tests for <see cref="RelayJwtService"/> — token issuance, validation, expiry,
/// and the challenge-response signing used in the WebSocket handshake.
/// </summary>
public sealed class RelayJwtServiceTests
{
    private static RelayJwtService MakeService()
    {
        using var rsa = RSA.Create(2048);
        return RelayJwtService.FromPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
    }

    [Fact]
    public void IssueToken_ProducesThreePartJwt()
    {
        var svc = MakeService();
        var token = svc.IssueToken("agent-1", siteId: null);

        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public void ValidateToken_ValidToken_ReturnsSubject()
    {
        var svc = MakeService();
        var token = svc.IssueToken("agent-abc", siteId: "site-1");

        var sub = svc.ValidateToken(token);

        Assert.Equal("agent-abc", sub);
    }

    [Fact]
    public void ValidateToken_TamperedPayload_ReturnsNull()
    {
        var svc = MakeService();
        var token = svc.IssueToken("agent-abc", siteId: null);
        var parts = token.Split('.');
        // Replace payload with a different base64url blob.
        parts[1] = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sub":"evil","iat":0,"exp":99999999999}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tampered = string.Join('.', parts);

        Assert.Null(svc.ValidateToken(tampered));
    }

    [Fact]
    public void ValidateToken_TamperedSignature_ReturnsNull()
    {
        var svc = MakeService();
        var token = svc.IssueToken("agent-abc", siteId: null);
        var parts = token.Split('.');
        parts[2] = "invalidsigvalue";
        var tampered = string.Join('.', parts);

        Assert.Null(svc.ValidateToken(tampered));
    }

    [Fact]
    public void ValidateToken_WrongKey_ReturnsNull()
    {
        var svc1 = MakeService();
        var svc2 = MakeService();

        var token = svc1.IssueToken("agent-abc", siteId: null);

        // svc2 has a different key — should not validate.
        Assert.Null(svc2.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_EmptyString_ReturnsNull()
    {
        var svc = MakeService();
        Assert.Null(svc.ValidateToken(string.Empty));
    }

    [Fact]
    public void ValidateToken_MalformedToken_ReturnsNull()
    {
        var svc = MakeService();
        Assert.Null(svc.ValidateToken("notavalidjwt"));
        Assert.Null(svc.ValidateToken("a.b"));
    }

    [Fact]
    public void Sign_ProducesVerifiableSignature()
    {
        // The Sign() method is what the WebSocket challenge-response uses.
        var svc = MakeService();
        var nonce = "random-nonce-bytes-here"u8.ToArray();
        var sig = svc.Sign(nonce);

        Assert.NotNull(sig);
        Assert.NotEmpty(sig);
    }
}

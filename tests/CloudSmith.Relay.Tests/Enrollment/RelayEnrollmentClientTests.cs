// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using CloudSmith.Relay.Enrollment;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSmith.Relay.Tests.Enrollment;

public sealed class RelayEnrollmentClientTests : IDisposable
{
    private readonly string _tmpDir;

    public RelayEnrollmentClientTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "csr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task EnrollAsync_PostsTokenAndPersistsIdentity()
    {
        // Arrange
        string? capturedBody = null;
        Uri? capturedUri = null;

        var handler = new StubHandler(async (req, ct) =>
        {
            capturedUri = req.RequestUri;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"relayId":"relay-abc-123","paasUrl":"https://paas.example.com"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var http = new HttpClient(handler);

        var client = new RelayEnrollmentClient(
            http,
            NullLogger<RelayEnrollmentClient>.Instance,
            "https://paas.example.com",
            _tmpDir);

        // Act
        var result = await client.EnrollAsync("one-time-token", "test-relay", null, CancellationToken.None);

        // Assert — response parsed correctly
        Assert.Equal("relay-abc-123", result.RelayId);
        Assert.Equal("https://paas.example.com", result.PaasUrl);

        // Endpoint shape
        Assert.NotNull(capturedUri);
        Assert.Equal("https://paas.example.com/api/v1/relays/enroll", capturedUri!.AbsoluteUri);

        // Body carried token + displayName + a PEM public key
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("one-time-token", doc.RootElement.GetProperty("token").GetString());
        Assert.Equal("test-relay", doc.RootElement.GetProperty("displayName").GetString());
        var pem = doc.RootElement.GetProperty("publicKeyPem").GetString();
        Assert.NotNull(pem);
        Assert.Contains("BEGIN PUBLIC KEY", pem);

        // Disk artifacts
        Assert.True(File.Exists(Path.Combine(_tmpDir, "relay.key")), "private key not persisted");
        Assert.True(File.Exists(Path.Combine(_tmpDir, "identity.json")), "identity.json not persisted");

        var keyText = await File.ReadAllTextAsync(Path.Combine(_tmpDir, "relay.key"));
        Assert.Contains("BEGIN PRIVATE KEY", keyText);

        var loaded = client.TryLoadIdentity();
        Assert.NotNull(loaded);
        Assert.Equal("relay-abc-123", loaded!.RelayId);
        Assert.Equal("test-relay", loaded.DisplayName);
        Assert.True(client.HasPersistedIdentity());
    }

    [Fact]
    public async Task EnrollAsync_ServerError_ThrowsInvalidOperation()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid token", Encoding.UTF8, "text/plain"),
        }));
        using var http = new HttpClient(handler);
        var client = new RelayEnrollmentClient(
            http, NullLogger<RelayEnrollmentClient>.Instance,
            "https://paas.example.com", _tmpDir);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnrollAsync("bad", "test", null, CancellationToken.None));
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task EnrollAsync_WithSiteId_IncludesSiteIdInPayload()
    {
        // Arrange
        string? capturedBody = null;

        var handler = new StubHandler(async (req, ct) =>
        {
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"relayId":"relay-site-test","paasUrl":"https://paas.example.com"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var client = new RelayEnrollmentClient(
            http, NullLogger<RelayEnrollmentClient>.Instance,
            "https://paas.example.com", _tmpDir);

        // Act
        await client.EnrollAsync("enroll-tok", "test-relay", "site-abc-123", CancellationToken.None);

        // Assert — siteId appears in the request body
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("site-abc-123", doc.RootElement.GetProperty("siteId").GetString());
    }

    [Fact]
    public async Task EnrollAsync_NullSiteId_OmitsSiteIdFromPayload()
    {
        // Arrange
        string? capturedBody = null;

        var handler = new StubHandler(async (req, ct) =>
        {
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"relayId":"relay-no-site","paasUrl":"https://paas.example.com"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var client = new RelayEnrollmentClient(
            http, NullLogger<RelayEnrollmentClient>.Instance,
            "https://paas.example.com", _tmpDir);

        // Act
        await client.EnrollAsync("enroll-tok", "test-relay", null, CancellationToken.None);

        // Assert — siteId is absent (serialized with WhenWritingNull)
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("siteId", out _),
            "siteId should be absent when not configured");
    }

    [Fact]
    public async Task EnrollAsync_NullToken_Throws()
    {
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var client = new RelayEnrollmentClient(
            http, NullLogger<RelayEnrollmentClient>.Instance,
            "https://paas.example.com", _tmpDir);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.EnrollAsync("", "test", null, CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => impl(request, cancellationToken);
    }
}

// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Enrollment;

/// <summary>
/// HTTPS-based enrollment client. Generates an RSA-2048 keypair, POSTs the public
/// key + one-time token to <c>{paasUrl}/api/v1/relays/enroll</c>, and persists the
/// resulting RelayId + private key under <see cref="IdentityDirectory"/>.
/// </summary>
public sealed class RelayEnrollmentClient(
    HttpClient http,
    ILogger<RelayEnrollmentClient> logger,
    string paasUrl,
    string identityDirectory) : IRelayEnrollmentClient
{
    /// <summary>Default on-disk identity directory inside the Relay container.</summary>
    public const string DefaultIdentityDirectory = "/var/lib/cloudsmith-relay/identity";

    private const string PrivateKeyFile = "relay.key";
    private const string IdentityFile = "identity.json";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public string PaasUrl { get; } = paasUrl.TrimEnd('/');
    public string IdentityDirectory { get; } = identityDirectory;

    /// <summary>True iff an identity has already been persisted on disk.</summary>
    public bool HasPersistedIdentity()
        => File.Exists(Path.Combine(IdentityDirectory, IdentityFile))
        && File.Exists(Path.Combine(IdentityDirectory, PrivateKeyFile));

    /// <summary>Load the persisted identity (or null if not enrolled).</summary>
    public RelayIdentity? TryLoadIdentity()
    {
        var path = Path.Combine(IdentityDirectory, IdentityFile);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RelayIdentity>(json, JsonOpts);
    }

    public async Task<EnrollmentResult> EnrollAsync(string token, string displayName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Directory.CreateDirectory(IdentityDirectory);

        logger.LogInformation("Enrolling Relay '{DisplayName}' with PaaS {PaasUrl}", displayName, PaasUrl);

        // 1. Generate RSA 2048 keypair.
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();

        // 2. POST enrollment.
        var endpoint = $"{PaasUrl}/api/v1/relays/enroll";
        var request = new EnrollRequest(token, displayName, publicKeyPem);
        using var resp = await http.PostAsJsonAsync(endpoint, request, JsonOpts, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Relay enrollment failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }

        var enrolled = await resp.Content.ReadFromJsonAsync<EnrollResponse>(JsonOpts, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Enrollment response body was empty.");

        if (string.IsNullOrWhiteSpace(enrolled.RelayId))
            throw new InvalidOperationException("Enrollment response missing relay_id.");

        // 3. Persist private key + identity, chmod 600 on POSIX.
        var keyPath = Path.Combine(IdentityDirectory, PrivateKeyFile);
        await File.WriteAllTextAsync(keyPath, privateKeyPem, ct).ConfigureAwait(false);
        TryChmod600(keyPath);

        var identity = new RelayIdentity(enrolled.RelayId, PaasUrl, displayName, DateTimeOffset.UtcNow);
        var identityPath = Path.Combine(IdentityDirectory, IdentityFile);
        await File.WriteAllTextAsync(identityPath, JsonSerializer.Serialize(identity, JsonOpts), ct).ConfigureAwait(false);
        TryChmod600(identityPath);

        logger.LogInformation("Relay enrolled: RelayId={RelayId}", enrolled.RelayId);
        return new EnrollmentResult(enrolled.RelayId, PaasUrl);
    }

    private static void TryChmod600(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best-effort — non-fatal on filesystems that don't support chmod.
        }
    }

    internal sealed record EnrollRequest(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("publicKeyPem")] string PublicKeyPem);

    internal sealed record EnrollResponse(
        [property: JsonPropertyName("relayId")] string RelayId,
        [property: JsonPropertyName("paasUrl")] string? PaasUrl);
}

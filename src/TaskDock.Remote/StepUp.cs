using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace TaskDock.Remote;

/// <summary>
/// Fingerprint checks bound to one request. The challenge is SHA-256(nonce ‖ approvalId ‖ SHA-256(tool_input) ‖ choice):
/// a signature for Allow on one approval is useless for any other approval or choice. Nonces are single use and last 60 s.
/// </summary>
sealed class StepUp(Func<IFido2> fido, DeviceStore devices, TimeProvider time)
{
    static readonly TimeSpan Life = TimeSpan.FromSeconds(60);

    sealed record Pending(string DeviceId, string ApprovalId, string Choice, string InputHash, string OptionsJson, DateTimeOffset Expires);

    readonly ConcurrentDictionary<string, Pending> _pending = new(); // key: challenge, base64url

    public static string Hash(string toolInput) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(toolInput)));

    /// <summary>Assertion options for this device's passkey, bound to one approval, its input and one choice.</summary>
    public string Options(Device device, string approvalId, string toolInput, string choice)
    {
        Sweep();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var inputHash = Hash(toolInput);
        var challenge = SHA256.HashData(Concat(nonce, approvalId, inputHash, choice));

        var o = fido().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [new PublicKeyCredentialDescriptor(Decode(device.Credential!.Id))],
            UserVerification = UserVerificationRequirement.Required,
        });
        o.Challenge = challenge;
        var json = o.ToJson();
        _pending[DeviceStore.Base64Url(challenge)] = new(device.Id, approvalId, choice, inputHash, json, time.GetUtcNow() + Life);
        return json;
    }

    /// <summary>True only for a fresh, user-verified signature by this device's passkey over exactly this request. Spends the nonce.</summary>
    public async Task<bool> Verify(Device device, string approvalId, string toolInput, string choice, JsonElement assertion)
    {
        AuthenticatorAssertionRawResponse raw;
        try { raw = assertion.Deserialize<AuthenticatorAssertionRawResponse>() ?? throw new JsonException(); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return false; }

        string challenge;
        try { challenge = JsonDocument.Parse(raw.Response.ClientDataJson).RootElement.GetProperty("challenge").GetString() ?? ""; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException) { return false; }

        // Spent whatever happens next: a nonce never gets a second try.
        if (!_pending.TryRemove(challenge, out var p)) return false;
        if (p.DeviceId != device.Id || p.ApprovalId != approvalId || p.Choice != choice || p.InputHash != Hash(toolInput)
            || time.GetUtcNow() > p.Expires || device.Credential is not { } cred) return false;

        try
        {
            var r = await fido().MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = raw,
                OriginalOptions = AssertionOptions.FromJson(p.OptionsJson),
                StoredPublicKey = Decode(cred.PublicKey),
                StoredSignatureCounter = cred.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true), // the credential ID is already pinned to this device
            });
            if (!raw.Id.Equals(cred.Id, StringComparison.Ordinal)) return false;
            devices.SetSignCount(device, r.SignCount);
            return true;
        }
        catch (Fido2VerificationException) { return false; }
    }

    void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (k, v) in _pending) if (now > v.Expires) _pending.TryRemove(k, out _);
    }

    static byte[] Concat(byte[] nonce, params string[] parts) => [.. nonce, .. parts.SelectMany(s => Encoding.UTF8.GetBytes("\n" + s))];

    static byte[] Decode(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}

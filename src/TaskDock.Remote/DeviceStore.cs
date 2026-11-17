using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskDock.Remote;

public sealed class StoredCredential
{
    public required string Id { get; init; }          // base64url
    public required string PublicKey { get; init; }   // base64url COSE key
    public uint SignCount { get; set; }
}

public sealed class Device
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string TokenSha256 { get; init; }
    public string Status { get; set; } = "pending"; // pending | active
    /// <summary>The Tailscale login that paired it; the first activated device's login becomes the owner.</summary>
    public string? Login { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public StoredCredential? Credential { get; set; }
    /// <summary>This phone's Web Push subscription, once it allowed notifications.</summary>
    public PushTarget? Push { get; set; }
    [JsonIgnore] public bool IsActive => Status == "active";
}

public sealed record PushTarget(string Endpoint, string P256dh, string Auth);

/// <summary>
/// %LOCALAPPDATA%\TaskDock\remote\devices.json and remote.json, written atomically. Local, not roaming: devices belong
/// to this PC. Stores only SHA-256 of each token.
/// </summary>
public sealed class DeviceStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    sealed class RemoteFile
    {
        public string? OwnerLogin { get; set; }
        public string? VapidPublic { get; set; }  // base64url, uncompressed P-256 point
        public string? VapidPrivate { get; set; } // base64url; same trust as hub.json's token (only this Windows user can read it)
    }

    readonly string _dir;
    readonly Lock _lock = new();
    readonly List<Device> _devices;
    readonly RemoteFile _remote;

    public DeviceStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _devices = Read<List<Device>>("devices.json") ?? [];
        _devices.RemoveAll(d => !d.IsActive); // a pairing interrupted by a restart never finished
        _remote = Read<RemoteFile>("remote.json") ?? new();
    }

    public string? OwnerLogin { get { lock (_lock) return _remote.OwnerLogin; } }

    public IReadOnlyList<Device> Devices { get { lock (_lock) return _devices.ToList(); } }

    /// <summary>Returns the raw token once; only its hash is kept.</summary>
    public (Device Device, string Token) AddPending(string name, string? login, DateTimeOffset now)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var d = new Device
        {
            Id = Base64Url(RandomNumberGenerator.GetBytes(9)), Name = name, TokenSha256 = Hash(token), Login = login,
            CreatedAt = now, LastSeenAt = now,
        };
        lock (_lock) _devices.Add(d); // pending devices live in memory until activated
        return (d, token);
    }

    /// <summary>The device this bearer token belongs to (pending or active), compared in fixed time.</summary>
    public Device? ByToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var h = Encoding.ASCII.GetBytes(Hash(token));
        lock (_lock)
            return _devices.FirstOrDefault(d => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenSha256), h));
    }

    public void Activate(Device d, StoredCredential credential)
    {
        lock (_lock)
        {
            d.Credential = credential;
            d.Status = "active";
            _remote.OwnerLogin ??= d.Login;
            Save();
        }
    }

    /// <summary>This PC's VAPID key pair for Web Push, made the first time it's needed.</summary>
    public (string Public, string Private) Vapid()
    {
        lock (_lock)
        {
            if (_remote is { VapidPublic: { } pub, VapidPrivate: { } priv }) return (pub, priv);
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = ec.ExportParameters(true);
            (_remote.VapidPublic, _remote.VapidPrivate) = (Base64Url([0x04, .. p.Q.X!, .. p.Q.Y!]), Base64Url(p.D!));
            Save();
            return (_remote.VapidPublic, _remote.VapidPrivate);
        }
    }

    public void SetPush(Device d, PushTarget? push)
    {
        lock (_lock)
        {
            d.Push = push;
            if (d.IsActive) Save();
        }
    }

    public void SetSignCount(Device d, uint count)
    {
        lock (_lock)
        {
            if (d.Credential is not { } c || c.SignCount == count) return;
            c.SignCount = count;
            Save();
        }
    }

    public void Touch(Device d, DateTimeOffset now)
    {
        lock (_lock)
        {
            d.LastSeenAt = now;
            if (d.IsActive) Save();
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_lock)
        {
            var n = _devices.RemoveAll(d => d.Id == deviceId);
            if (n > 0) Save();
            return n > 0;
        }
    }

    void Save()
    {
        Write("devices.json", JsonSerializer.Serialize(_devices.Where(d => d.IsActive).ToList(), Json));
        Write("remote.json", JsonSerializer.Serialize(_remote, Json));
    }

    T? Read<T>(string file) where T : class
    {
        var path = Path.Combine(_dir, file);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch (JsonException) { return null; } // corrupt: the phones re-pair rather than the widget failing to start
    }

    void Write(string file, string text)
    {
        var path = Path.Combine(_dir, file);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

using System.Security.Cryptography;
using System.Text.Json;

namespace CodexPhoneReminder.Agent;

public sealed class PairingService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (PairedDevice Device, string Hash)> _devices = [];
    private string? _code; private DateTimeOffset _expires;
    private readonly TransportIdentity _transport;
    private readonly string _devicesPath;
    public PairingService(IWebHostEnvironment env, TransportIdentity transport)
    {
        _transport = transport;
        var dir = Path.Combine(env.ContentRootPath, "data"); Directory.CreateDirectory(dir);
        _devicesPath = Path.Combine(dir, "paired-devices.json");
        LoadDevices();
    }
    public PairOffer CreateOffer(string address) { lock (_gate) { _code = RandomNumberGenerator.GetInt32(100000, 999999).ToString(); _expires = DateTimeOffset.UtcNow.AddMinutes(5); return new(address, _code, Fingerprint(), _expires); } }
    public PairResult? Claim(string code)
    {
        lock (_gate)
        {
            if (_code is null || DateTimeOffset.UtcNow > _expires || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(_code), System.Text.Encoding.UTF8.GetBytes(code))) return null;
            _code = null; var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var id = Guid.NewGuid().ToString("N");
            var d = new PairedDevice(id, "Codex Phone Client", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); _devices[id] = (d, Hash(token)); SaveDevices();
            return new(token, d, Fingerprint());
        }
    }
    public bool Validate(string token) { lock (_gate) return _devices.Values.Any(x => !x.Device.Revoked && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(x.Hash), Convert.FromHexString(Hash(token)))); }
    public object Devices() { lock (_gate) return _devices.Values.Select(x => x.Device).ToArray(); }
    public bool Revoke(string id) { lock (_gate) { if (!_devices.TryGetValue(id, out var v)) return false; _devices[id] = (v.Device with { Revoked = true }, v.Hash); SaveDevices(); return true; } }
    private void LoadDevices()
    {
        if (!File.Exists(_devicesPath)) return;
        try
        {
            var records = JsonSerializer.Deserialize<List<DeviceRecord>>(File.ReadAllText(_devicesPath)) ?? [];
            foreach (var record in records) _devices[record.Device.Id] = (record.Device, record.Hash);
        }
        catch { }
    }
    private void SaveDevices()
    {
        var records = _devices.Values.Select(x => new DeviceRecord(x.Device, x.Hash)).ToArray();
        var temp = _devicesPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _devicesPath, true);
    }
    private string Fingerprint() => _transport.Fingerprint;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private sealed record DeviceRecord(PairedDevice Device, string Hash);
}

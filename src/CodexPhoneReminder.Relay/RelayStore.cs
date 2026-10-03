using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CodexPhoneReminder.Relay;

/// <summary>
/// Small, durable queue for encrypted request envelopes. It deliberately has no knowledge of
/// Codex tasks, commands, plaintext, or encryption algorithms.
/// </summary>
public sealed class RelayStore
{
    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly RelayOptions _options;
    private readonly TimeProvider _clock;
    private readonly string _statePath;
    private RelayState _state;
    private TaskCompletionSource<bool> _changed = NewSignal();

    public RelayStore(IHostEnvironment environment, IOptions<RelayOptions> options)
        : this(ResolveStatePath(environment.ContentRootPath, options.Value.StatePath), options.Value)
    {
    }

    // Public for the no-package executable test project and for operators that want a local smoke test.
    public RelayStore(string statePath, RelayOptions options, TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _clock = clock ?? TimeProvider.System;
        _statePath = Path.GetFullPath(statePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        _state = Load(_statePath);
        lock (_gate)
        {
            NormalizeStateLocked();
            PruneLocked(UtcNow());
        }
    }

    public RegisterAgentResponse RegisterAgent(string agentId)
    {
        ValidateIdentifier(agentId, "agentId");
        lock (_gate)
        {
            var now = UtcNow();
            PruneLocked(now);
            var token = CreateToken();
            var existing = _state.Agents.SingleOrDefault(x => x.AgentId == agentId);
            if (existing is null)
            {
                _state.Agents.Add(new RelayAgentRecord
                {
                    AgentId = agentId,
                    TokenHash = HashToken("agent", agentId, null, token),
                    RegisteredAt = now,
                    LastSeenAt = now
                });
            }
            else
            {
                // Re-registration is intentionally a token rotation. The bootstrap secret is the
                // recovery authority, and no prior token is ever written to disk.
                existing.TokenHash = HashToken("agent", agentId, null, token);
                existing.LastSeenAt = now;
            }

            SaveAndNotifyLocked();
            return new RegisterAgentResponse(token);
        }
    }

    public EnrollDeviceResponse EnrollDevice(string agentId, string deviceId)
    {
        ValidateIdentifier(agentId, "agentId");
        ValidateIdentifier(deviceId, "deviceId");
        lock (_gate)
        {
            var now = UtcNow();
            PruneLocked(now);
            if (_state.Agents.All(x => x.AgentId != agentId))
                throw new RelayNotFoundException("Agent does not exist.");

            var token = CreateToken();
            var existing = _state.Devices.SingleOrDefault(x => x.AgentId == agentId && x.DeviceId == deviceId);
            if (existing is null)
            {
                _state.Devices.Add(new RelayDeviceRecord
                {
                    AgentId = agentId,
                    DeviceId = deviceId,
                    TokenHash = HashToken("device", agentId, deviceId, token),
                    EnrolledAt = now,
                    LastSeenAt = now
                });
            }
            else
            {
                // Agent-side re-enrolment deliberately rotates the opaque device bearer token.
                existing.TokenHash = HashToken("device", agentId, deviceId, token);
                existing.LastSeenAt = now;
            }

            SaveAndNotifyLocked();
            return new EnrollDeviceResponse(token);
        }
    }

    public bool TryAuthenticateAgent(string agentId, string? token, out AgentPrincipal? principal)
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;
        lock (_gate)
        {
            var agent = _state.Agents.SingleOrDefault(x => x.AgentId == agentId);
            if (agent is null || !HashEquals(agent.TokenHash, HashToken("agent", agentId, null, token))) return false;
            agent.LastSeenAt = UtcNow();
            principal = new AgentPrincipal(agent.AgentId);
            return true;
        }
    }

    public bool TryAuthenticateDevice(string agentId, string? token, out DevicePrincipal? principal)
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;
        lock (_gate)
        {
            var device = _state.Devices.FirstOrDefault(x =>
                x.AgentId == agentId && HashEquals(x.TokenHash, HashToken("device", agentId, x.DeviceId, token)));
            if (device is null) return false;
            device.LastSeenAt = UtcNow();
            principal = new DevicePrincipal(device.AgentId, device.DeviceId);
            return true;
        }
    }

    public SubmitRelayResponse SubmitRequest(DevicePrincipal device, SubmitRelayRequest request)
    {
        ArgumentNullException.ThrowIfNull(device);
        ValidateEnvelope(request.Nonce, request.Ciphertext);
        lock (_gate)
        {
            var now = UtcNow();
            PruneLocked(now);
            var duplicate = _state.Requests.SingleOrDefault(x =>
                x.AgentId == device.AgentId && x.DeviceId == device.DeviceId && x.Nonce == request.Nonce);
            if (duplicate is not null)
            {
                if (!string.Equals(duplicate.Ciphertext, request.Ciphertext, StringComparison.Ordinal))
                    throw new RelayConflictException("A request with this nonce already exists with different ciphertext.");
                return new SubmitRelayResponse(duplicate.RequestId);
            }
            var pendingCount = _state.Requests.Count(x => x.AgentId == device.AgentId && x.DeviceId == device.DeviceId &&
                                                        x.ResponseStatusCode is null && x.ExpiresAt > now);
            if (pendingCount >= _options.MaxPendingRequestsPerDevice)
                throw new RelayQueueFullException("Too many pending requests for this device. Try again shortly.");

            var record = new RelayRequestRecord
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentId = device.AgentId,
                DeviceId = device.DeviceId,
                Nonce = request.Nonce,
                Ciphertext = request.Ciphertext,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(_options.RequestLifetimeMinutes)
            };
            _state.Requests.Add(record);
            SaveAndNotifyLocked();
            return new SubmitRelayResponse(record.RequestId);
        }
    }

    public async Task<PendingRelayRequest?> PollAgentAsync(string agentId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = UtcNow().Add(wait);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task signal;
            lock (_gate)
            {
                var now = UtcNow();
                var record = _state.Requests
                    .Where(x => x.AgentId == agentId && x.ResponseStatusCode is null && x.ExpiresAt > now)
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.RequestId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (record is not null)
                    return new PendingRelayRequest(record.RequestId, record.DeviceId, new EncryptedEnvelope(record.Nonce, record.Ciphertext));
                signal = _changed.Task;
            }

            var remaining = deadline - UtcNow();
            if (remaining <= TimeSpan.Zero) return null;
            await WaitForChangeAsync(signal, remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    public void SubmitResponse(AgentPrincipal agent, string requestId, SubmitRelayResult result)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ValidateIdentifier(requestId, "requestId");
        ValidateStatusCode(result.StatusCode);
        ValidateEnvelope(result.Response.Nonce, result.Response.Ciphertext);
        lock (_gate)
        {
            var now = UtcNow();
            PruneLocked(now);
            var record = _state.Requests.SingleOrDefault(x => x.AgentId == agent.AgentId && x.RequestId == requestId);
            if (record is null || record.ExpiresAt <= now && record.ResponseStatusCode is null)
                throw new RelayNotFoundException("Request does not exist or has expired.");

            if (record.ResponseStatusCode is not null)
            {
                if (record.ResponseStatusCode == result.StatusCode &&
                    string.Equals(record.ResponseNonce, result.Response.Nonce, StringComparison.Ordinal) &&
                    string.Equals(record.ResponseCiphertext, result.Response.Ciphertext, StringComparison.Ordinal))
                    return; // idempotent retry after a lost 204 response
                throw new RelayConflictException("A different response already exists for this request.");
            }

            record.ResponseStatusCode = result.StatusCode;
            record.ResponseNonce = result.Response.Nonce;
            record.ResponseCiphertext = result.Response.Ciphertext;
            record.CompletedAt = now;
            record.ResultExpiresAt = now.AddMinutes(_options.ResultRetentionMinutes);
            SaveAndNotifyLocked();
        }
    }

    public async Task<RelayResult?> PollResultAsync(DevicePrincipal device, string requestId, TimeSpan wait, CancellationToken cancellationToken)
    {
        ValidateIdentifier(requestId, "requestId");
        var deadline = UtcNow().Add(wait);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task signal;
            lock (_gate)
            {
                var now = UtcNow();
                var record = _state.Requests.SingleOrDefault(x => x.AgentId == device.AgentId && x.RequestId == requestId);
                if (record is null || record.DeviceId != device.DeviceId || IsGone(record, now))
                    throw new RelayNotFoundException("Request does not exist, has expired, or is not owned by this device.");
                if (record.ResponseStatusCode is not null)
                    return new RelayResult(record.ResponseStatusCode.Value, new EncryptedEnvelope(record.ResponseNonce!, record.ResponseCiphertext!));
                signal = _changed.Task;
            }

            var remaining = deadline - UtcNow();
            if (remaining <= TimeSpan.Zero) return null;
            await WaitForChangeAsync(signal, remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForChangeAsync(Task signal, TimeSpan remaining, CancellationToken cancellationToken)
    {
        var delay = Task.Delay(remaining, cancellationToken);
        var completed = await Task.WhenAny(signal, delay).ConfigureAwait(false);
        if (completed == delay) await delay.ConfigureAwait(false);
    }

    private void SaveAndNotifyLocked()
    {
        PruneLocked(UtcNow());
        SaveLocked();
        var prior = _changed;
        _changed = NewSignal();
        prior.TrySetResult(true);
    }

    private void PruneLocked(DateTimeOffset now)
    {
        _state.Requests.RemoveAll(x => IsGone(x, now));
    }

    private static bool IsGone(RelayRequestRecord record, DateTimeOffset now) =>
        record.ResponseStatusCode is null ? record.ExpiresAt <= now : record.ResultExpiresAt is not null && record.ResultExpiresAt <= now;

    private void SaveLocked()
    {
        var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, StateJson));
            File.Move(temporaryPath, _statePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static RelayState Load(string path)
    {
        if (!File.Exists(path)) return new RelayState();
        try
        {
            return JsonSerializer.Deserialize<RelayState>(File.ReadAllText(path))
                ?? throw new InvalidOperationException("Relay state file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Relay state file '{path}' is not valid JSON. Restore a backup instead of overwriting it.", ex);
        }
    }

    private void NormalizeStateLocked()
    {
        _state.Agents ??= [];
        _state.Devices ??= [];
        _state.Requests ??= [];
        if (_state.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported relay state schema version {_state.SchemaVersion}.");
    }

    private DateTimeOffset UtcNow() => _clock.GetUtcNow();

    private void ValidateEnvelope(string? nonce, string? ciphertext)
    {
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(ciphertext))
            throw new RelayValidationException("nonce and ciphertext are required.");
        if (nonce.Length > _options.MaxEnvelopeBytes || ciphertext.Length > _options.MaxEnvelopeBytes ||
            Encoding.UTF8.GetByteCount(nonce) + Encoding.UTF8.GetByteCount(ciphertext) > _options.MaxEnvelopeBytes)
            throw new RelayValidationException($"Encrypted envelope exceeds {_options.MaxEnvelopeBytes} bytes.");
    }

    private static void ValidateStatusCode(int statusCode)
    {
        if (statusCode is < 100 or > 599)
            throw new RelayValidationException("statusCode must be an HTTP status code between 100 and 599.");
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new RelayValidationException($"{name} must contain 1-128 ASCII letters, digits, '.', '_' or '-'.");
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string kind, string agentId, string? deviceId, string token)
    {
        var material = Encoding.UTF8.GetBytes($"{kind}\0{agentId}\0{deviceId ?? string.Empty}\0{token}");
        return Convert.ToHexString(SHA256.HashData(material));
    }

    private static bool HashEquals(string expectedHex, string actualHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHex), Convert.FromHexString(actualHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ResolveStatePath(string contentRoot, string statePath) =>
        Path.IsPathRooted(statePath) ? statePath : Path.Combine(contentRoot, statePath);
}

public sealed class RelayState
{
    public int SchemaVersion { get; set; } = 1;
    public List<RelayAgentRecord> Agents { get; set; } = [];
    public List<RelayDeviceRecord> Devices { get; set; } = [];
    public List<RelayRequestRecord> Requests { get; set; } = [];
}

public sealed class RelayAgentRecord
{
    public string AgentId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class RelayDeviceRecord
{
    public string AgentId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset EnrolledAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class RelayRequestRecord
{
    public string RequestId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string Ciphertext { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseNonce { get; set; }
    public string? ResponseCiphertext { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ResultExpiresAt { get; set; }
}

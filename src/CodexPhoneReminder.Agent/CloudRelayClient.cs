using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace CodexPhoneReminder.Agent;

/// <summary>
/// Maintains the Agent's outbound-only connection to a self-hosted relay.
/// It deliberately never exposes the local Agent listener to the public network.
/// </summary>
public sealed class CloudRelayClient : IDisposable
{
    private const string AgentTokenHeader = "X-Relay-Agent-Token";
    private const string DeviceTokenHeader = "X-Relay-Device-Token";
    private const string BootstrapHeader = "X-Relay-Bootstrap";
    private const string InternalHeader = "X-Codex-Relay-Internal";
    private const string InternalDeviceHeader = "X-Codex-Relay-Device-Id";
    private const int MaximumForwardedBodyBytes = 524_288;

    private readonly CloudRelayOptions _options;
    private readonly PairingService _pairing;
    private readonly TransportIdentity _transport;
    private readonly ILogger<CloudRelayClient> _logger;
    private readonly Uri? _relayBaseUri;
    private readonly HttpClient _relayHttp;
    private readonly HttpClient _loopbackHttp;
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _outboxGate = new();
    private readonly string _identityPath;
    private readonly string _outboxPath;
    private readonly string _internalSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private string _agentId;
    private string? _agentToken;
    private DateTimeOffset? _lastConnectedAt;
    private string? _lastError;
    private string _state;
    private Dictionary<string, RelayResponseOutboxEntry> _outbox = new(StringComparer.Ordinal);

    public CloudRelayClient(
        IOptions<CloudRelayOptions> options,
        PairingService pairing,
        TransportIdentity transport,
        IWebHostEnvironment environment,
        ILogger<CloudRelayClient> logger,
        IConfiguration configuration)
    {
        _options = options.Value;
        _pairing = pairing;
        _transport = transport;
        _logger = logger;
        _state = "disabled";

        var dataDirectory = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(dataDirectory);
        _identityPath = Path.Combine(dataDirectory, "cloud-relay-identity.json");
        _outboxPath = Path.Combine(dataDirectory, "cloud-relay-outbox.json");
        var saved = LoadIdentity();
        _agentId = string.IsNullOrWhiteSpace(_options.AgentId) ? saved?.AgentId ?? Guid.NewGuid().ToString("N") : _options.AgentId.Trim();
        _agentToken = saved?.AgentId == _agentId ? saved.AgentToken : null;
        SaveIdentity();
        LoadOutbox();

        if (Uri.TryCreate(_options.Url.Trim(), UriKind.Absolute, out var relayUri) && relayUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _relayBaseUri = new Uri(relayUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        }
        else if (!string.IsNullOrWhiteSpace(_options.Url))
        {
            _state = "configuration-error";
            _lastError = "Relay:Url 必须是 https 地址";
        }

        if (_relayBaseUri is not null && string.IsNullOrWhiteSpace(_options.BootstrapKey))
        {
            _state = "configuration-error";
            _lastError = "未设置 Relay:BootstrapKey";
        }

        _relayHttp = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            Timeout = TimeSpan.FromSeconds(40)
        };
        var httpsPort = configuration.GetValue("Agent:HttpsPort", 5188);
        var localHandler = new SocketsHttpHandler
        {
            // The local dispatch is never allowed to enter a configured HTTP/
            // SOCKS proxy.  It must stay on the Agent's loopback interface.
            UseProxy = false,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                    return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(_transport.Fingerprint));
                }
            }
        };
        _loopbackHttp = new HttpClient(localHandler)
        {
            BaseAddress = new Uri($"https://localhost:{httpsPort}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(75)
        };
    }

    public bool Enabled => _relayBaseUri is not null && !string.IsNullOrWhiteSpace(_options.BootstrapKey);
    public string AgentId => _agentId;

    public CloudRelayStatus Status
    {
        get
        {
            lock (_stateGate) return new(Enabled, _state, Enabled ? _agentId : null, _lastConnectedAt, _lastError);
        }
    }

    public bool IsInternalRelayRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return false;
        if (!context.Request.Headers.TryGetValue(InternalHeader, out var receivedSecret) ||
            !context.Request.Headers.TryGetValue(InternalDeviceHeader, out var deviceId)) return false;
        return CryptographicOperations.FixedTimeEquals(
                   Encoding.UTF8.GetBytes(receivedSecret.ToString()), Encoding.UTF8.GetBytes(_internalSecret)) &&
               _pairing.IsActive(deviceId.ToString());
    }

    /// <summary>Creates a relay enrollment only after a local, pinned-TLS pairing succeeds.</summary>
    public async Task<RelayPairing?> EnrollDeviceAsync(PairResult pairingResult, CancellationToken cancellationToken)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(pairingResult.RelayKey)) return null;
        if (!await EnsureRegisteredAsync(cancellationToken)) return null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = CreateRequest(HttpMethod.Post, $"api/v1/agents/{Uri.EscapeDataString(_agentId)}/devices/enroll", _agentToken, AgentTokenHeader);
            request.Content = JsonContent.Create(new { deviceId = pairingResult.Device.Id });
            using var response = await _relayHttp.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                ClearAgentToken();
                if (attempt == 0 && await EnsureRegisteredAsync(cancellationToken)) continue;
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                SetFailure($"设备注册失败（HTTP {(int)response.StatusCode}）");
                return null;
            }
            var enrollment = await response.Content.ReadFromJsonAsync<RelayDeviceEnrollment>(_json, cancellationToken);
            if (string.IsNullOrWhiteSpace(enrollment?.DeviceToken))
            {
                SetFailure("中继返回了无效的设备凭据");
                return null;
            }
            SetConnected();
            return new RelayPairing(_relayBaseUri!.AbsoluteUri.TrimEnd('/'), _agentId, pairingResult.Device.Id,
                enrollment.DeviceToken, pairingResult.RelayKey);
        }
        return null;
    }

    /// <summary>Performs one long-poll and, when present, securely forwards that one request locally.</summary>
    public async Task<bool> PollAndProcessAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return true;
        if (!await EnsureRegisteredAsync(cancellationToken)) return false;

        var waitSeconds = Math.Clamp(_options.PollWaitSeconds, 1, 25);
        using var request = CreateRequest(HttpMethod.Get,
            $"api/v1/agents/{Uri.EscapeDataString(_agentId)}/requests?waitSeconds={waitSeconds}", _agentToken, AgentTokenHeader);
        using var response = await _relayHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            SetConnected();
            return true;
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ClearAgentToken();
            SetFailure("中继要求重新注册");
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            SetFailure($"中继轮询失败（HTTP {(int)response.StatusCode}）");
            return false;
        }

        var delivery = await response.Content.ReadFromJsonAsync<RelayDelivery>(_json, cancellationToken);
        if (delivery is null || string.IsNullOrWhiteSpace(delivery.RequestId) || string.IsNullOrWhiteSpace(delivery.DeviceId) || delivery.Request is null)
        {
            SetFailure("中继返回了无效请求");
            return false;
        }
        SetConnected();
        return await ProcessDeliveryAsync(delivery, cancellationToken);
    }

    private async Task<bool> ProcessDeliveryAsync(RelayDelivery delivery, CancellationToken cancellationToken)
    {
        if (!_pairing.TryGetRelayKey(delivery.DeviceId, out var key))
        {
            // A revoked device cannot invoke local APIs.  The relay expires this opaque request by TTL.
            _logger.LogWarning("Ignored relay request for revoked or unknown device {DeviceId}", delivery.DeviceId);
            return false;
        }

        // A lost response from the VPS must never cause the underlying Codex
        // operation to execute again.  The encrypted response is persisted
        // before it is submitted, then safely retried verbatim if needed.
        if (TryGetOutbox(delivery.RequestId, out var queued))
        {
            if (!string.Equals(queued.DeviceId, delivery.DeviceId, StringComparison.Ordinal))
            {
                _logger.LogWarning("Ignored relay request with an outbox device mismatch for {RequestId}", delivery.RequestId);
                return false;
            }
            return await SubmitResponseAsync(queued, cancellationToken);
        }

        var requestAad = $"{_agentId}|{delivery.DeviceId}|request";
        var responseAad = $"{_agentId}|{delivery.DeviceId}|response";
        var statusCode = 400;
        var responseBody = JsonSerializer.Serialize(new { error = "中继请求无效" }, _json);

        if (RelayEnvelopeCrypto.TryDecrypt(key, requestAad, delivery.Request, out var plaintext))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<RelayApiRequest>(plaintext, _json);
                if (payload is null) throw new JsonException();
                (statusCode, responseBody) = await ForwardToLocalAgentAsync(delivery.DeviceId, payload, cancellationToken);
            }
            catch (JsonException)
            {
                responseBody = JsonSerializer.Serialize(new { error = "中继请求格式无效" }, _json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                statusCode = 503;
                responseBody = JsonSerializer.Serialize(new { error = "电脑 Agent 暂时不可用" }, _json);
                _logger.LogWarning(ex, "Unable to forward relay request to local Agent");
            }
        }

        var entry = new RelayResponseOutboxEntry(delivery.RequestId, delivery.DeviceId, statusCode,
            RelayEnvelopeCrypto.Encrypt(key, responseAad, responseBody), DateTimeOffset.UtcNow);
        PutOutbox(entry);
        return await SubmitResponseAsync(entry, cancellationToken);
    }

    private async Task<bool> SubmitResponseAsync(RelayResponseOutboxEntry entry, CancellationToken cancellationToken)
    {
        using var submit = CreateRequest(HttpMethod.Post,
            $"api/v1/agents/{Uri.EscapeDataString(_agentId)}/requests/{Uri.EscapeDataString(entry.RequestId)}/response", _agentToken, AgentTokenHeader);
        submit.Content = JsonContent.Create(new { statusCode = entry.StatusCode, response = entry.Response });
        try
        {
            using var result = await _relayHttp.SendAsync(submit, cancellationToken);
            if (result.IsSuccessStatusCode || result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                // Conflict means this exact request already has a final relay
                // response (for example after a lost 204); a later poll can read it.
                RemoveOutbox(entry.RequestId);
                return true;
            }
            if (result.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                ClearAgentToken();
                SetFailure("中继要求重新注册");
                return false;
            }
            SetFailure($"中继响应提交失败（HTTP {(int)result.StatusCode}）");
            return false;
        }
        catch (HttpRequestException ex)
        {
            SetFailure("无法提交中继响应");
            _logger.LogWarning(ex, "Could not submit encrypted relay response");
            return false;
        }
    }

    private async Task<(int StatusCode, string Body)> ForwardToLocalAgentAsync(string deviceId, RelayApiRequest payload, CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, out var method, out var relativePath))
            return (403, JsonSerializer.Serialize(new { error = "该中继路径或方法不允许" }, _json));

        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.TryAddWithoutValidation(InternalHeader, _internalSecret);
        request.Headers.TryAddWithoutValidation(InternalDeviceHeader, deviceId);
        if (payload.Body is not null)
        {
            var bodyBytes = Encoding.UTF8.GetByteCount(payload.Body);
            if (bodyBytes > MaximumForwardedBodyBytes)
                return (413, JsonSerializer.Serialize(new { error = "请求内容过大" }, _json));
            request.Content = new StringContent(payload.Body, Encoding.UTF8, "application/json");
        }

        using var response = await _loopbackHttp.SendAsync(request, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (responseBytes.Length > MaximumForwardedBodyBytes)
            return (502, JsonSerializer.Serialize(new { error = "电脑 Agent 返回内容过大" }, _json));
        return ((int)response.StatusCode, Encoding.UTF8.GetString(responseBytes));
    }

    private static bool TryValidatePayload(RelayApiRequest payload, out HttpMethod method, out string relativePath)
    {
        method = HttpMethod.Get;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(payload.Method) || string.IsNullOrWhiteSpace(payload.Path)) return false;
        var upperMethod = payload.Method.Trim().ToUpperInvariant();
        if (upperMethod is not ("GET" or "POST" or "PUT" or "DELETE")) return false;
        if (!payload.Path.StartsWith('/') || payload.Path.StartsWith("//", StringComparison.Ordinal) ||
            payload.Path.Contains('\\') || payload.Path.Contains('\r') || payload.Path.Contains('\n')) return false;

        var barePath = payload.Path.Split('?', 2)[0];
        // Avoid URI normalisation turning an apparently permitted prefix such
        // as /api/tasks/../devices into a privileged local route.
        if (barePath.Contains('%') || barePath.Split('/').Any(segment => segment is "." or "..")) return false;
        var allowed = new[] { "/api/health", "/api/tasks", "/api/models", "/api/approvals", "/api/runs", "/api/settings" };
        if (!allowed.Any(prefix => barePath == prefix || barePath.StartsWith(prefix + "/", StringComparison.Ordinal))) return false;
        method = new HttpMethod(upperMethod);
        relativePath = payload.Path;
        return true;
    }

    private async Task<bool> EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return false;
        if (!string.IsNullOrWhiteSpace(_agentToken)) return true;
        await _registrationGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_agentToken)) return true;
            using var request = CreateRequest(HttpMethod.Post, "api/v1/agents/register", _options.BootstrapKey, BootstrapHeader);
            request.Content = JsonContent.Create(new { agentId = _agentId });
            using var response = await _relayHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                SetFailure($"中继注册失败（HTTP {(int)response.StatusCode}）");
                return false;
            }
            var registration = await response.Content.ReadFromJsonAsync<RelayRegistration>(_json, cancellationToken);
            if (string.IsNullOrWhiteSpace(registration?.AgentToken))
            {
                SetFailure("中继返回了无效的 Agent 凭据");
                return false;
            }
            _agentToken = registration.AgentToken;
            SaveIdentity();
            SetConnected();
            _logger.LogInformation("Cloud relay registered as Agent {AgentId}", _agentId);
            return true;
        }
        catch (HttpRequestException ex)
        {
            SetFailure("无法连接云中继");
            _logger.LogWarning(ex, "Cloud relay registration failed");
            return false;
        }
        finally { _registrationGate.Release(); }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl, string? token, string tokenHeader)
    {
        var request = new HttpRequestMessage(method, new Uri(_relayBaseUri!, relativeUrl));
        request.Headers.TryAddWithoutValidation(tokenHeader, token ?? string.Empty);
        return request;
    }

    private void ClearAgentToken()
    {
        _agentToken = null;
        SaveIdentity();
    }

    private RelayIdentity? LoadIdentity()
    {
        try { return File.Exists(_identityPath) ? JsonSerializer.Deserialize<RelayIdentity>(File.ReadAllText(_identityPath), _json) : null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read cloud relay identity; creating a new one");
            return null;
        }
    }

    private void SaveIdentity()
    {
        try
        {
            var temporary = _identityPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new RelayIdentity(_agentId, _agentToken), new JsonSerializerOptions(_json) { WriteIndented = true }));
            File.Move(temporary, _identityPath, true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist cloud relay identity"); }
    }

    private void LoadOutbox()
    {
        try
        {
            if (!File.Exists(_outboxPath)) return;
            var entries = JsonSerializer.Deserialize<List<RelayResponseOutboxEntry>>(File.ReadAllText(_outboxPath), _json) ?? [];
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
            _outbox = entries.Where(entry => entry.CreatedAt >= cutoff &&
                                             !string.IsNullOrWhiteSpace(entry.RequestId) &&
                                             !string.IsNullOrWhiteSpace(entry.DeviceId))
                .ToDictionary(entry => entry.RequestId, StringComparer.Ordinal);
            SaveOutboxLocked();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read cloud relay response outbox; pending responses will be retried only from the relay");
            _outbox = new(StringComparer.Ordinal);
        }
    }

    private bool TryGetOutbox(string requestId, out RelayResponseOutboxEntry entry)
    {
        lock (_outboxGate) return _outbox.TryGetValue(requestId, out entry!);
    }

    private void PutOutbox(RelayResponseOutboxEntry entry)
    {
        lock (_outboxGate)
        {
            _outbox[entry.RequestId] = entry;
            PruneOutboxLocked();
            SaveOutboxLocked();
        }
    }

    private void RemoveOutbox(string requestId)
    {
        lock (_outboxGate)
        {
            if (_outbox.Remove(requestId)) SaveOutboxLocked();
        }
    }

    private void PruneOutboxLocked()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        foreach (var stale in _outbox.Where(pair => pair.Value.CreatedAt < cutoff).Select(pair => pair.Key).ToArray())
            _outbox.Remove(stale);
    }

    private void SaveOutboxLocked()
    {
        var temporary = _outboxPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_outbox.Values, new JsonSerializerOptions(_json) { WriteIndented = true }));
        File.Move(temporary, _outboxPath, true);
    }

    private void SetConnected()
    {
        lock (_stateGate)
        {
            _state = "connected";
            _lastConnectedAt = DateTimeOffset.UtcNow;
            _lastError = null;
        }
    }

    private void SetFailure(string error)
    {
        lock (_stateGate)
        {
            _state = "reconnecting";
            _lastError = error;
        }
    }

    public void Dispose()
    {
        _relayHttp.Dispose();
        _loopbackHttp.Dispose();
        _registrationGate.Dispose();
    }

    private sealed record RelayIdentity(string AgentId, string? AgentToken);
    private sealed record RelayRegistration(string AgentToken);
    private sealed record RelayDeviceEnrollment(string DeviceToken);
    private sealed record RelayDelivery(string RequestId, string DeviceId, RelayEnvelope Request);
    private sealed record RelayResponseOutboxEntry(string RequestId, string DeviceId, int StatusCode, RelayEnvelope Response, DateTimeOffset CreatedAt);
}

public sealed class CloudRelayWorker(CloudRelayClient relay, ILogger<CloudRelayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!relay.Enabled)
        {
            logger.LogInformation("Cloud relay is disabled; LAN pairing remains available.");
            return;
        }

        logger.LogInformation("Cloud relay worker starting for Agent {AgentId}", relay.AgentId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await relay.PollAndProcessAsync(stoppingToken);
                if (!completed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cloud relay worker will retry");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

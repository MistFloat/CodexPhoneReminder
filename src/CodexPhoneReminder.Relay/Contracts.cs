namespace CodexPhoneReminder.Relay;

/// <summary>
/// Configuration for the relay. BootstrapKey must be supplied outside source control, for example
/// through a systemd environment file or a container secret.
/// </summary>
public sealed class RelayOptions
{
    public string BootstrapKey { get; set; } = string.Empty;
    public string StatePath { get; set; } = "data/relay-state.json";
    public int RequestLifetimeMinutes { get; set; } = 10;
    public int ResultRetentionMinutes { get; set; } = 10;
    public int MaxEnvelopeBytes { get; set; } = 262_144;
    public int MaxLongPollSeconds { get; set; } = 25;
    public int MaxPendingRequestsPerDevice { get; set; } = 32;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BootstrapKey) || BootstrapKey.Length < 24)
            throw new InvalidOperationException("Relay:BootstrapKey must be a random secret of at least 24 characters.");
        if (RequestLifetimeMinutes is < 1 or > 60)
            throw new InvalidOperationException("Relay:RequestLifetimeMinutes must be between 1 and 60.");
        if (ResultRetentionMinutes is < 1 or > 24 * 60)
            throw new InvalidOperationException("Relay:ResultRetentionMinutes must be between 1 and 1440.");
        if (MaxEnvelopeBytes is < 1024 or > 1_048_576)
            throw new InvalidOperationException("Relay:MaxEnvelopeBytes must be between 1024 and 1048576.");
        if (MaxLongPollSeconds is < 1 or > 25)
            throw new InvalidOperationException("Relay:MaxLongPollSeconds must be between 1 and 25.");
        if (MaxPendingRequestsPerDevice is < 1 or > 256)
            throw new InvalidOperationException("Relay:MaxPendingRequestsPerDevice must be between 1 and 256.");
    }
}

// These six request/response bodies intentionally contain no plaintext task data. Nonce and
// ciphertext are end-to-end encrypted-envelope fields owned by the Agent and Android client.
public sealed record RegisterAgentRequest(string AgentId);
public sealed record RegisterAgentResponse(string AgentToken);
public sealed record EnrollDeviceRequest(string DeviceId);
public sealed record EnrollDeviceResponse(string DeviceToken);
public sealed record EncryptedEnvelope(string Nonce, string Ciphertext);
public sealed record SubmitRelayRequest(string Nonce, string Ciphertext);
public sealed record SubmitRelayResponse(string RequestId);
public sealed record PendingRelayRequest(string RequestId, string DeviceId, EncryptedEnvelope Request);
public sealed record SubmitRelayResult(int StatusCode, EncryptedEnvelope Response);
public sealed record RelayResult(int StatusCode, EncryptedEnvelope Response);

public sealed record AgentPrincipal(string AgentId);
public sealed record DevicePrincipal(string AgentId, string DeviceId);

public sealed class RelayValidationException(string message) : Exception(message);
public sealed class RelayNotFoundException(string message) : Exception(message);
public sealed class RelayConflictException(string message) : Exception(message);
public sealed class RelayQueueFullException(string message) : Exception(message);

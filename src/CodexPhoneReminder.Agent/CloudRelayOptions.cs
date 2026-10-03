namespace CodexPhoneReminder.Agent;

/// <summary>
/// Settings for an optional, self-hosted cloud relay.  Secrets are intentionally
/// not supplied in appsettings.json; use protected process environment variables
/// (Relay__Url and Relay__BootstrapKey) in production.
/// </summary>
public sealed class CloudRelayOptions
{
    public string Url { get; set; } = string.Empty;
    public string BootstrapKey { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public int PollWaitSeconds { get; set; } = 20;
}

public sealed record CloudRelayStatus(
    bool Enabled,
    string State,
    string? AgentId,
    DateTimeOffset? LastConnectedAt,
    string? LastError);

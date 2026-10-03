using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CodexPhoneReminder.Relay;

/// <summary>
/// Keeps bearer-token parsing at the HTTP boundary. The persistent store only ever sees hashes.
/// </summary>
public sealed class RelayAuthorization
{
    public const string BootstrapHeader = "X-Relay-Bootstrap";
    public const string AgentTokenHeader = "X-Relay-Agent-Token";
    public const string DeviceTokenHeader = "X-Relay-Device-Token";

    private readonly RelayStore _store;
    private readonly byte[] _bootstrapHash;

    public RelayAuthorization(RelayStore store, IOptions<RelayOptions> options)
    {
        _store = store;
        _bootstrapHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.BootstrapKey));
    }

    public bool HasBootstrap(HttpRequest request)
    {
        var token = ReadSingleHeader(request, BootstrapHeader);
        if (token is null) return false;
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(_bootstrapHash, candidate);
    }

    public bool TryGetAgent(HttpRequest request, string agentId, out AgentPrincipal? agent) =>
        _store.TryAuthenticateAgent(agentId, ReadSingleHeader(request, AgentTokenHeader), out agent);

    public bool TryGetDevice(HttpRequest request, string agentId, out DevicePrincipal? device) =>
        _store.TryAuthenticateDevice(agentId, ReadSingleHeader(request, DeviceTokenHeader), out device);

    private static string? ReadSingleHeader(HttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue(name, out var values) || values.Count != 1) return null;
        var value = values[0];
        return string.IsNullOrWhiteSpace(value) || value.Length > 1024 ? null : value;
    }
}

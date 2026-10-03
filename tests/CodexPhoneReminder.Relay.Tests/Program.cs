using CodexPhoneReminder.Relay;

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "codex-phone-relay-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var statePath = Path.Combine(root, "relay-state.json");
    var options = new RelayOptions { BootstrapKey = "test-bootstrap-key-must-be-long-enough" };
    var store = new RelayStore(statePath, options);

    var registration = store.RegisterAgent("desktop-agent-1");
    Check("agent token authenticates", store.TryAuthenticateAgent("desktop-agent-1", registration.AgentToken, out var agent) && agent is not null);
    Check("bad agent token is rejected", !store.TryAuthenticateAgent("desktop-agent-1", "wrong-token", out _));
    Check("agent token is not persisted", !File.ReadAllText(statePath).Contains(registration.AgentToken, StringComparison.Ordinal));

    var enrollment = store.EnrollDevice("desktop-agent-1", "tablet-1");
    Check("device token authenticates", store.TryAuthenticateDevice("desktop-agent-1", enrollment.DeviceToken, out var device) && device is not null);
    Check("bad device token is rejected", !store.TryAuthenticateDevice("desktop-agent-1", "wrong-token", out _));

    var submitted = store.SubmitRequest(device!, new SubmitRelayRequest("nonce-a", "encrypted-request-a"));
    var duplicate = store.SubmitRequest(device!, new SubmitRelayRequest("nonce-a", "encrypted-request-a"));
    Check("request retry is idempotent", submitted.RequestId == duplicate.RequestId);

    var boundedStore = new RelayStore(Path.Combine(root, "bounded-state.json"), new RelayOptions
    {
        BootstrapKey = "test-bootstrap-key-must-be-long-enough",
        MaxPendingRequestsPerDevice = 1
    });
    var boundedRegistration = boundedStore.RegisterAgent("bounded-agent");
    boundedStore.TryAuthenticateAgent("bounded-agent", boundedRegistration.AgentToken, out var boundedAgent);
    var boundedEnrollment = boundedStore.EnrollDevice("bounded-agent", "bounded-device");
    boundedStore.TryAuthenticateDevice("bounded-agent", boundedEnrollment.DeviceToken, out var boundedDevice);
    boundedStore.SubmitRequest(boundedDevice!, new SubmitRelayRequest("bounded-nonce-1", "bounded-ciphertext-1"));
    try
    {
        boundedStore.SubmitRequest(boundedDevice!, new SubmitRelayRequest("bounded-nonce-2", "bounded-ciphertext-2"));
        Check("pending request cap rejects a second request", false);
    }
    catch (RelayQueueFullException)
    {
        Check("pending request cap rejects a second request", true);
    }

    var pending = await store.PollAgentAsync(agent!.AgentId, TimeSpan.Zero, CancellationToken.None);
    Check("agent receives encrypted request", pending is not null && pending.RequestId == submitted.RequestId && pending.DeviceId == "tablet-1" && pending.Request.Ciphertext == "encrypted-request-a");

    store.SubmitResponse(agent!, submitted.RequestId, new SubmitRelayResult(202, new EncryptedEnvelope("nonce-b", "encrypted-response-b")));
    var result = await store.PollResultAsync(device!, submitted.RequestId, TimeSpan.Zero, CancellationToken.None);
    Check("device receives encrypted response", result is not null && result.StatusCode == 202 && result.Response.Ciphertext == "encrypted-response-b");

    var restarted = new RelayStore(statePath, options);
    Check("agent token survives restart", restarted.TryAuthenticateAgent("desktop-agent-1", registration.AgentToken, out var restartedAgent) && restartedAgent is not null);
    Check("device token survives restart", restarted.TryAuthenticateDevice("desktop-agent-1", enrollment.DeviceToken, out var restartedDevice) && restartedDevice is not null);
    var persistedResult = await restarted.PollResultAsync(restartedDevice!, submitted.RequestId, TimeSpan.Zero, CancellationToken.None);
    Check("response survives restart", persistedResult?.Response.Nonce == "nonce-b");
}
finally
{
    Directory.Delete(root, true);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("All relay tests passed.");
return 0;

void Check(string name, bool passed)
{
    if (!passed) failures.Add("FAIL: " + name);
}

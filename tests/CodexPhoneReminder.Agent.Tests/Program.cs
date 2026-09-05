using CodexPhoneReminder.Agent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

var failures = new List<string>();
Check("secret redaction", AgentStore.Sanitize("token=abc123") == "token=***");
Check("path redaction", !AgentStore.Sanitize(@"run C:\Users\student\private\file.txt").Contains("student"));
Check("ordinary text", AgentStore.Sanitize("12 tests passed") == "12 tests passed");
var now = DateTimeOffset.UtcNow;
var messages = new List<ChatMessage>();
AgentStore.MergeMessage(messages, new ChatMessage("pending:" + Guid.NewGuid().ToString("N"), "user", "继续", now));
AgentStore.MergeMessage(messages, new ChatMessage("ABCDEF0123456789ABCDEF01", "user", "继续", now.AddSeconds(2)));
Check("canonical message replaces pending bubble", messages.Count == 1 && messages[0].Id == "ABCDEF0123456789ABCDEF01");
AgentStore.MergeMessage(messages, new ChatMessage("pending:" + Guid.NewGuid().ToString("N"), "user", "继续", now.AddMinutes(10)));
Check("intentional repeated input is retained", messages.Count == 2);
var oldMessages = new List<ChatMessage> { new(Guid.NewGuid().ToString("N"), "user", "旧消息", now) };
AgentStore.MergeMessage(oldMessages, new ChatMessage("1234567890ABCDEF12345678", "user", "旧消息", now.AddSeconds(1)));
Check("legacy optimistic GUID is replaced", oldMessages.Count == 1 && oldMessages[0].Id == "1234567890ABCDEF12345678");
var subscriptions = new ProgressSubscriptionService();
var firstLease = subscriptions.Renew("thread-1");
var renewedLease = subscriptions.Renew("thread-1");
Check("progress lease activates on demand", subscriptions.IsActive("thread-1") && firstLease.Active);
Check("progress lease renewal keeps generation", firstLease.Generation == renewedLease.Generation && renewedLease.ExpiresAt >= firstLease.ExpiresAt);
var pairingRoot = Path.Combine(Path.GetTempPath(), "codex-reminder-pairing-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pairingRoot);
try
{
    var environment = new TestEnvironment(pairingRoot);
    var firstIdentity = new TransportIdentity(environment);
    var firstService = new PairingService(environment, firstIdentity);
    var offer = firstService.CreateOffer("https://127.0.0.1:5188#" + firstIdentity.Fingerprint);
    var claim = firstService.Claim(offer.Code);
    var restartedIdentity = new TransportIdentity(environment);
    var restartedService = new PairingService(environment, restartedIdentity);
    Check("paired token survives agent restart", claim is not null && restartedService.Validate(claim.Token));
    Check("TLS identity survives agent restart", firstIdentity.Fingerprint == restartedIdentity.Fingerprint && offer.Fingerprint.Length == 64);
}
finally { Directory.Delete(pairingRoot, true); }
if (failures.Count > 0) { Console.Error.WriteLine(string.Join(Environment.NewLine, failures)); return 1; }
Console.WriteLine("All domain tests passed."); return 0;
void Check(string name, bool result) { if (!result) failures.Add($"FAIL: {name}"); }

sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "CodexPhoneReminder.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
}

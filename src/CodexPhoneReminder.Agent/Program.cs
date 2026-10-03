using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexPhoneReminder.Agent;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var httpPort = builder.Configuration.GetValue("Agent:HttpPort", 5187);
var httpsPort = builder.Configuration.GetValue("Agent:HttpsPort", 5188);
var transportIdentity = new TransportIdentity(builder.Environment);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort);
    options.ListenAnyIP(httpsPort, listen => listen.UseHttps(transportIdentity.Certificate));
});
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton(transportIdentity);
builder.Services.AddSingleton<LanAddressResolver>();
builder.Services.AddSingleton<PairingService>();
builder.Services.Configure<CloudRelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddSingleton<CloudRelayClient>();
builder.Services.AddHostedService<CloudRelayWorker>();
builder.Services.AddSingleton<CodexModelCatalog>();
builder.Services.Configure<CodexCliOptions>(builder.Configuration.GetSection("CodexCli"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<CodexCliLocator>();
builder.Services.AddSingleton<CodexWorkspaceRegistry>();
builder.Services.AddSingleton<ProgressSubscriptionService>();
builder.Services.AddSingleton<CodexCliRunner>();
builder.Services.AddHostedService<CodexSessionWatcher>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var insecurePairOffer = context.Request.Path == "/api/pair" && HttpMethods.IsGet(context.Request.Method);
    var insecureDownload = context.Request.Path.StartsWithSegments("/downloads");
    if (!context.Request.IsHttps && !insecurePairOffer && !insecureDownload)
    {
        var host = context.Request.Host.Host;
        context.Response.Redirect($"https://{host}:{httpsPort}{context.Request.Path}{context.Request.QueryString}");
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/api/pair") ||
        context.Request.Path == "/api/health")
    { await next(); return; }
    var pairing = context.RequestServices.GetRequiredService<PairingService>();
    var relay = context.RequestServices.GetRequiredService<CloudRelayClient>();
    if (relay.IsInternalRelayRequest(context)) { await next(); return; }
    if (!context.Request.Headers.TryGetValue("X-Device-Token", out var token) || !pairing.Validate(token!))
    { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "设备未配对或授权已撤销" }); return; }
    await next();
});

app.MapGet("/api/health", (AgentStore store, CodexCliLocator locator, CodexWorkspaceRegistry workspaces, LanAddressResolver lanAddresses, CloudRelayClient relay) =>
{
    var lan = lanAddresses.Resolve();
    return Results.Ok(new
{
    status = "online", computerName = Environment.MachineName, version = "0.1.0",
    source = "codex-cli", reliability = "structured-session", lastSyncAt = store.LastActivity,
    cliAvailable = locator.ResolvedPath is not null, cliPath = locator.DisplayPath,
    mappedWorkspaces = workspaces.Count, lanAddress = lan.Address,
    lanInterface = lan.InterfaceName, lanInterfaceType = lan.InterfaceType,
    relay = relay.Status
});
});

app.MapGet("/downloads/android-debug.apk", (IWebHostEnvironment env) =>
{
    var apk = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "android", "app", "build", "outputs", "apk", "debug", "app-debug.apk"));
    return File.Exists(apk)
        ? Results.File(apk, "application/vnd.android.package-archive", "codex-phone-reminder-debug.apk")
        : Results.NotFound(new { error = "Android debug APK 尚未构建，请先在 android 目录运行 gradlew.bat assembleDebug" });
});

app.MapGet("/api/pair", (PairingService pairing, TransportIdentity transport, LanAddressResolver lanAddresses, ILogger<Program> logger) =>
{
    var lan = lanAddresses.Resolve();
    logger.LogInformation("Pairing address selected: {Address} via {Interface} ({InterfaceType})", lan.Address, lan.InterfaceName, lan.InterfaceType);
    return Results.Ok(pairing.CreateOffer($"https://{lan.Address}:{httpsPort}#{transport.Fingerprint}", lan.InterfaceName, lan.InterfaceType));
});

app.MapPost("/api/pair/claim", async (PairClaim claim, PairingService pairing, CloudRelayClient relay, ILogger<Program> logger, CancellationToken ct) =>
{
    var result = pairing.Claim(claim.Code);
    if (result is null) return Results.BadRequest(new { error = "配对码无效或已过期" });
    try
    {
        var enrollment = await relay.EnrollDeviceAsync(result, ct);
        if (enrollment is not null) result = result with { Relay = enrollment };
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception ex)
    {
        // A LAN pairing must remain usable when a currently optional relay is offline.
        logger.LogWarning(ex, "Local pairing succeeded but cloud relay enrollment was unavailable");
    }
    return Results.Ok(result);
});

app.MapGet("/api/tasks", (AgentStore store) => Results.Ok(store.Tasks()));
app.MapGet("/api/models", (CodexModelCatalog models) => Results.Ok(models.Get()));
app.MapGet("/api/tasks/{id}", (string id, AgentStore store) =>
    store.Task(id) is { } task ? Results.Ok(task) : Results.NotFound());
app.MapGet("/api/tasks/{id}/progress", (string id, string? after, AgentStore store, ProgressSubscriptionService subscriptions) =>
{
    if (store.Task(id) is null) return Results.NotFound();
    var lease = subscriptions.Renew(id);
    return Results.Ok(store.Progress(id, after, lease.ExpiresAt));
});
app.MapPost("/api/tasks/{id}/read", (string id, AgentStore store) => store.MarkRead(id) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/tasks/{id}/mute", (string id, AgentStore store) => store.ToggleMute(id) is { } muted ? Results.Ok(new { muted }) : Results.NotFound());
app.MapPost("/api/tasks/{id}/archive", (string id, AgentStore store) => store.Archive(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/approvals/{id}", async (string id, ApprovalDecision decision, AgentStore store, CodexCliRunner cli, CancellationToken ct) =>
{
    var result = store.Decide(id, decision);
    if (result.Status == DecisionStatus.Success && store.Approval(id) is { } approval)
    {
        var prompt = decision.Approved
            ? $"用户已通过 Codex Phone Reminder 明确批准请求 {id}。继续原任务；仍须遵守当前 Codex CLI 的审批和沙箱策略。"
            : $"用户已通过 Codex Phone Reminder 拒绝请求 {id}。不要执行该操作，请采用安全替代方案；若无法继续则说明原因。";
        _ = cli.ResumeAsync(approval.TaskId, prompt, ct, store.PreferredModel(approval.TaskId));
    }
    return result.Status switch
    {
        DecisionStatus.Success => Results.Ok(result),
        DecisionStatus.NotFound => Results.NotFound(result),
        DecisionStatus.Expired => Results.Conflict(result),
        DecisionStatus.Replayed => Results.Conflict(result),
        _ => Results.BadRequest(result)
    };
});

app.MapPost("/api/tasks/{id}/reply", async (string id, TaskReply reply, AgentStore store, CodexCliRunner cli, CodexModelCatalog models, CancellationToken ct) =>
{
    if (store.Task(id) is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(reply.Message) || reply.Message.Length > 4000) return Results.BadRequest(new { error = "回复不能为空且不能超过 4000 字" });
    if (!models.IsAllowed(reply.Model)) return Results.BadRequest(new { error = "所选模型不在本机 Codex 可用模型目录中" });
    try
    {
        store.SetPreferredModel(id, reply.Model);
        var runId = await cli.ResumeAsync(id, AgentStore.Sanitize(reply.Message), ct, reply.Model);
        store.AddMessage(id, new ChatMessage($"pending:{Guid.NewGuid():N}", "user", reply.Message.Trim(), DateTimeOffset.UtcNow));
        return Results.Accepted($"/api/runs/{runId}", new { runId, message = "Codex CLI 已启动并开始继续该对话" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"无法启动 Codex CLI：{ex.Message}" }, statusCode: 503);
    }
});
app.MapGet("/api/runs/{id}", (string id, CodexCliRunner cli) => cli.Get(id) is { } run ? Results.Ok(run) : Results.NotFound());
app.MapGet("/api/tasks/{id}/run", (string id, CodexCliRunner cli) => cli.ActiveForThread(id) is { } run ? Results.Ok(run) : Results.NoContent());
app.MapPost("/api/runs/{id}/cancel", (string id, CodexCliRunner cli) => cli.Cancel(id) ? Results.Accepted(value: new { message = "取消指令已发送" }) : Results.Conflict(new { error = "该运行已经结束或不存在" }));

app.MapPost("/api/demo/events", (NewEvent input, AgentStore store) => Results.Ok(store.Add(input)));
app.MapGet("/api/settings", (AgentStore store) => Results.Ok(store.Settings));
app.MapPut("/api/settings", (NotificationSettings settings, AgentStore store) => { store.Settings = settings; store.Save(); return Results.Ok(settings); });
app.MapGet("/api/devices", (PairingService pairing) => Results.Ok(pairing.Devices()));
app.MapDelete("/api/devices/{id}", (string id, PairingService pairing) => pairing.Revoke(id) ? Results.NoContent() : Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }

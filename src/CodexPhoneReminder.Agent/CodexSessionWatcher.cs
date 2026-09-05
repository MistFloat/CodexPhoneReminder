using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace CodexPhoneReminder.Agent;

public sealed class CodexSessionWatcher(IOptions<CodexCliOptions> options, AgentStore store, CodexWorkspaceRegistry workspaces, ProgressSubscriptionService subscriptions, ILogger<CodexSessionWatcher> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = [];
    private readonly ConcurrentDictionary<string, string> _threadFiles = [];
    private readonly ConcurrentDictionary<string, long> _progressScans = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Scan(); } catch (Exception ex) { logger.LogWarning(ex, "Codex session scan failed"); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), stoppingToken);
        }
    }
    private void Scan()
    {
        var root = options.Value.SessionsPath; if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            var changed = File.GetLastWriteTimeUtc(file);
            var needsLeaseScan = _threadFiles.TryGetValue(file, out var knownThread) && subscriptions.IsActive(knownThread) &&
                _progressScans.GetValueOrDefault(file) < subscriptions.Generation(knownThread);
            if (_seen.TryGetValue(file, out var old) && old == changed && !needsLeaseScan) continue;
            try { Parse(file); _seen[file] = changed; }
            catch (IOException ex) { logger.LogDebug(ex, "Codex session is temporarily unavailable: {File}", file); }
        }
    }
    private void Parse(string file)
    {
        string? id = null, cwd = null, title = null; var sourceIsUser = true;
        var pending = new List<(string CallId, int Seq, string Raw, EventType Type, string Summary, string? Detail)>();
        var progressEvents = new List<(string Raw, EventType Type, string Summary)>();
        var messages = new List<ChatMessage>();
        var outputs = new HashSet<string>(); var toolLabels = new Dictionary<string, string>(); var seq = 0; var lastTerminal = -1;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            seq++;
            try
            {
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                var envelope = Str(root, "type"); if (!root.TryGetProperty("payload", out var payload)) continue;
                if (envelope == "session_meta")
                {
                    id = Str(payload, "id") ?? Str(payload, "session_id"); cwd = Str(payload, "cwd");
                    sourceIsUser = Str(payload, "thread_source") is not "subagent";
                    if (id is not null) _threadFiles[file] = id;
                }
                if (!sourceIsUser || id is null) continue;
                var ptype = Str(payload, "type");
                var progressActive = subscriptions.IsActive(id);
                if (envelope == "response_item" && Str(payload, "role") == "user") title ??= MessageText(payload);
                if (envelope == "response_item" && Str(payload, "role") is "user" or "assistant" && MessageText(payload) is { } message && !IsInjectedContext(message))
                    messages.Add(new ChatMessage(EventId(line), Str(payload, "role")!, message, ParseTimestamp(root)));
                if (envelope == "event_msg" && ptype == "task_started") { Add(id, cwd, title, line, EventType.Started, "Codex CLI 开始处理当前对话"); if (progressActive) { progressEvents.Clear(); progressEvents.Add((line, EventType.TurnStarted, "Codex 开始处理当前回合")); } }
                else if (envelope == "event_msg" && ptype == "task_complete") { lastTerminal = seq; Add(id, cwd, title, line, EventType.Completed, Str(payload, "last_agent_message") ?? "当前对话已完成"); if (progressActive) progressEvents.Add((line, EventType.TurnCompleted, "当前回合已完成")); }
                else if (envelope == "event_msg" && ptype == "turn_aborted") { lastTerminal = seq; Add(id, cwd, title, line, EventType.Failed, "当前运行已中断"); if (progressActive) progressEvents.Add((line, EventType.TurnFailed, "当前回合已中断")); }
                else if (envelope == "response_item" && ptype is "function_call" or "custom_tool_call")
                {
                    var callId = Str(payload, "call_id") ?? $"seq-{seq}"; var label = ToolLabel(Str(payload, "name")); toolLabels[callId] = label;
                    if (progressActive) progressEvents.Add((line, IsTestTool(payload) ? EventType.TestsUpdated : EventType.ToolStarted, IsTestTool(payload) ? "正在运行测试或构建" : $"正在使用{label}"));
                }
                else if (envelope == "response_item" && Str(payload, "type") is "function_call_output" or "custom_tool_call_output") { var outputId = Str(payload, "call_id"); if (outputId is not null) outputs.Add(outputId); }
                else if (envelope == "response_item" && Str(payload, "name") == "request_user_input") pending.Add((Str(payload, "call_id") ?? $"seq-{seq}", seq, line, EventType.WaitingReply, "Codex 正在等待你的回复", Str(payload, "arguments")));
                else if (envelope == "event_msg" && IsApprovalRequest(ptype))
                    Add(id, cwd, title, line, EventType.ApprovalRequested, "Codex 明确请求权限批准", ApprovalDetail(payload), RiskLevel.Medium);
                else if (envelope == "event_msg" && IsApprovalResolved(ptype))
                    Add(id, cwd, title, line, EventType.Resumed, "权限请求已处理，任务状态已更新");
                if (progressActive && envelope == "response_item" && (ptype is "function_call_output" or "custom_tool_call_output"))
                {
                    var callId = Str(payload, "call_id"); var label = callId is not null && toolLabels.TryGetValue(callId, out var known) ? known : "工具";
                    progressEvents.Add((line, EventType.ToolCompleted, $"{label}已完成"));
                }
                if (progressActive && envelope == "event_msg" && ptype == "patch_apply_end") progressEvents.Add((line, EventType.FilesChanged, "文件修改已应用"));
            }
            catch (JsonException) { }
        }
        if (id is null || !sourceIsUser) return;
        workspaces.Register(id, cwd);
        foreach (var progress in progressEvents) AddProgress(id, cwd, title, progress.Raw, progress.Type, progress.Summary);
        foreach (var p in pending.Where(x => x.Seq > lastTerminal && !outputs.Contains(x.CallId)))
            Add(id, cwd, title, p.Raw, p.Type, p.Summary, p.Detail, p.Type == EventType.ApprovalRequested ? RiskLevel.Medium : RiskLevel.Low);
        var project = string.IsNullOrWhiteSpace(cwd) ? "Codex" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));
        store.UpdateMetadata(id, project, Short(title));
        foreach (var message in messages) store.AddMessage(id, message);
        _progressScans[file] = subscriptions.Generation(id);
    }
    private void Add(string id, string? cwd, string? title, string raw, EventType type, string summary, string? detail = null, RiskLevel risk = RiskLevel.Low)
    {
        var eventId = EventId(raw);
        var project = string.IsNullOrWhiteSpace(cwd) ? "Codex" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));
        store.Add(new(eventId, id, project, Short(title) ?? "Codex 对话", type, summary, detail, risk));
    }
    private void AddProgress(string id, string? cwd, string? title, string raw, EventType type, string summary)
    {
        var eventId = $"progress-{EventId(raw)}-{type}";
        var project = string.IsNullOrWhiteSpace(cwd) ? "Codex" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));
        store.Add(new(eventId, id, project, Short(title) ?? "Codex 对话", type, summary));
    }
    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool IsApprovalRequest(string? type) => type is not null && type.Contains("approval", StringComparison.OrdinalIgnoreCase) && (type.Contains("request", StringComparison.OrdinalIgnoreCase) || type.Contains("required", StringComparison.OrdinalIgnoreCase));
    private static bool IsApprovalResolved(string? type) => type is not null && type.Contains("approval", StringComparison.OrdinalIgnoreCase) && (type.Contains("resolved", StringComparison.OrdinalIgnoreCase) || type.Contains("complete", StringComparison.OrdinalIgnoreCase) || type.Contains("response", StringComparison.OrdinalIgnoreCase));
    private static string? ApprovalDetail(JsonElement payload) => Str(payload, "command") ?? Str(payload, "reason") ?? Str(payload, "message");
    private static string? MessageText(JsonElement e)
    {
        if (!e.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) return null;
        foreach (var x in c.EnumerateArray()) { var s = Str(x, "text"); if (!string.IsNullOrWhiteSpace(s)) return s; }
        return null;
    }
    private static string EventId(string raw) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))[..24];
    private static DateTimeOffset ParseTimestamp(JsonElement root) => DateTimeOffset.TryParse(Str(root, "timestamp"), out var value) ? value : DateTimeOffset.UtcNow;
    private static bool IsInjectedContext(string text) => text.TrimStart().StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("<recommended_plugins>", StringComparison.OrdinalIgnoreCase);
    private static string ToolLabel(string? name) => name switch { "shell_command" or "exec_command" => "命令工具", "apply_patch" => "文件编辑工具", "wait" => "等待工具", "exec" => "任务编排工具", _ => "工具" };
    private static bool IsTestTool(JsonElement payload)
    {
        if (Str(payload, "name") is not ("shell_command" or "exec_command")) return false;
        var arguments = Str(payload, "arguments") ?? "";
        return Regex.IsMatch(arguments, @"(?i)\b(test|build|lint|assemble|verify|check)\b");
    }
    private static string? Short(string? s) => string.IsNullOrWhiteSpace(s) ? null : AgentStore.Sanitize(s).ReplaceLineEndings(" ")[..Math.Min(80, AgentStore.Sanitize(s).ReplaceLineEndings(" ").Length)];
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CodexPhoneReminder.Agent;

public sealed class CodexCliRunner(CodexCliLocator locator, CodexWorkspaceRegistry workspaces, AgentStore store, CodexModelCatalog models, ILogger<CodexCliRunner> logger)
{
    private readonly ConcurrentDictionary<string, ManagedRun> _active = [];
    private readonly ConcurrentDictionary<string, CliRunInfo> _runs = [];

    public CliRunInfo? Get(string runId) => _runs.GetValueOrDefault(runId);
    public CliRunInfo? ActiveForThread(string threadId) => _active.TryGetValue(threadId, out var run) ? _runs.GetValueOrDefault(run.RunId) : null;
    public bool Cancel(string runId)
    {
        var run = _active.Values.FirstOrDefault(x => x.RunId == runId);
        if (run is null) return false;
        run.Cancelled = true;
        try { if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public async Task<string> ResumeAsync(string threadId, string prompt, CancellationToken cancellationToken, string? model = null)
    {
        var runId = Guid.NewGuid().ToString("N");
        if (_active.ContainsKey(threadId)) throw new InvalidOperationException("该任务已有一个 CLI 恢复运行正在执行");
        Process process;
        try
        {
            if (locator.ResolvedPath is null) throw new FileNotFoundException("未找到 codex.exe；请在 appsettings.json 的 CodexCli:Executable 中配置完整路径");
            var workspace = workspaces.GetRequired(threadId);
            var psi = new ProcessStartInfo(locator.ResolvedPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = workspace
            };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add("--cd"); psi.ArgumentList.Add(workspace);
            psi.ArgumentList.Add("--sandbox"); psi.ArgumentList.Add("workspace-write");
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("resume");
            if (!string.IsNullOrWhiteSpace(model))
            {
                if (!models.IsAllowed(model)) throw new InvalidOperationException("所选模型不在本机 Codex 可用模型目录中");
                psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);
            }
            psi.ArgumentList.Add(threadId); psi.ArgumentList.Add(prompt);
            process = Process.Start(psi) ?? throw new InvalidOperationException("Codex CLI 进程未能启动");
        }
        catch { throw; }
        var managed = new ManagedRun(runId, process);
        if (!_active.TryAdd(threadId, managed)) { process.Kill(entireProcessTree: true); process.Dispose(); throw new InvalidOperationException("该任务已有一个 CLI 恢复运行正在执行"); }
        _runs[runId] = new(runId, threadId, "running", DateTimeOffset.UtcNow);
        _ = Task.Run(async () =>
        {
            try
            {
                store.Add(new($"cli-run-{runId}", threadId, "Codex CLI", "恢复对话", EventType.Resumed, "已从手机交给 Codex CLI 继续处理"));
                using (process)
                {
                while (await process.StandardOutput.ReadLineAsync() is { } line) ConsumeJsonLine(threadId, runId, line);
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (managed.Cancelled)
                {
                    _runs[runId] = _runs[runId] with { Status = "cancelled", FinishedAt = DateTimeOffset.UtcNow, ExitCode = process.ExitCode };
                    store.Add(new($"cli-cancel-{runId}", threadId, "Codex CLI", "恢复对话", EventType.Cancelled, "已从手机取消本次 CLI 运行"));
                }
                else if (process.ExitCode != 0)
                {
                    _runs[runId] = _runs[runId] with { Status = "failed", FinishedAt = DateTimeOffset.UtcNow, ExitCode = process.ExitCode };
                    store.Add(new($"cli-exit-{runId}", threadId, "Codex CLI", "恢复对话", EventType.Failed, $"Codex CLI 异常退出（{process.ExitCode}）", AgentStore.Sanitize(error)));
                }
                else _runs[runId] = _runs[runId] with { Status = "completed", FinishedAt = DateTimeOffset.UtcNow, ExitCode = 0 };
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Failed to resume Codex thread {ThreadId}", threadId); _runs[runId] = _runs[runId] with { Status = "failed", FinishedAt = DateTimeOffset.UtcNow }; store.Add(new($"cli-error-{runId}", threadId, "Codex CLI", "恢复对话", EventType.Failed, "Codex CLI 运行失败", ex.Message)); }
            finally { _active.TryRemove(threadId, out _); }
        }, CancellationToken.None);
        await Task.Yield(); return runId;
    }

    private void ConsumeJsonLine(string threadId, string runId, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is "turn.completed" or "task_complete") store.Add(new($"cli-complete-{runId}", threadId, "Codex CLI", "恢复对话", EventType.Completed, ExtractText(root) ?? "当前对话已完成"));
            else if (type is "turn.failed" or "error") store.Add(new($"cli-failed-{runId}", threadId, "Codex CLI", "恢复对话", EventType.Failed, ExtractText(root) ?? "Codex CLI 执行失败"));
        }
        catch (JsonException) { logger.LogDebug("Ignored non-JSON Codex output line"); }
    }
    private static string? ExtractText(JsonElement e)
    {
        foreach (var key in new[] { "text", "message", "output", "last_agent_message" }) if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return AgentStore.Sanitize(v.GetString());
        return null;
    }
    private sealed class ManagedRun(string runId, Process process)
    {
        public string RunId { get; } = runId;
        public Process Process { get; } = process;
        public volatile bool Cancelled;
    }
}

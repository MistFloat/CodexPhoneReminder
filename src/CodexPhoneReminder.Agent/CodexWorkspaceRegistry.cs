using System.Collections.Concurrent;

namespace CodexPhoneReminder.Agent;

/// <summary>Keeps absolute workspace paths on the Windows agent only; they are never serialized to the phone API.</summary>
public sealed class CodexWorkspaceRegistry
{
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _paths.Count;

    public void Register(string threadId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return;
        _paths[threadId] = Path.GetFullPath(path);
    }

    public string GetRequired(string threadId) => _paths.TryGetValue(threadId, out var path)
        ? path
        : throw new DirectoryNotFoundException("没有找到该对话的本地工作区；请等待会话扫描完成后重试");
}

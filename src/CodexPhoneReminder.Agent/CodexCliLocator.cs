using Microsoft.Extensions.Options;

namespace CodexPhoneReminder.Agent;

public sealed class CodexCliLocator
{
    public string? ResolvedPath { get; }
    public string DisplayPath => ResolvedPath ?? "未找到";

    public CodexCliLocator(IOptions<CodexCliOptions> options)
    {
        ResolvedPath = Resolve(options.Value.Executable);
    }

    private static string? Resolve(string configured)
    {
        if (Path.IsPathFullyQualified(configured)) return File.Exists(configured) ? configured : null;
        var names = Path.HasExtension(configured) ? new[] { configured } : new[] { configured + ".exe", configured };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var name in names)
            {
                try { var candidate = Path.Combine(directory, name); if (File.Exists(candidate)) return candidate; }
                catch { }
            }
        return null;
    }
}

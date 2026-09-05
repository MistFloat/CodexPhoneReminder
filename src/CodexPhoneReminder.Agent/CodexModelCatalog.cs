using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexPhoneReminder.Agent;

public sealed class CodexModelCatalog
{
    public ModelCatalogSnapshot Get()
    {
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var defaultModel = ReadDefault(Path.Combine(home, "config.toml"));
        var models = ReadModels(Path.Combine(home, "models_cache.json"));
        if (models.Count == 0 && defaultModel is not null) models.Add(new(defaultModel, defaultModel));
        return new(defaultModel, models);
    }

    public bool IsAllowed(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        return Get().Models.Any(item => string.Equals(item.Id, model, StringComparison.Ordinal));
    }

    private static string? ReadDefault(string path)
    {
        if (!File.Exists(path)) return null;
        var match = Regex.Match(File.ReadAllText(path), "(?m)^model\\s*=\\s*[\"']([^\"']+)[\"']");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<ModelOption> ReadModels(string path)
    {
        var result = new List<ModelOption>();
        if (!File.Exists(path)) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var model in document.RootElement.GetProperty("models").EnumerateArray())
            {
                if (model.TryGetProperty("visibility", out var visibility) && visibility.GetString() != "list") continue;
                var id = model.GetProperty("slug").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = model.TryGetProperty("display_name", out var display) ? display.GetString() : id;
                result.Add(new(id, name ?? id));
            }
        }
        catch (JsonException) { }
        return result;
    }
}

public sealed record ModelOption(string Id, string Name);
public sealed record ModelCatalogSnapshot(string? DefaultModel, IReadOnlyList<ModelOption> Models);

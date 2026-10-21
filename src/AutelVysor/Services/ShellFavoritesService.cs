using System.Text.Json;

namespace AutelVysor.Services;

public sealed class ShellFavoritesService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Autel", "AutelVysor", "shell-favorites.json");

    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<string>();
            return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Take(100)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public void Save(IEnumerable<string> commands)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var data = commands.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).Take(100).ToArray();
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}

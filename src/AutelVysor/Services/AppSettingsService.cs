using System.Text.Json;
using AutelVysor.Models;

namespace AutelVysor.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettingsService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autel", "AutelVysor");
        _path = Path.Combine(folder, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}

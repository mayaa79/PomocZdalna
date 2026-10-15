using System.IO;
using System.Text.Json;

namespace PomocZdalna.App.Core;

public sealed class Settings
{
    /// <summary>
    /// Domyślny adres serwera. Przed zbudowaniem wersji dla klientów zamień na publiczny adres swojego laptopa
    /// (nazwa DDNS lub IP z przekierowanym portem), np. "ws://moj-laptop.ddns.net:5080/ws".
    /// </summary>
    public const string DefaultServerUrl = "ws://localhost:5080/ws";

    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>Uruchamiaj serwer relay w tle razem z aplikacją (tryb technika).</summary>
    public bool EmbeddedServer { get; set; } = true;

    public int ServerPort { get; set; } = 5080;
    public string? HostId { get; set; }
    public string TechName { get; set; } = Environment.UserName;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PomocZdalna", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

using System.Text.Json;
using PSUPanel.Shared;

namespace PSUPanel.Tray;

internal sealed class SettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PSUPanel",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                MonitorJsonContext.Default.AppSettings) ?? new AppSettings();
            settings.PollIntervalMilliseconds = Math.Clamp(settings.PollIntervalMilliseconds, 500, 5000);
            return settings;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(settings, MonitorJsonContext.Default.AppSettings);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, true);
    }
}

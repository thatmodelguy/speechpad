using System.IO;
using System.Text.Json;

namespace SpeechPad.Services;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>WASAPI device id, or null/empty for default.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Copy notes to the clipboard automatically when dictation stops.</summary>
    public bool AutoCopyOnStop { get; set; } = true;

    /// <summary>"Fast" (base.en) or "Accurate" (small.en) speech model.</summary>
    public string Model { get; set; } = nameof(WhisperModelKind.Accurate);

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpeechPad",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = SettingsPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}

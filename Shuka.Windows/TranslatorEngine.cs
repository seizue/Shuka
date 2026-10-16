using System.Text.Json;

namespace Shuka;

public enum TranslatorEngine { Google, LocalLlama, PlaywrightTranslate }

public sealed class TranslatorSettings
{
    public TranslatorEngine Engine { get; set; } = TranslatorEngine.Google;

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shuka", "translator-settings.json");

    public static TranslatorSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return new TranslatorSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TranslatorSettings>(json)
                   ?? new TranslatorSettings();
        }
        catch { return new TranslatorSettings(); }
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}

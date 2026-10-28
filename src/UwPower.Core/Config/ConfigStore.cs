using System.Text.Json;
using System.Text.Json.Serialization;

namespace UwPower.Core.Config;

/// <summary>Reads and writes config.json in the app data folder. Writes are read-modify-write so the
/// CLI and the tray app don't overwrite each other's changes.</summary>
public static class ConfigStore
{
    private static readonly object Gate = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "config.json");

    public static AppConfig Load()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath))
                return new AppConfig();

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50); // another process is mid-write
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"{FilePath} is not valid: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>Loads the latest config from disk, applies <paramref name="change"/>, saves and returns it.</summary>
    public static AppConfig Update(Action<AppConfig> change)
    {
        lock (Gate)
        {
            var config = Load();
            change(config);
            Save(config);
            return config;
        }
    }

    private static void Save(AppConfig config)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}

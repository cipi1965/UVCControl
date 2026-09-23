using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace UVCControl.App.Services;

/// <summary>Small persistent key/value store for UI preferences.</summary>
public interface ISettingsStore
{
    string? GetString(string key);
    double? GetDouble(string key);
    void Set(string key, string? value);
    void Set(string key, double value);
}

/// <summary>Stores settings as JSON in the per-user application data folder.</summary>
internal sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _values;

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger)
    {
        _logger = logger;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVCControl");
        _path = Path.Combine(folder, "settings.json");
        _values = Load();
    }

    public string? GetString(string key)
    {
        lock (_lock) return _values.GetValueOrDefault(key);
    }

    public double? GetDouble(string key) =>
        double.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        lock (_lock)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
            Save();
        }
    }

    public void Set(string key, double value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.DictionaryStringString) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not read settings from {Path}", _path);
        }
        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, SettingsJsonContext.Default.DictionaryStringString));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not save settings to {Path}", _path);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

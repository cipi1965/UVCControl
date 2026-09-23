using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using UVCControl.Core.Ptz;

namespace UVCControl.App.Services;

/// <summary>A saved camera position. Axes left null aren't part of the preset and stay put on recall.</summary>
public sealed record PtzPreset(Guid Id, string Name, long? Pan, long? Tilt, long? Zoom, long? Focus)
{
    [JsonIgnore]
    public PtzPosition Position => new(Pan, Tilt, Zoom, Focus);
}

/// <summary>Presets per camera model, keyed by VID:PID.</summary>
public interface IPresetStore
{
    IReadOnlyList<PtzPreset> Load(string cameraKey);
    void Save(string cameraKey, IEnumerable<PtzPreset> presets);
}

/// <summary>Stores presets as JSON next to the settings file.</summary>
internal sealed class JsonPresetStore : IPresetStore
{
    private readonly string _path;
    private readonly ILogger<JsonPresetStore> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<PtzPreset>> _presets;

    public JsonPresetStore(ILogger<JsonPresetStore> logger)
    {
        _logger = logger;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVCControl");
        _path = Path.Combine(folder, "presets.json");
        _presets = Read();
    }

    public IReadOnlyList<PtzPreset> Load(string cameraKey)
    {
        lock (_lock) return _presets.GetValueOrDefault(cameraKey)?.ToList() ?? [];
    }

    public void Save(string cameraKey, IEnumerable<PtzPreset> presets)
    {
        lock (_lock)
        {
            _presets[cameraKey] = presets.ToList();
            Write();
        }
    }

    private Dictionary<string, List<PtzPreset>> Read()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), PresetJsonContext.Default.DictionaryStringListPtzPreset) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not read presets from {Path}", _path);
        }
        return [];
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_presets, PresetJsonContext.Default.DictionaryStringListPtzPreset));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not save presets to {Path}", _path);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, List<PtzPreset>>))]
internal sealed partial class PresetJsonContext : JsonSerializerContext;

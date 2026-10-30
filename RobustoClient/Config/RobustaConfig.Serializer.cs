using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Robust.Client.Input;

namespace RobustoClient;

public static partial class RobustaConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const BindingFlags PersistentFields = BindingFlags.Public | BindingFlags.Static;

    static RobustaConfig()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
    }

    private static string GetConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "RobustoClient", "Configs", "config.json");
    }

    /// <summary>Location of the persisted config, surfaced in logs for troubleshooting.</summary>
    public static string ConfigPath => GetConfigPath();

    public static void Save()
    {
        try
        {
            var file = GetConfigPath();
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var root = new JsonObject();
            foreach (var field in typeof(RobustaConfig).GetFields(PersistentFields))
            {
                if (field.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                    continue;

                // Explicit field type keeps enum/HashSet encoding identical to the previous hand-written DTO.
                root[field.Name] = JsonSerializer.SerializeToNode(field.GetValue(null), field.FieldType, JsonOptions);
            }

            File.WriteAllText(file, root.ToJsonString(JsonOptions));
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to save config.json: {ex.Message}");
        }
    }

    public static void Load()
    {
        try
        {
            var file = GetConfigPath();
            if (!File.Exists(file))
            {
                // First run: write the compiled-in defaults so there is a file to edit.
                Save();
                if (File.Exists(file))
                    MarseyLogger.Info($"Created default config at {file}");
                return;
            }

            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root)
                return;

            foreach (var field in typeof(RobustaConfig).GetFields(PersistentFields))
            {
                if (field.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                    continue;

                // Missing keys keep the compiled-in default, so old config files stay forward compatible.
                if (!root.TryGetPropertyValue(field.Name, out var node))
                    continue;

                // Explicit null in the file is treated as absent: the compiled-in default is kept.
                if (node == null)
                    continue;

                // Isolate failures so one malformed value cannot discard the rest of the config.
                try
                {
                    field.SetValue(null, JsonSerializer.Deserialize(node.ToJsonString(), field.FieldType, JsonOptions));
                }
                catch (JsonException ex)
                {
                    MarseyLogger.Warn($"Ignored invalid value for '{field.Name}' in config.json: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to load config.json: {ex.Message}");
        }
    }
}
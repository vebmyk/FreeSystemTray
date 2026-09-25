using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSystemTray.Placement;

namespace FreeSystemTray.Settings
{
    public sealed class AppSettings
    {
        /// <summary>Where the window's anchor corner is. Null until the window is first moved.</summary>
        public SavedPosition? Position { get; set; }

        /// <summary>When true, the window cannot be dragged.</summary>
        public bool LockPosition { get; set; }

        public bool SnapToEdges { get; set; } = true;

        /// <summary>How close an edge must come before it snaps, in device-independent pixels.</summary>
        public double SnapDistance { get; set; } = 12;
    }

    /// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
    public sealed class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public SettingsStore(string path)
        {
            FilePath = path;
        }

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeSystemTray", "settings.json");

        public string FilePath { get; }

        /// <summary>The saved settings, or defaults if the file is missing or unreadable (a broken file is kept as .bad).</summary>
        public AppSettings Load(Action<string>? log = null)
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                log?.Invoke($"Settings file {FilePath} could not be read ({ex.Message}); using defaults");
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch (IOException) { }
                return new AppSettings();
            }
        }

        /// <summary>Writes the settings atomically (temporary file, then replace).</summary>
        public void Save(AppSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, true);
        }
    }
}

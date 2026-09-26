using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSystemTray.Placement;
using FreeSystemTray.Scaling;
using FreeSystemTray.ShowHide;

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

        /// <summary>When the window shows and hides, and how.</summary>
        public ShowHideSettings ShowHide { get; set; } = new();

        /// <summary>Icon size and the size of the tray around the icons.</summary>
        public ScalingSettings Scaling { get; set; } = new();
    }

    /// <summary>
    /// The two sizes are independent: changing one never changes the other. Both take a percentage
    /// ("167%") or pixels ("24px", or a number); pixels are device-independent, so the tray has the
    /// same size on a 150% monitor as on a 100% one.
    /// </summary>
    public sealed class ScalingSettings
    {
        /// <summary>The icons' size. 100% is 16 px.</summary>
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string IconSize { get; set; } = "100%";

        /// <summary>
        /// Everything around the icons: the padding in each icon's cell, the grip, the border and the
        /// margins. A percentage scales all of it; pixels set the size of each icon's cell.
        /// </summary>
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string TraySize { get; set; } = "100%";

        /// <summary>
        /// Round sizes to whole screen pixels: sharper, but a little off the requested size. Off gives
        /// the exact size, somewhat softer.
        /// </summary>
        public bool SnapToPixels { get; set; } = true;

        public IconScaling IconScaling { get; set; } = IconScaling.Smooth;

        /// <summary>
        /// Icons drawn with their app's .exe icon instead of the image the app hands over: sharper at
        /// large sizes, but without status such as unread counts. Exe file names ("Discord.exe"), full
        /// paths or icon ids.
        /// </summary>
        public List<string> ExeIcons { get; set; } = new();
    }

    /// <summary>
    /// Show/hide settings. Hidden means only the window is hidden: the tray host keeps running and
    /// icons keep registering.
    /// </summary>
    public sealed class ShowHideSettings
    {
        /// <summary>
        /// False: always visible (the hotkey still hides it until it is pressed again). True: hidden
        /// until the hotkey or the hotspot shows it, and hidden again by a click elsewhere or a timer.
        /// </summary>
        public bool AutoHide { get; set; }

        /// <summary>Auto-hide: hide when the user clicks outside the tray or switches to another window.</summary>
        public bool HideOnClickAway { get; set; } = true;

        /// <summary>Auto-hide: hide this long after the pointer leaves the tray. 0 turns the timer off.</summary>
        public int HideDelayMs { get; set; } = 1500;

        /// <summary>Global hotkey that shows or hides the tray, for example "Ctrl+Alt+Shift+T". Empty for none.</summary>
        public string Hotkey { get; set; } = "Ctrl+Alt+Shift+T";

        /// <summary>A screen edge or corner of the tray's monitor that shows the tray when the pointer rests there.</summary>
        public HotspotLocation Hotspot { get; set; } = HotspotLocation.None;

        /// <summary>How long the pointer must rest in the hotspot.</summary>
        public int HotspotDelayMs { get; set; } = 300;

        /// <summary>Show and hide animation. Off while Windows' "Show animations in Windows" is off.</summary>
        public TrayAnimation Animation { get; set; } = TrayAnimation.Fade;

        public int AnimationMs { get; set; } = 150;
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

        /// <summary>The file's contents as this store last read or wrote them, to tell our own writes from the user's.</summary>
        public string? LastKnownContent { get; private set; }

        /// <summary>The saved settings, or defaults if the file is missing or unreadable (a broken file is kept as .bad).</summary>
        public AppSettings Load(Action<string>? log = null)
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            try
            {
                string json = File.ReadAllText(FilePath);
                AppSettings settings = Parse(json);
                LastKnownContent = json;
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                log?.Invoke($"Settings file {FilePath} could not be read ({ex.Message}); using defaults");
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch (IOException) { }
                return new AppSettings();
            }
        }

        /// <summary>
        /// Reads the file again after it changed on disk. Null if it is unchanged since this store last
        /// read or wrote it (our own save), missing, or not valid; <paramref name="error"/> says which.
        /// Unlike <see cref="Load"/>, a broken file is left alone: it may be half-saved by an editor.
        /// </summary>
        public AppSettings? Reload(out string? error)
        {
            error = null;
            try
            {
                string json = File.ReadAllText(FilePath);
                if (json == LastKnownContent) return null;
                AppSettings settings = Parse(json);
                LastKnownContent = json;
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Writes the settings atomically (temporary file, then replace).</summary>
        public void Save(AppSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            LastKnownContent = json;
            File.Move(temp, FilePath, true);
        }

        private static AppSettings Parse(string json)
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.ShowHide ??= new ShowHideSettings(); // "showHide": null
            settings.Scaling ??= new ScalingSettings();
            settings.Scaling.IconSize ??= "100%";
            settings.Scaling.TraySize ??= "100%";
            settings.Scaling.ExeIcons ??= new List<string>();
            return settings;
        }
    }
}

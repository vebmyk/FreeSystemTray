using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace FreeSystemTray.Theming
{
    public sealed record ThemeInfo(string Id, string Name, string Path, bool BuiltIn);

    /// <summary>
    /// Theme files: the user's own in a <c>themes</c> folder next to the settings file, and the
    /// built-in ones next to the exe. A theme's id is its file name without ".json"; a user theme
    /// with a built-in theme's id replaces it.
    /// </summary>
    public sealed class ThemeStore
    {
        public const string DefaultId = "Dark";
        private const int MaxChain = 8;

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public ThemeStore(string userDirectory, string builtInDirectory)
        {
            UserDirectory = userDirectory;
            BuiltInDirectory = builtInDirectory;
        }

        public string UserDirectory { get; }
        public string BuiltInDirectory { get; }

        public static string UserDirectoryFor(string settingsPath) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "themes");

        public static string DefaultBuiltInDirectory => Path.Combine(AppContext.BaseDirectory, "Themes");

        /// <summary>All themes, by name.</summary>
        public IReadOnlyList<ThemeInfo> List()
        {
            var found = new Dictionary<string, ThemeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach ((string dir, bool builtIn) in new[] { (BuiltInDirectory, true), (UserDirectory, false) })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string path in Directory.EnumerateFiles(dir, "*.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    ThemeFile? file = Read(path, new List<string>());
                    found[id] = new ThemeInfo(id, string.IsNullOrWhiteSpace(file?.Name) ? id : file!.Name!.Trim(), path, builtIn);
                }
            }
            return found.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>The file of theme <paramref name="id"/>: the user's own if there is one, else the built-in one.</summary>
        public string? PathOf(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            foreach (string dir in new[] { UserDirectory, BuiltInDirectory })
            {
                string path = Path.Combine(dir, id + ".json");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// Theme <paramref name="id"/> with every value filled in: its own values, then those of the
        /// themes it is based on, then the built-in defaults. A theme that cannot be found or read
        /// gives the defaults. What went wrong is added to <paramref name="problems"/>.
        /// </summary>
        public Theme Load(string id, List<string> problems)
        {
            var chain = new List<(string Id, ThemeFile File, string Directory)>();
            var seen = new List<string>();
            string? next = string.IsNullOrWhiteSpace(id) ? DefaultId : id.Trim();
            while (next != null)
            {
                if (seen.Contains(next, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"Theme '{seen[^1]}' is based on '{next}', which is based on it: {string.Join(" > ", seen.Append(next))}");
                    break;
                }
                seen.Add(next);
                if (chain.Count >= MaxChain)
                {
                    problems.Add($"Themes based on each other more than {MaxChain} deep: {string.Join(" > ", seen)}");
                    break;
                }
                string? path = PathOf(next);
                if (path == null)
                {
                    problems.Add($"Theme '{next}' not found in {UserDirectory} or {BuiltInDirectory}");
                    break;
                }
                if (Read(path, problems) is not ThemeFile file) break;
                chain.Add((next, file, Path.GetDirectoryName(path)!));
                next = string.IsNullOrWhiteSpace(file.BasedOn) ? null : file.BasedOn.Trim();
            }

            Theme theme = Theme.Default;
            for (int i = chain.Count - 1; i >= 0; i--) theme = theme.With(chain[i].File, chain[i].Id, chain[i].Directory, problems);
            if (theme.XamlPath != null && !File.Exists(theme.XamlPath))
            {
                problems.Add($"XAML file {theme.XamlPath} not found");
                theme = theme with { XamlPath = null };
            }
            return theme;
        }

        /// <summary>
        /// Copies a theme file (and a XAML file it names next to it) into the user's theme folder. The
        /// theme's id is the file's name. False, with the reason in <paramref name="problems"/>, if it
        /// is not a readable theme, or its XAML file would land outside the themes folder.
        /// </summary>
        public bool Import(string source, out string id, List<string> problems)
        {
            id = Path.GetFileNameWithoutExtension(source);
            if (!File.Exists(source))
            {
                problems.Add($"{source} not found");
                return false;
            }
            if (Read(source, problems) is not ThemeFile file) return false;
            string? xaml = null, xamlTarget = null;
            if (!string.IsNullOrWhiteSpace(file.Xaml) && !Path.IsPathRooted(file.Xaml))
            {
                xaml = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, file.Xaml);
                xamlTarget = Path.GetFullPath(Path.Combine(UserDirectory, file.Xaml));
                // "xaml": "..\\settings.json" would otherwise overwrite a file outside the themes folder.
                if (!IsInside(xamlTarget, UserDirectory))
                {
                    problems.Add($"{source}: its XAML file \"{file.Xaml}\" must be in the theme's folder or below it");
                    return false;
                }
            }
            Directory.CreateDirectory(UserDirectory);
            string target = Path.Combine(UserDirectory, id + ".json");
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, true);
            if (xaml != null && xamlTarget != null && File.Exists(xaml) &&
                !string.Equals(Path.GetFullPath(xaml), xamlTarget, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(xamlTarget)!);
                File.Copy(xaml, xamlTarget, true);
            }
            return true;
        }

        /// <summary>
        /// Writes <paramref name="theme"/> as a complete theme file, every value spelled out, so it
        /// needs no other theme. Its XAML file, if any, is copied next to it.
        /// </summary>
        public static void Export(Theme theme, string target)
        {
            ThemeFile file = theme.ToFile();
            string dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
            Directory.CreateDirectory(dir);
            if (theme.XamlPath != null)
            {
                string xamlTarget = Path.Combine(dir, Path.GetFileName(theme.XamlPath));
                if (!string.Equals(Path.GetFullPath(theme.XamlPath), xamlTarget, StringComparison.OrdinalIgnoreCase))
                    File.Copy(theme.XamlPath, xamlTarget, true);
                file.Xaml = Path.GetFileName(theme.XamlPath);
            }
            File.WriteAllText(target, JsonSerializer.Serialize(file, Json));
        }

        private static bool IsInside(string fullPath, string directory)
        {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }

        private static ThemeFile? Read(string path, List<string> problems)
        {
            try
            {
                return JsonSerializer.Deserialize<ThemeFile>(File.ReadAllText(path), Json) ?? new ThemeFile();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problems.Add($"{path} could not be read: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Calls back on the UI thread when theme files in the user's folder change, after a pause for an editor's several writes.</summary>
    internal sealed class ThemeWatcher : IDisposable
    {
        private readonly FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _debounce;

        public ThemeWatcher(string directory, Dispatcher dispatcher, Action changed, Action<string> log)
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                changed();
            };
            try
            {
                Directory.CreateDirectory(directory);
                _watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler restart = (_, e) =>
                {
                    if (e.Name == null || !(e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))) return;
                    dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        _debounce.Stop();
                        _debounce.Start();
                    });
                };
                _watcher.Changed += restart;
                _watcher.Created += restart;
                _watcher.Deleted += restart;
                _watcher.Renamed += (s, e) => restart(s, e);
                // Windows dropped notifications (its buffer overflowed, or the folder became unreachable):
                // a change may have been missed, so say so and load the theme again.
                _watcher.Error += (_, e) => dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    log($"Watching {directory} for theme changes failed ({e.GetException().Message}); loading the theme again");
                    _debounce.Stop();
                    _debounce.Start();
                });
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                log($"Cannot watch {directory} for theme changes ({ex.Message}); edits apply after a restart");
            }
        }

        public void Dispose()
        {
            _debounce.Stop();
            _watcher?.Dispose();
        }
    }
}

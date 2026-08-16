using System.Text.Json;
using IOPath = System.IO.Path;

namespace CCCOG.Bar.App;

/// <summary>
/// Minimal local settings store, adapted from TokenBar-Windows'
/// <c>TokenBar.Core/SettingsStore.cs</c> (wired up via
/// <c>TokenBar.App/AppSettings.cs</c> to <c>%APPDATA%\TokenBar\settings.json</c>).
/// CCCOG has no equivalent store, so this trims TokenBar's typed
/// String/Bool/Int/Double surface plus its <c>Changed</c> event down to the
/// single string Get/Set the resize-grip height persistence (bar/SYNC.md)
/// needs, at <c>%LOCALAPPDATA%\CCCG\bar-settings.json</c>. Same
/// atomic-write (temp file + rename) and load-failure/corruption handling
/// as the source; every entry point swallows its own exceptions — a broken
/// settings file must never crash the flyout.
/// </summary>
internal static class LocalSettings
{
    private static readonly string FilePath = IOPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CCCG", "bar-settings.json");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Values;
    private static readonly bool LoadFailed;

    static LocalSettings()
    {
        Values = Load(out LoadFailed);
    }

    private static Dictionary<string, string> Load(out bool loadFailed)
    {
        loadFailed = false;
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(FilePath)) ?? [];
            }
        }
        catch (JsonException)
        {
            // Genuine corruption: quarantine the unreadable file so it isn't
            // silently overwritten (it may be hand-recoverable), then rebuild
            // from defaults on the next write.
            Quarantine();
        }
        catch
        {
            // Transient read failure (an AV scan or sync tool holding the
            // file with a deny-read lock): the on-disk store is probably
            // intact, so DON'T let the next Save() clobber it with
            // defaults — refuse to persist for this session instead of
            // destroying good settings.
            loadFailed = true;
        }

        return [];
    }

    private static void Quarantine()
    {
        try
        {
            var corrupt = FilePath + ".corrupt";
            File.Delete(corrupt); // no-op if absent; File.Move won't overwrite
            File.Move(FilePath, corrupt);
        }
        catch
        {
            // Best effort; a later Save() overwrites the bad file if this fails.
        }
    }

    public static string? GetString(string key, string? fallback = null)
    {
        lock (Gate)
        {
            return Values.TryGetValue(key, out var value) ? value : fallback;
        }
    }

    public static void SetString(string key, string value)
    {
        lock (Gate)
        {
            if (Values.TryGetValue(key, out var old) && old == value)
            {
                return;
            }

            Values[key] = value;
            Save();
        }
    }

    private static void Save()
    {
        if (LoadFailed)
        {
            // The existing store couldn't be read at startup; writing now
            // would replace possibly-good on-disk settings with in-memory
            // defaults.
            return;
        }

        try
        {
            var dir = IOPath.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Values));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Disk trouble (AV scan or a sync tool holding the file) must
            // not crash the caller: memory keeps the new value and the next
            // successful save heals the file.
        }
    }
}

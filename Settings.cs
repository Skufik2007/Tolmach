using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Tolmach;

internal sealed class HotkeySettings
{
    // The selected text; with nothing selected, the line under the cursor
    // (read from the screen).
    public string TranslateSelection { get; set; } = "Alt+Q";
    // Drag a rectangle over any part of the screen.
    public string TranslateRegion { get; set; } = "Ctrl+Ё";
}

internal sealed class Settings
{
    // Everything is translated into this language...
    public string TargetLanguage { get; set; } = "ru";
    // ...except text already in it, which goes into this one.
    public string SecondLanguage { get; set; } = "en";

    // "auto": DeepL when there is a key, then Google, then MyMemory, each
    // standing in for the one before when it's blocked or out of quota.
    // "google", "mymemory" or "deepl": that one first, the others after.
    public string Provider { get; set; } = "auto";
    public string DeepLKey { get; set; } = "";

    // Pressing Ctrl+C twice quickly translates what was copied.
    public bool DoubleCopy { get; set; } = true;
    public bool ShowOriginal { get; set; } = true;

    // Offered in the popup for translating into something else.
    public List<string> Languages { get; set; } = new() { "ru", "en", "uk", "de", "fr", "es", "it", "pl", "tr", "ja", "ko", "zh-CN" };

    public HotkeySettings Hotkeys { get; set; } = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tolmach");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Current { get; private set; } = new();

    // Readable Cyrillic and "+" in hotkeys: the file is meant for editing.
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Settings load failed: " + ex.Message);
            Current = new Settings();
        }
        Current.Hotkeys ??= new HotkeySettings();
        if (Current.Languages == null || Current.Languages.Count == 0)
        {
            Current.Languages = new Settings().Languages;
        }
        Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write("Settings save failed: " + ex.Message);
        }
    }
}

internal static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                string file = Path.Combine(Settings.Folder, "log.txt");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                {
                    File.Delete(file);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}

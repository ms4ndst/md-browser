using System;
using System.IO;

namespace MdBrowser.Services;

public enum EditorPosition { Hidden, Bottom, Right, Top, Left }

public static class LayoutSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MdBrowser",
        "layout.txt");

    public static EditorPosition LoadOrDefault()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var text = File.ReadAllText(SettingsPath).Trim();
                if (Enum.TryParse<EditorPosition>(text, ignoreCase: true, out var pos))
                {
                    return pos;
                }
            }
        }
        catch { /* fall through */ }
        return EditorPosition.Bottom;
    }

    public static void Save(EditorPosition pos)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, pos.ToString());
        }
        catch { /* best-effort */ }
    }

    public static string DisplayName(EditorPosition pos) => pos switch
    {
        EditorPosition.Hidden => "Hidden",
        EditorPosition.Bottom => "Bottom",
        EditorPosition.Right => "Right",
        EditorPosition.Top => "Top",
        EditorPosition.Left => "Left",
        _ => pos.ToString()
    };
}

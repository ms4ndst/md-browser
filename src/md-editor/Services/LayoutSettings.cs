using System;
using System.IO;

namespace MdEditor.Services;

public enum EditorPosition { Hidden, Bottom, Right, Top, Left }

public static class LayoutSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "md-editor",
        "layout.txt");

    private static readonly string ExplorerSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "md-editor",
        "explorer.txt");

    public static bool LoadExplorerVisibleOrDefault()
    {
        try
        {
            if (File.Exists(ExplorerSettingsPath))
            {
                var text = File.ReadAllText(ExplorerSettingsPath).Trim();
                if (bool.TryParse(text, out var visible))
                {
                    return visible;
                }
            }
        }
        catch { /* fall through */ }
        return true;
    }

    public static void SaveExplorerVisible(bool visible)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExplorerSettingsPath)!);
            File.WriteAllText(ExplorerSettingsPath, visible.ToString());
        }
        catch { /* best-effort */ }
    }

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

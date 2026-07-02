using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace MdEditor.Services;

public enum CatppuccinFlavor { Latte, Frappe, Macchiato, Mocha }

public static class ThemeManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "md-editor",
        "theme.txt");

    public static CatppuccinFlavor Current { get; private set; } = CatppuccinFlavor.Mocha;

    public static event Action<CatppuccinFlavor>? FlavorChanged;

    public static readonly IReadOnlyList<CatppuccinFlavor> All =
        new[] { CatppuccinFlavor.Mocha, CatppuccinFlavor.Macchiato, CatppuccinFlavor.Frappe, CatppuccinFlavor.Latte };

    public static bool IsDark(CatppuccinFlavor f) => f != CatppuccinFlavor.Latte;

    public static string DisplayName(CatppuccinFlavor f) => f switch
    {
        CatppuccinFlavor.Latte => "Latte (light)",
        CatppuccinFlavor.Frappe => "Frappé (dark warm)",
        CatppuccinFlavor.Macchiato => "Macchiato (dark)",
        CatppuccinFlavor.Mocha => "Mocha (dark deep)",
        _ => f.ToString()
    };

    public static CatppuccinFlavor LoadSavedOrDefault()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var name = File.ReadAllText(SettingsPath).Trim();
                if (Enum.TryParse<CatppuccinFlavor>(name, ignoreCase: true, out var f))
                {
                    return f;
                }
            }
        }
        catch { /* fall through */ }
        return CatppuccinFlavor.Mocha;
    }

    public static void Apply(CatppuccinFlavor flavor)
    {
        var fileName = flavor switch
        {
            CatppuccinFlavor.Latte => "CatppuccinLatte.xaml",
            CatppuccinFlavor.Frappe => "CatppuccinFrappe.xaml",
            CatppuccinFlavor.Macchiato => "CatppuccinMacchiato.xaml",
            CatppuccinFlavor.Mocha => "CatppuccinMocha.xaml",
            _ => "CatppuccinMocha.xaml"
        };

        // The App.xaml convention: index 0 is the flavor dictionary, index 1 is Controls.
        // We replace index 0 only.
        var newFlavor = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{fileName}", UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = newFlavor;
        }
        else
        {
            merged.Add(newFlavor);
        }

        Current = flavor;
        Save(flavor);
        FlavorChanged?.Invoke(flavor);
    }

    private static void Save(CatppuccinFlavor flavor)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, flavor.ToString());
        }
        catch { /* best-effort */ }
    }
}

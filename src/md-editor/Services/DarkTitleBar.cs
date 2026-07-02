using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MdEditor.Services;

internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    public static void Apply(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int flag = dark ? 1 : 0;
            // Try modern attribute first; fall back to legacy id used by Windows 10 1903-1909.
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref flag, sizeof(int));
            }
        }
        catch
        {
            // Non-fatal: older OS without DWM support.
        }
    }
}

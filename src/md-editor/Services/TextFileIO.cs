using System;
using System.IO;
using System.Text;

namespace MdEditor.Services;

public static class TextFileIO
{
    /// <summary>
    /// Read a text file, auto-detecting encoding.
    /// Order: BOM (UTF-8/UTF-16 LE+BE/UTF-32 LE) -> strict UTF-8 -> Windows-1252 fallback.
    /// Returns the decoded text plus the detected encoding name (for status display).
    /// </summary>
    public static (string Text, string EncodingName) ReadAutoDetect(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // ---- BOM detection ----
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return (Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4), "UTF-32 LE (BOM)");
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 (BOM)");
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE (BOM)");
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE (BOM)");
        }

        // ---- No BOM: try strict UTF-8 ----
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // ---- Fallback: Windows-1252 ----
            // Requires CodePagesEncodingProvider.Instance to be registered (done in App).
            try
            {
                return (Encoding.GetEncoding(1252).GetString(bytes), "Windows-1252");
            }
            catch
            {
                // Last resort - lossy ASCII
                return (Encoding.ASCII.GetString(bytes), "ASCII (lossy)");
            }
        }
    }

    /// <summary>
    /// Write text using UTF-8 without BOM. Matches what most modern markdown tooling expects.
    /// </summary>
    public static void WriteUtf8NoBom(string path, string text)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(path, text, encoding);
    }
}

using System;
using System.IO;
using System.Reflection;
using System.Text;
using Godot;

namespace CardsAndRelicsChooser;

internal static class Log
{
    private static readonly object Sync = new();
    private static string? _filePath;

    public static string FilePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_filePath))
            {
                return _filePath;
            }

            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = AppContext.BaseDirectory;
            }

            var logDir = Path.Combine(dir, "logs");
            _filePath = Path.Combine(logDir, "cards_and_relics_chooser.log");
            return _filePath;
        }
    }

    public static void Info(string message) => Write("INFO", message, isError: false);

    public static void Warn(string message) => Write("WARN", message, isError: false);

    public static void Error(string message) => Write("ERROR", message, isError: true);

    private static void Write(string level, string message, bool isError)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        if (isError)
        {
            GD.PrintErr($"[CardsAndRelicsChooser][{level}] {message}");
        }
        else
        {
            GD.Print($"[CardsAndRelicsChooser][{level}] {message}");
        }

        try
        {
            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            lock (Sync)
            {
                File.AppendAllText(FilePath, line + System.Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore file logging failures to keep gameplay unaffected.
        }
    }
}



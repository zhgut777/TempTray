using System;
using System.IO;

namespace TempTray;

/// <summary>
/// Журнал порогов зарядки: что прошивка сообщала при каждом изменении и пробуждении и что программа с этим делала.
/// Лежит в общей папке данных, чтобы его можно было прочитать без прав администратора.
/// </summary>
internal static class ChargeLog
{
    private const long MaxSize = 300 * 1024;
    private static readonly object Sync = new();

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TempTray", "charge.log");

    public static void Write(string text)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize)
                {
                    // Оставляем вторую половину, чтобы журнал не рос бесконечно
                    string[] lines = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, lines[(lines.Length / 2)..]);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:dd.MM HH:mm:ss}  {text}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}

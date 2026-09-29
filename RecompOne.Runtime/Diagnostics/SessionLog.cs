using System.Text;

namespace RecompOne.Runtime.Diagnostics;

//RECOMPONE_LOG_DIR set (a launcher starts the game without a console): everything the game writes to the console
//also goes to <dir>/game_<time>.log, the newest 20 are kept. That is what a bug report's diagnostics zip carries.
public static class SessionLog
{
    private const int Keep = 20;
    public static string? Path { get; private set; }

    public static void StartIfConfigured()
    {
        var dir = Environment.GetEnvironmentVariable("RECOMPONE_LOG_DIR");
        if (string.IsNullOrEmpty(dir) || Path != null) return;
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "game_*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(Keep - 1))
            File.Delete(old);
        Path = System.IO.Path.Combine(dir, $"game_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
        var file = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(TextWriter.Synchronized(new Tee(Console.Out, file)));
        Console.SetError(TextWriter.Synchronized(new Tee(Console.Error, file)));
        Console.WriteLine($"[Session] {DateTime.Now:yyyy-MM-dd HH:mm:ss} log {Path}");
    }

    private sealed class Tee(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;

        public override void Write(char value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void Write(string? value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void WriteLine(string? value)
        {
            a.WriteLine(value);
            b.WriteLine(value);
        }

        public override void Flush()
        {
            a.Flush();
            b.Flush();
        }
    }
}

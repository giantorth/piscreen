using System;
using System.IO;

namespace PiScreen
{
    /// <summary>Minimal file + stderr logger (%LOCALAPPDATA%\PiScreen\driver.log, rolled at 5 MB).</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static StreamWriter file;

        public static string FilePath { get; private set; }

        public static void Init(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                FilePath = Path.Combine(directory, "driver.log");
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 5 * 1024 * 1024)
                {
                    string old = FilePath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Log file unavailable: " + ex.Message);
            }
        }

        public static void Info(string message) => Write("INF", message);
        public static void Warn(string message) => Write("WRN", message);
        public static void Error(string message) => Write("ERR", message);

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message;
            lock (Gate)
            {
                try { file?.WriteLine(line); } catch { }
                try { Console.Error.WriteLine(line); } catch { }
            }
        }
    }
}

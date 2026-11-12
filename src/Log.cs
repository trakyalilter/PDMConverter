using System;
using System.IO;
using System.Text;

namespace PdmApproveConvert
{
    /// <summary>
    /// Tiny file logger. Never throws: a logging failure must not break PDM.
    /// Log: %LOCALAPPDATA%\PdmApproveConvert\addin.log (per user, per PC).
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;
        private const long MaxBytes = 2 * 1024 * 1024;

        private static string PathOf()
        {
            if (_path != null) return _path;
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PdmApproveConvert");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "addin.log");
            return _path;
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string p = PathOf();
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = p + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // swallow
            }
        }

        public static void Error(string context, Exception ex)
        {
            Write("ERROR " + context + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}

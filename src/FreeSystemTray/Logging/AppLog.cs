using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace FreeSystemTray.Logging
{
    /// <summary>
    /// A text log written by a background thread. <see cref="Write"/> never blocks: it is called while
    /// tray messages are being handled on the UI thread, where any wait lets another tray message
    /// run in the middle (FINDINGS.md, "Other findings" 1). Hence a lock-free queue, not a lock.
    /// </summary>
    public static class AppLog
    {
        private const long MaxBytes = 4 * 1024 * 1024;

        private static readonly ConcurrentQueue<string> Queue = new();
        private static readonly AutoResetEvent Pending = new(false);
        private static readonly ManualResetEventSlim Idle = new(true);
        private static string? _path;

        public static string? FilePath => _path;

        public static void Init(string path)
        {
            if (_path != null) return;
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            new Thread(WriterLoop) { IsBackground = true, Name = "Log writer" }.Start();
        }

        public static void Write(string message)
        {
            Queue.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
            Idle.Reset();
            Pending.Set();
        }

        /// <summary>Waits until everything queued so far is on disk. Not for use while tray messages are handled.</summary>
        public static void Flush(TimeSpan timeout)
        {
            if (_path == null) return;
            DateTime deadline = DateTime.UtcNow + timeout;
            do
            {
                Pending.Set();
                Idle.Wait(TimeSpan.FromMilliseconds(50));
            }
            while ((!Queue.IsEmpty || !Idle.IsSet) && DateTime.UtcNow < deadline);
        }

        private static void WriterLoop()
        {
            StreamWriter? writer = null;
            while (true)
            {
                Pending.WaitOne();
                try
                {
                    writer ??= Open();
                    while (Queue.TryDequeue(out string? line)) writer.WriteLine(line);
                    writer.Flush();
                    if (writer.BaseStream.Length > MaxBytes)
                    {
                        writer.Dispose();
                        writer = null;
                        File.Move(_path!, Path.ChangeExtension(_path!, ".old.log"), true);
                    }
                }
                catch (IOException)
                {
                    // Logging must never take the app down; try again with the next line.
                    writer?.Dispose();
                    writer = null;
                }
                if (Queue.IsEmpty) Idle.Set();
            }
        }

        private static StreamWriter Open()
        {
            var stream = new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }
    }
}

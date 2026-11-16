using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RSTGameTranslation
{
    public static class RecentLog
    {
        private const int MaxLines = 5000;
        private static readonly Queue<string> _lines = new Queue<string>(MaxLines);
        private static readonly object _lock = new object();

        public static TextWriter Wrap(TextWriter inner) => new RecordingWriter(inner);

        private static void Add(string line)
        {
            lock (_lock)
            {
                if (_lines.Count >= MaxLines)
                    _lines.Dequeue();
                _lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
        }

        public static void SaveTo(string path)
        {
            List<string> lines;
            lock (_lock)
                lines = _lines.ToList();

            ConfigManager config = ConfigManager.Instance;
            var sb = new StringBuilder();
            sb.AppendLine($"RST log saved {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Windows: {MainWindow.Instance?.Windows_Version}");
            sb.AppendLine($"OCR: {config.GetOcrMethod()}");
            sb.AppendLine($"Translation service: {config.GetCurrentTranslationService()}");
            sb.AppendLine($"Languages: {config.GetSourceLanguage()} -> {config.GetTargetLanguage()}");
            sb.AppendLine($"Settle time: {config.GetBlockDetectionSettleTime():F2}s");
            sb.AppendLine($"Translation cache: {config.IsTranslationCacheEnabled()}, prefetch: {config.IsTranslationPrefetchEnabled()}");
            sb.AppendLine($"Hide stale translation: {config.IsHideStaleTranslationEnabled()}, separate window: {config.IsDetachedOverlayWindowEnabled()}");
            sb.AppendLine();
            sb.AppendLine("=== Translation timing ===");
            foreach (string line in lines.Where(IsTimingLine))
                sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine($"=== Full log (last {lines.Count} lines) ===");
            foreach (string line in lines)
                sb.AppendLine(line);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static bool IsTimingLine(string line) =>
            line.Contains("[OCR timing") ||
            line.Contains("Google Translate") ||
            line.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error", StringComparison.Ordinal);

        private sealed class RecordingWriter : TextWriter
        {
            private readonly TextWriter _inner;
            private readonly StringBuilder _pending = new StringBuilder();

            public RecordingWriter(TextWriter inner) => _inner = inner;

            public override Encoding Encoding => _inner.Encoding;

            public override void Write(char value)
            {
                _inner.Write(value);
                if (value == '\n')
                    FlushPending();
                else if (value != '\r')
                    _pending.Append(value);
            }

            public override void Write(string? value)
            {
                if (value == null) return;
                _inner.Write(value);
                foreach (char c in value)
                {
                    if (c == '\n')
                        FlushPending();
                    else if (c != '\r')
                        _pending.Append(c);
                }
            }

            public override void WriteLine(string? value)
            {
                _inner.WriteLine(value);
                _pending.Append(value);
                FlushPending();
            }

            public override void WriteLine()
            {
                _inner.WriteLine();
                FlushPending();
            }

            public override void Flush() => _inner.Flush();

            private void FlushPending()
            {
                Add(_pending.ToString());
                _pending.Clear();
            }
        }
    }
}

using System;
using System.IO;

namespace hydrogen
{
    internal static class Log
    {
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "player_log.txt");
        private static readonly object _lock = new();
        private const int MaxLines = 10000;
        private static int _currentLine;

        static Log()
        {
            // 只统计本次运行写入的日志，不读取旧文件，启动更快
            _currentLine = 0;
        }

        public static void WriteLog(string msg)
        {
            string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} || {msg}";

            lock (_lock)
            {
                if (_currentLine >= MaxLines)
                {
                    // 达到10000行，清空文件，计数器重置
                    File.WriteAllText(_logPath, string.Empty);
                    _currentLine = 0;
                }
                File.AppendAllText(_logPath, logLine + Environment.NewLine);
                _currentLine++;
            }
        }
    }
}

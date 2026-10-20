using System;
using System.Collections.Generic;
using Godot;

namespace PsdlViewer.Core {
    public enum LogLevel { Info, Warning, Error }

    public readonly record struct LogEntry(long Id, DateTime Time, LogLevel Level, string Text);

    /// <summary>Copy of everything printed to the terminal (stdout/stderr), shown by the Console panel.
    /// The original viewer only printed to the terminal it was started from. Thread safe: the engine logs from any thread.</summary>
    public static class AppLog {
        public const int Capacity = 5000;
        static readonly object sync = new();
        static readonly Queue<LogEntry> entries = new();
        static readonly int[] counts = new int[3];
        static long lastId;
        static int generation;
        static TerminalLogger logger;

        /// <summary>Id of the newest entry, 0 when nothing was logged.</summary>
        public static long LastId { get { lock (sync) return lastId; } }
        /// <summary>Changes when the log is cleared.</summary>
        public static int Generation { get { lock (sync) return generation; } }

        /// <summary>Starts capturing the engine output.</summary>
        public static void Install() {
            if (logger != null) return;
            logger = new TerminalLogger();
            OS.AddLogger(logger);
        }

        public static void Uninstall() {
            if (logger == null) return;
            OS.RemoveLogger(logger);
            logger = null;
        }

        public static void Add(LogLevel level, string text) {
            lock (sync) {
                entries.Enqueue(new LogEntry(++lastId, DateTime.Now, level, text));
                counts[(int)level]++;
                while (entries.Count > Capacity) counts[(int)entries.Dequeue().Level]--;
            }
        }

        /// <summary>Entries newer than the given id, oldest first.</summary>
        public static List<LogEntry> Since(long id) {
            lock (sync) {
                var list = new List<LogEntry>();
                foreach (var e in entries) {
                    if (e.Id > id) list.Add(e);
                }
                return list;
            }
        }

        public static int Count(LogLevel level) {
            lock (sync) return counts[(int)level];
        }

        public static void Clear() {
            lock (sync) {
                entries.Clear();
                Array.Clear(counts);
                generation++;
            }
        }
    }
}

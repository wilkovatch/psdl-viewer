using System.Text;
using Godot;

namespace PsdlViewer.Core {
    /// <summary>Receives the engine's stdout/stderr stream (GD.Print, GD.PrintErr, GD.PushWarning, GD.PushError, engine errors)
    /// and forwards it to <see cref="AppLog"/>, formatted like the terminal output. Called from any thread.</summary>
    public partial class TerminalLogger : Logger {
        public override void _LogMessage(string message, bool error) {
            AppLog.Add(error ? LogLevel.Error : LogLevel.Info, message.TrimEnd('\r', '\n'));
        }

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType,
                                       Godot.Collections.Array<ScriptBacktrace> scriptBacktraces) {
            string prefix = (ErrorType)errorType switch {
                ErrorType.Warning => "WARNING",
                ErrorType.Script => "SCRIPT ERROR",
                ErrorType.Shader => "SHADER ERROR",
                _ => "ERROR"
            };
            var text = new StringBuilder();
            text.Append(prefix).Append(": ").Append(string.IsNullOrEmpty(rationale) ? code : rationale);
            if (!string.IsNullOrEmpty(file)) text.Append($"\n   at: {function} ({file}:{line})");
            if (scriptBacktraces != null) {
                foreach (var backtrace in scriptBacktraces) {
                    if (backtrace != null && !backtrace.IsEmpty()) text.Append('\n').Append(backtrace.Format(3).TrimEnd('\r', '\n'));
                }
            }
            AppLog.Add((ErrorType)errorType == ErrorType.Warning ? LogLevel.Warning : LogLevel.Error, text.ToString());
        }
    }
}

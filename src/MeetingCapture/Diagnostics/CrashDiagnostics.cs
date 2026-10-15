using System.Diagnostics;
using System.Text;

namespace MeetingCapture.Diagnostics;

/// <summary>
/// Records metadata-only crash evidence: exception types and stack frames, never messages,
/// data, or inner-exception text, so the file is safe to share.
/// </summary>
public static class CrashDiagnostics
{
    public const string FileName = "crash.log";
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    internal static string? DirectoryOverride { get; set; }

    public static string DefaultDirectory => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingCapture", "Diagnostics");

    /// <summary>Appends one entry. Never throws; returns false if it could not be written.</summary>
    public static bool Record(string source, Exception? error, string? directory = null)
    {
        try
        {
            // Exclude exception messages, which may contain private content; keep only diagnostic metadata and stack names.
            var text = Format(source, error, DateTimeOffset.UtcNow);
            directory ??= DefaultDirectory;
            lock (Gate)
            {
                // Serialize rotation and append so concurrent crashes cannot interleave file operations.
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, FileName);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, Path.Combine(directory, "crash.1.log"), overwrite: true);
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Cannot write the Meeting Capture crash log ({0}).", ex.GetType().Name);
            return false;
        }
    }

    public static string Format(string source, Exception? error, DateTimeOffset now)
    {
        var text = new StringBuilder();
        // Keep enough context to locate the failing code without writing error payloads or input data.
        text.Append(now.ToString("O")).Append(" source=").Append(source)
            .Append(" pid=").Append(Environment.ProcessId).AppendLine();
        var depth = 0;
        for (var current = error; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            text.Append("  type=").Append(current.GetType().FullName)
                .Append(" hresult=0x").Append(current.HResult.ToString("X8")).AppendLine();
            foreach (var frame in new StackTrace(current, fNeedFileInfo: false).GetFrames())
            {
                // Exclude source paths and line data because they can expose local machine details.
                var method = frame.GetMethod();
                if (method is null) continue;
                text.Append("    at ").Append(method.DeclaringType?.FullName).Append('.')
                    .Append(method.Name).AppendLine();
            }
        }
        return text.ToString();
    }
}

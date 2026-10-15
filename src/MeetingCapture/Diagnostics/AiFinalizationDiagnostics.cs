using System.Diagnostics;
using MeetingCapture.Intelligence;

namespace MeetingCapture.Diagnostics;

/// <summary>
/// Metadata-only log for AI work that runs after a recording is stopped. It records the step,
/// the outcome, the failure kind, and the exception type, never messages, prompts, or transcript text.
/// </summary>
internal static class AiFinalizationDiagnostics
{
    internal const string FileName = "ai.log";
    private static readonly object Gate = new();

    internal static string Format(string stage, string outcome, Exception? error, long elapsedMilliseconds)
    {
        // Whitelist labels and record only safe failure metadata, never prompts, response text, or transcripts.
        var safeStage = stage is "final-visualization" or "final-notes" or "final-save" ? stage : "final";
        var safeOutcome = outcome is "kept-live-map" or "stale-skipped" or "retry-available" or "saved"
            ? outcome : "failed";
        var kind = error switch
        {
            null => "None",
            IntelligenceException failure => failure.Failure.Kind.ToString(),
            _ => "Unknown"
        };
        var status = error is HttpRequestException { StatusCode: { } httpStatus }
            ? ((int)httpStatus).ToString()
            : "-";
        return $"{DateTimeOffset.UtcNow:O} stage={safeStage} outcome={safeOutcome} kind={kind} " +
            $"type={error?.GetType().Name ?? "-"} http={status} elapsedMs={Math.Max(0, elapsedMilliseconds)}" +
            Environment.NewLine;
    }

    internal static void Record(
        string stage, string outcome, Exception? error, long elapsedMilliseconds, string? directory = null)
    {
        try
        {
            // Finalization diagnostics are best-effort and must not replace the original save or AI result.
            directory ??= CrashDiagnostics.DefaultDirectory;
            var text = Format(stage, outcome, error, elapsedMilliseconds);
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, FileName);
                if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
                    File.Move(path, Path.Combine(directory, "ai.1.log"), overwrite: true);
                File.AppendAllText(path, text);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Cannot write the AI finalization log ({0}).", ex.GetType().Name);
        }
    }
}

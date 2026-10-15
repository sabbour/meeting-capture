using System.Diagnostics;
using System.Text.RegularExpressions;
using MeetingCapture.WorkIq;

namespace MeetingCapture.Diagnostics;

/// <summary>
/// Metadata-only log for related-context (WorkIQ) failures. It records the operation, failure
/// kind, safe error code, HTTP status, and exception type, never message text.
/// </summary>
internal static class RelatedDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Regex SafeCode = new(
        @"\A(?:http:\d{3}|rpc:-?\d+|shape:[A-Za-z]+|AADSTS\d+|tool-error(?::[A-Za-z]{1,40})?)\z");

    internal static string Format(string operation, bool? authenticated, Exception error)
    {
        var failure = error as WorkIqException;
        // Accept only short, known-safe codes; provider messages and arbitrary operation text can contain data.
        var code = failure?.Code is { } value && SafeCode.IsMatch(value) ? value : "-";
        var status = error is HttpRequestException { StatusCode: { } httpStatus }
            ? ((int)httpStatus).ToString()
            : "-";
        // Keep the operation label from a fixed list so this log remains metadata-only.
        var safeOperation = operation is "initialize" or "tools/list" or "tools/call" or
            "notifications/initialized" or "related" or "coordinator" ? operation : "request";
        return $"{DateTimeOffset.UtcNow:O} endpoint=WorkIQ operation={safeOperation} " +
            $"authenticated={authenticated?.ToString() ?? "unknown"} " +
            $"kind={failure?.Kind.ToString() ?? "Unknown"} code={code} http={status} " +
            $"type={error.GetType().Name}{Environment.NewLine}";
    }

    /// <summary>Appends one entry to related.log, rotating the file at 256 KB. Never throws.</summary>
    internal static void Record(string operation, bool? authenticated, Exception error, string? directory = null)
    {
        try
        {
            directory ??= CrashDiagnostics.DefaultDirectory;
            lock (Gate)
            {
                // Rotate and append under one lock so simultaneous Work IQ failures remain separate lines.
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "related.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
                    File.Move(path, Path.Combine(directory, "related.1.log"), overwrite: true);
                File.AppendAllText(path, Format(operation, authenticated, error));
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Cannot write the related-sources log ({0}).", ex.GetType().Name);
        }
    }
}

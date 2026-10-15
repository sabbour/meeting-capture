using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingCapture.Intelligence;

namespace MeetingCapture.Storage;

/// <summary>
/// Durable hand-off for visualization sidecars that could not be written next to their
/// transcript. Visualization data is optional, so it must never hold capture resources or
/// keep the app open: a deferred sidecar is journaled here and retried on a later launch.
/// </summary>
public interface IPendingVisualizationJournal
{
    /// <summary>Keeps a topic map that could not be saved so it can be retried later.</summary>
    Task RecordAsync(string markdownPath, MeetingVisualizationDocument document, CancellationToken cancellationToken);

    /// <summary>Tries to save each journaled entry with <paramref name="storage"/> and returns the notes paths it handled.</summary>
    Task<IReadOnlyList<string>> DrainAsync(IMeetingVisualizationStorage storage, CancellationToken cancellationToken);
}

/// <summary>Journal that keeps each pending entry as a file on disk.</summary>
public sealed class FilePendingVisualizationJournal : IPendingVisualizationJournal
{
    internal const int CurrentEntryVersion = 1;
    private const long MaxEntryBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly AiDiagnosticsStream? _diagnostics;

    public FilePendingVisualizationJournal(string? root = null, AiDiagnosticsStream? diagnostics = null)
    {
        _root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetingCapture", "pending-visualizations")
            : root;
        _diagnostics = diagnostics;
    }

    public string Root => _root;

    private sealed record Entry(
        int SchemaVersion, string MarkdownPath, DateTimeOffset DeferredAt,
        MeetingVisualizationDocument? Document);

    public async Task RecordAsync(
        string markdownPath, MeetingVisualizationDocument document, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownPath);
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(_root);
        var path = EntryPath(markdownPath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new Entry(CurrentEntryVersion, markdownPath, DateTimeOffset.UtcNow, document), Json);
            if (bytes.LongLength > MaxEntryBytes)
                throw new IOException("Visualization retry data is too large to keep for recovery.");
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Retries every journaled sidecar. Entries whose transcript no longer exists are dropped;
    /// entries that still fail are retained for another launch. Returns the recovered transcripts.
    /// </summary>
    public async Task<IReadOnlyList<string>> DrainAsync(
        IMeetingVisualizationStorage storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!Directory.Exists(_root)) return [];
        List<string> recovered = [];
        foreach (var file in Directory.EnumerateFiles(_root, "*.json").ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry? entry;
            try
            {
                var info = new FileInfo(file);
                if (info.Length <= 0 || info.Length > MaxEntryBytes) continue;
                entry = JsonSerializer.Deserialize<Entry>(
                    await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false), Json);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (entry is null || entry.Document is null || entry.SchemaVersion < 1 ||
                string.IsNullOrWhiteSpace(entry.MarkdownPath))
                continue;
            if (!File.Exists(entry.MarkdownPath))
            {
                TryDelete(file);
                continue;
            }
            try
            {
                await storage.SaveAsync(entry.MarkdownPath, entry.Document, cancellationToken).ConfigureAwait(false);
                TryDelete(file);
                recovered.Add(entry.MarkdownPath);
                _diagnostics?.RecordPersistence(
                    AiDiagnosticStage.CompanionJsonRetry,
                    null,
                    AiDiagnosticRetryOutcome.RetrySucceeded);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Retained on disk: a later launch (or manual recovery) can retry it.
                _diagnostics?.RecordPersistence(
                    AiDiagnosticStage.CompanionJsonRetry,
                    ex,
                    AiDiagnosticRetryOutcome.RetryFailed);
            }
        }
        return recovered;
    }

    private string EntryPath(string markdownPath) =>
        Path.Combine(_root, Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(markdownPath).ToLowerInvariant()))) + ".json");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

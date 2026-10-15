namespace MeetingCapture.Intelligence;

/// <summary>Analyzes a meeting visualization document and returns updates to it.</summary>
public interface IMeetingVisualizationIntelligence
{
    /// <summary>Runs the analysis for the current state of <paramref name="document"/>.</summary>
    Task<MeetingVisualizationAnalysisResult> AnalyzeVisualizationAsync(
        MeetingVisualizationDocument document,
        CancellationToken cancellationToken);
}

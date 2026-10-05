namespace MeetingCapture.Intelligence;

public interface IMeetingVisualizationIntelligence
{
    Task<MeetingVisualizationAnalysisResult> AnalyzeVisualizationAsync(
        MeetingVisualizationDocument document,
        CancellationToken cancellationToken);
}

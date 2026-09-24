namespace CleanLens.Core.Models;

public enum ConfidenceLevel
{
    Low,
    Medium,
    High
}

public enum CandidateCategory
{
    ApplicationData,
    ProgramData,
    Temporary,
    UserData,
    Registry,
    Service,
    ScheduledTask,
    StartupEntry
}

public sealed record LeftoverCandidate(
    string Path,
    CandidateCategory Category,
    ConfidenceLevel Confidence,
    string Reason,
    long? SizeBytes,
    bool IsUserData,
    bool IsSelectedByDefault)
{
    public string ReasonKey { get; init; } = string.Empty;
    public string SizeText => SizeBytes is null ? "Not measured" : FormatSize(SizeBytes.Value);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

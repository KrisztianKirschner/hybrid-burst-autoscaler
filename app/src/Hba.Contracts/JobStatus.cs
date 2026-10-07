namespace Hba.Contracts;

/// <summary>Values of the <see cref="JobHash.Status"/> field.</summary>
public static class JobStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Failed = "failed";
}

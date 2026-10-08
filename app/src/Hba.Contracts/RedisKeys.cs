namespace Hba.Contracts;

public static class RedisKeys
{
    public const string Jobs = "hba:jobs";
    public const string Processing = "hba:jobs:processing";
    public const string JobPrefix = "hba:job:";

    public static string Job(Guid jobId) => $"{JobPrefix}{jobId:D}";
}

using Hba.Contracts;
using Hba.Worker;

namespace Hba.Worker.Tests;

public sealed class JobReaperTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T14:03:11.402+00:00");
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    [Fact]
    public void MissingHeartbeat_SeenOnce_IsNotStale()
    {
        // A job claimed a moment ago has no heartbeat yet; reaping it would process it twice.
        Assert.False(JobReaper.IsStale(null, missingOnPreviousPass: false, Now, StaleAfter));
    }

    [Fact]
    public void MissingHeartbeat_SeenOnTwoPasses_IsStale()
    {
        Assert.True(JobReaper.IsStale(null, missingOnPreviousPass: true, Now, StaleAfter));
    }

    [Fact]
    public void RecentHeartbeat_IsNotStale()
    {
        var heartbeat = JobHash.FormatTimestamp(Now.AddSeconds(-10));

        Assert.False(JobReaper.IsStale(heartbeat, missingOnPreviousPass: false, Now, StaleAfter));
    }

    [Fact]
    public void OldHeartbeat_IsStaleOnFirstSight()
    {
        var heartbeat = JobHash.FormatTimestamp(Now.AddSeconds(-61));

        Assert.True(JobReaper.IsStale(heartbeat, missingOnPreviousPass: false, Now, StaleAfter));
    }
}

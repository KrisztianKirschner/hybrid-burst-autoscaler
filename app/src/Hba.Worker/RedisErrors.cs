using StackExchange.Redis;

namespace Hba.Worker;

internal static class RedisErrors
{
    // RedisTimeoutException derives from TimeoutException, not RedisException,
    // so catching RedisException alone lets a single slow command escape and stop the host.
    public static bool IsRedisFailure(Exception exception) =>
        exception is RedisException or RedisTimeoutException;
}

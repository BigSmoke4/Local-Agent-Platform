using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Shared.Data;

/// <summary>
/// Serializes process startup work (EF migrations and idempotent seeders) across web
/// replicas sharing the same PostgreSQL database. PostgreSQL holds this session lock
/// until explicit release or connection loss; callers must use the same key everywhere.
/// </summary>
public static class PostgresStartupLock
{
    // ASCII "LAP_INIT"; fixed, project-specific 64-bit key, distinct from account locks.
    private const long LockKey = 0x4C41505F494E4954L;
    private const int LockCommandTimeoutSeconds = 300;

    public static async Task ExecuteAsync(
        PlatformDbContext db,
        Func<CancellationToken, Task> initializeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(initializeAsync);

        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var lockAcquired = false;
        var initializationSucceeded = false;

        try
        {
            await ExecuteLockCommandAsync(connection, "SELECT pg_catalog.pg_advisory_lock(@lockKey);", cancellationToken);
            lockAcquired = true;

            await initializeAsync(cancellationToken);
            initializationSucceeded = true;
        }
        finally
        {
            try
            {
                if (lockAcquired)
                {
                    var unlocked = await ExecuteLockCommandAsync(
                        connection,
                        "SELECT pg_catalog.pg_advisory_unlock(@lockKey);",
                        CancellationToken.None);
                    if (unlocked is not true)
                        throw new InvalidOperationException("PostgreSQL startup advisory lock was not held at release time.");
                }
            }
            catch when (!initializationSucceeded)
            {
                // Closing the session below releases a session-level lock. Preserve the
                // original migration/seeding exception rather than hiding it with cleanup.
            }
            finally
            {
                try
                {
                    await db.Database.CloseConnectionAsync();
                }
                catch when (!initializationSucceeded)
                {
                    // The failed initialization remains the primary exception.
                }
            }
        }
    }

    private static async Task<object?> ExecuteLockCommandAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = LockCommandTimeoutSeconds;
        var keyParameter = command.CreateParameter();
        keyParameter.ParameterName = "lockKey";
        keyParameter.DbType = DbType.Int64;
        keyParameter.Value = LockKey;
        command.Parameters.Add(keyParameter);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}

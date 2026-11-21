using Microsoft.Data.Sqlite;
using Xunit;

namespace Caravel.Clarion.Tests;

// Application-owned example. Only disposable synthetic database files may use this write probe.
public sealed class WorkflowSchemaReadinessTests
{
    [Theory]
    [InlineData(true, SqliteOpenMode.ReadWrite, "ready")]
    [InlineData(false, SqliteOpenMode.ReadWrite, "missing-work-schema")]
    [InlineData(true, SqliteOpenMode.ReadOnly, "work-write-denied")]
    public void RequiredWorkflowNeedsBothSchemaAndRuntimeAccess(
        bool applyWorkMigration, SqliteOpenMode runtimeMode, string expectedOperatorReason)
    {
        var directory = Directory.CreateTempSubdirectory("caravel-workflow-readiness-");
        var database = Path.Combine(directory.FullName, "synthetic.db");
        try
        {
            using (var migration = Open(database, SqliteOpenMode.ReadWriteCreate))
            {
                Execute(migration, "CREATE TABLE reference_items (id INTEGER PRIMARY KEY); INSERT INTO reference_items VALUES (1);");
                if (applyWorkMigration)
                    Execute(migration, "CREATE TABLE work_items (id INTEGER PRIMARY KEY);");
            }

            using (var runtime = Open(database, runtimeMode))
            {
                var connectionHealthy = Scalar(runtime, "SELECT 1;") == 1;
                var referenceReady = Scalar(runtime, "SELECT COUNT(*) FROM reference_items;") == 1;
                var operatorReason = CheckRequiredWork(runtime);
                var publicReadiness = new PublicReadiness(operatorReason == "ready" && referenceReady);

                Assert.True(connectionHealthy);
                Assert.True(referenceReady);
                Assert.Equal(expectedOperatorReason, operatorReason);
                Assert.Equal(expectedOperatorReason == "ready", publicReadiness.Ready);
            }

            // Neither the probe nor readiness processing leaves a row or creates missing schema.
            using var audit = Open(database, SqliteOpenMode.ReadOnly);
            Assert.Equal(applyWorkMigration, WorkSchemaExists(audit));
            if (applyWorkMigration)
                Assert.Equal(0, Scalar(audit, "SELECT COUNT(*) FROM work_items;"));
        }
        finally
        {
            File.Delete(database);
            directory.Delete();
        }
    }

    private static string CheckRequiredWork(SqliteConnection runtime)
    {
        if (!WorkSchemaExists(runtime)) return "missing-work-schema";
        using var transaction = runtime.BeginTransaction(deferred: true);
        using var probe = runtime.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = "INSERT INTO work_items VALUES (1);";
        try
        {
            probe.ExecuteNonQuery();
            return "ready";
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 8) // SQLITE_READONLY
        {
            return "work-write-denied";
        }
        finally
        {
            transaction.Rollback();
        }
    }

    private static bool WorkSchemaExists(SqliteConnection connection) => Scalar(connection,
        "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'work_items';") == 1;

    private static SqliteConnection Open(string database, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = mode, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private sealed record PublicReadiness(bool Ready);
}

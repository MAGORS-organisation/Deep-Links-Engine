using Dle.Control.Infrastructure;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// §D.6: while PostgreSQL is unavailable the control plane answers 503 and never 500. The whole
/// decision is the classification of an exception, and that is what is pinned here; the HTTP side of
/// it — the status, the <c>Retry-After</c>, the body free of internals — is exercised by the chaos
/// test that actually stops the database.
/// </summary>
public sealed class DependencyUnavailableExceptionHandlerTests
{
    [Fact]
    public void ADriverLevelFailure_IsAnOutage()
    {
        // No server answer at all — refused connection, broken stream, exhausted pool — all surface
        // as the base NpgsqlException.
        Assert.True(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(
            new NpgsqlException("Failed to connect to 127.0.0.1:5432")));
    }

    [Theory]
    [InlineData("08006")] // connection_failure
    [InlineData("08001")] // sqlclient_unable_to_establish_sqlconnection
    [InlineData("57P01")] // admin_shutdown
    [InlineData("57P03")] // cannot_connect_now
    [InlineData("53300")] // too_many_connections
    public void AServerAnswerAboutTheConnectionItself_IsAnOutage(string sqlState)
    {
        Assert.True(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(
            new PostgresException("the server is starting up", "FATAL", "FATAL", sqlState)));
    }

    [Theory]
    [InlineData("23505")] // unique_violation
    [InlineData("42P01")] // undefined_table
    [InlineData("22P02")] // invalid_text_representation
    public void AServerAnswerAboutTheRequest_IsAnErrorNotAnOutage(string sqlState)
    {
        // The server spoke. Answering 503 here would tell the client to retry a request that will
        // fail identically every time, and would hide a bug behind an outage.
        Assert.False(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(
            new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", sqlState)));
    }

    [Fact]
    public void TheClassificationLooksThroughWrappers()
    {
        // EF Core wraps a failed SaveChanges; a Task combinator wraps in an AggregateException. The
        // cause is the same and so is the answer.
        Assert.True(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(
            new DbUpdateException("An error occurred while saving the entity changes.", new NpgsqlException("connection reset"))));

        Assert.True(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(
            new AggregateException(new InvalidOperationException("unrelated"), new NpgsqlException("connection reset"))));

        Assert.False(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(
            new DbUpdateException("An error occurred while saving the entity changes.", new InvalidOperationException("unrelated"))));
    }

    [Fact]
    public void AnythingElse_IsLeftToTheDefaultHandler()
    {
        Assert.False(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(new InvalidOperationException("bug")));
        Assert.False(DependencyUnavailableExceptionHandler.IsDependencyUnavailable(new TimeoutException("slow")));
    }
}

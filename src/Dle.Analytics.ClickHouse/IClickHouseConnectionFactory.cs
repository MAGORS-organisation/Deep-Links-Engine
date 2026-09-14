using ClickHouse.Client.ADO;

namespace Dle.Analytics.ClickHouse;

/// <summary>
/// Creates opened ClickHouse connections for the analytics store and the event sink.
/// </summary>
/// <remarks>
/// The indirection exists so that nothing connects during composition: the factory is resolved
/// eagerly, but it opens its first socket only when a report or an insert actually runs. A host
/// that already owns a configured client can register its own implementation before calling
/// <c>AddDleAnalytics</c>, because the default registration uses <c>TryAdd</c>.
/// </remarks>
public interface IClickHouseConnectionFactory
{
    /// <summary>Command timeout applied to reporting queries, in seconds.</summary>
    int CommandTimeoutSeconds { get; }

    /// <summary>Opens a connection to the configured ClickHouse server.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An opened connection. The caller owns it and must dispose it.</returns>
    Task<ClickHouseConnection> OpenAsync(CancellationToken ct);
}

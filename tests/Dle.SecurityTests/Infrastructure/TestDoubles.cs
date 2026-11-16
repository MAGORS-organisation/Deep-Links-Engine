using System.Collections.Concurrent;

using Dle.Domain.Analytics;
using Dle.Domain.WellKnown;

namespace Dle.SecurityTests.Infrastructure;

/// <summary>
/// A click sink that keeps what the resolve pipeline wrote, so a test can assert on the click stream
/// rather than only on the response.
/// </summary>
/// <remarks>
/// TC-167 and T-05 are about what is <em>recorded</em>, not about what is returned: a tampered click
/// identifier has to be visible in the event as tampered, because the whole point of the flag is that
/// a partner payout can be recomputed without it.
/// </remarks>
public sealed class RecordingClickSink : IClickEventSink
{
    private readonly ConcurrentQueue<ClickEvent> _events = new();

    /// <summary>Everything written since the last <see cref="Clear"/>.</summary>
    public IReadOnlyCollection<ClickEvent> Events => _events;

    /// <inheritdoc />
    public long DroppedCount => 0;

    /// <inheritdoc />
    public bool TryWrite(ClickEvent clickEvent)
    {
        _events.Enqueue(clickEvent);
        return true;
    }

    /// <summary>Forgets every recorded event.</summary>
    public void Clear() => _events.Clear();
}

/// <summary>
/// A claim code writer that keeps what the interstitial persisted, and can be told to fail.
/// </summary>
public sealed class RecordingClaimCodeWriter : IClaimCodeWriter
{
    private readonly ConcurrentQueue<Dle.Domain.Entities.ClaimCodeRecord> _records = new();

    /// <summary>Every record written since the last <see cref="Clear"/>.</summary>
    public IReadOnlyCollection<Dle.Domain.Entities.ClaimCodeRecord> Records => _records;

    /// <summary>When set, every write throws, as a database outage would.</summary>
    public bool Fail { get; set; }

    /// <inheritdoc />
    public ValueTask WriteAsync(Dle.Domain.Entities.ClaimCodeRecord record, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Simulated claim code store outage.");
        }

        _records.Enqueue(record);
        return ValueTask.CompletedTask;
    }

    /// <summary>Forgets every recorded write and stops failing.</summary>
    public void Clear()
    {
        _records.Clear();
        Fail = false;
    }
}

/// <summary>
/// A domain configuration store that answers for one host and nothing else.
/// </summary>
/// <remarks>
/// Only the HTML responses consult it, and only for branding, the default language and the fallback
/// Open Graph metadata. It answers <see langword="null"/> for an unknown host so that a test host is
/// never accidentally serving branded pages for a name it does not own.
/// </remarks>
public sealed class FakeDomainConfigStore : IDomainConfigStore
{
    /// <summary>The single host this store knows about.</summary>
    public const string KnownHost = "link.example.test";

    /// <inheritdoc />
    public ValueTask<WellKnownDocument?> BuildAasaAsync(string host, CancellationToken ct) =>
        ValueTask.FromResult<WellKnownDocument?>(null);

    /// <inheritdoc />
    public ValueTask<WellKnownDocument?> BuildAssetLinksAsync(string host, CancellationToken ct) =>
        ValueTask.FromResult<WellKnownDocument?>(null);

    /// <inheritdoc />
    public ValueTask<DomainRuntimeConfig?> GetDomainAsync(string host, CancellationToken ct) =>
        ValueTask.FromResult(string.Equals(host, KnownHost, StringComparison.OrdinalIgnoreCase)
            ? new DomainRuntimeConfig
            {
                Id = new Guid("22222222-2222-2222-2222-222222222222"),
                TenantId = new Guid("11111111-1111-1111-1111-111111111111"),
                Host = KnownHost,
                TenantConsentMode = ConsentMode.AggregateOnly,
                IsActive = true,
            }
            : null);
}

/// <summary>
/// A crawler verifier that confirms nothing.
/// </summary>
/// <remarks>
/// The real one does a reverse and forward DNS lookup, which is a network call and therefore not
/// something a security test should depend on. Refusing every claim is also the conservative default:
/// a user agent claiming to be Googlebot is treated as the ordinary browser it probably is,
/// which is exactly the path the open-redirect corpus needs to travel.
/// </remarks>
public sealed class DenyingBotVerifier : IBotVerifier
{
    /// <inheritdoc />
    public ValueTask<bool> IsGenuineAsync(string crawlerName, IPAddress? address, CancellationToken ct) =>
        ValueTask.FromResult(false);
}

/// <summary>A crawler verifier that confirms every claim, for the Open Graph paths.</summary>
public sealed class ConfirmingBotVerifier : IBotVerifier
{
    /// <inheritdoc />
    public ValueTask<bool> IsGenuineAsync(string crawlerName, IPAddress? address, CancellationToken ct) =>
        ValueTask.FromResult(true);
}

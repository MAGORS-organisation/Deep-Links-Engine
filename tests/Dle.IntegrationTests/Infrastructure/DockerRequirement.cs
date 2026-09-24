using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// Decides, once per process, whether a container runtime is reachable — and therefore whether the
/// integration suite can run at all (§D.4).
/// </summary>
/// <remarks>
/// <para>
/// Every test in this assembly needs PostgreSQL 18 and Valkey 8 started by Testcontainers, because
/// the things worth testing here are exactly the ones an in-memory provider fakes away: declarative
/// partitioning, <c>jsonb</c>, <c>citext</c>, partial unique indexes and query filters. A developer
/// without Docker must therefore get an explicit, readable skip rather than a wall of red — and a
/// build server must get the opposite, because a suite that silently skips itself in CI is worse
/// than no suite at all.
/// </para>
/// <para>
/// That is what <see cref="RequireEnvironmentVariable"/> settles. Set
/// <c>DLE_TESTS_REQUIRE_DOCKER=1</c> and a missing runtime stops being a skip and becomes a
/// failure; leave it unset and the same absence is reported as a skip naming exactly what was
/// looked for and where.
/// </para>
/// <para>
/// The probe is deliberately cheap and never throws. It answers "is there something listening on
/// the endpoint Testcontainers will use", not "is the daemon healthy": a daemon that answers the
/// socket and then refuses to pull an image is a failure the suite should report loudly, not hide
/// behind a skip.
/// </para>
/// </remarks>
public static class DockerRequirement
{
    /// <summary>Environment variable that turns a missing container runtime into a failure.</summary>
    public const string RequireEnvironmentVariable = "DLE_TESTS_REQUIRE_DOCKER";

    /// <summary>Named pipe prefix every Windows named pipe lives under.</summary>
    private const string PipePrefix = @"\\.\pipe\";

    /// <summary>Named pipe the Docker engine listens on by default on Windows.</summary>
    public const string WindowsPipe = PipePrefix + "docker_engine";

    /// <summary>Unix socket the Docker engine listens on by default elsewhere.</summary>
    public const string UnixSocket = "/var/run/docker.sock";

    /// <summary>How long a TCP probe of an explicit <c>DOCKER_HOST</c> is given.</summary>
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly Lazy<Probe> Cached = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether a container runtime answered the probe.</summary>
    public static bool IsAvailable => Cached.Value.Available;

    /// <summary>
    /// Whether this machine insists on running the container-backed tests, so that their absence is
    /// a failure rather than a skip.
    /// </summary>
    public static bool IsRequired
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(RequireEnvironmentVariable);

            return !string.IsNullOrWhiteSpace(value)
                && (value.Equals("1", StringComparison.Ordinal)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Whether a container-backed test should execute. It runs when a runtime is available, and it
    /// also runs when this machine requires one — so that the failure is reported rather than hidden.
    /// </summary>
    public static bool ShouldRun => IsAvailable || IsRequired;

    /// <summary>Where the probe looked. Always non-empty.</summary>
    public static string Endpoint => Cached.Value.Endpoint;

    /// <summary>
    /// The sentence a skipped test carries. Names the endpoint that was probed and the variable that
    /// turns the skip into a failure, so nobody has to read this file to find out why nothing ran.
    /// </summary>
    public static string SkipReason => string.Create(
        CultureInfo.InvariantCulture,
        $"No container runtime answered at {Cached.Value.Endpoint}. This suite needs PostgreSQL 18 "
        + $"and Valkey 8 started by Testcontainers (docs/zadanie.md D.4) and refuses to substitute "
        + $"an in-memory provider, because partitioning, jsonb, citext and the unique indexes are "
        + $"the things under test. Start Docker or Podman and run again; set "
        + $"{RequireEnvironmentVariable}=1 to make this absence a failure instead of a skip.");

    /// <summary>Runs the probe.</summary>
    private static Probe Detect()
    {
        string? dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            return ProbeExplicitHost(dockerHost.Trim());
        }

        string endpoint = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsPipe : UnixSocket;

        return new Probe(EndpointExists(endpoint), endpoint);
    }

    /// <summary>Probes the endpoint named by <c>DOCKER_HOST</c>.</summary>
    private static Probe ProbeExplicitHost(string dockerHost)
    {
        if (!Uri.TryCreate(dockerHost, UriKind.Absolute, out Uri? uri))
        {
            // Not a URI: Testcontainers would fail on it too, so report it verbatim rather than
            // guessing at what was meant.
            return new Probe(Available: false, dockerHost);
        }

        return uri.Scheme switch
        {
            "npipe" => new Probe(EndpointExists(PipePrefix + uri.AbsolutePath.Trim('/')), dockerHost),
            "unix" => new Probe(EndpointExists(uri.AbsolutePath), dockerHost),
            "tcp" or "http" or "https" => new Probe(TcpAnswers(uri), dockerHost),
            _ => new Probe(Available: false, dockerHost),
        };
    }

    /// <summary>
    /// Whether a named pipe or a unix socket exists at <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Enumerating the pipe directory is the reliable way to see a named pipe on Windows;
    /// <see cref="File.Exists(string)"/> alone has been known to answer false for a pipe that is
    /// there. Both are tried, and any exception means "not found" rather than a failed run.
    /// </remarks>
    private static bool EndpointExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return true;
            }

            if (!path.StartsWith(PipePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string name = path[PipePrefix.Length..];

            foreach (string entry in Directory.EnumerateFileSystemEntries(PipePrefix))
            {
                if (Path.GetFileName(entry).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        // A probe must never be the reason a run fails. Anything that goes wrong here means the
        // endpoint could not be confirmed, which is the deny-by-default answer.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>Whether something accepts a TCP connection at <paramref name="uri"/>.</summary>
    private static bool TcpAnswers(Uri uri)
    {
        try
        {
            using var client = new TcpClient();

            return client.ConnectAsync(uri.Host, uri.Port).Wait(TcpProbeTimeout);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>The probe's answer.</summary>
    private readonly record struct Probe(bool Available, string Endpoint);
}

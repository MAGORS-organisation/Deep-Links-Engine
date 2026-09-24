using System.Net;
using System.Net.Http;
using System.Net.Sockets;

using Dle.Domain.Abuse;

namespace Dle.Control.Features.Shared;

/// <summary>
/// Makes an outbound HTTP client refuse to open a connection to an address that is not a public
/// one (T-02).
/// </summary>
/// <remarks>
/// <para>
/// Validating the host name before the request is not enough, and validating the address the
/// resolver returned before the request is not enough either: between the check and the connection
/// the resolver can answer differently, which is DNS rebinding. The check therefore lives in the
/// connect callback, where the address that is validated is the very address the socket is then
/// connected to. Nothing in between can move.
/// </para>
/// <para>
/// The policy itself is <see cref="TargetUrlPolicy"/>, so a webhook destination, a well-known file
/// fetch and a link target are judged by exactly one implementation. Changing what counts as a
/// forbidden range therefore changes it everywhere at once.
/// </para>
/// </remarks>
internal static class PublicEndpointGuard
{
    /// <summary>
    /// Builds a handler that resolves, validates and only then connects, and that never follows a
    /// redirect on its own.
    /// </summary>
    /// <param name="connectTimeout">Upper bound on establishing one TCP connection.</param>
    /// <param name="allowPrivate">
    /// Whether the address rules are turned off for this client. Only ever <see langword="true"/>
    /// where the operator has asked for it (<c>Dle:Webhooks:AllowPrivateDestinations</c>), which is
    /// a development machine or a test harness: a listener on loopback is unreachable otherwise,
    /// and the option would mean nothing.
    /// </param>
    /// <returns>The handler, ready to be used as the primary handler of a named client.</returns>
    /// <remarks>
    /// Redirects are off because a 302 to <c>http://169.254.169.254/</c> would otherwise be
    /// followed by the handler itself, and a redirect that the caller never sees is a redirect the
    /// caller cannot judge. A webhook endpoint that answers with a redirect is a misconfigured
    /// endpoint, and the delivery is recorded as failed with that status.
    /// </remarks>
    internal static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout, bool allowPrivate = false) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = connectTimeout,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = allowPrivate ? ConnectWithoutAddressRulesAsync : ConnectAsync,
    };

    /// <summary>
    /// Resolves the destination and connects, without judging the addresses.
    /// </summary>
    /// <param name="context">The connection being established.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connected stream.</returns>
    /// <exception cref="HttpRequestException">No address accepted a connection.</exception>
    /// <remarks>
    /// Everything else the handler does is unchanged: no redirects, no proxy, no cookies. This is
    /// reachable only on an instance whose operator turned the address rules off, and the defence
    /// stays exactly where it was for every other instance.
    /// </remarks>
    internal static async ValueTask<Stream> ConnectWithoutAddressRulesAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string host = context.DnsEndPoint.Host;

        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        return await ConnectToFirstAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
    }

    /// <summary>
    /// Resolves the destination, drops every address the policy forbids and connects to the first
    /// address that survived.
    /// </summary>
    /// <param name="context">The connection being established.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connected stream.</returns>
    /// <exception cref="HttpRequestException">The destination has no address that may be reached,
    /// or none of the permitted addresses accepted a connection.</exception>
    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        if (TargetUrlPolicy.IsForbiddenHost(host))
        {
            throw new HttpRequestException(
                "The destination host is an internal name and is never a permitted target.");
        }

        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        List<IPAddress> permitted = new(addresses.Length);

        foreach (IPAddress address in addresses)
        {
            if (!TargetUrlPolicy.IsForbiddenAddress(address))
            {
                permitted.Add(address);
            }
        }

        if (permitted.Count == 0)
        {
            // Fail closed: an address list that is empty because every entry was forbidden, and one
            // that is empty because the name does not resolve, are both refusals (§17.9).
            throw new HttpRequestException(
                "The destination resolves only to addresses that are not permitted targets (T-02).");
        }

        return await ConnectToFirstAsync(permitted, port, cancellationToken);
    }

    /// <summary>
    /// Connects to the first address that accepts, and hands the socket to a stream.
    /// </summary>
    /// <param name="addresses">The addresses to try, in order.</param>
    /// <param name="port">The port.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connected stream.</returns>
    /// <exception cref="HttpRequestException">None of them accepted a connection.</exception>
    private static async ValueTask<Stream> ConnectToFirstAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? last = null;

        foreach (IPAddress address in addresses)
        {
            Socket? socket = null;

            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);

                NetworkStream stream = new(socket, ownsSocket: true);

                // Ownership moves to the stream, which the message handler disposes.
                socket = null;
                return stream;
            }
            catch (SocketException e)
            {
                last = e;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        throw new HttpRequestException(
            "None of the permitted addresses of the destination accepted a connection.",
            last);
    }
}

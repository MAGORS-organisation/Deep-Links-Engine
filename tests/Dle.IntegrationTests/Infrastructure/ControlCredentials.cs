using System.Net.Http.Json;
using System.Text;

using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Crypto;

using Microsoft.Extensions.DependencyInjection;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// A credential the control plane will accept, minted the way the control plane mints them.
/// </summary>
/// <remarks>
/// <para>
/// The key is created by the host's own <see cref="ApiKeyHasher"/> — the same Argon2id parameters,
/// the same three-field shape, the same public prefix — and only its hash is stored, exactly as
/// <c>POST /api/v1/api-keys</c> would. Hand-writing a row with a hash computed some other way would
/// make every authenticated test a test of the test's own hashing.
/// </para>
/// <para>
/// Control-plane keys and SDK keys are deliberately separate types of credential and separate
/// authentication schemes (§E.2.1 TB2). Both are issued here because the attribution endpoints only
/// accept the second and the management endpoints only accept the first.
/// </para>
/// </remarks>
public sealed class ControlCredentials
{
    private readonly string _headerName;

    private ControlCredentials(string token, string headerName, Guid tenantId)
    {
        Token = token;
        _headerName = headerName;
        TenantId = tenantId;
    }

    /// <summary>The plaintext credential, as a caller would present it.</summary>
    public string Token { get; }

    /// <summary>The tenant the credential belongs to.</summary>
    public Guid TenantId { get; }

    /// <summary>
    /// Issues a control-plane API key for a tenant and stores its hash.
    /// </summary>
    /// <param name="host">The control plane host, which supplies the hasher.</param>
    /// <param name="database">The database the row is written to.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="role">One of <see cref="DleRoles"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<ControlCredentials> IssueApiKeyAsync(
        DleTestHost<DleControlOptions> host,
        TestDatabase database,
        Guid tenantId,
        string role,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(database);

        ApiKeyCredential credential = host.Services.GetRequiredService<ApiKeyHasher>().Create();

        _ = await Sql.ExecuteAsync(
            database.DataSource,
            """
            INSERT INTO api_keys (tenant_id, name, prefix, hash, role, scopes)
            VALUES ($1, $2, $3, $4, $5, $6)
            """,
            [tenantId, "integration-tests", credential.Prefix, credential.Hash, role, Array.Empty<string>()],
            cancellationToken);

        return new ControlCredentials(credential.Token, DleKeyAuthenticationOptions.ApiKeyHeader, tenantId);
    }

    /// <summary>
    /// Issues an SDK key for one application and stores its hash.
    /// </summary>
    /// <param name="host">The control plane host, which supplies the factory.</param>
    /// <param name="database">The database the row is written to.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="appId">The application the key identifies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<ControlCredentials> IssueSdkKeyAsync(
        DleTestHost<DleControlOptions> host,
        TestDatabase database,
        Guid tenantId,
        Guid appId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(database);

        (string token, string prefix, byte[] hash) = host.Services.GetRequiredService<DleSdkKeyFactory>().Create();

        _ = await Sql.ExecuteAsync(
            database.DataSource,
            """
            INSERT INTO sdk_keys (tenant_id, app_id, key_prefix, hash, is_active)
            VALUES ($1, $2, $3, $4, true)
            """,
            [tenantId, appId, prefix, hash],
            cancellationToken);

        return new ControlCredentials(token, DleKeyAuthenticationOptions.SdkKeyHeader, tenantId);
    }

    /// <summary>Issues an authenticated GET.</summary>
    /// <param name="client">The client.</param>
    /// <param name="path">Path and query, relative to the host root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(_headerName, Token);

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Issues an authenticated POST carrying a JSON body.</summary>
    /// <typeparam name="TBody">The body's type.</typeparam>
    /// <param name="client">The client.</param>
    /// <param name="path">Path and query, relative to the host root.</param>
    /// <param name="body">The body, serialized as snake_case JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public async Task<HttpResponseMessage> PostJsonAsync<TBody>(
        HttpClient client,
        string path,
        TBody body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(_headerName, Token);
        request.Content = JsonContent.Create(body, options: WireJson.Options);

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Issues an authenticated POST carrying a body written out by hand.</summary>
    /// <param name="client">The client.</param>
    /// <param name="path">Path and query, relative to the host root.</param>
    /// <param name="json">The body, exactly as it goes on the wire.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        string path,
        string json,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(_headerName, Token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a request with any method and an optional raw JSON body under this credential, for
    /// the PATCH, PUT and DELETE routes of the control plane.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">Path and query, relative to the host root.</param>
    /// <param name="json">The body, exactly as it goes on the wire, or <see langword="null"/> for none.</param>
    /// <param name="headers">Extra request headers, for example <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="method"/> is <see langword="null"/>.</exception>
    public async Task<HttpResponseMessage> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(method);

        using HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(_headerName, Token);

        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Issues an authenticated PATCH carrying a body written out by hand.</summary>
    /// <param name="client">The client.</param>
    /// <param name="path">Path and query, relative to the host root.</param>
    /// <param name="json">The body, exactly as it goes on the wire.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    public Task<HttpResponseMessage> PatchRawAsync(HttpClient client, string path, string json, CancellationToken cancellationToken) =>
        SendRawAsync(client, HttpMethod.Patch, path, json, headers: null, cancellationToken);

    /// <summary>Issues an authenticated DELETE.</summary>
    /// <param name="client">The client.</param>
    /// <param name="path">Path and query, relative to the host root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    public Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path, CancellationToken cancellationToken) =>
        SendRawAsync(client, HttpMethod.Delete, path, json: null, headers: null, cancellationToken);
}

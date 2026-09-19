using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace JellyfinMod.Services;

/// <summary>
/// The legacy Transmission RPC transport shared by Phase 3 seed protection and the Phase 4 acquisition driver:
/// one POST per method, HTTP Basic credentials when configured, and a single retry with the session id that a
/// 409 answer supplies.
/// </summary>
public static class TransmissionRpc
{
    /// <summary>The CSRF session header.</summary>
    public const string SessionHeader = "X-Transmission-Session-Id";

    /// <summary>Sends one RPC call, completing the session-id handshake once.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri endpoint, string method, object arguments,
        string? username, string? password, CancellationToken cancellationToken)
    {
        using var request = Request(endpoint, method, arguments, username, password);
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict ||
            !response.Headers.TryGetValues(SessionHeader, out var values) || values.FirstOrDefault() is not { } sessionId)
            return response;

        response.Dispose();
        using var retry = Request(endpoint, method, arguments, username, password);
        retry.Headers.TryAddWithoutValidation(SessionHeader, sessionId);
        return await client.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns true when the envelope reports success and carries an arguments object.</summary>
    public static bool Success(JsonElement root, out JsonElement arguments)
    {
        arguments = default;
        return root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String &&
            result.GetString() == "success" &&
            root.TryGetProperty("arguments", out arguments) && arguments.ValueKind == JsonValueKind.Object;
    }

    private static HttpRequestMessage Request(Uri endpoint, string method, object arguments, string? username, string? password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { method, arguments })
        };
        if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }

        return request;
    }
}

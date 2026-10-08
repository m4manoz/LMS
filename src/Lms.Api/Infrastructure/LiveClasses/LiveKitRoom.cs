using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lms.Api.Infrastructure.LiveClasses;

/// <summary>Raised when LiveKit refuses a request or cannot be reached; the message is safe to show to staff.</summary>
public sealed class LiveKitApiException(string message, bool notFound = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>LiveKit said the room or person is not there.</summary>
    public bool NotFound { get; } = notFound;
}

/// <summary>Calls to LiveKit's HTTP (Twirp) API, signed with the organization's key.</summary>
public static class LiveKitApi
{
    public static string Address(LiveKitCredentials credentials)
        => new Uri(credentials.Url).GetLeftPart(UriPartial.Authority).Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase).Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);

    public static async Task<JsonElement> CallAsync(IHttpClientFactory httpFactory, LiveKitCredentials credentials, string service, string method, object request, string token, CancellationToken cancellationToken)
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await client.PostAsJsonAsync($"{Address(credentials)}/twirp/{service}/{method}", request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string? reason = null;
                try { reason = JsonDocument.Parse(text).RootElement.GetProperty("msg").GetString(); } catch (Exception) { /* not JSON */ }
                throw new LiveKitApiException(string.IsNullOrWhiteSpace(reason) ? $"LiveKit answered {(int)response.StatusCode}." : $"LiveKit says: {(reason.Length > 300 ? reason[..300] : reason)}",
                    notFound: response.StatusCode == System.Net.HttpStatusCode.NotFound || (reason?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ?? false) || (reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false));
            }
            return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}").RootElement.Clone() : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (HttpRequestException exception) { throw new LiveKitApiException("LiveKit could not be reached.", inner: exception); }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw new LiveKitApiException("LiveKit took too long to answer.", inner: exception); }
        catch (JsonException exception) { throw new LiveKitApiException("LiveKit answered in an unexpected form.", inner: exception); }
    }
}

/// <summary>Someone who is in the room right now, and the microphones they are sending.</summary>
public sealed record RoomParticipant(string Identity, string? Name, IReadOnlyList<RoomTrack> Tracks);
public sealed record RoomTrack(string Sid, bool IsAudio, bool Muted);

/// <summary>The part of LiveKit's room service this system uses: who is in the room, switching someone's microphone off, and closing the room.</summary>
public interface ILiveKitRoomClient
{
    Task<IReadOnlyList<RoomParticipant>> ListParticipantsAsync(LiveKitCredentials credentials, string room, CancellationToken cancellationToken);
    /// <summary>Switches off the microphone of one person in the room (they can switch it on again). Returns how many microphones were on.</summary>
    Task<int> MuteAsync(LiveKitCredentials credentials, string room, string identity, CancellationToken cancellationToken);
    /// <summary>Closes the room: everyone in it is disconnected.</summary>
    Task CloseAsync(LiveKitCredentials credentials, string room, CancellationToken cancellationToken);
}

public sealed class LiveKitRoomClient(IHttpClientFactory httpFactory) : ILiveKitRoomClient
{
    private const string Service = "livekit.RoomService";

    public async Task<IReadOnlyList<RoomParticipant>> ListParticipantsAsync(LiveKitCredentials credentials, string room, CancellationToken cancellationToken)
    {
        JsonElement body;
        try { body = await LiveKitApi.CallAsync(httpFactory, credentials, Service, "ListParticipants", new { room }, LiveKitTokens.CreateAdminToken(credentials, room, TimeSpan.FromMinutes(5)), cancellationToken); }
        catch (LiveKitApiException exception) when (exception.NotFound) { return []; }   // nobody has joined, so there is no room yet
        var result = new List<RoomParticipant>();
        if (!body.TryGetProperty("participants", out var people) || people.ValueKind != JsonValueKind.Array) return result;
        foreach (var person in people.EnumerateArray())
        {
            var tracks = new List<RoomTrack>();
            if (person.TryGetProperty("tracks", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var track in list.EnumerateArray())
                    tracks.Add(new RoomTrack(Text(track, "sid") ?? string.Empty, IsAudio(track), track.TryGetProperty("muted", out var muted) && muted.ValueKind == JsonValueKind.True));
            result.Add(new RoomParticipant(Text(person, "identity") ?? string.Empty, Text(person, "name"), tracks));
        }
        return result;
    }

    public async Task<int> MuteAsync(LiveKitCredentials credentials, string room, string identity, CancellationToken cancellationToken)
    {
        var people = await ListParticipantsAsync(credentials, room, cancellationToken);
        var person = people.FirstOrDefault(item => item.Identity == identity);
        if (person is null) return 0;
        var muted = 0;
        foreach (var track in person.Tracks.Where(item => item.IsAudio && !item.Muted))
        {
            await LiveKitApi.CallAsync(httpFactory, credentials, Service, "MutePublishedTrack", new { room, identity, track_sid = track.Sid, muted = true }, LiveKitTokens.CreateAdminToken(credentials, room, TimeSpan.FromMinutes(5)), cancellationToken);
            muted++;
        }
        return muted;
    }

    public async Task CloseAsync(LiveKitCredentials credentials, string room, CancellationToken cancellationToken)
    {
        try { await LiveKitApi.CallAsync(httpFactory, credentials, Service, "DeleteRoom", new { room }, LiveKitTokens.CreateAdminToken(credentials, room, TimeSpan.FromMinutes(5)), cancellationToken); }
        catch (LiveKitApiException exception) when (exception.NotFound) { /* the room is already gone */ }
    }

    /// <summary>The kind arrives as a name (AUDIO) or a number (0) depending on the server.</summary>
    private static bool IsAudio(JsonElement track)
        => track.TryGetProperty("type", out var type) && (type.ValueKind == JsonValueKind.Number ? type.GetInt32() == 0 : string.Equals(type.GetString(), "AUDIO", StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Checks that a call to the webhook really comes from the organization's LiveKit server: the Authorization header is a token signed with the API secret that carries a fingerprint of the body.</summary>
public static class LiveKitWebhookVerifier
{
    public static bool IsValid(LiveKitCredentials credentials, string? authorization, string body, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return false;
        var token = authorization.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2]))) return false;
            var claims = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
            if (claims.TryGetProperty("iss", out var issuer) ? issuer.GetString() != credentials.ApiKey : true) return false;
            var instant = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            if (claims.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var expires) && expires < instant - 60) return false;
            if (claims.TryGetProperty("nbf", out var nbf) && nbf.TryGetInt64(out var notBefore) && notBefore > instant + 60) return false;
            if (!claims.TryGetProperty("sha256", out var fingerprint) || fingerprint.GetString() is not { Length: > 0 } sent) return false;
            var actual = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(sent));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException) { return false; }
    }

    private static byte[] FromBase64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
    }

    /// <summary>The header LiveKit would send (used by tests and by anything that wants to call the webhook itself).</summary>
    public static string Sign(LiveKitCredentials credentials, string body, DateTimeOffset? now = null)
    {
        var issued = now ?? DateTimeOffset.UtcNow;
        string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = credentials.ApiKey, nbf = issued.AddSeconds(-5).ToUnixTimeSeconds(), exp = issued.AddMinutes(5).ToUnixTimeSeconds(), sha256 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))) }));
        return $"{header}.{payload}.{Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.ASCII.GetBytes($"{header}.{payload}")))}";
    }
}

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BO1Tracker;

// ================================================================
//  INTÉGRATION TWITCH — Auth (Device Code Flow) + envoi chat
// ================================================================
//
// Fonctionnement :
//  1) L'utilisateur crée une app gratuite sur https://dev.twitch.tv/console/apps
//     (Client Type = "Public", pas besoin de Client Secret).
//  2) Le "Device Code Flow" permet de se connecter depuis l'app WPF sans
//     serveur web : on affiche un code à l'utilisateur, il l'entre sur
//     https://www.twitch.tv/activate, et on récupère un access token.
//  3) Le token est ensuite utilisé pour envoyer des messages dans le chat
//     via l'API Helix "Send Chat Message".
//
// Portée (scope) utilisée : user:write:chat
// ================================================================

record TwitchDeviceCodeResponse(
    string device_code,
    string user_code,
    string verification_uri,
    int expires_in,
    int interval);

record TwitchTokenResponse(
    string access_token,
    string? refresh_token,
    int expires_in,
    string[]? scope,
    string token_type);

record TwitchUserInfo(string Id, string Login, string DisplayName);

/// <summary>Résultat d'une tentative de connexion Twitch.</summary>
class TwitchAuthResult
{
    public bool    Success       { get; init; }
    public string? Error         { get; init; }
    public string? AccessToken   { get; init; }
    public string? RefreshToken  { get; init; }
    public string? UserId        { get; init; }
    public string? Login         { get; init; }
    public int     ExpiresIn     { get; init; } = 0; // secondes ; 0 si inconnu
}

static class TwitchAuthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string DeviceCodeUrl = "https://id.twitch.tv/oauth2/device";
    private const string TokenUrl      = "https://id.twitch.tv/oauth2/token";
    private const string UsersUrl      = "https://api.twitch.tv/helix/users";
    private const string Scope         = "user:read:chat user:write:chat";

    /// <summary>
    /// Démarre le Device Code Flow. Appelle onCodeReady dès que le code
    /// utilisateur est disponible (pour l'afficher à l'écran), puis poll
    /// jusqu'à ce que l'utilisateur ait validé sur twitch.tv/activate.
    /// </summary>
    public static async Task<TwitchAuthResult> AuthenticateAsync(
        string clientId,
        Action<string, string> onCodeReady, // (user_code, verification_uri)
        CancellationToken ct)
    {
        try
        {
            var deviceReq = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"]    = Scope,
            });

            var deviceResp = await Http.PostAsync(DeviceCodeUrl, deviceReq, ct);
            var deviceJson = await deviceResp.Content.ReadAsStringAsync(ct);
            if (!deviceResp.IsSuccessStatusCode)
                return new TwitchAuthResult { Success = false, Error = $"Client ID invalide ou refusé par Twitch ({(int)deviceResp.StatusCode})." };

            var device = JsonSerializer.Deserialize<TwitchDeviceCodeResponse>(deviceJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (device == null)
                return new TwitchAuthResult { Success = false, Error = "Réponse Twitch invalide." };

            onCodeReady(device.user_code, device.verification_uri);

            int interval = Math.Max(device.interval, 2);
            var deadline = DateTime.UtcNow.AddSeconds(device.expires_in);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                ct.ThrowIfCancellationRequested();

                var tokenReq = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"]   = clientId,
                    ["scopes"]      = Scope,
                    ["device_code"] = device.device_code,
                    ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
                });

                var tokenResp = await Http.PostAsync(TokenUrl, tokenReq, ct);
                var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);

                if (tokenResp.IsSuccessStatusCode)
                {
                    var token = JsonSerializer.Deserialize<TwitchTokenResponse>(tokenBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (token == null)
                        return new TwitchAuthResult { Success = false, Error = "Réponse token invalide." };

                    var user = await GetSelfAsync(clientId, token.access_token, ct);
                    if (user == null)
                        return new TwitchAuthResult { Success = false, Error = "Impossible de récupérer le compte Twitch." };

                    return new TwitchAuthResult
                    {
                        Success      = true,
                        AccessToken  = token.access_token,
                        RefreshToken = token.refresh_token,
                        UserId       = user.Id,
                        Login        = user.Login,
                    };
                }

                // "authorization_pending" -> on continue de poll. Toute autre erreur -> on arrête.
                if (!tokenBody.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
                    return new TwitchAuthResult { Success = false, Error = "Connexion refusée ou expirée. Réessaie." };
            }

            return new TwitchAuthResult { Success = false, Error = "Délai d'attente dépassé. Réessaie." };
        }
        catch (OperationCanceledException)
        {
            return new TwitchAuthResult { Success = false, Error = "Connexion annulée." };
        }
        catch (Exception ex)
        {
            return new TwitchAuthResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>Rafraîchit un access token expiré avec le refresh token stocké.</summary>
    public static async Task<TwitchAuthResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
    {
        try
        {
            var req = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = clientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"]    = "refresh_token",
            });
            var resp = await Http.PostAsync(TokenUrl, req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new TwitchAuthResult { Success = false, Error = "Le token a expiré, reconnecte-toi à Twitch." };

            var token = JsonSerializer.Deserialize<TwitchTokenResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (token == null)
                return new TwitchAuthResult { Success = false, Error = "Réponse refresh invalide." };

            var user = await GetSelfAsync(clientId, token.access_token, ct);

            return new TwitchAuthResult
            {
                Success      = true,
                AccessToken  = token.access_token,
                RefreshToken = token.refresh_token ?? refreshToken,
                UserId       = user?.Id,
                Login        = user?.Login,
            };
        }
        catch (Exception ex)
        {
            return new TwitchAuthResult { Success = false, Error = ex.Message };
        }
    }

    private static async Task<TwitchUserInfo?> GetSelfAsync(string clientId, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsersUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Add("Client-Id", clientId);

        var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) return null;

        var first = data[0];
        return new TwitchUserInfo(
            first.GetProperty("id").GetString()!,
            first.GetProperty("login").GetString()!,
            first.GetProperty("display_name").GetString()!);
    }
}

static class TwitchChatService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string SendMessageUrl = "https://api.twitch.tv/helix/chat/messages";

    /// <summary>Envoie un message dans le chat en tant que l'utilisateur connecté (sender = broadcaster).</summary>
    public static async Task<(bool Success, string? Error)> SendMessageAsync(
        string clientId, string accessToken, string broadcasterId, string senderId, string message)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, SendMessageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("Client-Id", clientId);

            var payload = JsonSerializer.Serialize(new
            {
                broadcaster_id = broadcasterId,
                sender_id      = senderId,
                message,
            });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, null);

            var body = await resp.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)resp.StatusCode} : {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

// ================================================================
//  ÉCOUTE DU CHAT EN TEMPS RÉEL — EventSub WebSocket
// ================================================================
//  Ouvre une connexion persistante à Twitch, s'abonne à
//  "channel.chat.message" et notifie l'app à chaque message reçu.
//  Se reconnecte automatiquement en cas de coupure.
// ================================================================
static class TwitchChatListener
{
    private const string WsUrl = "wss://eventsub.wss.twitch.tv/ws";
    private const string SubscriptionsUrl = "https://api.twitch.tv/helix/eventsub/subscriptions";

    private static System.Net.WebSockets.ClientWebSocket? _ws;
    private static CancellationTokenSource? _cts;
    private static int _generation = 0; // permet d'ignorer les anciennes boucles après un Stop()/Start()

    public static void Start(
        string clientId, string accessToken, string broadcasterId, string userId,
        Action<string, string, string> onChatMessage, // (chatterId, chatterLogin, text)
        Action<string> onStatus)
    {
        Stop();
        _cts = new CancellationTokenSource();
        int myGen = ++_generation;
        var ct = _cts.Token;
        _ = Task.Run(() => RunLoopAsync(myGen, clientId, accessToken, broadcasterId, userId, onChatMessage, onStatus, ct), ct);
    }

    public static void Stop()
    {
        _generation++; // invalide toute boucle en cours
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _ws = null;
    }

    private static async Task RunLoopAsync(
        int myGen, string clientId, string accessToken, string broadcasterId, string userId,
        Action<string, string, string> onChatMessage, Action<string> onStatus, CancellationToken ct)
    {
        string url = WsUrl;

        while (!ct.IsCancellationRequested && myGen == _generation)
        {
            try
            {
                var ws = new System.Net.WebSockets.ClientWebSocket();
                _ws = ws;
                await ws.ConnectAsync(new Uri(url), ct);
                var buffer = new byte[16 * 1024];
                bool mustReconnect = false;

                while (ws.State == System.Net.WebSockets.WebSocketState.Open
                       && !ct.IsCancellationRequested && myGen == _generation)
                {
                    using var ms = new MemoryStream();
                    System.Net.WebSockets.WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var metadata = root.GetProperty("metadata");
                    var msgType = metadata.GetProperty("message_type").GetString();

                    if (msgType == "session_welcome")
                    {
                        var sessionId = root.GetProperty("payload").GetProperty("session").GetProperty("id").GetString()!;
                        var (ok, err) = await CreateSubscriptionAsync(clientId, accessToken, broadcasterId, userId, sessionId);
                        onStatus(ok ? "Connecté à Twitch — écoute du chat active." : $"Erreur abonnement chat : {err}");
                    }
                    else if (msgType == "session_reconnect")
                    {
                        url = root.GetProperty("payload").GetProperty("session").GetProperty("reconnect_url").GetString() ?? WsUrl;
                        mustReconnect = true;
                        try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "reconnect", ct); } catch { }
                        break;
                    }
                    else if (msgType == "notification")
                    {
                        var subType = metadata.GetProperty("subscription_type").GetString();
                        if (subType == "channel.chat.message")
                        {
                            var ev = root.GetProperty("payload").GetProperty("event");
                            var chatterId    = ev.TryGetProperty("chatter_user_id", out var cid) ? cid.GetString() ?? "" : "";
                            var chatterLogin = ev.TryGetProperty("chatter_user_login", out var cl) ? cl.GetString() ?? "" : "";
                            var text = ev.TryGetProperty("message", out var m) && m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                            onChatMessage(chatterId, chatterLogin, text);
                        }
                    }
                    // session_keepalive / revocation : rien à faire
                }

                if (!mustReconnect) url = WsUrl; // coupure inattendue -> on repart de l'URL standard
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                onStatus($"Connexion chat perdue ({ex.Message}), reconnexion…");
                url = WsUrl;
            }

            if (!ct.IsCancellationRequested && myGen == _generation)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
            }
        }
    }

    private static async Task<(bool Success, string? Error)> CreateSubscriptionAsync(
        string clientId, string accessToken, string broadcasterId, string userId, string sessionId)
    {
        try
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, SubscriptionsUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("Client-Id", clientId);

            var payload = JsonSerializer.Serialize(new
            {
                type = "channel.chat.message",
                version = "1",
                condition = new { broadcaster_user_id = broadcasterId, user_id = userId },
                transport = new { method = "websocket", session_id = sessionId },
            });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, null);

            var body = await resp.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)resp.StatusCode} : {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

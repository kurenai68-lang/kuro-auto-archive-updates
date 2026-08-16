using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace KUROAutoArchiveNative;

internal sealed record TwitchDeviceInfo(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);
internal sealed record TwitchIdentity(string AccessToken, string RefreshToken, int ExpiresIn, string UserId, string Login, string DisplayName);

internal sealed class TwitchService
{
    private readonly ConfigStore _config;
    public TwitchService(ConfigStore config) => _config = config;

    public async Task<TwitchDeviceInfo> StartDeviceAsync(CancellationToken ct = default)
    {
        var obj = await HttpUtil.PostFormAsync("https://id.twitch.tv/oauth2/device", new[]
        {
            KeyValuePair.Create("client_id", AppInfo.TwitchClientId),
            KeyValuePair.Create("scopes", "")
        }, ct);
        return new TwitchDeviceInfo(
            S(obj,"device_code"), S(obj,"user_code"), S(obj,"verification_uri"), I(obj,"expires_in",600), Math.Max(1,I(obj,"interval",5)));
    }

    public async Task<TwitchIdentity?> PollDeviceAsync(TwitchDeviceInfo device, CancellationToken ct)
    {
        try
        {
            var obj = await HttpUtil.PostFormAsync("https://id.twitch.tv/oauth2/token", new[]
            {
                KeyValuePair.Create("client_id", AppInfo.TwitchClientId),
                KeyValuePair.Create("scopes", ""),
                KeyValuePair.Create("device_code", device.DeviceCode),
                KeyValuePair.Create("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            }, ct);
            var access = S(obj,"access_token");
            if (string.IsNullOrWhiteSpace(access)) return null;
            return await ValidateAndBuildAsync(access, S(obj,"refresh_token"), I(obj,"expires_in",14400), ct);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            var b = ex.ResponseBody.ToLowerInvariant();
            if (b.Contains("authorization_pending") || b.Contains("slow_down") || string.IsNullOrWhiteSpace(b)) return null;
            if (b.Contains("access_denied") || b.Contains("authorization_declined")) throw new InvalidOperationException("Twitch連携がキャンセルされました。");
            if (b.Contains("expired")) throw new InvalidOperationException("Twitch認証コードの有効期限が切れました。");
            return null;
        }
    }

    public async Task<TwitchIdentity> CheckAsync(CancellationToken ct = default)
    {
        var access = _config.GetString("TwitchAccessToken");
        var refresh = _config.GetString("TwitchRefreshToken");
        if (string.IsNullOrWhiteSpace(access))
        {
            if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("Twitchが未接続です。");
            return await RefreshAsync(ct);
        }
        try { return await ValidateAndBuildAsync(access, refresh, 0, ct, save:true); }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { return await RefreshAsync(ct); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var access = _config.GetString("TwitchAccessToken");
        if (!string.IsNullOrWhiteSpace(access))
        {
            try
            {
                await HttpUtil.PostFormAsync("https://id.twitch.tv/oauth2/revoke", new[]
                {
                    KeyValuePair.Create("client_id", AppInfo.TwitchClientId), KeyValuePair.Create("token", access)
                }, ct);
            }
            catch (Exception ex) { Log.Write("Twitch revoke warning: " + ex.Message); }
        }
        foreach (var k in new[]{"TwitchAccessToken","TwitchRefreshToken","TwitchTokenExpiresAt","TwitchLastValidatedAt","TwitchUserId","TwitchDisplayName","TwitchLogin"}) _config.Set(k, "");
        _config.Save();
    }

    public void SaveIdentity(TwitchIdentity id)
    {
        _config.Set("TwitchClientId", AppInfo.TwitchClientId); _config.Set("TwitchClientSecret", "");
        _config.Set("TwitchAuthMode", "device"); _config.Set("TwitchAuthSchema", 1);
        _config.Set("TwitchAccessToken", id.AccessToken); _config.Set("TwitchRefreshToken", id.RefreshToken);
        _config.Set("TwitchTokenExpiresAt", DateTime.UtcNow.AddSeconds(Math.Max(60,id.ExpiresIn)).ToString("o"));
        _config.Set("TwitchLastValidatedAt", DateTime.UtcNow.ToString("o"));
        _config.Set("TwitchUserId", id.UserId); _config.Set("TwitchLogin", id.Login); _config.Set("TwitchDisplayName", id.DisplayName);
        _config.Save();
        Log.Write($"Twitch native auth saved: {id.Login} user_id={id.UserId}");
    }

    private async Task<TwitchIdentity> RefreshAsync(CancellationToken ct)
    {
        var refresh = _config.GetString("TwitchRefreshToken");
        if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("Twitchの再認証が必要です。");
        var obj = await HttpUtil.PostFormAsync("https://id.twitch.tv/oauth2/token", new[]
        {
            KeyValuePair.Create("grant_type","refresh_token"), KeyValuePair.Create("refresh_token",refresh), KeyValuePair.Create("client_id",AppInfo.TwitchClientId)
        }, ct);
        var id = await ValidateAndBuildAsync(S(obj,"access_token"), S(obj,"refresh_token"), I(obj,"expires_in",14400), ct);
        SaveIdentity(id); return id;
    }

    private async Task<TwitchIdentity> ValidateAndBuildAsync(string access, string refresh, int expires, CancellationToken ct, bool save=false)
    {
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("Twitch Access Tokenを取得できませんでした。");
        var v = await HttpUtil.GetJsonAsync("https://id.twitch.tv/oauth2/validate", new AuthenticationHeaderValue("OAuth",access), null, ct);
        if (!string.Equals(S(v,"client_id"), AppInfo.TwitchClientId, StringComparison.Ordinal)) throw new InvalidOperationException("Twitch TokenのClient IDが一致しません。");
        var uid=S(v,"user_id"); var login=S(v,"login");
        if (string.IsNullOrWhiteSpace(uid)||string.IsNullOrWhiteSpace(login)) throw new InvalidOperationException("Twitchアカウント情報を取得できませんでした。");
        var display=login;
        try
        {
            var p = await HttpUtil.GetJsonAsync("https://api.twitch.tv/helix/users?id="+Uri.EscapeDataString(uid), new AuthenticationHeaderValue("Bearer",access), new(){["Client-Id"]=AppInfo.TwitchClientId}, ct);
            if (p["data"] is JsonArray a && a.Count>0 && a[0] is JsonObject u) display=S(u,"display_name",login);
        }
        catch (Exception ex) { Log.Write("Twitch profile warning: "+ex.Message); }
        var actualExpires = expires>0 ? expires : I(v,"expires_in",14400);
        var id = new TwitchIdentity(access, string.IsNullOrWhiteSpace(refresh)?_config.GetString("TwitchRefreshToken"):refresh, actualExpires, uid, login, display);
        if (save) SaveIdentity(id);
        return id;
    }

    private static string S(JsonObject o,string k,string f="") { try{return o[k]?.GetValue<string>()??f;}catch{return f;} }
    private static int I(JsonObject o,string k,int f=0) { try{return o[k]?.GetValue<int>()??f;}catch{return f;} }
}

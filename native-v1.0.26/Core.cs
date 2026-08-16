using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace KUROAutoArchiveNative;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal static class AppInfo
{
    public const string Name = "KURO Auto Archive";
    public const string Version = "1.0.26";
    public const string TwitchClientId = "ujcqifi2ej0ayauu6dmu5pk04ilb98";
    public const string YouTubeClientId = "1099154611642-vhmfjad8545rjgs6rhp04fcfi3tlujj7.apps.googleusercontent.com";
    public const string YouTubeScope = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
}

internal static class AppPaths
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KURO_TwitchAutoArchive");
    public static readonly string ConfigPath = Path.Combine(DataDir, "config.dat");
    public static readonly string YouTubeTokenPath = Path.Combine(DataDir, "youtube_token.dat");
    public static readonly string LogPath = Path.Combine(DataDir, "app.log");

    public static void Ensure() => Directory.CreateDirectory(DataDir);
}

internal static class Log
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            AppPaths.Ensure();
            lock (Gate)
                File.AppendAllText(AppPaths.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}

internal static class DpapiStore
{
    public static string ProtectString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectString(string cipherText)
    {
        var bytes = Convert.FromBase64String(cipherText.Trim());
        var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static void SaveJson(string path, JsonNode node)
    {
        AppPaths.Ensure();
        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, ProtectString(json), Encoding.UTF8);
    }

    public static JsonObject? LoadJson(string path)
    {
        if (!File.Exists(path)) return null;
        var enc = File.ReadAllText(path, Encoding.UTF8);
        var json = UnprotectString(enc);
        return JsonNode.Parse(json) as JsonObject;
    }
}

internal sealed class ConfigStore
{
    public JsonObject Config { get; private set; } = new();

    public void Load()
    {
        AppPaths.Ensure();
        try { Config = DpapiStore.LoadJson(AppPaths.ConfigPath) ?? NewDefaults(); }
        catch (Exception ex)
        {
            Log.Write("Config load failed; defaults used: " + ex.Message);
            Config = NewDefaults();
        }
        MergeDefaults(Config);
        Config["TwitchClientId"] = AppInfo.TwitchClientId;
        Config["TwitchClientSecret"] = "";
        Config["TwitchAuthMode"] = "device";
        Config["TwitchAuthSchema"] = 1;
        Config["YouTubeClientJson"] = "";
        Config["YouTubeAuthMode"] = "oneclick";
        Config["YouTubeAuthSchema"] = 2;
        Save();
    }

    public void Save() => DpapiStore.SaveJson(AppPaths.ConfigPath, Config);

    public string GetString(string key, string fallback = "")
    {
        try { return Config[key]?.GetValue<string>() ?? fallback; } catch { return fallback; }
    }
    public int GetInt(string key, int fallback)
    {
        try { return Config[key]?.GetValue<int>() ?? fallback; } catch { return fallback; }
    }
    public double GetDouble(string key, double fallback)
    {
        try { return Config[key]?.GetValue<double>() ?? fallback; } catch { return fallback; }
    }
    public bool GetBool(string key, bool fallback)
    {
        try { return Config[key]?.GetValue<bool>() ?? fallback; } catch { return fallback; }
    }
    public void Set(string key, string value) => Config[key] = value;
    public void Set(string key, int value) => Config[key] = value;
    public void Set(string key, double value) => Config[key] = value;
    public void Set(string key, bool value) => Config[key] = value;

    private static JsonObject NewDefaults()
    {
        var download = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TwitchAutoArchive");
        return new JsonObject
        {
            ["TwitchLogin"] = "", ["TwitchClientId"] = AppInfo.TwitchClientId, ["TwitchClientSecret"] = "",
            ["TwitchAuthMode"] = "device", ["TwitchAuthSchema"] = 1, ["TwitchAccessToken"] = "", ["TwitchRefreshToken"] = "",
            ["TwitchTokenExpiresAt"] = "", ["TwitchLastValidatedAt"] = "", ["TwitchUserId"] = "", ["TwitchDisplayName"] = "",
            ["YouTubeClientJson"] = "", ["YouTubeAuthMode"] = "oneclick", ["YouTubeAuthSchema"] = 2,
            ["PollSeconds"] = 60, ["LookbackHours"] = 72, ["SkipExistingOnFirstStart"] = true,
            ["PrivacyStatus"] = "private", ["TitleTemplate"] = "{title}",
            ["DescriptionTemplate"] = "Twitch 配信アーカイブ\r\n配信日: {date}\r\nTwitch VOD: {url}",
            ["Tags"] = "Twitch,配信アーカイブ", ["CategoryId"] = "20", ["DownloadDir"] = download,
            ["DeleteAfterUpload"] = true, ["AutoRetryMinutes"] = 30, ["MaxRetryCount"] = 3,
            ["AutoStartWithWindows"] = false, ["AutoStartWorker"] = false,
            ["DiscordNotifyOnFailure"] = false, ["DiscordNotifyOnSuccess"] = false, ["DiscordWebhookUrl"] = "",
            ["YouTubeUploadLimitMbps"] = 20.0, ["DiskSpaceWarningEnabled"] = false, ["DiskSpaceWarningGb"] = 20.0,
            ["UpdateCheckEnabled"] = true,
            ["UpdateManifestUrl"] = "https://raw.githubusercontent.com/kurenai68-lang/kuro-auto-archive-updates/main/latest.json",
            ["UpdateCheckHours"] = 6, ["OfficialUpdateManifestV1"] = true, ["AutoPostEnabled"] = true,
            ["MonitoringEnabled"] = true, ["MonitoringPausedByUser"] = false,
            ["MonitoringPreferenceExplicitlySet"] = false, ["MonitoringStateSchema"] = 2,
            ["MonitoringUserStoppedV2"] = false, ["AcceptedTermsVersion"] = ""
        };
    }

    private static void MergeDefaults(JsonObject target)
    {
        var defaults = NewDefaults();
        foreach (var kv in defaults)
            if (!target.ContainsKey(kv.Key)) target[kv.Key] = kv.Value?.DeepClone();
    }
}

internal sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string ResponseBody { get; }
    public ApiException(string message, HttpStatusCode? status = null, string body = "") : base(message)
    { StatusCode = status; ResponseBody = body; }
}

internal static class HttpUtil
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<JsonObject> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var resp = await Client.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new ApiException($"HTTP {(int)resp.StatusCode}", resp.StatusCode, body);
        return (JsonNode.Parse(body) as JsonObject) ?? throw new InvalidOperationException("JSON response is invalid.");
    }

    public static async Task<JsonObject> GetJsonAsync(string url, AuthenticationHeaderValue? auth = null, Dictionary<string,string>? headers = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth != null) req.Headers.Authorization = auth;
        if (headers != null) foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        using var resp = await Client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new ApiException($"HTTP {(int)resp.StatusCode}", resp.StatusCode, body);
        return (JsonNode.Parse(body) as JsonObject) ?? throw new InvalidOperationException("JSON response is invalid.");
    }
}

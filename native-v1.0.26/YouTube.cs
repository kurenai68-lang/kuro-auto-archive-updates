using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace KUROAutoArchiveNative;

internal sealed record YouTubeIdentity(string ChannelId, string Title);

internal sealed class YouTubeService
{
    private readonly ConfigStore _config;
    public YouTubeService(ConfigStore config) => _config=config;

    public async Task<YouTubeIdentity> ConnectAsync(CancellationToken ct)
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect=$"http://127.0.0.1:{port}/";
        try
        {
            var auth = "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new Dictionary<string,string>
            {
                ["client_id"]=AppInfo.YouTubeClientId,["redirect_uri"]=redirect,["response_type"]="code",["scope"]=AppInfo.YouTubeScope,
                ["access_type"]="offline",["prompt"]="consent",["state"]=state,["code_challenge"]=challenge,["code_challenge_method"]="S256"
            });
            Log.Write("YouTube native OAuth start redirect="+redirect);
            Process.Start(new ProcessStartInfo(auth){UseShellExecute=true});
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen:true);
            var requestLine = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("Googleからの戻りURLを読み取れませんでした。");
            var parts=requestLine.Split(' ');
            if(parts.Length<2) throw new InvalidOperationException("Googleからの戻りURL形式が不正です。");
            while(!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            var callback=new Uri($"http://127.0.0.1:{port}{parts[1]}");
            var q=ParseQuery(callback.Query);
            var returnedState=q.GetValueOrDefault("state",""); var code=q.GetValueOrDefault("code",""); var error=q.GetValueOrDefault("error","");
            var html="<!doctype html><html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif;background:#111;color:#eee;padding:40px\"><h2>KURO Auto Archive</h2><p>認証が完了しました。このタブは閉じて大丈夫です。</p></body></html>";
            var bytes=Encoding.UTF8.GetBytes(html);
            var headers=$"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            var hb=Encoding.ASCII.GetBytes(headers); await stream.WriteAsync(hb,timeout.Token); await stream.WriteAsync(bytes,timeout.Token); await stream.FlushAsync(timeout.Token);
            if(!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("Google認証エラー: "+error);
            if(returnedState!=state) throw new InvalidOperationException("OAuth stateが一致しません。");
            if(string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Googleから認証コードを受け取れませんでした。");
            var token=await HttpUtil.PostFormAsync("https://oauth2.googleapis.com/token", new[]
            {
                KeyValuePair.Create("code",code),KeyValuePair.Create("client_id",AppInfo.YouTubeClientId),KeyValuePair.Create("redirect_uri",redirect),
                KeyValuePair.Create("grant_type","authorization_code"),KeyValuePair.Create("code_verifier",verifier)
            }, timeout.Token);
            var refresh=S(token,"refresh_token");
            if(string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("refresh_tokenを取得できませんでした。Google側のアクセス許可を取り消して再接続してください。");
            var saved=new JsonObject
            {
                ["access_token"]=S(token,"access_token"),["refresh_token"]=refresh,["token_type"]=S(token,"token_type"),
                ["expires_at"]=DateTime.UtcNow.AddSeconds(Math.Max(60,I(token,"expires_in",3600)-120)).ToString("o"),["scope"]=S(token,"scope",AppInfo.YouTubeScope)
            };
            DpapiStore.SaveJson(AppPaths.YouTubeTokenPath,saved);
            return await GetChannelAsync(timeout.Token);
        }
        finally { listener.Stop(); }
    }

    public async Task<YouTubeIdentity> CheckAsync(CancellationToken ct=default) => await GetChannelAsync(ct);

    public async Task DisconnectAsync(CancellationToken ct=default)
    {
        var token=DpapiStore.LoadJson(AppPaths.YouTubeTokenPath);
        if(token!=null)
        {
            var revoke=S(token,"refresh_token"); if(string.IsNullOrWhiteSpace(revoke)) revoke=S(token,"access_token");
            if(!string.IsNullOrWhiteSpace(revoke))
            {
                try
                {
                    using var content=new FormUrlEncodedContent(new[]{KeyValuePair.Create("token",revoke)});
                    using var resp=await HttpUtil.Client.PostAsync("https://oauth2.googleapis.com/revoke",content,ct);
                }
                catch(Exception ex){Log.Write("YouTube revoke warning: "+ex.Message);}
            }
        }
        if(File.Exists(AppPaths.YouTubeTokenPath)) File.Delete(AppPaths.YouTubeTokenPath);
    }

    private async Task<YouTubeIdentity> GetChannelAsync(CancellationToken ct)
    {
        var access=await GetAccessTokenAsync(ct);
        var obj=await HttpUtil.GetJsonAsync("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true&maxResults=1",new AuthenticationHeaderValue("Bearer",access),null,ct);
        if(obj["items"] is not JsonArray items || items.Count<1 || items[0] is not JsonObject c) throw new InvalidOperationException("YouTubeチャンネル情報を取得できませんでした。");
        var title=""; if(c["snippet"] is JsonObject sn) title=S(sn,"title");
        return new YouTubeIdentity(S(c,"id"),string.IsNullOrWhiteSpace(title)?"接続済み":title);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var token=DpapiStore.LoadJson(AppPaths.YouTubeTokenPath) ?? throw new InvalidOperationException("YouTubeが未接続です。");
        DateTime expiry=DateTime.MinValue; DateTime.TryParse(S(token,"expires_at"),out expiry); expiry=expiry.ToUniversalTime();
        if(expiry>DateTime.UtcNow.AddMinutes(3) && !string.IsNullOrWhiteSpace(S(token,"access_token"))) return S(token,"access_token");
        var refresh=S(token,"refresh_token"); if(string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("YouTubeの再認証が必要です。");
        var obj=await HttpUtil.PostFormAsync("https://oauth2.googleapis.com/token",new[]
        {
            KeyValuePair.Create("client_id",AppInfo.YouTubeClientId),KeyValuePair.Create("refresh_token",refresh),KeyValuePair.Create("grant_type","refresh_token")
        },ct);
        token["access_token"]=S(obj,"access_token"); token["token_type"]=S(obj,"token_type","Bearer");
        token["expires_at"]=DateTime.UtcNow.AddSeconds(Math.Max(60,I(obj,"expires_in",3600)-120)).ToString("o");
        if(obj["scope"]!=null) token["scope"]=S(obj,"scope");
        DpapiStore.SaveJson(AppPaths.YouTubeTokenPath,token);
        return S(token,"access_token");
    }

    private static string Query(Dictionary<string,string> q)=>string.Join("&",q.Select(kv=>$"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    private static Dictionary<string,string> ParseQuery(string query)
    {
        var d=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var part in query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries))
        {
            var i=part.IndexOf('=');
            var k=Uri.UnescapeDataString(i>=0?part[..i]:part);
            var v=Uri.UnescapeDataString(i>=0?part[(i+1)..]:"");
            d[k]=v;
        }
        return d;
    }
    private static string Base64Url(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static string S(JsonObject o,string k,string f="") { try{return o[k]?.GetValue<string>()??f;}catch{return f;} }
    private static int I(JsonObject o,string k,int f=0) { try{return o[k]?.GetValue<int>()??f;}catch{return f;} }
}

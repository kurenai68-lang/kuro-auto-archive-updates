using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace KUROAutoArchiveNative;

internal sealed class TwitchLoginForm : Form
{
    private readonly Label _status = new();
    private readonly Label _code = new();
    private readonly Button _open = new();
    private readonly Button _cancel = new();
    private readonly TwitchService _service;
    private readonly TwitchDeviceInfo _device;
    private readonly CancellationTokenSource _cts = new();
    public TwitchIdentity? ResultIdentity { get; private set; }

    public TwitchLoginForm(TwitchService service,TwitchDeviceInfo device)
    {
        _service=service;_device=device;
        Text="KURO Auto Archive - Twitch連携"; Width=540;Height=330;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
        BackColor=Color.FromArgb(28,29,33);ForeColor=Color.White;
        Controls.Add(new Label{Text="ブラウザでTwitchへの接続を許可してください。",Left=24,Top=22,Width=470,Height=28,Font=new Font("Segoe UI",11,FontStyle.Bold)});
        _code.Text=device.UserCode;_code.Left=24;_code.Top=70;_code.Width=470;_code.Height=52;_code.TextAlign=ContentAlignment.MiddleCenter;_code.Font=new Font("Consolas",22,FontStyle.Bold);_code.ForeColor=Color.FromArgb(180,140,255);Controls.Add(_code);
        _open.Text="Twitch認証ページを開く";_open.Left=110;_open.Top=132;_open.Width=300;_open.Height=38;_open.Click+=(s,e)=>OpenBrowser(device.VerificationUri);Controls.Add(_open);
        _status.Text="Twitchの許可を待っています…";_status.Left=24;_status.Top=186;_status.Width=470;_status.Height=28;_status.TextAlign=ContentAlignment.MiddleCenter;Controls.Add(_status);
        _cancel.Text="キャンセル";_cancel.Left=190;_cancel.Top=230;_cancel.Width=150;_cancel.Height=34;_cancel.Click+=(s,e)=>Close();Controls.Add(_cancel);
        Shown+=async(s,e)=>{OpenBrowser(device.VerificationUri);await PollAsync();};
        FormClosing+=(s,e)=>_cts.Cancel();
    }

    private async Task PollAsync()
    {
        var until=DateTime.UtcNow.AddSeconds(_device.ExpiresIn);
        try
        {
            while(DateTime.UtcNow<until&&!_cts.IsCancellationRequested)
            {
                var id=await _service.PollDeviceAsync(_device,_cts.Token);
                if(id!=null){ResultIdentity=id;_service.SaveIdentity(id);DialogResult=DialogResult.OK;Close();return;}
                var left=Math.Max(0,(int)(until-DateTime.UtcNow).TotalSeconds);_status.Text=$"Twitchの許可を待っています… 残り約 {Math.Ceiling(left/60.0)}分";
                await Task.Delay(TimeSpan.FromSeconds(_device.Interval),_cts.Token);
            }
            if(!_cts.IsCancellationRequested) _status.Text="認証が時間切れになりました。";
        }
        catch(OperationCanceledException){ }
        catch(Exception ex){Log.Write("Twitch login failed: "+ex);MessageBox.Show(this,ex.Message,AppInfo.Name,MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
    private static void OpenBrowser(string u){try{Process.Start(new ProcessStartInfo(u){UseShellExecute=true});}catch{}}
}

internal sealed class MainForm : Form
{
    private readonly ConfigStore _config=new();
    private TwitchService _twitch=null!;
    private YouTubeService _youtube=null!;
    private readonly Label _twitchStatus=new();
    private readonly Label _youtubeStatus=new();
    private readonly NumericUpDown _poll=new();
    private readonly NumericUpDown _lookback=new();
    private readonly ComboBox _privacy=new();
    private readonly TextBox _downloadDir=new();
    private readonly CheckBox _deleteAfter=new();
    private readonly Label _saveStatus=new();
    private readonly Button _twConnect=new(),_twCheck=new(),_twDisconnect=new(),_ytConnect=new(),_ytCheck=new(),_ytDisconnect=new();

    private Color Bg=>Color.FromArgb(25,26,30); private Color Panel=>Color.FromArgb(36,38,44); private Color Muted=>Color.FromArgb(170,174,184); private Color Accent=>Color.FromArgb(92,92,255); private Color Green=>Color.FromArgb(76,210,140); private Color Red=>Color.FromArgb(240,90,90);

    public MainForm()
    {
        Text=$"{AppInfo.Name} v{AppInfo.Version} Native TEST";Width=940;Height=680;MinimumSize=new Size(860,600);StartPosition=FormStartPosition.CenterScreen;BackColor=Bg;ForeColor=Color.White;
        _config.Load();_twitch=new TwitchService(_config);_youtube=new YouTubeService(_config);
        BuildUi();LoadSettings();Shown+=async(s,e)=>await InitialStatusAsync();
    }

    private void BuildUi()
    {
        var title=new Label{Text=AppInfo.Name,Left=24,Top=18,Width=500,Height=38,Font=new Font("Segoe UI",20,FontStyle.Bold),ForeColor=Color.White};Controls.Add(title);
        var sub=new Label{Text="v1.0.26 Native TEST — PowerShellを使わない認証・設定保存テスト",Left=26,Top=57,Width=760,Height=24,ForeColor=Muted};Controls.Add(sub);
        var tabs=new TabControl{Left=20,Top=92,Width=884,Height=525,Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right};Controls.Add(tabs);
        var settings=NewTab("① 設定");var connect=NewTab("② 接続");var info=NewTab("③ Native TEST情報");tabs.TabPages.Add(settings);tabs.TabPages.Add(connect);tabs.TabPages.Add(info);
        BuildSettings(settings);BuildConnect(connect);BuildInfo(info);
    }

    private TabPage NewTab(string text)=>new(text){BackColor=Panel,ForeColor=Color.White};
    private Label L(string text,int x,int y,int w=300,int h=28,bool bold=false)=>new(){Text=text,Left=x,Top=y,Width=w,Height=h,ForeColor=Color.White,Font=new Font("Segoe UI",9.5f,bold?FontStyle.Bold:FontStyle.Regular)};
    private Button B(string text,int x,int y,int w=150)=>new(){Text=text,Left=x,Top=y,Width=w,Height=38,BackColor=Color.FromArgb(55,57,65),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};

    private void BuildSettings(TabPage p)
    {
        p.Controls.Add(L("基本設定（旧PowerShell版と同じ config.dat を使用）",24,22,650,30,true));
        p.Controls.Add(L("監視間隔（秒）",24,78,160));_poll.Left=190;_poll.Top=74;_poll.Width=120;_poll.Minimum=15;_poll.Maximum=3600;p.Controls.Add(_poll);
        p.Controls.Add(L("VOD検索範囲（時間）",24,126,170));_lookback.Left=190;_lookback.Top=122;_lookback.Width=120;_lookback.Minimum=1;_lookback.Maximum=720;p.Controls.Add(_lookback);
        p.Controls.Add(L("YouTube公開設定",24,174,160));_privacy.Left=190;_privacy.Top=170;_privacy.Width=180;_privacy.DropDownStyle=ComboBoxStyle.DropDownList;_privacy.Items.AddRange(new object[]{"private","unlisted","public"});p.Controls.Add(_privacy);
        p.Controls.Add(L("一時保存先",24,222,160));_downloadDir.Left=190;_downloadDir.Top=218;_downloadDir.Width=520;p.Controls.Add(_downloadDir);var browse=B("参照",724,216,100);browse.Click+=(s,e)=>Browse();p.Controls.Add(browse);
        _deleteAfter.Text="YouTube投稿成功後、ダウンロード動画を削除";_deleteAfter.Left=24;_deleteAfter.Top=274;_deleteAfter.Width=430;p.Controls.Add(_deleteAfter);
        var save=B("設定を保存",24,326,160);save.BackColor=Accent;save.Click+=(s,e)=>SaveSettings();p.Controls.Add(save);
        _saveStatus.Left=200;_saveStatus.Top=335;_saveStatus.Width=560;_saveStatus.Height=28;_saveStatus.ForeColor=Muted;p.Controls.Add(_saveStatus);
        var note=L("※ このNative TESTでは、監視・ダウンロード・YouTube投稿処理はまだ実行しません。まず認証と設定保存だけをC#へ移植しています。",24,402,800,60);note.ForeColor=Color.FromArgb(235,190,80);p.Controls.Add(note);
    }

    private void BuildConnect(TabPage p)
    {
        p.Controls.Add(L("Twitch OAuth 接続",28,28,320,30,true));
        var help=L("Developer登録は不要。共有Public Client ID＋Device Code Flowで接続します。",28,62,760,28);help.ForeColor=Muted;p.Controls.Add(help);
        _twConnect.Text="Twitchに接続";SetupButton(_twConnect,28,104,170,Accent);_twConnect.Click+=async(s,e)=>await TwitchConnectAsync();p.Controls.Add(_twConnect);
        _twCheck.Text="接続確認";SetupButton(_twCheck,212,104,120,null);_twCheck.Click+=async(s,e)=>await TwitchCheckAsync(false);p.Controls.Add(_twCheck);
        _twDisconnect.Text="接続解除";SetupButton(_twDisconnect,346,104,120,Red);_twDisconnect.Click+=async(s,e)=>await TwitchDisconnectAsync();p.Controls.Add(_twDisconnect);
        _twitchStatus.Left=490;_twitchStatus.Top=112;_twitchStatus.Width=350;_twitchStatus.Height=28;_twitchStatus.ForeColor=Muted;p.Controls.Add(_twitchStatus);

        p.Controls.Add(L("YouTube OAuth",28,194,320,30,true));
        var yh=L("Client ID内蔵＋PKCE(S256)＋127.0.0.1ランダムポート。YouTubeClientJsonは不要です。",28,228,800,28);yh.ForeColor=Muted;p.Controls.Add(yh);
        _ytConnect.Text="YouTubeに接続";SetupButton(_ytConnect,28,270,180,Accent);_ytConnect.Click+=async(s,e)=>await YouTubeConnectAsync();p.Controls.Add(_ytConnect);
        _ytCheck.Text="接続確認";SetupButton(_ytCheck,222,270,120,null);_ytCheck.Click+=async(s,e)=>await YouTubeCheckAsync(false);p.Controls.Add(_ytCheck);
        _ytDisconnect.Text="接続解除";SetupButton(_ytDisconnect,356,270,120,Red);_ytDisconnect.Click+=async(s,e)=>await YouTubeDisconnectAsync();p.Controls.Add(_ytDisconnect);
        _youtubeStatus.Left=28;_youtubeStatus.Top=330;_youtubeStatus.Width=800;_youtubeStatus.Height=30;_youtubeStatus.ForeColor=Muted;p.Controls.Add(_youtubeStatus);
    }

    private void BuildInfo(TabPage p)
    {
        p.Controls.Add(L("v1.0.26 Native TEST",24,26,500,34,true));
        var text=L("この版はPowerShell / CMDを一切起動しないC# WinForms版です。\r\n\r\n実装済み:\r\n・設定の読み込み / 保存（旧 config.dat と互換）\r\n・Twitchワンクリック接続 / 確認 / 解除\r\n・YouTubeワンクリック接続 / 確認 / 解除\r\n・DPAPI(CurrentUser)によるローカル暗号化\r\n\r\n未実装:\r\n・Twitch VOD監視\r\n・yt-dlpダウンロード\r\n・YouTube動画アップロード\r\n・Discord通知 / 履歴 / キュー / 自動更新\r\n\r\nデータ保存先:\r\n"+AppPaths.DataDir,24,82,810,330);text.ForeColor=Muted;p.Controls.Add(text);
        var open=B("データフォルダを開く",24,430,190);open.Click+=(s,e)=>{AppPaths.Ensure();Process.Start(new ProcessStartInfo("explorer.exe",AppPaths.DataDir){UseShellExecute=true});};p.Controls.Add(open);
        var log=B("app.logを開く",230,430,150);log.Click+=(s,e)=>{AppPaths.Ensure();if(!File.Exists(AppPaths.LogPath))File.WriteAllText(AppPaths.LogPath,"");Process.Start(new ProcessStartInfo("notepad.exe",AppPaths.LogPath){UseShellExecute=true});};p.Controls.Add(log);
    }

    private void SetupButton(Button b,int x,int y,int w,Color? c){b.Left=x;b.Top=y;b.Width=w;b.Height=38;b.FlatStyle=FlatStyle.Flat;b.ForeColor=Color.White;b.BackColor=c??Color.FromArgb(55,57,65);}
    private void LoadSettings(){_poll.Value=Math.Clamp(_config.GetInt("PollSeconds",60),(int)_poll.Minimum,(int)_poll.Maximum);_lookback.Value=Math.Clamp(_config.GetInt("LookbackHours",72),(int)_lookback.Minimum,(int)_lookback.Maximum);var pv=_config.GetString("PrivacyStatus","private");_privacy.SelectedItem=_privacy.Items.Contains(pv)?pv:"private";_downloadDir.Text=_config.GetString("DownloadDir",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"TwitchAutoArchive"));_deleteAfter.Checked=_config.GetBool("DeleteAfterUpload",true);}
    private void SaveSettings(){try{_config.Set("PollSeconds",(int)_poll.Value);_config.Set("LookbackHours",(int)_lookback.Value);_config.Set("PrivacyStatus",_privacy.SelectedItem?.ToString()??"private");_config.Set("DownloadDir",_downloadDir.Text.Trim());_config.Set("DeleteAfterUpload",_deleteAfter.Checked);_config.Save();_saveStatus.Text="保存しました。";_saveStatus.ForeColor=Green;Log.Write("Native settings saved.");}catch(Exception ex){MessageBox.Show(this,ex.Message,AppInfo.Name,MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    private void Browse(){using var d=new FolderBrowserDialog{Description="Twitch VODの一時保存先を選択"};if(d.ShowDialog(this)==DialogResult.OK)_downloadDir.Text=d.SelectedPath;}

    private async Task InitialStatusAsync(){SetStoredStatus();await Task.CompletedTask;}
    private void SetStoredStatus(){var tl=_config.GetString("TwitchLogin");var td=_config.GetString("TwitchDisplayName");if(!string.IsNullOrWhiteSpace(tl)){_twitchStatus.Text=$"接続情報あり: {(string.IsNullOrWhiteSpace(td)?tl:td)} ({tl})";_twitchStatus.ForeColor=Green;}else{_twitchStatus.Text="未接続";_twitchStatus.ForeColor=Muted;}_youtubeStatus.Text=File.Exists(AppPaths.YouTubeTokenPath)?"接続情報あり（接続確認を押してください）":"未接続";_youtubeStatus.ForeColor=File.Exists(AppPaths.YouTubeTokenPath)?Green:Muted;}

    private void Busy(bool b){foreach(var x in new[]{_twConnect,_twCheck,_twDisconnect,_ytConnect,_ytCheck,_ytDisconnect})x.Enabled=!b;UseWaitCursor=b;}
    private async Task TwitchConnectAsync(){Busy(true);try{var device=await _twitch.StartDeviceAsync();using var dlg=new TwitchLoginForm(_twitch,device);if(dlg.ShowDialog(this)==DialogResult.OK&&dlg.ResultIdentity is TwitchIdentity id){_twitchStatus.Text=$"接続済み: {id.DisplayName} ({id.Login})";_twitchStatus.ForeColor=Green;MessageBox.Show(this,$"Twitchに接続しました。\r\n\r\n{id.DisplayName} ({id.Login})",AppInfo.Name,MessageBoxButtons.OK,MessageBoxIcon.Information);}}catch(Exception ex){Fail("Twitch接続",ex);}finally{Busy(false);}}
    private async Task TwitchCheckAsync(bool silent){Busy(true);try{var id=await _twitch.CheckAsync();_twitchStatus.Text=$"OK: {id.DisplayName} ({id.Login})";_twitchStatus.ForeColor=Green;Log.Write("Twitch native check OK: "+id.Login);if(!silent)MessageBox.Show(this,"Twitch接続は正常です。",AppInfo.Name,MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){_twitchStatus.Text="接続失敗: "+ex.Message;_twitchStatus.ForeColor=Red;if(!silent)Fail("Twitch接続確認",ex);}finally{Busy(false);}}
    private async Task TwitchDisconnectAsync(){if(MessageBox.Show(this,"Twitchとの接続を解除しますか？",AppInfo.Name,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;Busy(true);try{await _twitch.DisconnectAsync();_twitchStatus.Text="未接続";_twitchStatus.ForeColor=Muted;}catch(Exception ex){Fail("Twitch接続解除",ex);}finally{Busy(false);}}
    private async Task YouTubeConnectAsync(){Busy(true);try{_youtubeStatus.Text="Google認証を開いています…";_youtubeStatus.ForeColor=Muted;using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(6));var id=await _youtube.ConnectAsync(cts.Token);_youtubeStatus.Text="接続済み: "+id.Title;_youtubeStatus.ForeColor=Green;MessageBox.Show(this,"YouTubeに接続しました。\r\n\r\n"+id.Title,AppInfo.Name,MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){_youtubeStatus.Text="接続失敗";_youtubeStatus.ForeColor=Red;Fail("YouTube接続",ex);}finally{Busy(false);}}
    private async Task YouTubeCheckAsync(bool silent){Busy(true);try{var id=await _youtube.CheckAsync();_youtubeStatus.Text="接続済み: "+id.Title;_youtubeStatus.ForeColor=Green;Log.Write("YouTube native check OK: "+id.Title);if(!silent)MessageBox.Show(this,"YouTube接続は正常です。\r\n\r\n"+id.Title,AppInfo.Name,MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){_youtubeStatus.Text="未接続 / 再確認が必要";_youtubeStatus.ForeColor=Red;if(!silent)Fail("YouTube接続確認",ex);}finally{Busy(false);}}
    private async Task YouTubeDisconnectAsync(){if(MessageBox.Show(this,"Google側の許可を取り消し、このPCのYouTube接続情報も削除しますか？",AppInfo.Name,MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;Busy(true);try{await _youtube.DisconnectAsync();_youtubeStatus.Text="未接続";_youtubeStatus.ForeColor=Muted;}catch(Exception ex){Fail("YouTube接続解除",ex);}finally{Busy(false);}}
    private void Fail(string where,Exception ex){Log.Write(where+" failed: "+ex);MessageBox.Show(this,$"{where}に失敗しました。\r\n\r\n{ex.Message}",AppInfo.Name,MessageBoxButtons.OK,MessageBoxIcon.Error);}
}

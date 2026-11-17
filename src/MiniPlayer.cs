using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassBar
{
    /// Floating always-on-top mini video player (like the browser's picture-in-picture).
    /// The video playing in the browser is found on YouTube and continues here from the same second.
    public class MiniPlayer : Window
    {
        static MiniPlayer instance;
        readonly WebView2 view = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
        readonly TextBlock title = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        string videoId;
        Task init;
        IntPtr hwnd;

        static readonly HttpClient http = CreateHttp();
        static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            return h;
        }

        MiniPlayer()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Transparent;
            Width = 560; Height = 352; MinWidth = 300; MinHeight = 200;
            FontFamily = new FontFamily("Segoe UI");
            Foreground = Flyout.Res("TextPrimary");
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 36, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14), UseAeroCaptionButtons = false
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Button B(string glyph, string tip, Action a)
            {
                var b = Flyout.SmallIcon(glyph, tip, a);
                WindowChrome.SetIsHitTestVisibleInChrome(b, true);
                buttons.Children.Add(b);
                return b;
            }
            B("", "Open in browser", () =>
            {
                if (videoId != null) BarWindow.Instance.Launch("https://www.youtube.com/watch?v=" + videoId);
                else if (pageUrl != null) BarWindow.Instance.Launch(pageUrl);
            });
            B("", "Close", Close);

            var header = new DockPanel { Height = 36, Margin = new Thickness(14, 0, 6, 0) };
            DockPanel.SetDock(buttons, Dock.Right);
            header.Children.Add(buttons);
            var icon = Flyout.Glyph("", 13, new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)));
            icon.Margin = new Thickness(0, 0, 9, 0);
            header.Children.Add(icon);
            header.Children.Add(title);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(new Border { Margin = new Thickness(6, 0, 6, 6), CornerRadius = new CornerRadius(9), ClipToBounds = true, Background = Brushes.Black, Child = view });

            Content = new Border
            {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = root,
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0A, 0x13, 0x28)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))
            };

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.MakeToolWindow(hwnd);
                Native.EnableBlur(hwnd);
            };
            SizeChanged += (s, e) => { if (hwnd != IntPtr.Zero) Native.SetRoundRegion(hwnd, (int)(14 * VisualTreeHelper.GetDpi(this).DpiScaleX)); };
            Closed += (s, e) => { instance = null; try { view.Dispose(); } catch { } };
        }

        /// Finds the video on YouTube from its title + channel and plays it here from the given second.
        public static async Task<bool> Open(string videoTitle, string channel, int seconds)
        {
            var id = await FindVideo(videoTitle, channel);
            if (id == null) return false;

            if (instance == null)
            {
                instance = new MiniPlayer();
                PlaceBottomRight(instance);
                instance.Show();
            }
            instance.title.Text = videoTitle;
            instance.videoId = id;
            instance.Activate();
            await instance.Play(id, seconds);
            return true;
        }

        static void EnsureInstance(string videoTitle)
        {
            if (instance == null)
            {
                instance = new MiniPlayer();
                PlaceBottomRight(instance);
                instance.Show();
            }
            instance.title.Text = videoTitle;
            instance.Activate();
        }

        /// A YouTube video by id: plays in the clean embedded player.
        public static async Task<bool> OpenYouTube(string id, string videoTitle, int seconds)
        {
            EnsureInstance(videoTitle);
            instance.videoId = id;
            instance.pageUrl = null;
            await instance.Play(id, seconds);
            return true;
        }

        /// Any other site (SVT Play, Twitch, …): opens the page itself and blows its video up to fill the player.
        public static async Task<bool> OpenPage(string url, string videoTitle, int seconds)
        {
            EnsureInstance(videoTitle);
            instance.videoId = null;
            instance.pageUrl = url;
            instance.pageSeconds = seconds;
            instance.init ??= instance.Init();
            await instance.init;
            if (instance.view.CoreWebView2 == null) return false;
            instance.view.CoreWebView2.Navigate(url);
            return true;
        }

        string pageUrl;
        int pageSeconds;

        // finds the biggest <video>, jumps to the time, plays it and makes it fill the window
        const string FillVideoScript = @"(function(){var t=%T%,n=0,done=false;
function big(){var vs=Array.from(document.querySelectorAll('video'));vs.sort(function(a,b){return b.offsetWidth*b.offsetHeight-a.offsetWidth*a.offsetHeight});return vs[0];}
function clickPlay(){var re=/^(spela|play|play now|watch|watch now|titta|fortsätt titta|fortsätt|resume|spela upp)$/i;
 var c=Array.from(document.querySelectorAll('a,button,[role=button]')).filter(function(e){return e.offsetWidth>0&&(re.test((e.textContent||'').trim())||re.test((e.getAttribute('aria-label')||'').trim()))});
 if(c[0])c[0].click();}
function go(){var v=big();
 if(!v||v.readyState<1){if(n==4||n==12||n==24)clickPlay();if(++n<240)setTimeout(go,500);else window.chrome.webview.postMessage('novideo');return;}
 if(!done){done=true;try{if(t>0&&Math.abs(v.currentTime-t)>3)v.currentTime=t;}catch(e){}
  setTimeout(function(){try{if(t>0&&Math.abs(v.currentTime-t)>5)v.currentTime=t;}catch(e){}},2500);}
 v.muted=false;var p=v.play();if(p&&p.catch)p.catch(function(){});
 var st=document.getElementById('gb-mini');if(!st){st=document.createElement('style');st.id='gb-mini';document.documentElement.appendChild(st);}
 st.textContent='html,body{overflow:hidden!important;background:#000!important}'+
  'video.gb-mini{position:fixed!important;left:0!important;top:0!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;'+
  'z-index:2147483647!important;background:#000!important;object-fit:contain!important;transform:none!important;margin:0!important}';
 v.classList.add('gb-mini');
 for(var a=v.parentElement;a&&a!==document.body;a=a.parentElement){a.style.setProperty('transform','none','important');a.style.setProperty('filter','none','important');a.style.setProperty('contain','none','important');}
 window.chrome.webview.postMessage('ok');
 setTimeout(go,3000);}
go();})();";

        /// Dev only: run a script and return its result.
        public static async Task<string> TestEval(string js) => instance?.view.CoreWebView2 == null ? null : await instance.view.CoreWebView2.ExecuteScriptAsync(js);

        /// Dev only (snapshot test): click a button on the page by its text.
        public static async Task TestClick(string label)
        {
            if (instance?.view.CoreWebView2 == null) return;
            var js = "Array.from(document.querySelectorAll('button')).filter(b=>b.textContent.trim().toLowerCase()==" + System.Text.Json.JsonSerializer.Serialize(label.ToLowerInvariant()) + ").forEach(b=>b.click());";
            await instance.view.CoreWebView2.ExecuteScriptAsync(js);
        }

        /// Dev only (snapshot test): mute and save what the player shows.
        public static async Task TestCapture(string file)
        {
            if (instance?.view.CoreWebView2 == null) return;
            instance.view.CoreWebView2.IsMuted = true;
            await Task.Delay(7000);
            using var fs = File.Create(file);
            await instance.view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            instance.Close();
        }

        static void PlaceBottomRight(Window w)
        {
            var work = SystemParameters.WorkArea;
            w.Left = work.Right - w.Width - 16;
            w.Top = work.Bottom - w.Height - 16;
        }

        async Task Play(string id, int seconds)
        {
            init ??= Init();
            await init;
            if (view.CoreWebView2 == null) return;
            view.CoreWebView2.Navigate($"https://glassbar.player/index.html?v={id}&t={Math.Max(0, seconds)}");
        }

        async Task Init()
        {
            // the YouTube embed needs to sit on a real web origin, so serve a tiny page from a local folder
            var dir = Path.Combine(BarSettings.DataDir, "player");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.html"), PlayerHtml);
            await view.EnsureCoreWebView2Async(await BarWindow.WebEnv());
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("glassbar.player", dir, CoreWebView2HostResourceAccessKind.Allow);
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            if (Environment.GetEnvironmentVariable("GB_MINI") != null) view.CoreWebView2.IsMuted = true;   // dev test
            view.CoreWebView2.NewWindowRequested += (s, e) => { e.Handled = true; BarWindow.Instance.Launch(e.Uri); };
            view.CoreWebView2.NavigationCompleted += async (s, e) =>
            {
                if (pageUrl == null || view.CoreWebView2.Source.StartsWith("https://glassbar.player")) return;
                try { await view.CoreWebView2.ExecuteScriptAsync(FillVideoScript.Replace("%T%", pageSeconds.ToString())); } catch { }
            };
            view.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                string m = null;
                try { m = e.TryGetWebMessageAsString(); } catch { }
                if (m == "novideo") BarWindow.Instance?.ShowToast("No video found on that page for the mini player", 5);
            };
        }

        const string PlayerHtml = @"<!doctype html><html><head><meta charset=""utf-8"">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}iframe{border:0;width:100%;height:100%;display:block}</style></head>
<body><iframe id=""f"" allow=""autoplay; encrypted-media; picture-in-picture; fullscreen"" allowfullscreen referrerpolicy=""strict-origin-when-cross-origin""></iframe>
<script>
var q = new URLSearchParams(location.search);
document.getElementById('f').src = 'https://www.youtube.com/embed/' + encodeURIComponent(q.get('v')) +
  '?autoplay=1&rel=0&playsinline=1&start=' + (parseInt(q.get('t')) || 0);
</script></body></html>";

        /// The YouTube video id in a youtube.com / youtu.be address, if it is one.
        public static string YouTubeId(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            var host = u.Host.ToLowerInvariant();
            if (host == "youtu.be") return Valid(u.AbsolutePath.Trim('/'));
            if (!host.EndsWith("youtube.com")) return null;
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            if (q["v"] is string v) return Valid(v);
            var m = Regex.Match(u.AbsolutePath, @"^/(shorts|live|embed)/([A-Za-z0-9_-]{11})");
            return m.Success ? m.Groups[2].Value : null;
            static string Valid(string id) => Regex.IsMatch(id ?? "", "^[A-Za-z0-9_-]{11}$") ? id : null;
        }

        /// Finds the browser window whose current tab plays this media and reads its address bar.
        /// Returns (address, window title); either can be null.
        public static (string url, string title) FindBrowserPage(string mediaTitle)
        {
            if (string.IsNullOrWhiteSpace(mediaTitle)) return (null, null);
            // compare letters and digits only: sites write "-" where the tab title has "–" and so on
            static string Norm(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " "), @"\s+", " ").Trim();
            var key = Norm(mediaTitle);
            if (key.Length > 40) key = key.Substring(0, 40).Trim();
            if (key.Length == 0) return (null, null);
            foreach (var h in Native.TaskbarWindows(IntPtr.Zero))
            {
                var t = Native.WindowTitle(h) ?? "";
                if (!Norm(t).Contains(key)) continue;
                return (AddressBar(h), t);
            }
            return (null, null);
        }

        static string AddressBar(IntPtr hwnd)
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                var edit = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Edit));
                if (edit != null && edit.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var p))
                {
                    var v = ((System.Windows.Automation.ValuePattern)p).Current.Value?.Trim();
                    if (string.IsNullOrEmpty(v) || v.Contains(' ') || !v.Contains('.')) return null;
                    if (!v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) v = "https://" + v;
                    return v;
                }
            }
            catch { }
            return null;
        }

        static async Task<string> FindVideo(string videoTitle, string channel)
        {
            if (string.IsNullOrWhiteSpace(videoTitle)) return null;
            try
            {
                var query = Uri.EscapeDataString((videoTitle + " " + (channel ?? "")).Trim());
                var html = await http.GetStringAsync("https://www.youtube.com/results?search_query=" + query);
                var m = Regex.Match(html, "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }
    }
}

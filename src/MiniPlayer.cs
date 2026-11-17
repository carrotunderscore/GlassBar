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
            B("", "Open in browser", () => { if (videoId != null) BarWindow.Instance.Launch("https://www.youtube.com/watch?v=" + videoId); });
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
        }

        const string PlayerHtml = @"<!doctype html><html><head><meta charset=""utf-8"">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}iframe{border:0;width:100%;height:100%;display:block}</style></head>
<body><iframe id=""f"" allow=""autoplay; encrypted-media; picture-in-picture; fullscreen"" allowfullscreen referrerpolicy=""strict-origin-when-cross-origin""></iframe>
<script>
var q = new URLSearchParams(location.search);
document.getElementById('f').src = 'https://www.youtube.com/embed/' + encodeURIComponent(q.get('v')) +
  '?autoplay=1&rel=0&playsinline=1&start=' + (parseInt(q.get('t')) || 0);
</script></body></html>";

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

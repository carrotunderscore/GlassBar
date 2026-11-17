using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassBar
{
    /// Messenger, Google Messages (SMS) and Gmail in glass flyouts. Each keeps its own login
    /// (stored in %LOCALAPPDATA%\GlassBar\WebView2) and runs in the background so unread badges update.
    public partial class BarWindow
    {
        readonly Dictionary<string, WebFlyout> web = new();
        static CoreWebView2Environment webEnv;

        static readonly (string key, string title, string url, double w, double h)[] WebApps =
        {
            ("messenger", "Messenger", "https://www.facebook.com/messages/", 520, 720),
            ("messages", "Messages", "https://messages.google.com/web/conversations", 560, 720),
            ("gmail", "Gmail", "https://mail.google.com/mail/u/0/#inbox", 960, 760),
        };

        public static async Task<CoreWebView2Environment> WebEnv()
        {
            if (webEnv != null) return webEnv;
            var dir = Path.Combine(BarSettings.DataDir, "WebView2");
            // videos in the mini player may start with sound without a click
            var opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            webEnv = await CoreWebView2Environment.CreateAsync(null, dir, opts);
            return webEnv;
        }

        WebFlyout GetWeb(string key)
        {
            if (web.TryGetValue(key, out var f)) return f;
            foreach (var a in WebApps)
                if (a.key == key)
                {
                    f = Register(new WebFlyout(a.title, a.url, a.w, a.h));
                    f.UnreadChanged += n => SetBadge(key, n);
                    f.UnloadWhenHidden = !Settings.ChatsInBackground;
                    web[key] = f;
                    return f;
                }
            return null;
        }

        void InitWeb()
        {
            // create them hidden so they log in / count unread in the background
            if (!Settings.ChatsInBackground) return;   // they load when you open them
            foreach (var a in WebApps) _ = GetWeb(a.key).Preload();
        }

        void Web_Click(object s, RoutedEventArgs e)
        {
            var key = (string)((FrameworkElement)s).Tag;
            GetWeb(key)?.Toggle((FrameworkElement)s);
        }

        void SetBadge(string key, int n)
        {
            var (badge, text) = key switch
            {
                "messenger" => (MessengerBadge, MessengerCount),
                "messages" => (SmsBadge, SmsCount),
                _ => (GmailBadge, GmailCount)
            };
            badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            text.Text = n > 99 ? "99+" : n.ToString();
        }

        void ToggleChatsInBackground()
        {
            Settings.ChatsInBackground = !Settings.ChatsInBackground;
            Settings.Save();
            foreach (var f in web.Values)
            {
                f.UnloadWhenHidden = !Settings.ChatsInBackground;
                if (!Settings.ChatsInBackground && !f.IsVisible) f.Unload();
            }
            if (Settings.ChatsInBackground) InitWeb();
            ShowToast(Settings.ChatsInBackground
                ? "Chats run in the background: unread badges on, uses more memory"
                : "Chats load when you open them: less memory, no unread badges");
        }

        async void ClearWebData()
        {
            foreach (var f in web.Values) await f.ClearLogin();
            ShowToast("Signed out of Messenger, Messages and Gmail");
        }
    }

    public class WebFlyout : Flyout
    {
        readonly string url;
        readonly WebView2 view = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 12, 20, 40) };
        Task init;
        public event Action<int> UnreadChanged;
        public event Action<string> Message;        // window.chrome.webview.postMessage from our scripts
        public event Action<bool> PageLoaded;       // navigation finished (success?)
        public CoreWebView2 Core => view.CoreWebView2;
        bool unloaded;
        public bool UnloadWhenHidden;                // close the page a while after the pop-up is hidden
        System.Windows.Threading.DispatcherTimer unloadTimer;

        /// Close the page (keeps the login). It loads again when the pop-up opens.
        public void Unload()
        {
            try
            {
                if (view.CoreWebView2 == null || unloaded) return;
                unloaded = true;
                view.CoreWebView2.Navigate("about:blank");
                UnreadChanged?.Invoke(0);
            }
            catch { }
        }

        void SetMemoryLow(bool low)
        {
            try { if (view.CoreWebView2 != null) view.CoreWebView2.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; }
            catch { }
        }
        static readonly Regex Count = new(@"\((\d+)\)");

        public WebFlyout(string title, string url, double w, double h) : base(title, w, h)
        {
            this.url = url;
            var frame = new Border { Margin = new Thickness(8, 0, 8, 8), CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = view };
            Body.Children.Add(frame);
            AddHeaderButton("", "Reload", () => view.CoreWebView2?.Reload());
            IsVisibleChanged += (s, e) =>
            {
                // hidden: tell the page to use as little memory as it can
                SetMemoryLow(!IsVisible);
                if (IsVisible) { unloadTimer?.Stop(); return; }
                if (!UnloadWhenHidden) return;
                unloadTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
                unloadTimer.Tick -= UnloadTick;
                unloadTimer.Tick += UnloadTick;
                unloadTimer.Start();
            };
            AddHeaderButton("", "Back", () => { if (view.CoreWebView2?.CanGoBack == true) view.CoreWebView2.GoBack(); });
            AddHeaderButton("", "Open in browser", () => BarWindow.Instance.Launch(view.Source?.ToString() ?? url));
        }

        public Task Preload()
        {
            if (init != null) return init;
            // needs a real window handle; show it far off-screen once, then hide
            ShowActivated = false;
            Show();
            Hide();
            ShowActivated = true;
            return init = InitView();
        }

        async Task InitView()
        {
            try
            {
                await view.EnsureCoreWebView2Async(await BarWindow.WebEnv());
                var core = view.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.DocumentTitleChanged += (s, e) => ParseTitle(core.DocumentTitle);
                core.WebMessageReceived += (s, e) => { try { Message?.Invoke(e.TryGetWebMessageAsString()); } catch { } };
                core.NavigationCompleted += (s, e) => PageLoaded?.Invoke(e.IsSuccess);
                core.NewWindowRequested += (s, e) =>
                {
                    // links open in the normal browser; Google/Facebook login popups stay inside
                    var u = e.Uri ?? "";
                    if (u.Contains("accounts.google.com") || u.Contains("facebook.com/login") || u.Contains("appleid.apple.com")
                        || u.Contains("login.microsoftonline.com") || u.Contains("login.live.com")) return;
                    e.Handled = true;
                    BarWindow.Instance.Launch(u);
                };
                core.PermissionRequested += (s, e) =>
                {
                    if (e.PermissionKind == CoreWebView2PermissionKind.Notifications) e.State = CoreWebView2PermissionState.Allow;
                };
                core.Navigate(url);
                SetMemoryLow(!IsVisible);
            }
            catch (Exception ex)
            {
                Body.Children.Clear();
                Body.Children.Add(new TextBlock
                {
                    Text = "Couldn't start the web view:\n" + ex.Message, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20), Foreground = Res("TextSecondary")
                });
            }
        }

        void UnloadTick(object s, EventArgs e) { unloadTimer.Stop(); if (!IsVisible) Unload(); }

        /// Navigate (also wakes an unloaded page).
        public void Go(string address)
        {
            unloaded = false;
            view.CoreWebView2?.Navigate(address);
        }

        void ParseTitle(string title)
        {
            var m = Count.Match(title ?? "");
            UnreadChanged?.Invoke(m.Success ? int.Parse(m.Groups[1].Value) : 0);
        }

        protected override void OnShown()
        {
            _ = Preload().ContinueWith(_ => Dispatcher.Invoke(() => { if (unloaded) Go(url); }));
            view.Focus();
        }

        public async Task ClearLogin()
        {
            try
            {
                await Preload();
                await view.CoreWebView2.Profile.ClearBrowsingDataAsync();
                view.CoreWebView2.Navigate(url);
            }
            catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    /// One AI app on the usage card.
    public class AiProvider
    {
        public string Key, Name, Origin, ApiUrl, LoginUrl, Script;
        public StartApp App;
        public bool HasMeter = true;

        public double? Session, Week;
        public DateTime? SessionReset, WeekReset;
        public string Plan, Error;
        public string State = "loading";   // loading | ok | login | error
        public DateTime Updated, LoginShownAt;
        public WebFlyout Web;

        public Button Tile;
        public Border SessionBar, WeekBar;
        public TextBlock SessionPct, WeekPct, Status;
        public FrameworkElement Meters;
    }

    /// Usage limits of the AI apps you have installed (ChatGPT/Codex, Claude, Gemini), read from
    /// your own signed-in web session in a hidden web view, the same numbers their usage pages show.
    public partial class BarWindow
    {
        readonly List<AiProvider> ai = new();
        DispatcherTimer aiTimer;
        ListFlyout aiFlyout;

        // Claude: claude.ai/settings/usage reads these two endpoints.
        const string ClaudeScript = @"(async()=>{const post=o=>window.chrome.webview.postMessage(JSON.stringify(o));
try{const r=await fetch('/api/organizations',{credentials:'include',headers:{'accept':'application/json'}});
if(r.status==401||r.status==403){post({login:true});return;}
if(!r.ok){post({err:'organizations '+r.status});return;}
const orgs=await r.json();if(!Array.isArray(orgs)||!orgs.length){post({login:true});return;}
const has=(o,c)=>(o.capabilities||[]).some(x=>String(x).includes(c));
const org=orgs.find(o=>has(o,'claude_max')||has(o,'claude_pro'))||orgs.find(o=>has(o,'chat'))||orgs[0];
const u=await fetch('/api/organizations/'+org.uuid+'/usage',{credentials:'include',headers:{'accept':'application/json'}});
if(!u.ok){post({err:'usage '+u.status});return;}
const d=await u.json();const f=d.five_hour||{},s=d.seven_day||{};
post({ok:true,s:f.utilization,sr:f.resets_at,w:s.utilization,wr:s.resets_at,plan:org.name||''});
}catch(e){post({err:String(e)});}})();";

        // ChatGPT / Codex: the same limits the Codex app and chatgpt.com/codex/settings/usage show.
        const string ChatGptScript = @"(async()=>{const post=o=>window.chrome.webview.postMessage(JSON.stringify(o));
try{const r=await fetch('/api/auth/session',{credentials:'include'});
if(!r.ok){post({login:true});return;}
const s=await r.json();if(!s||!s.accessToken){post({login:true});return;}
const h={'Authorization':'Bearer '+s.accessToken,'accept':'application/json'};
if(s.account&&s.account.id)h['ChatGPT-Account-Id']=s.account.id;
const u=await fetch('/backend-api/wham/usage',{headers:h,credentials:'include'});
if(u.status==401){post({login:true});return;}
if(!u.ok){post({err:'usage '+u.status});return;}
const d=await u.json();const rl=d.rate_limit||{};
const at=w=>!w?null:(w.reset_at?new Date(w.reset_at*1000).toISOString():(w.reset_after_seconds!=null?new Date(Date.now()+w.reset_after_seconds*1000).toISOString():null));
const p=rl.primary_window,q=rl.secondary_window;
post({ok:true,s:p?p.used_percent:null,sr:at(p),w:q?q.used_percent:null,wr:at(q),plan:d.plan_type||''});
}catch(e){post({err:String(e)});}})();";

        void InitAi()
        {
            var apps = StartApps.All;
            StartApp Find(Func<StartApp, bool> f) => apps.FirstOrDefault(f);

            var gpt = Find(a => a.Name == "ChatGPT" || a.AppID.StartsWith("OpenAI.", StringComparison.OrdinalIgnoreCase) || a.Name == "Codex");
            var claude = Find(a => a.Name == "Claude" || a.AppID.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) || a.AppID.StartsWith("AnthropicClaude", StringComparison.OrdinalIgnoreCase));
            var gemini = Find(a => a.Name == "Gemini" || a.AppID.Contains("gemini", StringComparison.OrdinalIgnoreCase));
            bool none = apps.Count == 0;   // no app list yet: show all three

            if (gpt != null || none)
                ai.Add(new AiProvider
                {
                    Key = "chatgpt", Name = "ChatGPT", App = gpt, Origin = "https://chatgpt.com",
                    ApiUrl = "https://chatgpt.com/", LoginUrl = "https://chatgpt.com/auth/login", Script = ChatGptScript
                });
            if (claude != null || none)
                ai.Add(new AiProvider
                {
                    Key = "claude", Name = "Claude", App = claude, Origin = "https://claude.ai",
                    ApiUrl = "https://claude.ai/settings/usage", LoginUrl = "https://claude.ai/login", Script = ClaudeScript
                });
            if (gemini != null || none)
                ai.Add(new AiProvider { Key = "gemini", Name = "Gemini", App = gemini, HasMeter = false, State = "ok" });

            if (snapshotPath != null && Environment.GetEnvironmentVariable("GB_AIDEMO") != null)
                foreach (var p in ai.Where(p => p.HasMeter))
                { p.State = "ok"; p.Session = 41; p.Week = 87; p.SessionReset = DateTime.Now.AddMinutes(160); p.WeekReset = DateTime.Now.AddDays(4); }
            foreach (var p in ai) AiPanel.Children.Add(BuildAiTile(p));
            UpdateAiVisibility();
        }

        void UpdateAiVisibility() => AiCard.Visibility = Settings.AiUsage && ai.Count > 0 && !IsHidden(AiCard) ? Visibility.Visible : Visibility.Collapsed;

        void StartAi()
        {
            if (!Settings.AiUsage || snapshotPath != null) return;
            foreach (var p in ai.Where(p => p.HasMeter)) _ = RefreshAi(p);
            aiTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            aiTimer.Tick -= AiTick;
            aiTimer.Tick += AiTick;
            aiTimer.Start();
        }

        void AiTick(object s, EventArgs e)
        {
            // signed out ones wait until you click Sign in (no page kept loaded for them)
            foreach (var p in ai.Where(p => p.HasMeter && p.State != "login")) _ = RefreshAi(p);
        }

        void ToggleAi()
        {
            Settings.AiUsage = !Settings.AiUsage;
            Settings.Save();
            UpdateAiVisibility();
            if (Settings.AiUsage) StartAi(); else aiTimer?.Stop();
        }

        // ---------------------------------------------------------------
        // reading the numbers
        // ---------------------------------------------------------------
        WebFlyout AiWeb(AiProvider p)
        {
            if (p.Web != null) return p.Web;
            p.Web = Register(new WebFlyout("Sign in to " + p.Name, p.ApiUrl, 520, 720));
            p.Web.KeepOpen = true;   // a Google/Apple login pop-up takes focus: stay open until closed or signed in
            p.Web.Message += m => OnAiMessage(p, m);
            p.Web.PageLoaded += ok => { AiLog(p, "page " + (ok ? "loaded " : "failed ") + Short(p.Web.Core?.Source)); if (ok) _ = RunAiScript(p); };
            return p.Web;
        }

        async Task RefreshAi(AiProvider p)
        {
            if (!p.HasMeter) return;
            var w = AiWeb(p);
            try
            {
                await w.Preload();
                var core = w.Core;
                if (core == null) { SetAiState(p, "error", "The web view didn't start"); return; }
                // the page is unloaded between checks: load it, PageLoaded runs the script
                if (!(core.Source ?? "").StartsWith(p.Origin, StringComparison.OrdinalIgnoreCase)) { w.Go(p.ApiUrl); return; }
                await RunAiScript(p);
            }
            catch (Exception ex) { SetAiState(p, "error", ex.Message); }
        }

        async Task RunAiScript(AiProvider p)
        {
            var core = p.Web?.Core;
            if (core == null || !(core.Source ?? "").StartsWith(p.Origin, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (core.IsSuspended) core.Resume();
                await core.ExecuteScriptAsync(p.Script);
            }
            catch (Exception ex) { AiLog(p, "script failed: " + ex.Message); }
        }

        /// Hidden in the background between refreshes: let the page sleep so it uses no CPU.
        /// Got the numbers (or found out we're signed out): unload the page so it doesn't sit in memory.
        void UnloadAi(AiProvider p)
        {
            if (p.Web != null && !p.Web.IsVisible) p.Web.Unload();
        }

        static string Short(string url) => url == null ? "" : url.Length > 70 ? url.Substring(0, 70) : url;

        static void AiLog(AiProvider p, string msg)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(BarSettings.DataDir, "ai.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {p.Name}: {msg}\r\n"); } catch { }
        }

        void OnAiMessage(AiProvider p, string msg)
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                var r = doc.RootElement;
                if (r.TryGetProperty("login", out _)) { AiLog(p, "not signed in"); SetAiState(p, "login"); UnloadAi(p); return; }
                if (r.TryGetProperty("err", out var err)) { AiLog(p, "error " + err.GetString()); SetAiState(p, "error", err.GetString()); UnloadAi(p); return; }
                if (!r.TryGetProperty("ok", out _)) return;

                static double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                static DateTime? Time(JsonElement e, string k) =>
                    e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime() : null;

                p.Session = Num(r, "s");
                p.Week = Num(r, "w");
                p.SessionReset = Time(r, "sr");
                p.WeekReset = Time(r, "wr");
                p.Plan = r.TryGetProperty("plan", out var plan) ? plan.GetString() : null;
                p.Updated = DateTime.Now;
                AiLog(p, $"ok session={p.Session} weekly={p.Week}");
                SetAiState(p, "ok");
                if (p.Web != null && p.Web.IsVisible)
                {
                    // signed in: close the sign-in window. If it only just opened (you were already
                    // signed in), show the numbers instead of a window that blinks away.
                    bool justOpened = (DateTime.Now - p.LoginShownAt).TotalSeconds < 5;
                    p.Web.Hide();
                    if (justOpened) ShowAiFlyout();
                    else ShowToast($"{p.Name} connected – usage shows on the bar");
                }
                UnloadAi(p);
            }
            catch { }
        }

        void SetAiState(AiProvider p, string state, string error = null)
        {
            p.State = state;
            p.Error = error;
            UpdateAiTile(p);
            if (aiFlyout?.IsVisible == true) FillAiFlyout();
        }

        // ---------------------------------------------------------------
        // the card
        // ---------------------------------------------------------------
        static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
        static Brush UsageBrush(double pct) => Hex(pct >= 85 ? "#FF6B7A" : pct >= 60 ? "#F5C451" : "#5BD68A");

        static ImageSource AiIcon(AiProvider p) => p.App?.Icon;

        Button BuildAiTile(AiProvider p)
        {
            var tile = new Button { Style = (Style)FindResource("Flat"), Padding = new Thickness(6, 4, 8, 4), Margin = new Thickness(0, 0, 2, 0), Tag = p };
            tile.Click += (s, e) => AiTile_Click(p);
            tile.MouseRightButtonUp += (s, e) => { e.Handled = true; AiTile_Menu(p, tile); };

            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement icon = AiIcon(p) is ImageSource src
                ? new Image { Source = src, Width = 30, Height = 30 }
                : new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Hex("#22FFFFFF"),
                    Child = new TextBlock { Text = p.Name.Substring(0, 1), FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            icon.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(icon);

            if (!p.HasMeter)
            {
                // Gemini: no usage meter published, so just the app
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                name.Children.Add(new TextBlock { Text = p.Name, FontSize = 12, FontWeight = FontWeights.SemiBold });
                name.Children.Add(new TextBlock { Text = "Open", FontSize = 10.5, Foreground = Flyout.Res("TextTertiary") });
                Grid.SetColumn(name, 1);
                g.Children.Add(name);
                tile.Content = g;
                tile.ToolTip = $"{p.Name}: Google doesn't publish usage limits for the Gemini app. Click to open it.";
                p.Tile = tile;
                return tile;
            }

            var meters = new Grid { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            meters.RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });
            meters.RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });

            (Border fill, TextBlock pct) Row(int row, string label)
            {
                var l = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(l, row);
                meters.Children.Add(l);
                var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
                track.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Hex("#22FFFFFF") });
                var fill = new Border { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = UsageBrush(0) };
                track.Children.Add(fill);
                Grid.SetRow(track, row); Grid.SetColumn(track, 1);
                meters.Children.Add(track);
                var pct = new TextBlock { Text = "–", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(pct, row); Grid.SetColumn(pct, 2);
                meters.Children.Add(pct);
                return (fill, pct);
            }
            (p.SessionBar, p.SessionPct) = Row(0, "Session");
            (p.WeekBar, p.WeekPct) = Row(1, "Weekly");

            p.Status = new TextBlock { FontSize = 11.5, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            Grid.SetColumn(meters, 1);
            Grid.SetColumn(p.Status, 1);
            g.Children.Add(meters);
            g.Children.Add(p.Status);
            p.Meters = meters;
            tile.Content = g;
            p.Tile = tile;
            UpdateAiTile(p);
            return tile;
        }

        static string Until(DateTime? t)
        {
            if (t == null) return "";
            var d = t.Value - DateTime.Now;
            if (d.TotalMinutes < 1) return "now";
            if (d.TotalHours < 24) return d.Hours > 0 ? $"{(int)d.TotalHours}h {d.Minutes:00}m" : $"{d.Minutes}m";
            return t.Value.ToString("ddd d");
        }

        void UpdateAiTile(AiProvider p)
        {
            if (p.Tile == null || !p.HasMeter) return;
            bool ok = p.State == "ok";
            p.Meters.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            p.Status.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            switch (p.State)
            {
                case "login":
                    p.Status.Text = "Sign in";
                    p.Status.Foreground = Flyout.Res("Accent");
                    p.Tile.ToolTip = $"{p.Name}: sign in once to see your usage limits";
                    break;
                case "error":
                    p.Status.Text = "No data";
                    p.Status.Foreground = Flyout.Res("TextTertiary");
                    p.Tile.ToolTip = $"{p.Name}: couldn't read usage ({p.Error}). Click for details.";
                    break;
                case "loading":
                    p.Status.Text = "…";
                    p.Status.Foreground = Flyout.Res("TextTertiary");
                    p.Tile.ToolTip = $"{p.Name}: loading usage";
                    break;
                default:
                    void Set(Border bar, TextBlock pct, double? v)
                    {
                        double x = Math.Clamp(v ?? 0, 0, 100);
                        bar.Width = 48 * x / 100;
                        bar.Background = UsageBrush(x);
                        pct.Text = v == null ? "–" : $"{Math.Round(x)}%";
                    }
                    Set(p.SessionBar, p.SessionPct, p.Session);
                    Set(p.WeekBar, p.WeekPct, p.Week);
                    var tip = $"{p.Name}";
                    if (p.Session != null) tip += $" · Session {Math.Round(p.Session.Value)}%" + (p.SessionReset != null ? $", resets in {Until(p.SessionReset)}" : "");
                    if (p.Week != null) tip += $" · Weekly {Math.Round(p.Week.Value)}%" + (p.WeekReset != null ? $", resets {WeekText(p.WeekReset)}" : "");
                    p.Tile.ToolTip = tip;
                    break;
            }
            RefreshHover(p.Tile);
        }

        static string WeekText(DateTime? t) => t == null ? "" : (t.Value - DateTime.Now).TotalHours < 24 ? "in " + Until(t) : t.Value.ToString("ddd d MMM HH:mm");

        void AiTile_Click(AiProvider p)
        {
            if (!p.HasMeter) { OpenAiApp(p); return; }
            if (p.State == "login") { ShowAiLogin(p); return; }
            ShowAiFlyout();
        }

        void AiTile_Menu(AiProvider p, FrameworkElement anchor)
        {
            var m = new ContextMenu();
            m.Items.Add(MI("Open " + p.Name, () => OpenAiApp(p), enabled: p.App != null));
            if (p.HasMeter)
            {
                m.Items.Add(MI("Refresh usage", () => _ = RefreshAi(p)));
                m.Items.Add(MI(p.State == "login" ? "Sign in…" : "Show the web page", () => ShowAiLogin(p)));
            }
            m.Items.Add(new Separator());
            m.Items.Add(MI("Hide the AI usage card", ToggleAi));
            m.PlacementTarget = anchor;
            m.IsOpen = true;
        }

        void OpenAiApp(AiProvider p)
        {
            if (p.App != null) StartApps.Launch(p.App);
            else if (p.Key == "gemini") Launch("https://gemini.google.com/app");
        }

        void ShowAiLogin(AiProvider p)
        {
            var w = AiWeb(p);
            p.LoginShownAt = DateTime.Now;
            _ = w.Preload().ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                try { if (w.Core?.IsSuspended == true) w.Core.Resume(); } catch { }
                w.Go(p.State != "ok" ? p.LoginUrl : p.Origin + "/");
            }));
            w.ShowAbove(p.Tile ?? (FrameworkElement)AiCard);
        }

        // ---------------------------------------------------------------
        // details pop-up
        // ---------------------------------------------------------------
        void ShowAiFlyout()
        {
            if (aiFlyout == null)
            {
                aiFlyout = Register(new ListFlyout("AI usage", 440, 400));
                aiFlyout.EmptyText = "No AI apps found.";
                aiFlyout.AddFooter(Flyout.Pill("Refresh", () => { foreach (var p in ai) _ = RefreshAi(p); }, ""));
            }
            FillAiFlyout();
            aiFlyout.Toggle(AiCard);
        }

        void FillAiFlyout()
        {
            var rows = new List<ListRow>();
            foreach (var p in ai)
            {
                var icon = AiIcon(p);
                if (!p.HasMeter)
                {
                    rows.Add(new ListRow { Icon = icon, Glyph = "", Title = p.Name, Subtitle = "Google doesn't publish usage limits for the Gemini app", Right = "", Open = () => OpenAiApp(p) });
                    continue;
                }
                switch (p.State)
                {
                    case "login":
                        rows.Add(new ListRow { Icon = icon, Glyph = "", Title = p.Name, Subtitle = "Click to sign in once – then usage shows here", Right = "Sign in", Open = () => ShowAiLogin(p) });
                        break;
                    case "error":
                        rows.Add(new ListRow { Icon = icon, Glyph = "", Title = p.Name, Subtitle = "Couldn't read usage: " + p.Error, Right = "", Open = () => ShowAiLogin(p),
                            Actions = { ("", "Retry", () => _ = RefreshAi(p)) }, StayOpen = true });
                        break;
                    case "loading":
                        rows.Add(new ListRow { Icon = icon, Glyph = "", Title = p.Name, Subtitle = "Loading…", Right = "" });
                        break;
                    default:
                        string plan = string.IsNullOrEmpty(p.Plan) ? "" : $" ({p.Plan})";
                        rows.Add(new ListRow
                        {
                            Icon = icon, Glyph = "", Title = $"{p.Name} · Session{plan}",
                            Subtitle = p.SessionReset != null ? $"5-hour window · resets in {Until(p.SessionReset)} ({p.SessionReset:HH:mm})" : "5-hour window",
                            Right = p.Session == null ? "–" : $"{Math.Round(p.Session.Value)}%", Open = () => OpenAiApp(p)
                        });
                        rows.Add(new ListRow
                        {
                            Icon = icon, Glyph = "", Title = $"{p.Name} · Weekly",
                            Subtitle = p.WeekReset != null ? $"Resets {WeekText(p.WeekReset)}" : "7-day window",
                            Right = p.Week == null ? "–" : $"{Math.Round(p.Week.Value)}%", Open = () => OpenAiApp(p)
                        });
                        break;
                }
            }
            aiFlyout.SetRows(rows);
            var last = ai.Where(p => p.Updated > DateTime.MinValue).Select(p => p.Updated).DefaultIfEmpty().Max();
            aiFlyout.SetSubtitle(last == default ? "" : "updated " + last.ToString("HH:mm"));
        }
    }
}

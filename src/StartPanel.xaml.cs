using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassBar
{
    public class StartApp
    {
        public string Name { get; set; }
        public string AppID { get; set; }
        ImageSource icon;
        bool iconTried;
        public ImageSource Icon
        {
            get
            {
                if (!iconTried)
                {
                    iconTried = true;
                    icon = Native.ShellImage(@"shell:AppsFolder\" + AppID, 48);
                    if (icon == null && Path.IsPathRooted(AppID) && File.Exists(AppID)) icon = Native.ShellImage(AppID, 48);
                }
                return icon;
            }
        }
    }

    public class StartHeader { public string Letter { get; set; } }

    /// The apps Windows lists in Start (desktop programs and Store apps), via Get-StartApps.
    public static class StartApps
    {
        static string CacheFile => Path.Combine(BarSettings.DataDir, "startapps.json");
        public static List<StartApp> All { get; private set; } = new();
        static DateTime loaded = DateTime.MinValue;
        static Task refreshing;

        public static void LoadCache()
        {
            try { if (File.Exists(CacheFile)) All = Clean(JsonSerializer.Deserialize<List<StartApp>>(File.ReadAllText(CacheFile))); }
            catch { }
        }

        public static Task Refresh(bool force = false)
        {
            if (!force && (DateTime.Now - loaded).TotalMinutes < 5) return Task.CompletedTask;
            return refreshing ??= Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
                    using var p = Process.Start(psi);
                    var json = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    var list = JsonSerializer.Deserialize<List<StartApp>>(json);
                    if (list?.Count > 0)
                    {
                        var clean = Clean(list);
                        // keep already-loaded icons for unchanged apps
                        var old = All.ToDictionary(a => a.AppID, a => a);
                        All = clean.Select(a => old.TryGetValue(a.AppID, out var o) && o.Name == a.Name ? o : a).ToList();
                        File.WriteAllText(CacheFile, json);
                        loaded = DateTime.Now;
                    }
                }
                catch { }
                finally { refreshing = null; }
            });
        }

        static List<StartApp> Clean(List<StartApp> list) =>
            (list ?? new()).Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.AppID)
                                    && !a.Name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
                           .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                           .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        public static void Launch(StartApp a, bool admin = false)
        {
            try
            {
                if (admin && Path.IsPathRooted(a.AppID) && File.Exists(a.AppID))
                    Process.Start(new ProcessStartInfo(a.AppID) { UseShellExecute = true, Verb = "runas" });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"shell:AppsFolder\\" + a.AppID + "\"") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (Exception ex) { BarWindow.Instance.ShowToast("Couldn't open: " + ex.Message); }
        }
    }

    /// Glass Start menu: search, pinned apps, all apps, recent files and power.
    public partial class StartPanel : UserControl
    {
        readonly Flyout host;

        public StartPanel(Flyout host)
        {
            this.host = host;
            InitializeComponent();
            var name = BarWindow.Demo ? "User" : Environment.UserName;
            UserName.Text = name;
            UserInitial.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        }

        BarSettings S => BarWindow.Instance.Settings;

        public async void Opened()
        {
            Search.Text = "";
            ShowView(HomeView);
            Fill();
            Dispatcher.BeginInvoke(() => { Search.Focus(); Keyboard.Focus(Search); }, System.Windows.Threading.DispatcherPriority.Input);
            await StartApps.Refresh();
            if (host.IsVisible) Fill();
        }

        void Fill()
        {
            EnsureDefaultPins();
            PinnedList.ItemsSource = S.StartPins.Select(id => StartApps.All.FirstOrDefault(a => a.AppID == id)).Where(a => a != null).ToList();
            RecentList.ItemsSource = BarWindow.Demo ? new List<RecentFile>() : BarWindow.Instance.RecentFiles.Take(6).ToList();
            if (BarWindow.Demo && RecentList.Parent is DockPanel recentBox) recentBox.Visibility = Visibility.Collapsed;
            var items = new List<object>();
            string last = null;
            foreach (var a in StartApps.All)
            {
                var c = char.ToUpperInvariant(a.Name[0]);
                var letter = char.IsLetter(c) ? c.ToString() : "#";
                if (letter != last) { items.Add(new StartHeader { Letter = letter }); last = letter; }
                items.Add(a);
            }
            AllList.ItemsSource = items;
            if (SearchView.Visibility == Visibility.Visible) RunSearch();
        }

        /// First run: pin what's on the taskbar plus the usual Windows apps.
        void EnsureDefaultPins()
        {
            if (S.StartPins != null || StartApps.All.Count == 0) return;
            var want = new List<string>();
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
                if (Directory.Exists(dir)) want.AddRange(Directory.GetFiles(dir, "*.lnk").Select(Path.GetFileNameWithoutExtension));
            }
            catch { }
            want.AddRange(new[] { "File Explorer", "Settings", "Microsoft Store", "Photos", "Calculator", "Spotify", "Notepad", "Paint", "Snip & Sketch", "Mail", "Calendar" });
            S.StartPins = want.Select(n => StartApps.All.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                              .Where(a => a != null).Select(a => a.AppID).Distinct().Take(18).ToList();
            S.Save();
        }

        void ShowView(FrameworkElement v)
        {
            HomeView.Visibility = v == HomeView ? Visibility.Visible : Visibility.Collapsed;
            AllView.Visibility = v == AllView ? Visibility.Visible : Visibility.Collapsed;
            SearchView.Visibility = v == SearchView ? Visibility.Visible : Visibility.Collapsed;
        }

        void AllApps_Click(object s, RoutedEventArgs e) { ShowView(AllView); AllList.ScrollIntoView(AllList.Items.Count > 0 ? AllList.Items[0] : null); }
        void Back_Click(object s, RoutedEventArgs e) => ShowView(HomeView);

        // ---------- search ----------
        void Search_TextChanged(object s, TextChangedEventArgs e)
        {
            SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Search.Text.Trim().Length == 0) { ShowView(HomeView); return; }
            ShowView(SearchView);
            RunSearch();
        }

        void RunSearch()
        {
            var q = Search.Text.Trim();
            var results = StartApps.All
                .Select(a => (a, score: Score(a.Name, q)))
                .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.a.Name.Length)
                .Select(x => x.a).Take(12).ToList();
            ResultList.ItemsSource = results;
            if (results.Count > 0) ResultList.SelectedIndex = 0;
            SearchTitle.Text = results.Count > 0 ? "Best matches" : "No apps found";
            WinSearchText.Text = $"Search Windows and the web for \"{q}\"";
        }

        static int Score(string name, string q)
        {
            if (name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 100;
            // start of a word ("code" -> "Visual Studio Code")
            if (name.Split(' ', '-', '.', '(').Any(w => w.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))) return 80;
            if (name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return 60;
            // initials ("vsc" -> "Visual Studio Code")
            var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));
            return initials.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 40 : 0;
        }

        void Search_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (SearchView.Visibility == Visibility.Visible && ResultList.SelectedItem is StartApp a) Run(a);
                else if (Search.Text.Trim().Length > 0) WinSearch_Click(null, null);
            }
            else if (e.Key == Key.Down && SearchView.Visibility == Visibility.Visible && ResultList.Items.Count > 0)
            {
                e.Handled = true;
                ResultList.SelectedIndex = Math.Min(ResultList.SelectedIndex + 1, ResultList.Items.Count - 1);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
            }
            else if (e.Key == Key.Up && SearchView.Visibility == Visibility.Visible && ResultList.Items.Count > 0)
            {
                e.Handled = true;
                ResultList.SelectedIndex = Math.Max(ResultList.SelectedIndex - 1, 0);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
            }
        }

        async void WinSearch_Click(object s, RoutedEventArgs e)
        {
            var q = Search.Text.Trim();
            host.Hide();
            Native.PressKeys(0x5B);   // the real Start menu search (our keyboard hook lets injected keys through)
            await Task.Delay(450);
            if (q.Length > 0) Native.TypeText(q);
        }

        // ---------- launching ----------
        void Run(StartApp a)
        {
            host.Hide();
            StartApps.Launch(a);
        }

        void Pinned_Click(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is StartApp a) Run(a);
        }

        void List_Click(object s, MouseButtonEventArgs e)
        {
            if (BarWindow.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is StartApp a) Run(a);
        }

        void List_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (s as ListBox)?.SelectedItem is StartApp a) { e.Handled = true; Run(a); }
        }

        void Recent_Click(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is RecentFile f) { host.Hide(); BarWindow.Instance.Launch(f.Path); }
        }

        void App_RightClick(object s, MouseButtonEventArgs e)
        {
            var a = (BarWindow.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext ?? (s as FrameworkElement)?.DataContext) as StartApp;
            if (a == null) return;
            e.Handled = true;
            bool pinned = S.StartPins?.Contains(a.AppID) == true;
            var m = new ContextMenu();
            m.Items.Add(Item("Open", () => Run(a)));
            if (Path.IsPathRooted(a.AppID) && File.Exists(a.AppID))
            {
                m.Items.Add(Item("Run as administrator", () => { host.Hide(); StartApps.Launch(a, admin: true); }));
                m.Items.Add(Item("Open file location", () => { host.Hide(); BarWindow.Instance.Launch("explorer.exe", $"/select,\"{a.AppID}\""); }));
            }
            m.Items.Add(new Separator());
            m.Items.Add(Item(pinned ? "Unpin from Start" : "Pin to Start", () =>
            {
                S.StartPins ??= new List<string>();
                if (pinned) S.StartPins.Remove(a.AppID); else S.StartPins.Add(a.AppID);
                S.Save();
                Fill();
            }));
            if (pinned)
            {
                int i = S.StartPins.IndexOf(a.AppID);
                if (i > 0) m.Items.Add(Item("Move to front", () => { S.StartPins.Remove(a.AppID); S.StartPins.Insert(0, a.AppID); S.Save(); Fill(); }));
            }
            host.KeepOpen = true;
            m.Closed += (o, ev) => host.KeepOpen = false;
            m.PlacementTarget = s as UIElement;
            m.IsOpen = true;
        }

        static MenuItem Item(string text, Action a)
        {
            var m = new MenuItem { Header = text };
            m.Click += (s, e) => a();
            return m;
        }

        // ---------- bottom bar ----------
        void Place_Click(object s, RoutedEventArgs e)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var bar = BarWindow.Instance;
            string explorer = File.Exists(BarWindow.GlassExplorerPath) ? BarWindow.GlassExplorerPath : "explorer.exe";
            host.Hide();
            switch ((string)((FrameworkElement)s).Tag)
            {
                case "docs": bar.Launch(explorer, $"\"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\""); break;
                case "downloads": bar.Launch(explorer, $"\"{Path.Combine(home, "Downloads")}\""); break;
                case "pictures": bar.Launch(explorer, $"\"{Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)}\""); break;
                case "explorer": bar.Launch(explorer); break;
                case "settings": bar.Launch("ms-settings:"); break;
            }
        }

        void User_Click(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            m.Items.Add(Item("Account settings", () => { host.Hide(); BarWindow.Instance.Launch("ms-settings:yourinfo"); }));
            m.Items.Add(Item("Lock", () => { host.Hide(); LockWorkStation(); }));
            m.Items.Add(Item("Sign out", () => { host.Hide(); Shutdown("/l"); }));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Open the Windows Start menu", () => { host.Hide(); Native.PressKeys(0x5B); }));
            Popup(m, (UIElement)s);
        }

        void Power_Click(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            m.Items.Add(Item("Lock", () => { host.Hide(); LockWorkStation(); }));
            m.Items.Add(Item("Sleep", () => { host.Hide(); SetSuspendState(false, false, false); }));
            m.Items.Add(Item("Restart", () => { host.Hide(); Shutdown("/r /t 0"); }));
            m.Items.Add(Item("Shut down", () => { host.Hide(); Shutdown("/s /t 0"); }));
            Popup(m, (UIElement)s);
        }

        void Popup(ContextMenu m, UIElement target)
        {
            host.KeepOpen = true;
            m.Closed += (o, ev) => host.KeepOpen = false;
            m.PlacementTarget = target;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            m.IsOpen = true;
        }

        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
        static void Shutdown(string args)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false }); } catch { }
        }
    }
}

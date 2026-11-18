using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace GlassBar
{
    public class RecentApp
    {
        public string Key { get; set; }      // exe path, or "app:" + AppID for Store apps
        public string Name { get; set; }
        public string Exe { get; set; }
        public string AppID { get; set; }
        public DateTime When { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public ImageSource Icon { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string Tip => $"{Name} · {BarWindow.AgoLong(When)}";
    }

    /// "Recently opened": the apps you started or switched to, newest first.
    public partial class BarWindow
    {
        List<RecentApp> recentApps = new();
        string lastRecentKey;
        static string RecentAppsFile => Path.Combine(BarSettings.DataDir, "recent-apps.json");

        void InitRecentApps()
        {
            try { recentApps = JsonSerializer.Deserialize<List<RecentApp>>(File.ReadAllText(RecentAppsFile)) ?? new(); } catch { }
            foreach (var a in recentApps) a.Icon = AppIcon(a);
            lastRecentKey = recentApps.FirstOrDefault()?.Key;
            UpdateRecentMini();
        }

        /// Called when the foreground window changes.
        void NoteRecentApp(IntPtr h)
        {
            try
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                var exe = Native.ProcessPath(pid);
                if (exe == null) return;
                var file = Path.GetFileName(exe);
                if (file.Equals("GlassBar.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
                    || file.Equals("SearchApp.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
                    || file.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase)) return;
                if (!Native.TaskbarWindows(Hwnd).Contains(h)) return;   // only real app windows, not pop-ups or the desktop

                RecentApp app;
                if (file.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Store app: the window title starts with the app's name
                    var title = Native.WindowTitle(h) ?? "";
                    var sa = StartApps.All.Where(x => title.Equals(x.Name, StringComparison.OrdinalIgnoreCase) || title.EndsWith(" - " + x.Name, StringComparison.OrdinalIgnoreCase)
                                                      || title.StartsWith(x.Name + " - ", StringComparison.OrdinalIgnoreCase))
                                          .OrderByDescending(x => x.Name.Length).FirstOrDefault();
                    if (sa == null) return;
                    app = new RecentApp { Key = "app:" + sa.AppID, Name = sa.Name, AppID = sa.AppID };
                }
                else
                {
                    var key = exe.ToLowerInvariant();
                    var sa = StartApps.All.FirstOrDefault(x => string.Equals(x.AppID, exe, StringComparison.OrdinalIgnoreCase));
                    string name = sa?.Name;
                    if (string.IsNullOrWhiteSpace(name)) try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim(); } catch { }
                    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exe);
                    app = new RecentApp { Key = key, Name = name, Exe = exe };
                }
                if (app.Key == lastRecentKey) { recentApps[0].When = DateTime.Now; return; }
                lastRecentKey = app.Key;
                app.When = DateTime.Now;
                var old = recentApps.FirstOrDefault(x => x.Key == app.Key);
                if (old != null) recentApps.Remove(old);
                app.Icon = old?.Icon ?? AppIcon(app);
                recentApps.Insert(0, app);
                if (recentApps.Count > 30) recentApps.RemoveRange(30, recentApps.Count - 30);
                SaveRecentApps();
                UpdateRecentMini();
                if (recentFly?.IsVisible == true && !recentShowsFiles) FillRecentApps();
            }
            catch { }
        }

        static ImageSource AppIcon(RecentApp a)
        {
            try
            {
                if (a.Exe != null) return Native.ShellImage(a.Exe, 48);
                if (a.AppID != null) return Native.ShellImage(@"shell:AppsFolder\" + a.AppID, 48);
            }
            catch { }
            return null;
        }

        void SaveRecentApps()
        {
            try { File.WriteAllText(RecentAppsFile, JsonSerializer.Serialize(recentApps)); } catch { }
        }

        void UpdateRecentMini() => RecentMini.ItemsSource = recentApps.Take(3).ToList();

        void OpenRecentApp(RecentApp a)
        {
            // already running: bring its window forward
            var w = allWindows.Values.FirstOrDefault(x => a.Exe != null && x.Exe == a.Exe.ToLowerInvariant());
            if (w != null) { Native.Activate(w.Hwnd); return; }
            if (a.AppID != null) StartApps.Launch(new StartApp { AppID = a.AppID, Name = a.Name });
            else if (a.Exe != null) Launch(a.Exe);
        }

        void FillRecentApps()
        {
            recentFly.Title2 = "Recently opened";
            recentClearBtn.Visibility = recentApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            int today = recentApps.Count(a => a.When.Date == DateTime.Today);
            recentFly.SetSubtitle(today == 1 ? "1 app today" : $"{today} apps today");
            recentFly.EmptyText = "Apps you open show up here";
            recentFly.SetRows(recentApps.Select(a => new ListRow
            {
                Icon = a.Icon, Title = a.Name, Subtitle = AgoLong(a.When),
                Open = () => OpenRecentApp(a),
                Actions = new() { ("", "Remove from the list", () => { recentApps.Remove(a); SaveRecentApps(); UpdateRecentMini(); FillRecentApps(); }) }
            }).ToList());
        }

        void ClearRecentApps()
        {
            if (recentShowsFiles) return;
            recentApps.Clear();
            lastRecentKey = null;
            SaveRecentApps();
            UpdateRecentMini();
            FillRecentApps();
        }

        public static string AgoLong(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "Just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
            if (d.TotalDays < 2) return "Yesterday";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays} days ago";
            return t.ToString("d MMM");
        }
    }
}

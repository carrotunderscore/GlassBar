using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    public class RecentFile
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public DateTime When { get; set; }
        public ImageSource Icon { get; set; }
    }

    /// Recent files, task manager and OMEN Monitor widgets.
    public partial class BarWindow
    {
        // =============================================================
        // Recent files
        // =============================================================
        List<RecentFile> recent = new();
        ListFlyout recentFly;

        void InitRecent()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            t.Tick += async (s, e) => await LoadRecent();
            t.Start();
            _ = LoadRecent();
        }

        async Task LoadRecent()
        {
            var list = await Task.Run(ReadRecent);
            recent = list;
            RecentMini.ItemsSource = list.Take(3).ToList();
            if (recentFly?.IsVisible == true) FillRecentFlyout();
        }

        static List<RecentFile> ReadRecent()
        {
            var map = new Dictionary<string, RecentFile>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, DateTime when)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (map.TryGetValue(path, out var r) && r.When >= when) return;
                map[path] = new RecentFile { Path = path, Name = System.IO.Path.GetFileName(path), When = when };
            }

            // Windows' own recent items (shortcuts in %APPDATA%\Microsoft\Windows\Recent)
            try
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                var links = new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(60).ToList();
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                foreach (var l in links)
                {
                    try { Add((string)shell.CreateShortcut(l.FullName).TargetPath, l.LastWriteTime); } catch { }
                }
            }
            catch { }

            // files opened from ModernGlass Explorer
            try
            {
                var settings = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassExplorer", "settings.json");
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("Recent", out var arr))
                    foreach (var r in arr.EnumerateArray())
                        if (r.TryGetProperty("IsFolder", out var f) && !f.GetBoolean())
                            Add(r.GetProperty("Path").GetString(), r.GetProperty("Time").GetDateTime());
            }
            catch { }

            var list = map.Values.OrderByDescending(r => r.When).Take(40).ToList();
            foreach (var r in list) r.Icon = Native.ShellImage(r.Path, 32);
            return list;
        }

        void RecentItem_Click(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is RecentFile f) Launch(f.Path);
        }

        void Recent_Click(object s, RoutedEventArgs e)
        {
            recentFly ??= Register(new ListFlyout("Recent files", 460, 560));
            FillRecentFlyout();
            recentFly.Toggle(RecentCard);
        }

        void FillRecentFlyout()
        {
            recentFly.SetRows(recent.Select(r => new ListRow
            {
                Icon = r.Icon, Title = r.Name, Subtitle = System.IO.Path.GetDirectoryName(r.Path), Right = Ago(r.When),
                Open = () => Launch(r.Path),
                Actions = new()
                {
                    ("\uE8C8", "Copy file (paste it anywhere)", () => { CopyFiles(r.Path); ShowToast("Copied " + r.Name); }),
                    ("\uE838", "Show in folder", () => Launch("explorer.exe", $"/select,\"{r.Path}\"")),
                },
                DragPath = r.Path
            }));
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours} h";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays} d";
            return t.ToString("d MMM");
        }

        public static void CopyFiles(params string[] paths)
        {
            var sc = new StringCollection();
            sc.AddRange(paths);
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetFileDropList(sc); return; }
                catch { System.Threading.Thread.Sleep(60); }
            }
        }

        // =============================================================
        // Task manager (CPU / RAM + processes)
        // =============================================================
        readonly Queue<double> cpuHist = new(), ramHist = new();
        ulong lastIdle, lastTotal;
        ListFlyout procFly;
        Dictionary<int, TimeSpan> lastProcCpu = new();
        DateTime lastProcSample;

        void InitPerf()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) => SamplePerf();
            t.Start();
            SamplePerf();
        }

        void SamplePerf()
        {
            Native.GetSystemTimes(out var idle, out var kernel, out var user);
            ulong total = kernel.Value + user.Value;
            double cpu = 0;
            if (lastTotal != 0 && total > lastTotal) cpu = 100.0 * (1 - (double)(idle.Value - lastIdle) / (total - lastTotal));
            lastIdle = idle.Value; lastTotal = total;
            var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            Native.GlobalMemoryStatusEx(ref mem);

            Push(cpuHist, Math.Clamp(cpu, 0, 100));
            Push(ramHist, mem.dwMemoryLoad);
            CpuText.Text = $"{cpu:0}%";
            RamText.Text = $"{mem.dwMemoryLoad}%";
            RamText.ToolTip = $"{(mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0:0.0} of {mem.ullTotalPhys / 1073741824.0:0.0} GB in use";
            Draw(CpuGraph, cpuHist);
            Draw(RamGraph, ramHist);
            if (procFly?.IsVisible == true && (DateTime.Now - lastProcSample).TotalSeconds >= 2) _ = FillProcesses();
        }

        static void Push(Queue<double> q, double v) { q.Enqueue(v); while (q.Count > 40) q.Dequeue(); }

        static void Draw(System.Windows.Shapes.Polyline line, Queue<double> q)
        {
            double w = 100, h = 22;
            var pts = new PointCollection();
            int i = 0, n = q.Count;
            foreach (var v in q) pts.Add(new Point(w - (n - 1 - i++) * (w / 39.0), h - v / 100 * h + 1));
            line.Points = pts;
        }

        void Tasks_Click(object s, RoutedEventArgs e)
        {
            if (procFly == null)
            {
                procFly = Register(new ListFlyout("Processes", 500, 600));
                procFly.AddFooter(Flyout.Pill("Open Task Manager", () => Launch("taskmgr.exe"), "\uE9D9"));
                procFly.AddHeaderButton("\uE72C", "Refresh", () => _ = FillProcesses());
            }
            _ = FillProcesses();
            procFly.Toggle(TasksCard);
        }

        async Task FillProcesses()
        {
            lastProcSample = DateTime.Now;
            var prev = lastProcCpu;
            var stamp = DateTime.Now;
            var data = await Task.Run(() =>
            {
                var now = new Dictionary<int, TimeSpan>();
                var groups = new Dictionary<string, (double cpu, long mem, int count, List<int> pids, string exe)>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == 0 || p.Id == 4) continue;
                        long mem = 0; try { mem = p.PrivateMemorySize64; } catch { }
                        double cpu = 0;
                        try
                        {
                            var t = p.TotalProcessorTime;
                            now[p.Id] = t;
                            if (prev.TryGetValue(p.Id, out var old)) cpu = (t - old).TotalMilliseconds;
                        }
                        catch { }
                        var name = p.ProcessName;
                        groups.TryGetValue(name, out var g);
                        g.pids ??= new List<int>();
                        g.pids.Add(p.Id);
                        if (g.exe == null) try { g.exe = Native.ProcessPath((uint)p.Id); } catch { }
                        groups[name] = (g.cpu + cpu, g.mem + mem, g.count + 1, g.pids, g.exe);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                return (now, groups);
            });
            double elapsedMs = Math.Max(1, (stamp - lastProcStamp).TotalMilliseconds) * Environment.ProcessorCount;
            bool havePrev = prev.Count > 0;
            lastProcCpu = data.now;
            lastProcStamp = stamp;

            var rows = data.groups
                .Select(kv => (name: kv.Key, cpu: havePrev ? kv.Value.cpu / elapsedMs * 100 : 0, kv.Value.mem, kv.Value.count, kv.Value.pids, kv.Value.exe))
                .OrderByDescending(r => r.cpu).ThenByDescending(r => r.mem)
                .Take(40)
                .Select(r => new ListRow
                {
                    Icon = r.exe != null ? (exeIcons.TryGetValue(r.exe, out var ic) ? ic : exeIcons[r.exe] = Native.ShellImage(r.exe, 32)) : null,
                    Title = r.name + (r.count > 1 ? $"  ({r.count})" : ""),
                    Subtitle = $"{r.mem / 1048576.0:0} MB",
                    Right = havePrev ? $"{r.cpu:0.0}%" : "",
                    Actions = new() { ("\uE711", "End task", () => EndTask(r.name, r.pids)) }
                }).ToList();
            procFly?.SetRows(rows);
            procFly?.SetSubtitle($"CPU {CpuText.Text}  ·  RAM {RamText.Text}");
        }

        DateTime lastProcStamp = DateTime.Now;

        void EndTask(string name, List<int> pids)
        {
            int ok = 0;
            foreach (var id in pids)
            {
                try { using var p = Process.GetProcessById(id); p.Kill(); ok++; } catch { }
            }
            ShowToast(ok > 0 ? $"Ended {name}" : $"Couldn't end {name} (needs admin?)");
            _ = FillProcesses();
        }

        // =============================================================
        // OMEN Monitor (reads the live file OMEN Monitor writes every update)
        // =============================================================
        static string OmenDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmenMonitor");
        string OmenExe => Settings.OmenMonitorPath ?? "";

        void InitOmen()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s, e) => UpdateOmen();
            t.Start();
            UpdateOmen();
        }

        bool omenLive;

        void UpdateOmen()
        {
            try
            {
                var file = Path.Combine(OmenDir, "live.json");
                var fi = new FileInfo(file);
                if (!fi.Exists || (DateTime.Now - fi.LastWriteTime).TotalSeconds > 15) { OmenOffline(); return; }
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                double? D(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                double cpuLimit = D("CpuThreshold") ?? 80, gpuLimit = D("GpuThreshold") ?? 85;
                SetTemp(OmenCpuTemp, D("CpuTemp"), cpuLimit);
                SetTemp(OmenGpuTemp, D("GpuTemp"), gpuLimit);
                OmenCpuLoad.Text = D("CpuLoad") is double cl ? $"{cl:0}% load" : "–";
                OmenGpuLoad.Text = D("GpuLoad") is double gl ? $"{gl:0}% load" : "–";
                omenLive = true;
                OmenCard.ToolTip = "Open OMEN Monitor";
            }
            catch { }
        }

        void OmenOffline()
        {
            omenLive = false;
            OmenCpuTemp.Text = OmenGpuTemp.Text = "–";
            OmenCpuTemp.Foreground = OmenGpuTemp.Foreground = Flyout.Res("TextTertiary");
            // an older OMEN Monitor (before the bar widget existed) runs but never writes live.json
            omenOld = Process.GetProcessesByName("OmenMonitor").Length > 0;
            OmenCpuLoad.Text = omenOld ? "restart" : "OMEN Monitor";
            OmenGpuLoad.Text = omenOld ? "OMEN Monitor" : "not running";
            OmenCard.ToolTip = omenOld ? "OMEN Monitor is running an old version: quit it from its tray icon and start it again" : "Start OMEN Monitor";
        }
        bool omenOld;

        /// OMEN Monitor needs admin; its scheduled task starts it without a UAC prompt.
        static bool StartOmenTask()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/Run /TN OmenMonitor") { CreateNoWindow = true, UseShellExecute = false });
                return p.WaitForExit(4000) && p.ExitCode == 0;
            }
            catch { return false; }
        }

        static void SetTemp(TextBlock tb, double? t, double limit)
        {
            if (t is not > 0) { tb.Text = "–"; tb.Foreground = Flyout.Res("TextTertiary"); return; }
            tb.Text = $"{t:0}";
            var c = t >= limit ? Color.FromRgb(0xFF, 0x4D, 0x5E) : t >= limit - 8 ? Color.FromRgb(0xF5, 0xC4, 0x51) : Color.FromRgb(0x5B, 0xD6, 0x8A);
            tb.Foreground = new SolidColorBrush(c);
        }

        void Omen_Click(object s, RoutedEventArgs e)
        {
            if (omenLive)
            {
                // OMEN Monitor runs as admin; ask it to show its window through a small request file
                try { Directory.CreateDirectory(OmenDir); File.WriteAllText(Path.Combine(OmenDir, "show.request"), DateTime.Now.ToString("o")); }
                catch { }
            }
            else if (omenOld)
                ShowToast("OMEN Monitor is an old version without the bar link. Quit it from its tray icon (Avsluta) and start it again.", 7);
            else if (StartOmenTask()) { }
            else if (File.Exists(OmenExe)) Launch(OmenExe);
            else
            {
                // first time: let the user point to OmenMonitor.exe
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Where is OmenMonitor.exe?", Filter = "OMEN Monitor|OmenMonitor.exe|Programs|*.exe" };
                if (dlg.ShowDialog() == true)
                {
                    Settings.OmenMonitorPath = dlg.FileName;
                    Settings.Save();
                    Launch(dlg.FileName);
                }
            }
        }
    }

    // =================================================================
    // Generic list flyout (recent files, processes, recordings)
    // =================================================================
    public class ListRow
    {
        public ImageSource Icon;
        public string Glyph;
        public string Title, Subtitle, Right;
        public Action Open;
        public string DragPath;
        public bool StayOpen;
        public List<(string glyph, string tip, Action act)> Actions = new();
    }

    public class ListFlyout : Flyout
    {
        readonly StackPanel rows = new();
        readonly ScrollViewer scroll;
        readonly WrapPanel footer = new() { Margin = new Thickness(16, 6, 16, 14) };
        readonly TextBlock sub = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock empty = new() { Margin = new Thickness(20), FontSize = 13, Visibility = Visibility.Collapsed };

        public ListFlyout(string title, double w, double h) : base(title, w, h)
        {
            sub.Foreground = Res("TextSecondary");
            empty.Foreground = Res("TextSecondary");
            HeaderExtra.Children.Add(sub);
            DockPanel.SetDock(footer, Dock.Bottom);
            footer.Visibility = Visibility.Collapsed;
            Body.Children.Add(footer);
            scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8, 0, 4, 6), Padding = new Thickness(0, 0, 6, 0) };
            var stack = new StackPanel();
            stack.Children.Add(empty);
            stack.Children.Add(rows);
            scroll.Content = stack;
            Body.Children.Add(scroll);
        }

        public string EmptyText { set => empty.Text = value; }
        public void SetSubtitle(string s) => sub.Text = s;
        public void AddFooter(UIElement e) { footer.Children.Add(e); footer.Visibility = Visibility.Visible; }

        public void SetRows(IEnumerable<ListRow> list)
        {
            rows.Children.Clear();
            foreach (var r in list) rows.Children.Add(Build(r));
            empty.Visibility = rows.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        UIElement Build(ListRow r)
        {
            var g = new Grid { Height = 46 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement icon = r.Icon != null
                ? new Image { Source = r.Icon, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center }
                : Glyph(r.Glyph ?? "\uE7C3", 16);
            if (icon is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Center;
            g.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            text.Children.Add(new TextBlock { Text = r.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(r.Subtitle))
                text.Children.Add(new TextBlock { Text = r.Subtitle, FontSize = 11, Foreground = Res("TextTertiary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            var right = new TextBlock { Text = r.Right ?? "", FontSize = 12, Foreground = Res("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(right, 2);
            g.Children.Add(right);

            var acts = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.5 };
            foreach (var a in r.Actions) acts.Children.Add(SmallIcon(a.glyph, a.tip, a.act));
            Grid.SetColumn(acts, 3);
            g.Children.Add(acts);

            var bd = new Border { CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Padding = new Thickness(4, 0, 4, 0), Child = g, Cursor = r.Open != null ? Cursors.Hand : null, ToolTip = r.Subtitle };
            bd.MouseEnter += (s, e) => { bd.Background = Res("CardHover"); acts.Opacity = 1; };
            bd.MouseLeave += (s, e) => { bd.Background = Brushes.Transparent; acts.Opacity = 0.5; };
            Point down = default;
            bool pressed = false;
            bd.MouseLeftButtonDown += (s, e) => { if (BarWindow.FindAncestor<Button>(e.OriginalSource) == null) { pressed = true; down = e.GetPosition(bd); } };
            bd.MouseMove += (s, e) =>
            {
                // drag a file straight out of the list (e.g. into a chat in the browser)
                if (!pressed || r.DragPath == null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(bd);
                if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < 6) return;
                pressed = false;
                KeepOpen = true;
                try { DragDrop.DoDragDrop(bd, new DataObject(DataFormats.FileDrop, new[] { r.DragPath }), DragDropEffects.Copy); } catch { }
                KeepOpen = false;
            };
            bd.MouseLeftButtonUp += (s, e) =>
            {
                if (!pressed) return;
                pressed = false;
                if (r.Open != null) { r.Open(); if (!r.StayOpen) Hide(); }
            };
            return bd;
        }
    }
}

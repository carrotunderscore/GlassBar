using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassBar
{
    /// LocalSend in the bar: drop files on the card (or pick them) and send to the phone; files from the
    /// phone land in Downloads and are put on the clipboard so they can be pasted right away.
    public partial class BarWindow
    {
        LocalSendService ls;
        LocalSendFlyout lsFly;
        readonly List<(string path, string text, string from, DateTime time)> lsReceived = new();
        List<string> lsPending;
        CancellationTokenSource lsSending;
        int lsUnseen;

        static string DownloadsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        async void InitLocalSend()
        {
            ls = new LocalSendService(Environment.MachineName, DownloadsDir);
            ls.DevicesChanged += () => Dispatcher.BeginInvoke(() => lsFly?.Refresh());
            ls.AskAccept = inc => Dispatcher.Invoke(() => AskIncoming(inc));
            ls.Progress += inc => Dispatcher.BeginInvoke(() =>
            {
                long done = 0;
                lock (inc.Saved) done = inc.Saved.Count;
                LsLabel.Text = $"{done}/{inc.Files.Count} in";
            });
            ls.Received += inc => Dispatcher.BeginInvoke(() => OnReceived(inc));
            ls.TextReceived += (from, text) => Dispatcher.BeginInvoke(() => OnText(from, text));
            try { await Task.Run(ls.StartAsync); }
            catch (Exception ex) { lsError = ex.Message; }
            if (ls.Error != null) lsError = ls.Error;
            lsFly?.Refresh();
        }
        string lsError;

        void Ls_Click(object s, RoutedEventArgs e) => OpenLocalSend();

        void OpenLocalSend()
        {
            lsFly ??= Register(new LocalSendFlyout());
            lsUnseen = 0;
            LsBadge.Visibility = Visibility.Collapsed;
            if (!lsFly.IsVisible) { LsCard.BringIntoView(); lsFly.ShowAbove(LsBtn); }
            ls?.Announce();
            lsFly.Refresh();
        }

        void Ls_DragOver(object s, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Ls_Drop(object s, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            lsPending = ExpandFiles(files);
            OpenLocalSend();
            Activate();
        }

        static List<string> ExpandFiles(IEnumerable<string> paths)
        {
            var list = new List<string>();
            foreach (var p in paths)
            {
                if (File.Exists(p)) list.Add(p);
                else if (Directory.Exists(p))
                    try { list.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Take(500)); } catch { }
            }
            return list;
        }

        // ---------- sending ----------
        internal async void LsSendTo(LocalSendService.Device d, List<string> files = null, string text = null)
        {
            if (ls == null || lsSending != null) return;
            files ??= lsPending;
            if (files == null && text == null)
            {
                lsFly.KeepOpen = true;
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send to " + d.Alias };
                bool ok = dlg.ShowDialog() == true;
                lsFly.KeepOpen = false;
                lsFly.Activate();
                if (!ok) return;
                files = dlg.FileNames.ToList();
            }
            lsPending = null;
            lsSending = new CancellationTokenSource();
            lsFly.Status = $"Waiting for {d.Alias} to accept…";
            LsLabel.Text = "Waiting";
            LocalSendService.SendResult r;
            try
            {
                r = text != null
                    ? await ls.SendText(d, text, lsSending.Token)
                    : await ls.SendFiles(d, files, (done, total) => Dispatcher.BeginInvoke(() =>
                    {
                        int pct = total > 0 ? (int)(done * 100 / total) : 0;
                        lsFly.Status = $"Sending to {d.Alias}… {pct}%";
                        LsLabel.Text = pct + "%";
                    }), lsSending.Token);
            }
            catch (OperationCanceledException) { r = LocalSendService.SendResult.Failed; lsFly.Status = "Cancelled"; }
            catch (Exception ex) { r = LocalSendService.SendResult.Failed; lsFly.Status = ex.Message; }
            lsSending = null;
            LsLabel.Text = "LocalSend";
            string msg = r switch
            {
                LocalSendService.SendResult.Done => text != null ? $"Text sent to {d.Alias}" : $"Sent {files.Count} file{(files.Count == 1 ? "" : "s")} to {d.Alias}",
                LocalSendService.SendResult.Rejected => $"{d.Alias} declined (or didn't answer)",
                LocalSendService.SendResult.Busy => $"{d.Alias} is busy with another transfer",
                LocalSendService.SendResult.NeedsPin => $"{d.Alias} requires a PIN – turn off \"Require PIN\" in LocalSend on the phone",
                _ => $"Couldn't send to {d.Alias}"
            };
            ShowToast(msg);
            lsFly.Status = msg;
            lsFly.Refresh();
        }

        internal void LsCancel() => lsSending?.Cancel();

        /// Sends whatever is on the clipboard: copied files, a screenshot (e.g. from the snipping tool) or text.
        internal void LsSendClipboard(LocalSendService.Device d)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    LsSendTo(d, ExpandFiles(Clipboard.GetFileDropList().Cast<string>()));
                    return;
                }
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource img)
                {
                    var file = Path.Combine(Path.GetTempPath(), $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                    using (var fs = File.Create(file))
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(img));
                        enc.Save(fs);
                    }
                    LsSendTo(d, new List<string> { file });
                    return;
                }
                if (Clipboard.ContainsText())
                {
                    LsSendTo(d, text: Clipboard.GetText());
                    return;
                }
            }
            catch { }
            ShowToast("The clipboard is empty");
        }

        // ---------- receiving ----------
        Task<bool> AskIncoming(LocalSendService.Incoming inc)
        {
            if (Settings.LocalSendAutoAccept) return Task.FromResult(true);
            var tcs = new TaskCompletionSource<bool>();
            var f = new Flyout("LocalSend", 420, 250) { Topmost = true };
            var body = new StackPanel { Margin = new Thickness(18, 0, 18, 14) };
            body.Children.Add(new TextBlock
            {
                Text = $"{inc.SenderAlias} wants to send {inc.Files.Count} file{(inc.Files.Count == 1 ? "" : "s")} ({Size(inc.TotalBytes)})",
                FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            var names = string.Join("\n", inc.Files.Take(4).Select(x => x.name)) + (inc.Files.Count > 4 ? $"\n+{inc.Files.Count - 4} more" : "");
            body.Children.Add(new TextBlock { Text = names, FontSize = 12, Foreground = Flyout.Res("TextSecondary"), Margin = new Thickness(0, 8, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            void Done(bool ok) { tcs.TrySetResult(ok); f.Hide(); f.Close(); }
            buttons.Children.Add(Flyout.Pill("Accept", () => Done(true), ""));
            buttons.Children.Add(Flyout.Pill("Decline", () => Done(false)));
            buttons.Children.Add(Flyout.Pill("Always accept", () => { Settings.LocalSendAutoAccept = true; Settings.Save(); Done(true); }));
            body.Children.Add(buttons);
            f.Body.Children.Add(body);
            f.IsVisibleChanged += (s, e) => { if (!f.IsVisible) tcs.TrySetResult(false); };
            LsCard.BringIntoView();
            f.ShowAbove(LsBtn);
            f.KeepOpen = true;   // don't vanish when focus moves; decide with the buttons
            _ = Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (!tcs.Task.IsCompleted) Done(false); }));
            return tcs.Task;
        }

        void OnReceived(LocalSendService.Incoming inc)
        {
            LsLabel.Text = "LocalSend";
            var paths = inc.Saved.Values.ToList();
            foreach (var p in paths) lsReceived.Insert(0, (p, null, inc.SenderAlias, DateTime.Now));
            Trim();
            try { CopyFiles(paths.ToArray()); } catch { }
            ShowToast(paths.Count == 1
                ? $"Got {Path.GetFileName(paths[0])} from {inc.SenderAlias} – copied, paste with Ctrl+V"
                : $"Got {paths.Count} files from {inc.SenderAlias} (in Downloads) – copied, paste with Ctrl+V", 6);
            Badge(paths.Count);
        }

        void OnText(string from, string text)
        {
            lsReceived.Insert(0, (null, text, from, DateTime.Now));
            Trim();
            try { Clipboard.SetText(text); } catch { }
            var shortText = text.Length > 60 ? text[..60] + "…" : text;
            ShowToast($"Text from {from} copied: {shortText.Replace('\n', ' ')}", 6);
            Badge(1);
        }

        void Trim() { while (lsReceived.Count > 30) lsReceived.RemoveAt(lsReceived.Count - 1); lsFly?.Refresh(); }

        void Badge(int n)
        {
            if (lsFly?.IsVisible == true) return;
            lsUnseen += n;
            LsBadge.Visibility = Visibility.Visible;
            LsCount.Text = lsUnseen > 99 ? "99+" : lsUnseen.ToString();
        }

        static string Size(long b) => b >= 1 << 30 ? $"{b / (double)(1 << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

        internal IEnumerable<ListRow> LsRows()
        {
            if (ls != null)
                foreach (var d in ls.Devices.Values.OrderByDescending(x => x.IsMobile).ThenBy(x => x.Alias))
                {
                    var dev = d;
                    yield return new ListRow
                    {
                        Glyph = d.IsMobile ? "" : "",
                        Title = d.Alias,
                        Subtitle = (lsPending != null ? $"Click to send {lsPending.Count} file{(lsPending.Count == 1 ? "" : "s")} · " : "Click to pick files · ") + $"{d.Model ?? d.Type} · {d.Ip}",
                        Open = () => LsSendTo(dev),
                        StayOpen = true,
                        Actions = { ("", "Send what's on the clipboard (files, screenshot or text)", () => LsSendClipboard(dev)) }
                    };
                }
            foreach (var r in lsReceived)
            {
                var item = r;
                if (r.text != null)
                    yield return new ListRow
                    {
                        Glyph = "", Title = r.text.Replace('\n', ' '), Subtitle = $"Text from {r.from} · {r.time:HH:mm}",
                        Open = () => { try { Clipboard.SetText(item.text); ShowToast("Copied"); } catch { } }
                    };
                else
                    yield return new ListRow
                    {
                        Icon = Native.ShellImage(r.path, 32), Title = Path.GetFileName(r.path), Subtitle = $"From {r.from} · {r.time:HH:mm}",
                        Open = () => Launch(item.path), DragPath = r.path,
                        Actions =
                        {
                            ("", "Copy (paste anywhere)", () => { CopyFiles(item.path); ShowToast("Copied"); }),
                            ("", "Show in folder", () => Launch("explorer.exe", $"/select,\"{item.path}\""))
                        }
                    };
            }
        }

        internal string LsStatusLine()
        {
            if (lsError != null) return lsError;
            if (ls == null) return "Starting…";
            if (lsPending != null) return $"{lsPending.Count} file{(lsPending.Count == 1 ? "" : "s")} ready – pick a device";
            return ls.Devices.IsEmpty ? $"Visible as {ls.Alias} · searching…" : $"Visible as {ls.Alias}";
        }

        internal Task LsScan() => ls?.ScanSubnets() ?? Task.CompletedTask;
        internal void LsAnnounce() => ls?.Announce();
    }

    public class LocalSendFlyout : ListFlyout
    {
        string status;
        readonly Button autoBtn;

        public LocalSendFlyout() : base("LocalSend", 430, 560)
        {
            EmptyText = "Looking for devices… Open LocalSend on your phone, on the same Wi-Fi.";
            AddHeaderButton("", "Search again (also scans the network)", async () =>
            {
                Status = "Searching…";
                BarWindow.Instance.LsAnnounce();
                await BarWindow.Instance.LsScan();
                Status = null;
                Refresh();
            });
            AddFooter(Pill("Choose files…", () =>
            {
                KeepOpen = true;
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Files to send" };
                bool ok = dlg.ShowDialog() == true;
                KeepOpen = false;
                Activate();
                if (ok) BarWindow.Instance.LsSetPending(dlg.FileNames.ToList());
            }, ""));
            AddFooter(Pill("Downloads", () => BarWindow.Instance.Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))));
            autoBtn = Pill("", () =>
            {
                var s = BarWindow.Instance.Settings;
                s.LocalSendAutoAccept = !s.LocalSendAutoAccept;
                s.Save();
                Refresh();
            });
            AddFooter(autoBtn);
        }

        public string Status { set { status = value; Refresh(); } }

        protected override void OnShown() => Refresh();

        public void Refresh()
        {
            SetSubtitle(status ?? BarWindow.Instance.LsStatusLine());
            SetRows(BarWindow.Instance.LsRows());
            autoBtn.Content = BarWindow.Instance.Settings.LocalSendAutoAccept ? "Auto-accept: on" : "Auto-accept: off";
        }
    }

    public partial class BarWindow
    {
        internal void LsSetPending(List<string> files)
        {
            lsPending = files.Count > 0 ? files : null;
            lsFly?.Refresh();
        }
    }
}

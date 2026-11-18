using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace GlassBar
{
    /// Dev only: with --snapshot and GB_FLY=recent,tasks,terminal,rec the named flyouts are opened and saved as PNGs too.
    public partial class BarWindow
    {
        async Task SnapshotFlyouts()
        {
            if (Environment.GetEnvironmentVariable("GB_MINI") is string mini)
            {
                var parts = mini.Split('|');
                bool ok = parts[0].StartsWith("http")
                    ? await MiniPlayer.OpenPage(parts[0], "test", 600)
                    : await MiniPlayer.Open(parts[0], parts.Length > 1 ? parts[1] : "", 30);
                if (ok && parts[0].StartsWith("http"))
                {
                    await Task.Delay(6000);
                    if (Environment.GetEnvironmentVariable("GB_MINI_CLICK") is string label) await MiniPlayer.TestClick(label);
                    await Task.Delay(12000);
                    if (Environment.GetEnvironmentVariable("GB_MINI_EVAL") is string js)
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshotPath)), "mini_eval.txt"), await MiniPlayer.TestEval(js) ?? "null");
                }
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshotPath)), "mini_result.txt"), ok ? "found" : "not found");
                if (ok) await MiniPlayer.TestCapture(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshotPath)), "mini.png"));
            }
            var which = Environment.GetEnvironmentVariable("GB_FLY");
            if (string.IsNullOrEmpty(which)) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(snapshotPath));
            foreach (var name in which.Split(','))
            {
                Flyout f = null;
                switch (name.Trim())
                {
                    case "recent": Recent_Click(null, null); f = recentFly; break;
                    case "tasks": Tasks_Click(null, null); await Task.Delay(2500); _ = FillProcesses(); f = procFly; break;
                    case "rec": RecMenu_Click(null, null); f = recFly; break;
                    case "clock": Clock_Click(null, null); f = clockFly; break;
                    case "notes": Notes_Click(null, null); f = notesFly; break;
                    case "calendar": Calendar_Click(null, null); f = calFly; break;
                    case "start": await StartApps.Refresh(force: true); ToggleStart(); f = startFly; break;
                    case "terminal":
                        RunCommand("Get-ChildItem $env:WINDIR -Directory | Select-Object -First 6 Name, LastWriteTime");
                        await Task.Delay(400);
                        RunCommand("cd $env:USERPROFILE\\Documents; 1..3 | % { \"line $_\" }");
                        f = termFly; break;
                }
                if (f == null) continue;
                await Task.Delay(2500);
                f.UpdateLayout();
                SaveSnapshot(f, (FrameworkElement)f.Content, Path.Combine(dir, $"fly_{name.Trim()}.png"));
                f.Hide();
            }
        }
    }
}

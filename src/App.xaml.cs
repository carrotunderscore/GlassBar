using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GlassBar
{
    public partial class App : Application
    {
        static Mutex single;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var en = new System.Globalization.CultureInfo("en-GB");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = en;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = en;
            Thread.CurrentThread.CurrentCulture = en;
            Thread.CurrentThread.CurrentUICulture = en;

            string snapshot = null;
            for (int i = 0; i < e.Args.Length; i++)
                if (e.Args[i] == "--snapshot" && i + 1 < e.Args.Length) snapshot = e.Args[++i];

            if (snapshot == null)
            {
                single = new Mutex(true, "GlassBar-SingleInstance", out bool first);
                if (!first) { Shutdown(); return; }
            }

            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                BarWindow.Instance?.ShowToast("Error: " + ex.Exception.Message, 6);
            };
            TaskScheduler.UnobservedTaskException += (s, ex) => ex.SetObserved();
            AppDomain.CurrentDomain.ProcessExit += (s, ex) => { try { BarWindow.Instance?.Terminal?.Dispose(); } catch { } };
            // never leave the Windows taskbar hidden if the bar goes away
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => { try { Native.SetWindowsTaskbarHidden(false); } catch { } };
            Exit += (s, ex) => { try { Native.SetWindowsTaskbarHidden(false); } catch { } };

            var w = new BarWindow(snapshot);
            MainWindow = w;
            w.Show();
        }
    }
}

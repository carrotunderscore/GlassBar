using System;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace GlassBar
{
    /// The Windows accent colour (Settings > Colours), set the same way the Settings app does it:
    /// uxtheme's SetUserColorPreference updates the palette, the Start/taskbar colour and title bars at once.
    static class WinAccent
    {
        [StructLayout(LayoutKind.Sequential)] public struct Pref { public uint Start, Accent; }   // 0xAABBGGRR
        [DllImport("uxtheme.dll", EntryPoint = "#120")] static extern int GetUserColorPreference(out Pref p, bool forceReload);
        [DllImport("uxtheme.dll", EntryPoint = "#122")] static extern int SetUserColorPreference(ref Pref p, bool forceCommit);

        static uint Abgr(Color c) => 0xFF000000u | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;

        public static Pref? Get()
        {
            try { return GetUserColorPreference(out var p, false) == 0 ? p : null; }
            catch { return null; }
        }

        public static bool Set(Pref p)
        {
            try { return SetUserColorPreference(ref p, true) == 0; }
            catch { return false; }
        }

        /// Accent = the theme colour; Start = a darker shade of it (what Windows itself pairs with an accent).
        public static bool Set(Color accent) =>
            Set(new Pref { Accent = Abgr(accent), Start = Abgr(GlassTheme.Scale(accent, 0.42)) });
    }
}

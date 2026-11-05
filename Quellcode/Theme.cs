// Sync Pilot – Farben, Schriften, Akzentfarbe, Hell- und Dunkelmodus
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SyncPilot
{
    public static class Theme
    {
        public static bool Dark;
        /// <summary>Feste Vorgabe statt der Windows-Einstellung (sonst null). Wird von ModernMenu.cs gelesen.</summary>
        public static bool? Override;
        public static float Scale = 1f;

        public static Color Bg, Surface, Surface2, Border, Text, TextDim, Hover, Pressed, NavSel, SelBg;
        public static Color Input, ControlBg, ControlHover, ControlBorder, Disabled, DisabledText;
        public static Color Accent, AccentHover, AccentText, AccentSoft, AccentSoftText;
        public static Color Ok, OkBg, Warn, WarnBg, Err, ErrBg, Purple, PurpleBg, Neutral, NeutralBg;

        public static Font Body, Small, Bold, H1, H2, Big, Mono, MonoSmall;
        public static string IconFamily;

        public const string GSync = "", GAdd = "", GBack = "", GSearch = "",
            GSettings = "", GHistory = "", GFolder = "", GMore = "", GRefresh = "",
            GCheck = "", GWarn = "", GError = "", GForward = "", GSwitch = "",
            GInfo = "", GCancel = "", GDoc = "", GPlay = "", GTasks = "";

        public static void Init()
        {
            Dark = Override.HasValue ? Override.Value : ReadDark();
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;

            // Grundfarben wie im Monitor Dimmer (Windows-11-Werte)
            if (Dark)
            {
                Bg = Hex(0x202020); Surface = Hex(0x2B2B2B); Surface2 = Hex(0x202020); Border = Hex(0x3D3D3D);
                Text = Hex(0xFAFAFA); TextDim = Hex(0xCECECE); Hover = Hex(0x343434); Pressed = Hex(0x3E3E3E);
                NavSel = Hex(0x2D2D2D); SelBg = Hex(0x373737);
                Input = Hex(0x323232); ControlBg = Hex(0x373737); ControlHover = Hex(0x424242); ControlBorder = Hex(0x555555);
                Disabled = Hex(0x333333); DisabledText = Hex(0x6E6E6E);
                Ok = Hex(0x6CCB5F); OkBg = Hex(0x1E3A1E); Warn = Hex(0xFCE100); WarnBg = Hex(0x433519);
                Err = Hex(0xFF99A4); ErrBg = Hex(0x442726); Purple = Hex(0xC4B5FD); PurpleBg = Hex(0x352C4D);
                Neutral = Hex(0xCECECE); NeutralBg = Hex(0x3A3A3A);
            }
            else
            {
                Bg = Hex(0xF3F3F3); Surface = Hex(0xFFFFFF); Surface2 = Hex(0xF3F3F3); Border = Hex(0xE5E5E5);
                Text = Hex(0x1B1B1B); TextDim = Hex(0x616161); Hover = Hex(0xF6F6F6); Pressed = Hex(0xECECEC);
                NavSel = Hex(0xEAEAEA); SelBg = Hex(0xF0F0F0);
                Input = Hex(0xFFFFFF); ControlBg = Hex(0xFFFFFF); ControlHover = Hex(0xF6F6F6); ControlBorder = Hex(0xD1D1D1);
                Disabled = Hex(0xEDEDED); DisabledText = Hex(0x9D9D9D);
                Ok = Hex(0x0F7B0F); OkBg = Hex(0xDFF6DD); Warn = Hex(0x9D5D00); WarnBg = Hex(0xFFF4CE);
                Err = Hex(0xC42B1C); ErrBg = Hex(0xFDE7E9); Purple = Hex(0x5B3FBF); PurpleBg = Hex(0xF1ECFB);
                Neutral = Hex(0x424242); NeutralBg = Hex(0xEDEDED);
            }

            // Windows-Akzentfarbe (wie Monitor Dimmer): hell Dunkel1/2/3, dunkel Hell2/1/Basis
            Color[] p = AccentColors.Palette;
            if (p != null)
            {
                Accent = Dark ? p[1] : p[4];
                AccentHover = Dark ? p[2] : p[5];
                AccentSoftText = Dark ? p[1] : p[5];
                AccentSoft = AccentColors.Mix(p[3], Bg, Dark ? 0.20 : 0.10);
            }
            else
            {
                Accent = Dark ? Hex(0x4CC2FF) : Hex(0x0067C0);
                AccentHover = Dark ? Hex(0x47B1E8) : Hex(0x1975C5);
                AccentSoftText = Dark ? Hex(0x99D8FF) : Hex(0x005A9E);
                AccentSoft = Dark ? Hex(0x1C3346) : Hex(0xEBF3FB);
            }
            AccentText = AccentColors.Luminance(Accent) > 0.5 ? Hex(0x141414) : Color.White;

            string ui = "Segoe UI";
            string semi = Has("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";
            Body = new Font(ui, 9.5f);
            Small = new Font(ui, 8.75f);
            Bold = new Font(semi, 9.5f);
            H2 = new Font(semi, 11.5f);
            Big = new Font(semi, 16f);
            H1 = new Font(semi, 20f);
            string mono = Has("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            Mono = new Font(mono, 9.25f);
            MonoSmall = new Font(mono, 8.5f);
            IconFamily = Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }

        static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        static bool Has(string family)
        {
            foreach (FontFamily f in FontFamily.Families)
                if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool ReadDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return false;
        }

        public static int P(int px) { return (int)Math.Round(px * Scale); }

        public static Font IconFont(float size) { return new Font(IconFamily, size); }

        /// <summary>
        /// Symbol aus der Windows-Symbolschrift, nach sichtbaren Pixeln zentriert (MenuHelper.Glyph) - wird nie
        /// abgeschnitten oder mit "..." gekürzt. size in Punkt; HorizontalCenter, sonst links bzw. Right rechts.
        /// </summary>
        public static void DrawGlyph(Graphics g, string glyph, float size, Rectangle r, Color c, TextFormatFlags align)
        {
            if (string.IsNullOrEmpty(glyph)) return;
            int px = Math.Max(8, (int)Math.Round(size * 96f / 72f * Scale));
            Image img = MenuHelper.Glyph(glyph[0], c, px);
            if (img == null) return;
            int x = (align & TextFormatFlags.HorizontalCenter) != 0 ? r.X + (r.Width - px) / 2
                  : (align & TextFormatFlags.Right) != 0 ? r.Right - px : r.X;
            g.DrawImage(img, x, r.Y + (r.Height - px) / 2, px, px);
            img.Dispose();
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Rectangle r, int radius, Color c, float width)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        public static void DrawText(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, text, f, r, c,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter | extra);
        }

        public static void Chip(Graphics g, string text, Font f, int x, int centerY, Color fg, Color bg)
        {
            Size s = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            Rectangle r = new Rectangle(x, centerY - P(11), s.Width + P(16), P(22));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillRound(g, r, P(11), bg);
            DrawText(g, text, f, r, fg, TextFormatFlags.HorizontalCenter);
        }

        public static int ChipWidth(Graphics g, string text, Font f)
        {
            return TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + P(16);
        }

        // ---------- Fenster und Bildlaufleisten dunkel ----------

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        public static void ApplyTitleBar(Form f)
        {
            if (!Dark) return;
            try
            {
                int v = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
            }
            catch { }
        }

        public static void DarkScroll(Control c)
        {
            if (!Dark) return;
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        public static void DarkHeader(ListView lv)
        {
            if (!Dark) return;
            try
            {
                IntPtr header = SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
            }
            catch { }
        }
    }

    /// <summary>Abgerundete Rechtecke (wird auch von ModernMenu.cs verwendet).</summary>
    internal static class Shapes
    {
        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>
    /// Windows-Akzentfarbe (Verfahren wie im Monitor Dimmer). Palette wie in Windows 11 (7 Abstufungen):
    /// 0 Hell3, 1 Hell2, 2 Hell1, 3 Basis, 4 Dunkel1, 5 Dunkel2, 6 Dunkel3. null = nicht lesbar.
    /// </summary>
    internal static class AccentColors
    {
        private static Color[] _palette;
        private static bool _loaded;

        public static Color[] Palette
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _palette = Read();
                }
                return _palette;
            }
        }

        private static Color[] Read()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    byte[] b = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (b != null && b.Length >= 28)
                    {
                        Color[] p = new Color[7];
                        for (int i = 0; i < 7; i++) p[i] = Color.FromArgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                        return p;
                    }
                }
            }
            catch { }
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = key == null ? null : key.GetValue("AccentColor");
                    if (v is int)
                    {
                        int c = (int)v;
                        Color baseColor = Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF);
                        return new Color[]
                        {
                            Mix(Color.White, baseColor, 0.75), Mix(Color.White, baseColor, 0.50), Mix(Color.White, baseColor, 0.25),
                            baseColor,
                            Mix(Color.Black, baseColor, 0.20), Mix(Color.Black, baseColor, 0.40), Mix(Color.Black, baseColor, 0.60)
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Mischt a in b: t = 0 ergibt b, t = 1 ergibt a.</summary>
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(b.R + (a.R - b.R) * t),
                (int)Math.Round(b.G + (a.G - b.G) * t),
                (int)Math.Round(b.B + (a.B - b.B) * t));
        }

        /// <summary>Relative Helligkeit 0..1 (sRGB).</summary>
        public static double Luminance(Color c)
        {
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        private static double Lin(int v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
    }
}
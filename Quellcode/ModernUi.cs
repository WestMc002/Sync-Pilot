// Sync Pilot – Footer und Info-Fenster (Vorlage: Monitor Dimmer)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Programm-Icon und GS-Logo (Datei in Resources neben der EXE, sonst aus der EXE eingebettet).</summary>
    internal static class AppImages
    {
        private static Image _logo;
        private static bool _logoLoaded;

        public static Image Logo
        {
            get
            {
                if (_logoLoaded) return _logo;
                _logoLoaded = true;
                _logo = Load("GS-Logo.png");
                return _logo;
            }
        }

        public static Image AppIcon(int size)
        {
            try
            {
                string p = Path.Combine(Path.Combine(Application.StartupPath, "Resources"), "SyncPilot.ico");
                if (File.Exists(p))
                    using (Icon ico = new Icon(p, size, size)) return ico.ToBitmap();
                using (Stream rs = typeof(AppImages).Assembly.GetManifestResourceStream("SyncPilot.ico"))
                    if (rs != null)
                        using (Icon ico = new Icon(rs, size, size)) return ico.ToBitmap();
                using (Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    return new Bitmap(ico.ToBitmap(), size, size);
            }
            catch
            {
                return null;
            }
        }

        private static Image Load(string name)
        {
            try
            {
                string p = Path.Combine(Path.Combine(Application.StartupPath, "Resources"), name);
                if (File.Exists(p))
                    using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Image img = Image.FromStream(fs))
                        return new Bitmap(img);
                using (Stream rs = typeof(AppImages).Assembly.GetManifestResourceStream(name))
                    if (rs != null)
                        using (Image img = Image.FromStream(rs))
                            return new Bitmap(img);
            }
            catch { }
            return null;
        }
    }

    /// <summary>Fußzeile unten im Fenster: links Copyright, rechts GS-Logo mit Name und Website (anklickbar).</summary>
    internal sealed class SignatureFooter : Panel
    {
        public const string Url = "https://www.westmc002.de";
        private const string Name1 = "Günther Schoder";
        private const string Name2 = "www.westmc002.de";
        private readonly float k;
        private Rectangle _link;
        private bool _hover;

        public SignatureFooter()
        {
            k = Theme.Scale;
            Dock = DockStyle.Bottom;
            Height = (int)(64 * k);
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int pad = (int)(28 * k);
            int right = Width - pad;
            using (Font f1 = new Font("Segoe UI Semibold", 9.5f))
            using (Font f2 = new Font("Segoe UI", 9f, _hover ? FontStyle.Underline : FontStyle.Regular))
            using (Font f3 = new Font("Segoe UI", 9f))
            {
                TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                Size s1 = TextRenderer.MeasureText(g, Name1, f1, Size.Empty, flags);
                Size s2 = TextRenderer.MeasureText(g, Name2, f2, Size.Empty, flags);
                int tw = Math.Max(s1.Width, s2.Width);
                int th = s1.Height + (int)(2 * k) + s2.Height;
                int top = (Height - th) / 2;
                int tx = right - tw;
                TextRenderer.DrawText(g, Name1, f1, new Point(tx, top), Theme.Text, flags);
                int y2 = top + s1.Height + (int)(2 * k);
                TextRenderer.DrawText(g, Name2, f2, new Point(tx, y2), _hover ? Theme.AccentSoftText : Theme.TextDim, flags);
                _link = new Rectangle(tx, top, tw, th);

                Image logo = AppImages.Logo;
                if (logo != null)
                {
                    int lh = (int)(26 * k);
                    int lw = logo.Width * lh / Math.Max(1, logo.Height);
                    Rectangle r = new Rectangle(tx - (int)(10 * k) - lw, (Height - lh) / 2, lw, lh);
                    g.DrawImage(logo, r);
                    _link = Rectangle.Union(_link, r);
                }

                string left = "© " + DateTime.Now.Year + " · Sync Pilot";
                Size s3 = TextRenderer.MeasureText(g, left, f3, Size.Empty, flags);
                TextRenderer.DrawText(g, left, f3, new Point(pad, (Height - s3.Height) / 2), Theme.TextDim, flags);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = _link.Contains(e.Location);
            if (h != _hover) { _hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover) { _hover = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _link.Contains(e.Location))
            {
                try
                {
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(Url);
                    psi.UseShellExecute = true;
                    System.Diagnostics.Process.Start(psi);
                }
                catch { }
            }
            base.OnMouseClick(e);
        }
    }

    /// <summary>Weiße Karte mit abgerundeten Ecken und feinem Rahmen (wie Monitor Dimmer).</summary>
    internal sealed class CardPanel : Panel
    {
        private readonly float k;

        public CardPanel()
        {
            k = Theme.Scale;
            BackColor = Theme.Surface;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding((int)(24 * k), (int)(18 * k), (int)(24 * k), (int)(18 * k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath p = Shapes.Rounded(r, 8 * k))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using (Pen pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            }
        }
    }

    /// <summary>Knopf des Info-Fensters (wie Monitor Dimmer): Primary = Akzentfarbe, sonst weiß mit Rahmen.</summary>
    internal sealed class ModernButton : Button
    {
        private readonly float k;
        private bool _hover, _down;
        public bool Primary;
        public char Glyph;
        /// <summary>Symbolgröße in Punkt (0 = Standard).</summary>
        public float GlyphSize;

        public ModernButton(string text, char glyph, bool primary)
        {
            k = Theme.Scale;
            Text = text;
            Glyph = glyph;
            Primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI Semibold", 9.5f);
            Cursor = Cursors.Hand;
            AutoSize = false;
            UpdateSize();
        }

        public void UpdateSize()
        {
            int tw = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
            int icon = Glyph == '\0' ? 0 : (int)((string.IsNullOrEmpty(Text) ? 16 : 26) * k);
            Size = new Size(tw + icon + (int)(36 * k), (int)(40 * k));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, text, border;
            if (Primary && Enabled)
            {
                fill = _down ? Theme.Blend(Theme.Accent, Theme.Bg, 0.2f) : _hover ? Theme.AccentHover : Theme.Accent;
                text = Theme.AccentText;
                border = fill;
            }
            else
            {
                fill = _down ? Theme.Pressed : _hover ? Theme.Hover : Theme.Surface;
                text = Enabled ? Theme.Text : Theme.DisabledText;
                border = Theme.Border;
            }
            using (GraphicsPath p = Shapes.Rounded(r, 4 * k))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }

            int tw = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
            int icon = Glyph == '\0' ? 0 : (int)((string.IsNullOrEmpty(Text) ? 16 : 26) * k);
            int x = (Width - tw - icon) / 2;
            if (Glyph != '\0')
            {
                int gs = GlyphSize > 0 ? (int)Math.Round(GlyphSize * 96f / 72f * k) : (int)(16 * k);
                Image img = MenuHelper.Glyph(Glyph, text, gs);
                if (img != null)
                {
                    g.DrawImage(img, x, (Height - gs) / 2, gs, gs);
                    img.Dispose();
                }
                x += icon;
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, tw + 2, Height), text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                using (GraphicsPath p = Shapes.Rounded(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), 3 * k))
                using (Pen pen = new Pen(text)) g.DrawPath(pen, p);
        }
    }

    /// <summary>„Info über Sync Pilot“ (Vorlage: Monitor Dimmer): großes GS-Logo, Version, Eckdaten, Knöpfe.</summary>
    internal sealed class AboutDialog : Form
    {
        private static AboutDialog _open;
        private readonly float k;

        public static void ShowFor(Form owner) { ShowFor(owner, null); }

        public static void ShowFor(Form owner, UpdateHooks updates)
        {
            if (_open != null) { _open.Activate(); return; }
            using (AboutDialog d = new AboutDialog(owner, updates))
            {
                _open = d;
                try { if (owner != null && owner.Visible) d.ShowDialog(owner); else d.ShowDialog(); }
                finally { _open = null; }
            }
        }

        /// <summary>Für die Screenshots (Demo.cs): Info-Fenster ohne Besitzer.</summary>
        public static AboutDialog CreateForScreenshot()
        {
            // Screenshots: keine Prüfung, aber die Update-Zeilen mit einem geprüften Stand (Globale Richtlinien, Updates)
            UpdateHooks demo = new UpdateHooks();
            demo.Status = delegate { return MainForm.DemoUpdateStatus; };
            demo.Auto = delegate { return true; };
            demo.SetAuto = delegate (bool on) { };
            demo.CheckNow = delegate { return Task.FromResult(MainForm.DemoUpdateStatus); };
            demo.CanInstall = delegate { return false; };
            return new AboutDialog(null, demo);
        }

        private AboutDialog(Form owner, UpdateHooks updates)
        {
            Text = "Info über Sync Pilot";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            // Normaler Rahmen wie bei den Hauptfenstern (Icon mit gleichem Abstand), Größe bleibt fest
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = owner == null;
            StartPosition = owner != null && owner.Visible ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            k = Theme.Scale;
            int W = S(updates != null ? 540 : 480); // mit Updates-Zeilen breiter (Globale Richtlinien, Updates)

            int y = S(24);
            PictureBox logo = new PictureBox();
            logo.Image = AppImages.Logo ?? AppImages.AppIcon(128);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Location = new Point(0, y);
            logo.Size = new Size(W, S(130));
            logo.BackColor = Color.Transparent;
            Controls.Add(logo);
            y += S(146);

            Controls.Add(CenterLabel("Sync Pilot", new Font("Segoe UI Semibold", 15f), Theme.Text, y, S(36), W));
            y += S(38);
            Controls.Add(CenterLabel("Version " + AppInfo.Version, new Font("Segoe UI", 10.5f), Theme.AccentSoftText, y, S(26), W));
            y += S(40);

            // ---------- Karte mit Eckdaten ----------
            CardPanel card = new CardPanel();
            card.Location = new Point(S(24), y);
            card.Size = new Size(W - S(48), S(140));
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.ColumnCount = 2;
            table.BackColor = Theme.Surface;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(118)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ModernToolTip tip = new ModernToolTip();
            string built;
            try { built = File.GetLastWriteTime(Application.ExecutablePath).ToString("dd.MM.yyyy"); }
            catch { built = "-"; }
            AddRow(table, tip, "Entwickler", "Günther Schoder");
            AddRow(table, tip, "Erstellt", built);
            AddRow(table, tip, "Farbschema", (Theme.Dark ? "dunkel" : "hell") + " (folgt Windows)");
            AddRow(table, tip, "Programm", Demo.Active ? @"C:\Tools\Sync Pilot" : Application.StartupPath); // Screenshots: kein privater Pfad
            if (updates != null)
            {
                card.Height += S(60 + 32);
                AddUpdateRows(table, updates);
            }
            card.Controls.Add(table);
            Controls.Add(card);
            y += card.Height + S(24);

            // ---------- Knöpfe ----------
            ModernButton manual = new ModernButton("Anleitung", MenuHelper.Document, false);
            manual.Click += delegate { Open(Path.Combine(Application.StartupPath, "Liesmich.txt"), true); };
            ModernButton logs = new ModernButton("Protokolle", MenuHelper.Folder, false);
            logs.Click += delegate
            {
                try { Directory.CreateDirectory(Store.LogDir); } catch { }
                Open(Store.LogDir, false);
            };
            ModernButton close = new ModernButton("Schließen", '\0', true);
            close.DialogResult = DialogResult.Cancel;
            int gap = S(14);
            int total = manual.Width + logs.Width + close.Width + 2 * gap;
            int bx = (W - total) / 2;
            manual.Location = new Point(bx, y);
            logs.Location = new Point(manual.Right + gap, y);
            close.Location = new Point(logs.Right + gap, y);
            Controls.Add(manual);
            Controls.Add(logs);
            Controls.Add(close);
            CancelButton = close;
            AcceptButton = close;
            y += close.Height + S(22);

            Controls.Add(CenterLabel("© " + DateTime.Now.Year + " Günther Schoder", Font, Theme.TextDim, y, S(24), W));
            y += S(24) + S(20);

            ClientSize = new Size(W, y);
            Shown += delegate { close.Focus(); };
        }

        private static Label CenterLabel(string text, Font f, Color c, int y, int h, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = Theme.Bg;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Location = new Point(0, y);
            l.Size = new Size(w, h);
            l.UseMnemonic = false;
            return l;
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size;
            MaximumSize = Size;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_NCHITTEST: Rand und Ecken wie festen Rahmen behandeln -> Größe bleibt fest
            if (m.Msg == 0x0084)
            {
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1;
            }
        }

        /// <summary>
        /// Updates (Globale Richtlinien, Referenz Wake on LAN ab v1.34): Symbol und Stand, darunter die letzte Prüfung,
        /// rechts ein kleiner Knopf ("Jetzt prüfen", bei neuer Version "Aktualisieren" bzw. "Release-Seite"),
        /// darunter "Automatisch prüfen" mit Schalter.
        /// </summary>
        private void AddUpdateRows(TableLayoutPanel t, UpdateHooks updates)
        {
            int row = t.RowCount;
            t.RowCount = row + 2;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(60)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));
            Label name = new Label();
            name.Text = "Updates";
            name.ForeColor = Theme.TextDim;
            name.BackColor = Theme.Surface;
            name.AutoSize = false;
            name.Size = new Size(S(110), S(22));
            name.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            name.Margin = new Padding(3, S(9), 3, 0);
            name.TextAlign = ContentAlignment.MiddleLeft;
            t.Controls.Add(name, 0, row);

            Panel cell = new Panel();
            cell.Dock = DockStyle.Fill;
            cell.BackColor = Theme.Surface;
            cell.Margin = new Padding(3, 0, 3, 0);
            StatusGlyph glyph = new StatusGlyph();
            glyph.BackColor = Theme.Surface;
            glyph.Size = new Size(S(20), S(22));
            glyph.Location = new Point(0, S(9));
            Label title = new Label();
            title.ForeColor = Theme.Text; title.BackColor = Theme.Surface; title.AutoSize = false; title.AutoEllipsis = true;
            title.UseMnemonic = false; title.TextAlign = ContentAlignment.MiddleLeft;
            Label detail = new Label();
            detail.ForeColor = Theme.TextDim; detail.BackColor = Theme.Surface; detail.AutoSize = false; detail.AutoEllipsis = true;
            detail.UseMnemonic = false; detail.TextAlign = ContentAlignment.TopLeft; detail.Font = new Font("Segoe UI", 8.75f);
            ModernButton check = new ModernButton("Jetzt prüfen", '\uE72C', false);
            ModernButton install = new ModernButton("Aktualisieren", '\uE896', true);
            ModernButton page = new ModernButton("Release-Seite", '\uE71B', false);
            ModernToolTip tip = new ModernToolTip();
            tip.SetToolTip(install, "Sync Pilot beenden, auf die neue Version aktualisieren und neu starten");
            foreach (ModernButton b in new ModernButton[] { check, install, page })
            {
                b.Font = new Font("Segoe UI Semibold", 9f);
                b.GlyphSize = 10f;
                b.UpdateSize();
                b.Height = S(30);
                b.Margin = Padding.Empty;
                cell.Controls.Add(b);
            }
            cell.Controls.Add(glyph);
            cell.Controls.Add(title);
            cell.Controls.Add(detail);

            Action layout = delegate
            {
                // Knopf rechtsbündig mit dem Schalter darunter und auf Höhe der ersten Zeile –
                // so hat die zweite Zeile (letzte Prüfung) die volle Breite
                int right = cell.Width - S(2);
                foreach (ModernButton b in new ModernButton[] { check, install, page }) b.Location = new Point(right - b.Width, S(20) - b.Height / 2);
                ModernButton shown = install.Visible ? install : page.Visible ? page : check;
                int tx = S(28), tw = Math.Max(S(40), shown.Left - S(12) - tx);
                title.SetBounds(tx, S(9), tw, S(22));
                detail.SetBounds(tx, S(33), Math.Max(S(40), cell.Width - tx), S(20));
            };
            cell.Resize += delegate { layout(); };

            Action<UpdateStatus> show = delegate (UpdateStatus st)
            {
                switch (st.Kind)
                {
                    case UpdateKind.Current: glyph.Set('\uE930', Theme.Ok); break;
                    case UpdateKind.Available: glyph.Set('\uE896', Theme.AccentSoftText); break;
                    case UpdateKind.Failed: glyph.Set('\uE7BA', Theme.Err); break;
                    case UpdateKind.Checking: glyph.Set('\uE895', Theme.TextDim); break;
                    default: glyph.Set('\uE946', Theme.TextDim); break;
                }
                title.Text = st.Title;
                title.ForeColor = st.Kind == UpdateKind.Available ? Theme.AccentSoftText : st.Kind == UpdateKind.Checking ? Theme.TextDim : Theme.Text;
                detail.Text = st.Detail;
                tip.SetToolTip(detail, st.Detail);
                bool available = st.Kind == UpdateKind.Available;
                bool canInstall = available && updates.CanInstall != null && updates.CanInstall();
                install.Visible = canInstall;
                page.Visible = available && !canInstall;
                check.Visible = !available;
                check.Enabled = st.Kind != UpdateKind.Checking;
                layout();
            };

            check.Click += async delegate
            {
                show(new UpdateStatus(UpdateKind.Checking, "Prüfe auf GitHub \u2026", ""));
                UpdateStatus st;
                try { st = await updates.CheckNow(); }
                catch (Exception ex) { st = new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", ex.Message); }
                if (!IsDisposed) show(st);
            };
            install.Click += delegate
            {
                Close();
                if (updates.Install != null) updates.Install();
            };
            page.Click += delegate { if (updates.OpenPage != null) updates.OpenPage(); };
            show(updates.Status());
            t.Controls.Add(cell, 1, row);

            // Automatisch prüfen: Text bündig mit den Werten darüber, Schalter rechts
            Panel autoCell = new Panel();
            autoCell.Dock = DockStyle.Fill;
            autoCell.BackColor = Theme.Surface;
            autoCell.Margin = new Padding(3, 0, 3, 0);
            ToggleSwitch auto = new ToggleSwitch();
            auto.Checked = updates.Auto();
            auto.AccessibleName = "Automatisch prüfen";
            Label autoText = new Label();
            autoText.Text = "Automatisch prüfen";
            autoText.ForeColor = Theme.Text;
            autoText.BackColor = Theme.Surface;
            autoText.TextAlign = ContentAlignment.MiddleLeft;
            autoText.AutoEllipsis = true;
            autoText.Cursor = Cursors.Hand;
            autoText.Click += delegate { auto.Checked = !auto.Checked; };
            auto.CheckedChanged += delegate { updates.SetAuto(auto.Checked); };
            autoCell.Controls.Add(autoText);
            autoCell.Controls.Add(auto);
            autoCell.Resize += delegate
            {
                auto.Location = new Point(autoCell.Width - S(2) - auto.Width, (autoCell.Height - auto.Height) / 2);
                autoText.SetBounds(0, 0, Math.Max(10, auto.Left - S(8)), autoCell.Height);
            };
            t.Controls.Add(autoCell, 1, row + 1);
        }

        private Label AddRow(TableLayoutPanel t, ToolTip tip, string name, string value)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
            Label n = new Label();
            n.Text = name;
            n.ForeColor = Theme.TextDim;
            n.Dock = DockStyle.Fill;
            n.TextAlign = ContentAlignment.MiddleLeft;
            n.BackColor = Theme.Surface;
            t.Controls.Add(n, 0, row);
            Label v = new Label();
            v.Text = value;
            v.ForeColor = Theme.Text;
            v.Dock = DockStyle.Fill;
            v.TextAlign = ContentAlignment.MiddleLeft;
            v.AutoEllipsis = true;
            v.BackColor = Theme.Surface;
            v.UseMnemonic = false;
            tip.SetToolTip(v, value);
            t.Controls.Add(v, 1, row);
            return v;
        }

        private static void Open(string path, bool notepadFallback)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(path);
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                if (notepadFallback)
                    try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
            }
        }
    }
}
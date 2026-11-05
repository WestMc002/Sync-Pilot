// Sync Pilot – eigene Steuerelemente im Windows-11-Stil
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SyncPilot
{
    public enum BtnKind { Primary, Secondary, Soft, Subtle, Nav }

    public class FlatButton : Button
    {
        public BtnKind Kind;
        public string Glyph;
        /// <summary>Symbolgröße in Punkt (0 = Standard: Navigation 14, sonst 10).</summary>
        public float GlyphSize;
        bool selected, hover, down;

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public FlatButton(string text, string glyph, BtnKind kind)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Text = text;
            Glyph = glyph;
            Kind = kind;
            Font = kind == BtnKind.Primary ? Theme.Bold : Theme.Body;
            Cursor = Cursors.Hand;
            Height = Theme.P(kind == BtnKind.Nav ? 40 : 36);
            FitWidth();
            if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(glyph)) AccessibleName = "Schaltfläche";
        }

        public void FitWidth()
        {
            if (Kind == BtnKind.Nav) return;
            if (string.IsNullOrEmpty(Text)) { Width = Height; return; }
            int w = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width + Theme.P(28);
            if (!string.IsNullOrEmpty(Glyph)) w += Theme.P(24);
            Width = w;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color parentBg = Parent != null ? Parent.BackColor : Theme.Bg;
            g.Clear(parentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Theme.P(5);
            Color bg = parentBg, fg = Theme.Text, border = Color.Empty;

            switch (Kind)
            {
                case BtnKind.Primary:
                    bg = down ? Theme.Blend(Theme.Accent, parentBg, 0.2f) : hover ? Theme.AccentHover : Theme.Accent;
                    fg = Theme.AccentText;
                    break;
                case BtnKind.Secondary:
                    bg = down ? Theme.Pressed : hover ? Theme.ControlHover : Theme.ControlBg;
                    border = Theme.ControlBorder;
                    break;
                case BtnKind.Soft:
                    bg = down ? Theme.Blend(Theme.AccentSoft, Theme.Accent, 0.15f) : hover ? Theme.Blend(Theme.AccentSoft, Theme.Accent, 0.08f) : Theme.AccentSoft;
                    fg = Theme.AccentSoftText;
                    border = Theme.Blend(Theme.AccentSoft, Theme.Accent, 0.25f);
                    break;
                case BtnKind.Subtle:
                    bg = down ? Theme.Pressed : hover ? Theme.Hover : parentBg;
                    break;
                case BtnKind.Nav:
                    bg = selected ? Theme.NavSel : down ? Theme.Pressed : hover ? Theme.Hover : parentBg;
                    break;
            }
            if (!Enabled)
            {
                fg = Theme.DisabledText;
                if (Kind == BtnKind.Primary || Kind == BtnKind.Soft) bg = Theme.Disabled;
                if (border != Color.Empty) border = Theme.Border;
            }

            if (bg != parentBg) Theme.FillRound(g, r, rad, bg);
            if (border != Color.Empty) Theme.DrawRound(g, r, rad, border, 1f);

            if (Kind == BtnKind.Nav)
            {
                if (selected)
                    Theme.FillRound(g, new Rectangle(0, Height / 2 - Theme.P(8), Theme.P(3), Theme.P(16)), Theme.P(2), Theme.Accent);
                int x = Theme.P(14);
                if (!string.IsNullOrEmpty(Glyph))
                {
                    // etwas größer, damit die eingeklappte Navigation gut lesbar bleibt
                    Theme.DrawGlyph(g, Glyph, GlyphSize > 0 ? GlyphSize : 14f, new Rectangle(x - Theme.P(2), 0, Theme.P(24), Height), fg, TextFormatFlags.HorizontalCenter);
                    x += Theme.P(32);
                }
                if (Width - x - Theme.P(8) > Theme.P(20)) // eingeklappt: nur das Symbol
                    Theme.DrawText(g, Text, selected ? Theme.Bold : Font, new Rectangle(x, 0, Width - x - Theme.P(8), Height), fg, TextFormatFlags.Left);
            }
            else
            {
                bool hasText = !string.IsNullOrEmpty(Text);
                bool hasGlyph = !string.IsNullOrEmpty(Glyph);
                int tw = hasText ? TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width : 0;
                int gw = hasGlyph ? Theme.P(GlyphSize > 10f ? 20 : 16) : 0;
                int gap = hasText && hasGlyph ? Theme.P(8) : 0;
                int x = (Width - (tw + gw + gap)) / 2;
                if (hasGlyph)
                {
                    Theme.DrawGlyph(g, Glyph, GlyphSize > 0 ? GlyphSize : 10f, new Rectangle(x, 0, gw, Height), fg, TextFormatFlags.HorizontalCenter);
                }
                if (hasText)
                    Theme.DrawText(g, Text, Font, new Rectangle(x + gw + gap, 0, tw + Theme.P(4), Height), fg, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }

            if (Focused && ShowFocusCues)
                Theme.DrawRound(g, new Rectangle(1, 1, Width - 3, Height - 3), rad, Theme.Text, 1.5f);
        }
    }

    public class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Size = new Size(Theme.P(44), Theme.P(24));
            Cursor = Cursors.Hand;
            Text = "";
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            int rad = r.Height / 2;
            Color on = Enabled ? Theme.Accent : Theme.DisabledText;
            Color off = Enabled ? Theme.TextDim : Theme.DisabledText;
            int d = Theme.P(12);
            int y = r.Top + (r.Height - d) / 2;
            if (Checked)
            {
                Theme.FillRound(g, r, rad, on);
                using (SolidBrush b = new SolidBrush(Theme.Dark ? Theme.AccentText : Color.White))
                    g.FillEllipse(b, r.Right - Theme.P(5) - d, y, d, d);
            }
            else
            {
                Theme.DrawRound(g, r, rad, off, 1f);
                using (SolidBrush b = new SolidBrush(off))
                    g.FillEllipse(b, r.Left + Theme.P(5), y, d, d);
            }
            if (Focused && ShowFocusCues)
                Theme.DrawRound(g, new Rectangle(0, 0, Width - 1, Height - 1), rad + 1, Theme.Text, 1f);
        }
    }

    public class ModeCard : RadioButton
    {
        public string Glyph, Title, Desc;
        bool hover;

        public ModeCard(string glyph, string title, string desc)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Glyph = glyph;
            Title = title;
            Desc = desc;
            Text = title;
            AutoSize = false;
            Height = Theme.P(124);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            int rad = Theme.P(8);
            Color bg = Checked ? Theme.AccentSoft : hover ? Theme.Hover : Theme.Surface;
            Theme.FillRound(g, r, rad, bg);
            Theme.DrawRound(g, r, rad, Checked ? Theme.Accent : Theme.Border, Checked ? 2f : 1f);
            int pad = Theme.P(14);
            Theme.DrawGlyph(g, Glyph, 15f, new Rectangle(pad, pad, Theme.P(28), Theme.P(28)), Checked ? Theme.Accent : Theme.TextDim, TextFormatFlags.Left);
            Theme.DrawText(g, Title, Theme.Bold, new Rectangle(pad, Theme.P(48), Width - 2 * pad, Theme.P(22)), Theme.Text, TextFormatFlags.Left);
            TextRenderer.DrawText(g, Desc, Theme.Small, new Rectangle(pad, Theme.P(72), Width - 2 * pad, Height - Theme.P(78)),
                Theme.TextDim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
            if (Focused && ShowFocusCues)
                Theme.DrawRound(g, new Rectangle(4, 4, Width - 9, Height - 9), rad - 2, Theme.Text, 1f);
        }
    }

    public class Pill : RadioButton
    {
        int count;
        bool hover;
        readonly bool showCount;

        public int Count
        {
            get { return count; }
            set { count = value; FitWidth(); Invalidate(); }
        }

        public Pill(string text) : this(text, true) { }

        /// <summary>showCount = false: reine Auswahl ohne Zähler (z. B. in den Einstellungen).</summary>
        public Pill(string text, bool showCount)
        {
            this.showCount = showCount;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            AutoSize = false;
            Height = Theme.P(32);
            Cursor = Cursors.Hand;
            Font = Theme.Body;
            FitWidth();
        }

        void FitWidth()
        {
            Width = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width +
                    (showCount ? TextRenderer.MeasureText(count.ToString("N0"), Theme.Small).Width + Theme.P(40) : Theme.P(24));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = r.Height / 2;
            Color fg;
            if (Checked)
            {
                Theme.FillRound(g, r, rad, Theme.Accent);
                fg = Theme.AccentText;
            }
            else
            {
                Theme.FillRound(g, r, rad, hover ? Theme.ControlHover : Theme.ControlBg);
                Theme.DrawRound(g, r, rad, Theme.ControlBorder, 1f);
                fg = Theme.Text;
            }
            int tw = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            int x = Theme.P(12);
            Theme.DrawText(g, Text, Font, new Rectangle(x, 0, tw + 2, Height), fg, TextFormatFlags.NoPadding);
            if (!showCount)
            {
                if (Focused && ShowFocusCues)
                    Theme.DrawRound(g, new Rectangle(2, 2, Width - 5, Height - 5), rad - 2, Checked ? Theme.AccentText : Theme.Text, 1f);
                return;
            }
            string c = count.ToString("N0");
            int cw = TextRenderer.MeasureText(g, c, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.P(12);
            Rectangle br = new Rectangle(x + tw + Theme.P(8), Height / 2 - Theme.P(9), cw, Theme.P(18));
            Theme.FillRound(g, br, Theme.P(9), Checked ? Theme.Blend(Theme.Accent, Theme.AccentText, 0.25f) : Theme.NeutralBg);
            Theme.DrawText(g, c, Theme.Small, br, Checked ? Theme.AccentText : Theme.Text, TextFormatFlags.HorizontalCenter);
            if (Focused && ShowFocusCues)
                Theme.DrawRound(g, new Rectangle(2, 2, Width - 5, Height - 5), rad - 2, Checked ? Theme.AccentText : Theme.Text, 1f);
        }
    }

    public class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.P(8), Theme.Surface);
            Theme.DrawRound(g, r, Theme.P(8), Theme.Border, 1f);
        }
    }

    public class InputBox : Panel
    {
        public readonly TextBox Box;

        public InputBox(bool mono, bool multiline)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Input;
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Input;
            Box.ForeColor = Theme.Text;
            Box.Font = mono ? Theme.Mono : Theme.Body;
            Box.Multiline = multiline;
            if (multiline)
            {
                Box.ScrollBars = ScrollBars.Vertical;
                Box.AcceptsReturn = true;
                Box.WordWrap = false;
            }
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
            Height = Theme.P(36);
            Cursor = Cursors.IBeam;
            Click += delegate { Box.Focus(); };
            Box.HandleCreated += delegate { Theme.DarkScroll(Box); };
        }

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // grauer Hinweistext im leeren Feld (z. B. „Aufgaben suchen“)
        string cue;
        public string Cue
        {
            get { return cue; }
            set
            {
                cue = value;
                if (Box.IsHandleCreated) SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue ?? "");
                else Box.HandleCreated += delegate { SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue ?? ""); };
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.P(10);
            if (Box.Multiline)
                Box.SetBounds(pad, Theme.P(8), Math.Max(10, Width - 2 * pad), Math.Max(10, Height - Theme.P(16)));
            else
                Box.SetBounds(pad, (Height - Box.Height) / 2, Math.Max(10, Width - 2 * pad), Box.Height);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Theme.P(5);
            Theme.FillRound(g, r, rad, Theme.Input);
            bool focus = Box.Focused;
            Theme.DrawRound(g, r, rad, Theme.ControlBorder, 1f);
            if (focus)
            {
                g.SmoothingMode = SmoothingMode.None;
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, rad, Height - Theme.P(2), Width - 2 * rad, Theme.P(2));
            }
        }
    }

    public class ThemedListView : ListView
    {
        public Func<DrawListViewSubItemEventArgs, Color, bool> CustomDraw;
        public bool StretchLast = true;
        /// <summary>Spalte, die die restliche Breite bekommt (-1 = letzte Spalte). Mit gesetzter Spalte passt die Liste
        /// ohne waagrechte Bildlaufleiste in das Fenster, solange die Spalte ihre Mindestbreite behält.</summary>
        public int FillColumn = -1;
        public int FillMin = 120;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int index);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Der dunkle Windows-Stil zeichnet unter der letzten Zeile senkrechte Spaltenlinien – die freie
            // Fläche danach einfarbig übermalen (hell und dunkel gleich)
            if (m.Msg == 0x000F && View == View.Details && IsHandleCreated)
            {
                try
                {
                    int rows = VirtualMode ? VirtualListSize : Items.Count;
                    int top = 0;
                    if (rows > 0) top = GetItemRect(rows - 1).Bottom;
                    else
                    {
                        IntPtr header = SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
                        RECT hr;
                        if (header != IntPtr.Zero && GetWindowRect(header, out hr)) top = hr.Bottom - hr.Top;
                    }
                    if (top < ClientSize.Height)
                    {
                        top = Math.Max(0, top);
                        using (Graphics g = CreateGraphics())
                        using (SolidBrush b = new SolidBrush(BackColor))
                            g.FillRectangle(b, 0, top, ClientSize.Width, ClientSize.Height - top);
                    }
                }
                catch { }
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessage")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct LVITEM
        {
            public int mask, iItem, iSubItem, state, stateMask;
            public IntPtr pszText;
            public int cchTextMax, iImage;
            public IntPtr lParam;
            public int iIndent, iGroupId, cColumns;
            public IntPtr puColumns, piColFmt;
            public int iGroup;
        }

        /// <summary>Alle Zeilen markieren (auch im virtuellen Modus schnell).</summary>
        public void SelectAllItems()
        {
            if (!MultiSelect || !IsHandleCreated) return;
            LVITEM it = new LVITEM();
            it.mask = 0x8;       // LVIF_STATE
            it.state = 0x2;      // LVIS_SELECTED
            it.stateMask = 0x2;
            SendMessage(Handle, 0x102B, (IntPtr)(-1), ref it); // LVM_SETITEMSTATE, alle Einträge
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.A && e.Control && !e.Alt && !e.Shift)
            {
                SelectAllItems();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public ThemedListView()
        {
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            DoubleBuffered = true;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            ImageList il = new ImageList();
            il.ImageSize = new Size(1, Theme.P(34));
            SmallImageList = il;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkScroll(this);
            Theme.DarkHeader(this);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Stretch();
        }

        public void Stretch()
        {
            if (!StretchLast || Columns.Count == 0) return;
            if (FillColumn >= 0 && FillColumn < Columns.Count)
            {
                int others = 0;
                for (int i = 0; i < Columns.Count; i++) if (i != FillColumn) others += Columns[i].Width;
                // Kommt gleich eine senkrechte Bildlaufleiste dazu (mehr Zeilen als Platz), deren Breite schon
                // abziehen – sonst entsteht eine waagrechte Leiste. Nach dem Laden der Einträge erneut aufrufen.
                // Gerechnet wird mit der Gesamtbreite (ohne Rahmen = Inhalt + Bildlaufleisten), nicht mit ClientSize:
                // ClientSize ist zum Zeitpunkt des Aufrufs mal mit, mal ohne senkrechte Leiste.
                int avail = Width - 2;
                int rows = VirtualMode ? VirtualListSize : Items.Count;
                if (rows * Theme.P(34) + Theme.P(36) > Height) avail -= SystemInformation.VerticalScrollBarWidth;
                int fw = Math.Max(Theme.P(FillMin), avail - others);
                if (Columns[FillColumn].Width != fw) Columns[FillColumn].Width = fw;
                return;
            }
            int used = 0;
            for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
            int w = ClientSize.Width - used - 1;
            if (w > Theme.P(80)) Columns[Columns.Count - 1].Width = w;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            Rectangle r = new Rectangle(e.Bounds.X + Theme.P(12), e.Bounds.Y, e.Bounds.Width - Theme.P(20), e.Bounds.Height);
            TextFormatFlags al = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            Theme.DrawText(e.Graphics, e.Header.Text, Theme.Bold, r, Theme.TextDim, al);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            bool sel = e.Item.Selected;
            Color bg = sel ? Theme.SelBg : Theme.Surface;
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Theme.Blend(Theme.Surface, Theme.Border, 0.6f)))
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if (sel && e.ColumnIndex == 0)
                using (SolidBrush ab = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(ab, e.Bounds.X, e.Bounds.Y + Theme.P(8), Theme.P(3), Math.Max(2, e.Bounds.Height - Theme.P(16)));
            if (CustomDraw != null && CustomDraw(e, bg)) return;
            Rectangle r = new Rectangle(e.Bounds.X + Theme.P(12), e.Bounds.Y, e.Bounds.Width - Theme.P(20), e.Bounds.Height);
            TextFormatFlags al = Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            Color fg = e.ColumnIndex == 0 ? Theme.Text : Theme.TextDim;
            Theme.DrawText(e.Graphics, e.SubItem.Text, Font, r, fg, al);
        }
    }

    public static class Ui
    {
        public static Label Label(string text, Font f, Color fg, Color bg)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = f;
            l.ForeColor = fg;
            l.BackColor = bg;
            l.AutoSize = true;
            l.UseMnemonic = false;
            return l;
        }

        public static Label WrapLabel(string text, Font f, Color fg, Color bg)
        {
            Label l = Label(text, f, fg, bg);
            l.AutoSize = false;
            return l;
        }

        /// <summary>Auswahlfeld im Windows-11-Stil (Knopf mit Pfeil, Auswahl über ein Menü).</summary>
        public static ChoiceBox Combo()
        {
            return new ChoiceBox();
        }

        public static void Explore(string path, bool select)
        {
            try
            {
                if (select) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public static void Open(string file)
        {
            try { System.Diagnostics.Process.Start(file); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    /// <summary>
    /// Auswahlfeld im Windows-11-Stil: Knopf mit Text links und Pfeil (Chevron) rechts, Auswahl über ein Menü
    /// mit Auswahlpunkt (Vorlage: Wake on LAN, "Status prüfen über"). Ersetzt die klassische Auswahlliste.
    /// </summary>
    public class ChoiceBox : Control
    {
        public readonly List<object> Items = new List<object>();
        public event EventHandler SelectedIndexChanged;
        /// <summary>Optional: Symbol je Eintrag (Menü und Knopf).</summary>
        public Func<int, char> GlyphFor;
        /// <summary>Optional: angezeigter Text je Eintrag (sonst ToString).</summary>
        public Func<int, string> TextFor;
        /// <summary>Geschlossen nur das Symbol zeigen (z. B. Geräteauswahl neben einem Pfad).</summary>
        public bool IconOnly;
        int selected = -1;
        bool hover, down;

        public ChoiceBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = true;
            Font = Theme.Body;
            Height = Theme.P(36);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            AccessibleRole = AccessibleRole.ComboBox;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                int v = value < -1 || value >= Items.Count ? -1 : value;
                if (v == selected) return;
                selected = v;
                AccessibleDescription = ItemText(v);
                Invalidate();
                EventHandler h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public object SelectedItem { get { return selected >= 0 && selected < Items.Count ? Items[selected] : null; } }

        string ItemText(int i)
        {
            if (i < 0 || i >= Items.Count) return "";
            return TextFor != null ? TextFor(i) : Convert.ToString(Items[i]);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; Focus(); Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasDown = down;
            down = false;
            Invalidate();
            base.OnMouseUp(e);
            if (wasDown && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) ShowMenu();
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // wie eine Auswahlliste: Pfeile wechseln den Eintrag, Leertaste/Enter/F4/Alt+Pfeil öffnen das Menü
            if (e.KeyCode == Keys.F4 || e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || (e.Alt && e.KeyCode == Keys.Down))
            { ShowMenu(); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Down && selected < Items.Count - 1) { SelectedIndex = selected + 1; e.Handled = true; return; }
            if (e.KeyCode == Keys.Up && selected > 0) { SelectedIndex = selected - 1; e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        public void ShowMenu()
        {
            if (!Enabled || Items.Count == 0) return;
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip m = mb.CreateMenu();
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                char glyph = GlyphFor != null ? GlyphFor(i) : '\0';
                GlyphMenuItem item = mb.Item(ItemText(i), glyph, delegate { SelectedIndex = idx; });
                if (glyph == '\0')
                {
                    item.Checked = i == selected;
                    item.Radio = true;
                }
                else if (i == selected) item.Font = new Font(item.Font, FontStyle.Bold); // mit Symbol: Auswahl fett
                m.Items.Add(item);
            }
            m.MinimumSize = new Size(Width, 0);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(this, new Point(0, Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = !Enabled ? Theme.ControlBg : down ? Theme.ControlHover : hover ? Theme.ControlHover : Theme.Input;
            Color border = Focused ? Theme.Accent : Theme.ControlBorder;
            Theme.FillRound(g, r, Theme.P(5), fill);
            Theme.DrawRound(g, r, Theme.P(5), border, 1f);
            Color fg = Enabled ? Theme.Text : Theme.DisabledText;
            int pad = Theme.P(12), chev = Theme.P(14);
            Rectangle chevR = new Rectangle(Width - pad - chev, 0, chev, Height);
            Theme.DrawGlyph(g, "\uE70D", 8f, chevR, Enabled ? Theme.TextDim : Theme.DisabledText, TextFormatFlags.HorizontalCenter);
            char glyph = GlyphFor != null && selected >= 0 ? GlyphFor(selected) : '\0';
            int x = pad;
            if (glyph != '\0')
            {
                int gw = Theme.P(20);
                int gx = IconOnly ? Math.Max(Theme.P(6), (chevR.Left - gw) / 2) : x;
                Theme.DrawGlyph(g, glyph.ToString(), 12f, new Rectangle(gx, 0, gw, Height), fg, TextFormatFlags.HorizontalCenter);
                x = gx + gw + Theme.P(8);
            }
            if (!IconOnly)
                Theme.DrawText(g, ItemText(selected), Font, new Rectangle(x, 0, Math.Max(10, chevR.Left - Theme.P(6) - x), Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }
}
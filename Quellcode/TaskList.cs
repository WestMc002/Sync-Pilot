// Sync Pilot – kompakte Aufgabenliste: Spaltenkopf, Gruppenköpfe und Zeilen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Spaltenaufteilung der Liste (Kopf und Zeilen benutzen dieselbe Rechnung).</summary>
    public static class TaskColumns
    {
        public const int Name = 0, Folders = 1, Status = 2, Last = 3;

        /// <summary>x-Position und Breite der Spalten: Symbol, Name, Ordner, Status, Zuletzt, Aktionen.</summary>
        public static void Compute(int width, out Rectangle icon, out Rectangle name, out Rectangle folders,
                                   out Rectangle status, out Rectangle last, out Rectangle actions, int height)
        {
            int pad = Theme.P(12), gap = Theme.P(12);
            int iw = Theme.P(28), sw = Theme.P(150), lw = Theme.P(150), aw = Theme.P(76);
            int flex = Math.Max(Theme.P(120), width - 2 * pad - iw - sw - lw - aw - 5 * gap);
            int nw = flex * 55 / 100, fw = flex - nw;
            int x = pad;
            icon = new Rectangle(x, 0, iw, height); x += iw + gap;
            name = new Rectangle(x, 0, nw, height); x += nw + gap;
            folders = new Rectangle(x, 0, fw, height); x += fw + gap;
            status = new Rectangle(x, 0, sw, height); x += sw + gap;
            last = new Rectangle(x, 0, lw, height); x += lw + gap;
            actions = new Rectangle(x, 0, aw, height);
        }
    }

    /// <summary>Spaltenköpfe; ein Klick sortiert (erneuter Klick dreht die Richtung).</summary>
    public class TaskListHeader : Control
    {
        readonly TasksView owner;
        static readonly string[] Titles = { "Name", "Ordner", "Status", "Zuletzt" };

        public TaskListHeader(TasksView owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.P(34);
            Cursor = Cursors.Hand;
            BackColor = Theme.Surface;
        }

        Rectangle[] Cols()
        {
            Rectangle ic, n, f, s, l, a;
            TaskColumns.Compute(Width, out ic, out n, out f, out s, out l, out a, Height);
            return new Rectangle[] { n, f, s, l };
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Rectangle[] c = Cols();
            for (int i = 0; i < c.Length; i++)
                if (e.X >= c[i].Left - Theme.P(6) && e.X < c[i].Right + Theme.P(6)) { owner.SortBy(i); return; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);
            Rectangle[] c = Cols();
            for (int i = 0; i < c.Length; i++)
            {
                string t = Titles[i] + (owner.SortColumn == i ? (owner.SortDesc ? "  ▾" : "  ▴") : "");
                Theme.DrawText(g, t, Theme.Small, c[i], owner.SortColumn == i ? Theme.Text : Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>Kopf einer Gruppe: Klick klappt auf/zu, rechts „Gruppe synchronisieren“.</summary>
    /// <summary>Aufgaben per Drag &amp; Drop in eine andere Gruppe schieben (Zeilen, Kacheln, Gruppenköpfe).</summary>
    public static class TaskDrag
    {
        public const string Format = "SyncPilot.JobIds";

        /// <summary>Ziehen beginnen, sobald die Maus bei gedrückter Taste weit genug bewegt wurde.</summary>
        public static bool Moved(Point down, Point now)
        {
            Size d = SystemInformation.DragSize;
            return Math.Abs(now.X - down.X) > d.Width / 2 + 2 || Math.Abs(now.Y - down.Y) > d.Height / 2 + 2;
        }

        public static void Begin(Control src, TasksView owner, SyncJob job)
        {
            if (!owner.CanDragGroups) return;
            string[] ids = owner.DragIds(job);
            DataObject d = new DataObject();
            d.SetData(Format, string.Join("|", ids));
            d.SetData(DataFormats.UnicodeText, ids.Length == 1 ? job.Name : ids.Length + " Aufgaben");
            try { src.DoDragDrop(d, DragDropEffects.Move); }
            finally { owner.SetDropGroup(null); }
        }

        /// <summary>Steuerelement als Ziel: Ablegen verschiebt in dessen Gruppe.</summary>
        public static void Target(Control target, TasksView owner, Func<string> groupOf)
        {
            target.AllowDrop = true;
            DragEventHandler over = delegate (object s, DragEventArgs e)
            {
                string[] ids = Ids(e.Data);
                string g = groupOf();
                bool ok = ids != null && owner.CanDragGroups && owner.WouldMove(ids, g);
                e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
                owner.SetDropGroup(ok ? g : null);
                owner.DragScroll(new Point(e.X, e.Y));
            };
            target.DragEnter += over;
            target.DragOver += over;
            target.DragLeave += delegate { owner.SetDropGroup(null); };
            target.DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] ids = Ids(e.Data);
                owner.SetDropGroup(null);
                if (ids != null) owner.MoveToGroup(ids, groupOf());
            };
        }

        static string[] Ids(IDataObject d)
        {
            if (d == null || !d.GetDataPresent(Format)) return null;
            string v = d.GetData(Format) as string;
            return string.IsNullOrEmpty(v) ? null : v.Split('|');
        }
    }

    /// <summary>Name für eine neue Gruppe abfragen (mit Vorschlägen aus den vorhandenen Gruppen).</summary>
    public class GroupDialog : FixedDialog
    {
        readonly InputBox box;
        public string Group { get; private set; }

        public GroupDialog(string title, string hint, string[] existing) : base(title)
        {
            int W = Theme.P(420), pad = Theme.P(24), y = Theme.P(20), g = Theme.P(8);
            Label l = Ui.Label("Name der Gruppe", Theme.Bold, Theme.Text, Theme.Bg);
            l.Location = new Point(pad, y);
            y += Theme.P(26);
            box = new InputBox(false, false);
            box.BackColor = Theme.Bg;
            box.Box.AccessibleName = "Name der Gruppe";
            box.Cue = "z. B. NAS";
            AutoCompleteStringCollection ac = new AutoCompleteStringCollection();
            ac.AddRange(existing);
            box.Box.AutoCompleteCustomSource = ac;
            box.Box.AutoCompleteSource = AutoCompleteSource.CustomSource;
            box.Box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            box.SetBounds(pad, y, W - 2 * pad, box.Height);
            y += box.Height + Theme.P(8);
            Label h = Ui.WrapLabel(hint, Theme.Small, Theme.TextDim, Theme.Bg);
            int hh = TextRenderer.MeasureText(hint, Theme.Small, new Size(W - 2 * pad, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.P(4);
            h.SetBounds(pad, y, W - 2 * pad, hh);
            y += hh + Theme.P(20);
            FlatButton ok = new FlatButton("Verschieben", Theme.GCheck, BtnKind.Primary);
            FlatButton cancel = new FlatButton("Abbrechen", null, BtnKind.Secondary);
            // "Abbrechen" links, Primärknopf ganz rechts (Globale Richtlinien), 10 Abstand
            ok.Location = new Point(W - pad - ok.Width, y);
            cancel.Location = new Point(ok.Left - Theme.P(10) - cancel.Width, y);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate
            {
                string v = box.Text.Trim();
                if (v.Length == 0) { box.Box.Focus(); return; }
                Group = v;
                DialogResult = DialogResult.OK;
            };
            AcceptButton = ok;
            CancelButton = cancel;
            y += ok.Height + Theme.P(20);
            Controls.AddRange(new Control[] { l, box, h, cancel, ok });
            ClientSize = new Size(W, y);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            box.Box.Focus();
        }
    }

    public class TaskGroupHeader : Control
    {
        public readonly string Group;
        public bool Collapsed;
        public string Info = "";
        public bool CanRun = true;
        readonly TasksView owner;
        bool hoverLink;
        Rectangle link;

        public TaskGroupHeader(TasksView owner, string group, bool listMode)
        {
            this.owner = owner;
            Group = group;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.P(listMode ? 38 : 40);
            Cursor = Cursors.Hand;
            BackColor = listMode ? Theme.Surface2 : Theme.Bg;
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = group;
            TaskDrag.Target(this, owner, delegate { return Group; });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = CanRun && link.Contains(e.Location);
            if (h != hoverLink) { hoverLink = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverLink) { hoverLink = false; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (CanRun && link.Contains(e.Location)) owner.RunGroup(Group);
            else owner.ToggleGroup(Group);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            int x = Theme.P(10);
            Theme.DrawGlyph(g, Collapsed ? "\uE76C" : "\uE70D", 8f, new Rectangle(x, 0, Theme.P(20), Height), Theme.TextDim, TextFormatFlags.HorizontalCenter);
            x += Theme.P(26);
            int nw = TextRenderer.MeasureText(g, Group, Theme.Bold, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            Theme.DrawText(g, Group, Theme.Bold, new Rectangle(x, 0, Math.Min(nw + 4, Width / 2), Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += Math.Min(nw + 4, Width / 2) + Theme.P(10);

            string run = "Gruppe synchronisieren";
            int rw = TextRenderer.MeasureText(g, run, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.P(24);
            link = new Rectangle(Width - Theme.P(14) - rw, (Height - Theme.P(26)) / 2, rw, Theme.P(26));
            if (CanRun)
            {
                if (hoverLink) Theme.FillRound(g, link, Theme.P(4), Theme.Hover);
                Color lc = hoverLink ? Theme.AccentHover : Theme.AccentSoftText;
                Theme.DrawGlyph(g, Theme.GSync, 9f, new Rectangle(link.X + Theme.P(4), link.Y, Theme.P(18), link.Height), lc, TextFormatFlags.HorizontalCenter);
                Theme.DrawText(g, run, Theme.Small, new Rectangle(link.X + Theme.P(22), link.Y, link.Width - Theme.P(22), link.Height), lc,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            Theme.DrawText(g, Info, Theme.Small, new Rectangle(x, 0, Math.Max(10, link.Left - x - Theme.P(8)), Height), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            // Ziel beim Ziehen: Rahmen in Akzentfarbe und Hinweis
            if (string.Equals(owner.DropGroup, Group, StringComparison.OrdinalIgnoreCase))
            {
                Theme.DrawRound(g, new Rectangle(1, 1, Width - 3, Height - 3), Theme.P(4), Theme.Accent, 2f);
                if (CanRun) Theme.FillRound(g, link, Theme.P(4), BackColor);
                Theme.DrawText(g, "Hierher verschieben", Theme.Small, link, Theme.AccentSoftText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    /// <summary>Eine Aufgabe als Zeile: Klick wählt aus (Strg/Umschalt für mehrere), Doppelklick bearbeitet, Rechtsklick Menü.</summary>
    public class TaskRow : Control
    {
        public readonly SyncJob Job;
        public bool Selected;
        readonly TasksView owner;
        bool hover;
        int hotAction = -1; // 0 = Analysieren, 1 = Synchronisieren
        Rectangle actAnalyze, actSync;
        static readonly ToolTip Tip = new ToolTip();
        Point downAt;
        bool down, pendingSelect;
        readonly string tipText;

        public TaskRow(TasksView owner, SyncJob job)
        {
            this.owner = owner;
            Job = job;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = Theme.P(40);
            BackColor = Theme.Surface;
            AccessibleRole = AccessibleRole.ListItem;
            AccessibleName = job.Name;
            tipText = job.FolderA + "\n" + (job.Mode == SyncMode.Abgleichen ? "⇄ " : "→ ") + job.FolderB +
                "\n\nDoppelklick: bearbeiten · Rechtsklick: weitere Aktionen · Ziehen auf eine Gruppe: verschieben";
            Tip.SetToolTip(this, tipText);
            TaskDrag.Target(this, owner, delegate { return TasksView.GroupOf(Job); });
        }

        int HitAction(Point p)
        {
            if (!hover && !Selected) return -1;
            if (actAnalyze.Contains(p)) return 0;
            if (!Job.Disabled && actSync.Contains(p)) return 1;
            return -1;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; hotAction = -1; Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (down && e.Button == MouseButtons.Left && TaskDrag.Moved(downAt, e.Location))
            {
                down = false;
                pendingSelect = false;
                if (!Selected) owner.RowClick(this, Keys.None);
                TaskDrag.Begin(this, owner, Job);
                return;
            }
            int h = HitAction(e.Location);
            if (h != hotAction)
            {
                hotAction = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Tip.SetToolTip(this, h == 0 ? "Analysieren – Vorschau mit allen Änderungen"
                    : h == 1 ? "Jetzt synchronisieren – ohne Vorschau, Konflikte werden übersprungen" : tipText);
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                int a = HitAction(e.Location);
                if (a == 0) { owner.Main_.ShowAnalysis(Job, false); return; }
                if (a == 1) { owner.QuickRun(Job); return; } // ohne Vorschau, im Hintergrund
                down = true;
                downAt = e.Location;
                // Klick auf eine schon ausgewählte Zeile: Auswahl erst beim Loslassen ändern,
                // damit mehrere ausgewählte Aufgaben zusammen gezogen werden können
                if (Selected && ModifierKeys == Keys.None) pendingSelect = true;
                else owner.RowClick(this, ModifierKeys);
            }
            else if (e.Button == MouseButtons.Right && !Selected) owner.RowClick(this, Keys.None);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && pendingSelect) owner.RowClick(this, Keys.None);
            down = pendingSelect = false;
            if (e.Button == MouseButtons.Right) owner.ShowJobMenu(Job, owner.LastOf(Job), this, e.Location);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && HitAction(e.Location) < 0) owner.Main_.ShowEdit(Job);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Apps || keyData == (Keys.Shift | Keys.F10)) { owner.ShowJobMenu(Job, owner.LastOf(Job), this, new Point(Theme.P(40), Height)); return true; }
            if (keyData == Keys.Enter) { owner.Main_.ShowAnalysis(Job, false); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = Selected ? Theme.SelBg : hover ? Theme.Hover : Theme.Surface;
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            if (Selected)
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, Theme.P(8), Theme.P(3), Height - Theme.P(16));

            Rectangle ic, nm, fo, st, la, ac;
            TaskColumns.Compute(Width, out ic, out nm, out fo, out st, out la, out ac, Height);
            HistoryEntry last = owner.LastOf(Job);
            Color dim = Job.Disabled ? Theme.DisabledText : Theme.TextDim;

            // Richtung im farbigen Kreis wie bei den Kacheln (bei Fehler bzw. Konflikten in deren Farbe)
            string stText; Color stFg, stBg;
            TasksView.Status(last, out stText, out stFg, out stBg);
            int kind = TasksView.StatusKind(last);
            Color cBg = Job.Disabled ? Theme.NeutralBg : kind >= 2 ? stBg : Theme.AccentSoft;
            Color cFg = Job.Disabled ? Theme.DisabledText : kind >= 2 ? stFg : Theme.AccentSoftText;
            int d = Theme.P(26);
            Rectangle circle = new Rectangle(ic.X + (ic.Width - d) / 2, (Height - d) / 2, d, d);
            using (SolidBrush b = new SolidBrush(cBg)) g.FillEllipse(b, circle);
            Theme.DrawGlyph(g, Job.Mode == SyncMode.Abgleichen ? Theme.GSwitch : Theme.GForward, 9f, circle, cFg, TextFormatFlags.HorizontalCenter);
            // Name
            Theme.DrawText(g, Job.Name, Theme.Body, nm, Job.Disabled ? Theme.DisabledText : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            // Ordner: eigene Bezeichnungen, sonst kurze Namen aus dem Pfad ("PC (F:) → NAS");
            // die vollen Pfade stehen im Tooltip
            string arrow = Job.Mode == SyncMode.Abgleichen ? "  ⇄  " : "  →  ";
            TextFormatFlags pf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Job.ShortSide(true) + arrow + Job.ShortSide(false), Theme.Small, fo, dim, pf | TextFormatFlags.EndEllipsis);
            // Status
            if (Job.Disabled) { stText = "Deaktiviert"; stFg = Theme.Neutral; stBg = Theme.NeutralBg; }
            if (owner.IsRunningNow(Job)) { stText = "Läuft …"; stFg = Theme.AccentSoftText; stBg = Theme.AccentSoft; }
            int maxChip = st.Width;
            string chip = stText;
            while (chip.Length > 3 && Theme.ChipWidth(g, chip, Theme.Small) > maxChip) chip = chip.Substring(0, chip.Length - 2) + "…";
            Theme.Chip(g, chip, Theme.Small, st.X, Height / 2, stFg, stBg);
            // Zuletzt (Uhr = Zeitplan oder Ereignis)
            int lx = la.X;
            if (Job.Schedule != ScheduleKind.Manuell || Job.HasEvents)
            {
                Theme.DrawGlyph(g, "\uE823", 9f, new Rectangle(lx, 0, Theme.P(18), Height), Job.Disabled ? Theme.DisabledText : Theme.AccentSoftText, TextFormatFlags.Left);
                lx += Theme.P(20);
            }
            Theme.DrawText(g, last == null ? "Noch nie" : Fmt.When(last.Start), Theme.Small, new Rectangle(lx, 0, la.Right - lx, Height), dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            // Aktionen nur bei Maus darüber bzw. ausgewählt
            int bs = Theme.P(30);
            actSync = new Rectangle(ac.Right - bs, (Height - bs) / 2, bs, bs);
            actAnalyze = new Rectangle(actSync.Left - Theme.P(6) - bs, actSync.Y, bs, bs);
            if (hover || Selected)
            {
                DrawAction(g, actAnalyze, "\uE721", hotAction == 0, true);
                DrawAction(g, actSync, Theme.GPlay, hotAction == 1, !Job.Disabled);
            }
            if (Focused)
                using (Pen p = new Pen(Theme.Accent)) { p.DashStyle = DashStyle.Dot; g.DrawRectangle(p, 1, 1, Width - 3, Height - 3); }
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, Theme.P(12), Height - 1, Width - Theme.P(12), Height - 1);
        }

        void DrawAction(Graphics g, Rectangle r, string glyph, bool hot, bool enabled)
        {
            if (hot) Theme.FillRound(g, r, Theme.P(4), Theme.ControlHover);
            Theme.DrawGlyph(g, glyph, 10f, r, enabled ? (hot ? Theme.AccentHover : Theme.Text) : Theme.DisabledText, TextFormatFlags.HorizontalCenter);
        }
    }
}

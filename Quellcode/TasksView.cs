// Sync Pilot – Aufgabenübersicht
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SyncPilot
{
    public class StatTile : Card
    {
        public string Glyph, Label;
        public int Value;
        public Color Fg, CircleBg;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Theme.P(36), x = Theme.P(16), y = (Height - d) / 2;
            using (SolidBrush b = new SolidBrush(CircleBg)) g.FillEllipse(b, x, y, d, d);
            Theme.DrawGlyph(g, Glyph, 12f, new Rectangle(x, y, d, d), Fg, TextFormatFlags.HorizontalCenter);
            int tx = x + d + Theme.P(14);
            Theme.DrawText(g, Value.ToString("N0"), Theme.Big, new Rectangle(tx, y - Theme.P(4), Width - tx - Theme.P(8), Theme.P(28)), Theme.Text, TextFormatFlags.Left);
            Theme.DrawText(g, Label, Theme.Small, new Rectangle(tx, y + Theme.P(22), Width - tx - Theme.P(8), Theme.P(20)), Theme.TextDim, TextFormatFlags.Left);
        }
    }

    public class TaskCard : Card
    {
        public readonly SyncJob Job;
        public HistoryEntry Last;
        readonly FlatButton btnAnalyze, btnSync;
        static readonly ToolTip Tip = new ToolTip();
        readonly TasksView owner;

        public TaskCard(TasksView owner, SyncJob job)
        {
            this.owner = owner;
            Job = job;
            btnAnalyze = new FlatButton("Analysieren", null, BtnKind.Secondary);
            btnSync = new FlatButton("Synchronisieren", null, BtnKind.Soft);
            foreach (FlatButton b in new FlatButton[] { btnAnalyze, btnSync })
            {
                b.Height = Theme.P(32);
                b.FitWidth();
                Controls.Add(b);
            }
            Height = Theme.P(62); // niedrig, damit viele Aufgaben ohne Blättern passen
            btnAnalyze.Click += delegate { owner.Main_.ShowAnalysis(Job, false); };
            btnSync.Click += delegate { owner.Main_.ShowAnalysis(Job, true); };
            btnSync.Enabled = !job.Disabled; // deaktiviert: nur noch Analysieren
            DoubleClick += delegate { owner.Main_.ShowEdit(Job); };
            Tip.SetToolTip(this, "Doppelklick: bearbeiten · Rechtsklick: weitere Aktionen");
            // Menü der Aufgabe: Rechtsklick auf die Karte (an der Mausposition)
            MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) owner.ShowJobMenu(this, this, e.Location);
            };
        }

        // Kontextmenü-Taste bzw. Umschalt+F10 auf der Karte
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Apps || keyData == (Keys.Shift | Keys.F10))
            {
                owner.ShowJobMenu(this, this, new Point(btnSync.Left, btnSync.Bottom));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (btnSync == null) return;
            int y = (Height - btnSync.Height) / 2;
            int x = Width - Theme.P(16) - btnSync.Width;
            btnSync.Location = new Point(x, y);
            x -= Theme.P(6) + btnAnalyze.Width;
            btnAnalyze.Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string stText; Color stFg, stBg;
            TasksView.Status(Last, out stText, out stFg, out stBg);
            if (Job.Disabled) { stText = "Deaktiviert"; stFg = Theme.Neutral; stBg = Theme.NeutralBg; }

            int d = Theme.P(36), ix = Theme.P(14), iy = (Height - d) / 2;
            bool ok = TasksView.StatusKind(Last) <= 1;
            Theme.FillRound(g, new Rectangle(ix, iy, d, d), Theme.P(8), ok ? Theme.AccentSoft : stBg);
            Theme.DrawGlyph(g, Devices.Glyph(Job.DeviceA, Job.FolderA).ToString(), 13f, new Rectangle(ix, iy, d, d), ok ? Theme.AccentSoftText : stFg, TextFormatFlags.HorizontalCenter);

            int x0 = ix + d + Theme.P(16);
            int right = btnAnalyze.Left - Theme.P(16);
            int infoW = Theme.P(230);
            bool showInfo = right - infoW - Theme.P(10) > x0 + Theme.P(280);
            int textRight = showInfo ? right - infoW - Theme.P(10) : right;

            int nameW = Math.Min(TextRenderer.MeasureText(g, Job.Name, Theme.Bold).Width, Math.Max(Theme.P(40), textRight - x0 - Theme.P(150)));
            Theme.DrawText(g, Job.Name, Theme.Bold, new Rectangle(x0, Theme.P(8), nameW, Theme.P(22)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            Theme.Chip(g, stText, Theme.Small, x0 + nameW + Theme.P(10), Theme.P(19), stFg, stBg);

            int py = Theme.P(32), ph = Theme.P(22);
            int gw = Theme.P(26);
            int bw = Theme.P(20); // Geraete-Icon vor Ordner B
            int pw = Math.Max(Theme.P(40), (textRight - x0 - gw - bw) / 2);
            TextFormatFlags pf = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis;
            int aw = Math.Min(pw, TextRenderer.MeasureText(g, Job.FolderA, Theme.MonoSmall).Width);
            TextRenderer.DrawText(g, Job.FolderA, Theme.MonoSmall, new Rectangle(x0, py, aw, ph), Theme.TextDim,
                pf | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            Theme.DrawGlyph(g, Job.Mode == SyncMode.Abgleichen ? Theme.GSwitch : Theme.GForward, 9f, new Rectangle(x0 + aw, py, gw, ph), Theme.Text, TextFormatFlags.HorizontalCenter);
            Theme.DrawGlyph(g, Devices.Glyph(Job.DeviceB, Job.FolderB).ToString(), 9f, new Rectangle(x0 + aw + gw, py, bw, ph), Theme.TextDim, TextFormatFlags.Left);
            TextRenderer.DrawText(g, Job.FolderB, Theme.MonoSmall, new Rectangle(x0 + aw + gw + bw, py, pw, ph), Theme.TextDim,
                pf | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            if (showInfo)
            {
                int ix2 = right - infoW;
                string when = Last == null ? "Noch nie" : Fmt.When(Last.Start);
                Theme.DrawText(g, when, Theme.Small, new Rectangle(ix2, Theme.P(8), infoW, Theme.P(22)), Theme.Text, TextFormatFlags.Left);
                Theme.DrawText(g, Job.ModeText + " · " + Job.ScheduleText, Theme.Small,
                    new Rectangle(ix2, Theme.P(32), infoW, Theme.P(22)), Theme.TextDim, TextFormatFlags.Left);
            }
        }
    }

    public class TasksView : BaseView
    {
        readonly Label title, subtitle, empty, stripText;
        readonly FlatButton btnAll, btnNew, btnCancel;
        readonly StatTile tOk, tWarn, tErr;
        readonly Panel list, strip;
        readonly List<TaskCard> cards = new List<TaskCard>();
        readonly System.Windows.Forms.Timer timer;
        List<HistoryEntry> history;
        DateTime historyStamp;
        volatile bool busy, cancel;
        volatile string liveStatus = "";
        string filter = "";
        int ticks;

        public MainForm Main_ { get { return Main; } }
        public override bool Busy { get { return busy; } }
        public override string NavKey { get { return "tasks"; } }
        public override void RequestCancel() { cancel = true; }

        public TasksView(MainForm main) : base(main)
        {
            title = Ui.Label("Aufgaben", Theme.H1, Theme.Text, Theme.Bg);
            subtitle = Ui.Label("Ordnerpaare abgleichen, sichern und spiegeln", Theme.Body, Theme.TextDim, Theme.Bg);
            btnAll = new FlatButton("Alle synchronisieren", Theme.GSync, BtnKind.Secondary);
            btnNew = new FlatButton("Neue Aufgabe", Theme.GAdd, BtnKind.Primary);
            btnAll.Click += delegate { RunAll(); };
            btnNew.Click += delegate { Main.ShowEdit(null); };

            tOk = Tile(Theme.GCheck, "Aktuell", Theme.Ok, Theme.OkBg);
            tWarn = Tile(Theme.GWarn, "Mit offenen Konflikten", Theme.Warn, Theme.WarnBg);
            tErr = Tile(Theme.GError, "Fehler beim letzten Lauf", Theme.Err, Theme.ErrBg);

            strip = new Card();
            strip.Height = Theme.P(48);
            strip.Visible = false;
            stripText = Ui.WrapLabel("", Theme.Body, Theme.Text, Theme.Surface);
            stripText.TextAlign = ContentAlignment.MiddleLeft;
            btnCancel = new FlatButton("Abbrechen", Theme.GCancel, BtnKind.Secondary);
            btnCancel.Height = Theme.P(32);
            btnCancel.Click += delegate { cancel = true; btnCancel.Enabled = false; };
            strip.Controls.Add(stripText);
            strip.Controls.Add(btnCancel);
            strip.Resize += delegate
            {
                btnCancel.Location = new Point(strip.Width - btnCancel.Width - Theme.P(10), (strip.Height - btnCancel.Height) / 2);
                stripText.SetBounds(Theme.P(16), Theme.P(4), Math.Max(10, btnCancel.Left - Theme.P(24)), strip.Height - Theme.P(8));
            };

            list = new Panel();
            list.AutoScroll = true;
            list.BackColor = Theme.Bg;
            list.HandleCreated += delegate { Theme.DarkScroll(list); };
            list.Resize += delegate { LayoutCards(); };

            empty = Ui.WrapLabel("Noch keine Aufgaben vorhanden.\nLege mit „Neue Aufgabe“ das erste Ordnerpaar an.",
                Theme.Body, Theme.TextDim, Theme.Bg);
            empty.TextAlign = ContentAlignment.TopCenter;

            Controls.AddRange(new Control[] { title, subtitle, btnAll, btnNew, tOk, tWarn, tErr, strip, list, empty });

            foreach (SyncJob j in Main.Jobs)
            {
                TaskCard c = new TaskCard(this, j);
                cards.Add(c);
                list.Controls.Add(c);
            }
            LoadHistory();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 150;
            timer.Tick += OnTick;
            timer.Start();
        }

        StatTile Tile(string glyph, string label, Color fg, Color bg)
        {
            StatTile t = new StatTile();
            t.Glyph = glyph;
            t.Label = label;
            t.Fg = fg;
            t.CircleBg = bg;
            t.Height = Theme.P(64);
            return t;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); }
            base.Dispose(disposing);
        }

        void OnTick(object sender, EventArgs e)
        {
            if (busy) stripText.Text = liveStatus;
            if (++ticks % 60 == 0 && !busy)
            {
                DateTime s = Store.HistoryStamp();
                if (s != historyStamp) LoadHistory();
            }
        }

        void LoadHistory()
        {
            historyStamp = Store.HistoryStamp();
            history = Store.LoadHistory();
            int ok = 0, warn = 0, err = 0;
            foreach (TaskCard c in cards)
            {
                c.Last = Store.LastRun(history, c.Job.Id);
                int k = StatusKind(c.Last);
                if (k == 1) ok++;
                else if (k == 2) warn++;
                else if (k == 3) err++;
                c.Invalidate();
            }
            tOk.Value = ok; tWarn.Value = warn; tErr.Value = err;
            tOk.Invalidate(); tWarn.Invalidate(); tErr.Invalidate();
        }

        public static int StatusKind(HistoryEntry h)
        {
            if (h == null) return 0;
            if (h.Errors > 0 || h.Cancelled) return 3;
            if (h.Conflicts > 0) return 2;
            return 1;
        }

        public static void Status(HistoryEntry h, out string text, out Color fg, out Color bg)
        {
            switch (StatusKind(h))
            {
                case 1: text = "Aktuell"; fg = Theme.Ok; bg = Theme.OkBg; break;
                case 2: text = h.Conflicts + (h.Conflicts == 1 ? " Konflikt offen" : " Konflikte offen"); fg = Theme.Warn; bg = Theme.WarnBg; break;
                case 3: text = h.Cancelled ? "Abgebrochen" : "Fehler"; fg = Theme.Err; bg = Theme.ErrBg; break;
                default: text = "Noch nie ausgeführt"; fg = Theme.Neutral; bg = Theme.NeutralBg; break;
            }
        }

        public override void ApplySearch(string text)
        {
            filter = (text ?? "").Trim();
            foreach (TaskCard c in cards)
            {
                bool vis = filter.Length == 0 ||
                    c.Job.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.Job.FolderA.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.Job.FolderB.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                c.Visible = vis;
            }
            LayoutCards();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (title == null || list == null) return;
            int pad = Theme.P(32), W = ClientSize.Width, H = ClientSize.Height;
            title.Location = new Point(pad - Theme.P(3), Theme.P(22));
            subtitle.Location = new Point(pad, title.Bottom + Theme.P(2));
            btnNew.Location = new Point(W - pad - btnNew.Width, Theme.P(30));
            btnAll.Location = new Point(btnNew.Left - Theme.P(8) - btnAll.Width, Theme.P(30));

            int gap = Theme.P(12), ty = subtitle.Bottom + Theme.P(20);
            int tw = (W - 2 * pad - 2 * gap) / 3;
            tOk.SetBounds(pad, ty, tw, tOk.Height);
            tWarn.SetBounds(pad + tw + gap, ty, tw, tWarn.Height);
            tErr.SetBounds(pad + 2 * (tw + gap), ty, W - 2 * pad - 2 * (tw + gap), tErr.Height);

            int y = tOk.Bottom + Theme.P(16);
            if (strip.Visible)
            {
                strip.SetBounds(pad, y, W - 2 * pad, strip.Height);
                y = strip.Bottom + Theme.P(12);
            }
            list.SetBounds(pad - 1, y, W - 2 * pad + 2, Math.Max(10, H - y - Theme.P(16)));
            empty.SetBounds(pad, y + Theme.P(40), W - 2 * pad, Theme.P(60));
            empty.Visible = cards.Count == 0;
            LayoutCards();
        }

        void LayoutCards()
        {
            if (list == null) return;
            // Karten sind nie breiter als die Liste: eine beim Verkleinern hängen gebliebene
            // waagrechte Bildlaufleiste entfernen, die senkrechte Position bleibt erhalten
            int keepY = -1;
            if (list.HorizontalScroll.Visible)
            {
                keepY = -list.AutoScrollPosition.Y;
                list.AutoScrollPosition = new Point(0, 0);
                list.AutoScroll = false;
                list.HorizontalScroll.Maximum = 0;
                list.HorizontalScroll.Visible = false;
                list.AutoScroll = true;
            }
            int gap = Theme.P(6), total = 0;
            foreach (TaskCard c in cards) if (c.Visible) total += c.Height + gap;
            int w = list.ClientSize.Width;
            if (total > list.Height) w = list.Width - SystemInformation.VerticalScrollBarWidth - Theme.P(2);
            int y = list.AutoScrollPosition.Y;
            list.SuspendLayout();
            foreach (TaskCard c in cards)
            {
                if (!c.Visible) continue;
                c.SetBounds(1, y, Math.Max(Theme.P(200), w - 2), c.Height);
                y += c.Height + gap;
            }
            list.AutoScrollMinSize = new Size(0, total);
            list.ResumeLayout();
            if (keepY > 0) list.AutoScrollPosition = new Point(0, keepY);
        }

        // ---------- Menue einer Aufgabe ----------

        public void ShowJobMenu(TaskCard card, Control anchor)
        {
            ShowJobMenu(card, anchor, new Point(0, anchor.Height));
        }

        // at: Position im Koordinatensystem von anchor (Rechtsklick: an der Maus)
        public void ShowJobMenu(TaskCard card, Control anchor, Point at)
        {
            SyncJob job = card.Job;
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip m = mb.CreateMenu();
            m.Items.Add(mb.Item("Bearbeiten", '\uE70F', delegate { Main.ShowEdit(job); }));
            m.Items.Add(mb.Item("Duplizieren", '\uE8C8', delegate
            {
                SyncJob copy = job.Clone();
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Name = job.Name + " (Kopie)";
                copy.Schedule = ScheduleKind.Manuell;
                copy.ScheduledTask = null;
                Main.Jobs.Add(copy);
                Main.SaveJobs();
                Main.ShowTasks();
            }));
            m.Items.Add(mb.Item(job.Disabled ? "Aktivieren" : "Deaktivieren", job.Disabled ? '\uE768' : '\uE769', delegate
            {
                job.Disabled = !job.Disabled;
                string err = Scheduler.Apply(job); // Zeitplan ruht, solange die Aufgabe deaktiviert ist
                Main.SaveJobs();
                if (err != null)
                    MessageBox.Show("Zeitplan konnte nicht angepasst werden:\n" + err, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Main.ShowTasks();
            }));
            ToolStripMenuItem link = mb.Item("Verkn\u00fcpfung anlegen", '\uE71B', null);
            link.DropDownItems.Add(mb.Item("Auf dem Desktop", '\uE7F4', delegate { Shortcuts.Create(this, job, null); }));
            link.DropDownItems.Add(mb.Item("In einem anderen Ordner \u2026", MenuHelper.Folder, delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "Ordner f\u00fcr die Verkn\u00fcpfung zu \u201e" + job.Name + "\u201c";
                    d.ShowNewFolderButton = true;
                    if (d.ShowDialog(this) == DialogResult.OK) Shortcuts.Create(this, job, d.SelectedPath);
                }
            }));
            mb.StyleDropDown(link);
            m.Items.Add(link);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(mb.Item("Ordner A \u00f6ffnen", MenuHelper.Folder, delegate { Ui.Explore(Paths.TryResolve(job.FolderA), false); }));
            m.Items.Add(mb.Item("Ordner B \u00f6ffnen", MenuHelper.Folder, delegate { Ui.Explore(Paths.TryResolve(job.FolderB), false); }));
            GlyphMenuItem log = mb.Item("Letztes Protokoll \u00f6ffnen", MenuHelper.Document, delegate
            {
                if (card.Last != null) Ui.Open(card.Last.LogFile);
            });
            log.Enabled = card.Last != null && !string.IsNullOrEmpty(card.Last.LogFile) && File.Exists(card.Last.LogFile);
            m.Items.Add(log);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(mb.Item("L\u00f6schen \u2026", '\uE74D', delegate
            {
                DialogResult r = MessageBox.Show("Aufgabe \u201e" + job.Name + "\u201c l\u00f6schen?\n\nDie Ordner und Dateien selbst bleiben unver\u00e4ndert.",
                    "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                Main.DeleteJob(job);
                Main.ShowTasks();
            }));
            m.Show(anchor, at);
        }

        // ---------- Alle synchronisieren ----------

        void Ui_(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        void RunAll()
        {
            int active = Main.Jobs.Count(x => !x.Disabled);
            if (active == 0) return;
            DialogResult r = MessageBox.Show("Alle " + active + " aktiven Aufgaben jetzt synchronisieren?\n\n" +
                "Konflikte werden dabei übersprungen. Bei auffällig vielen Löschungen wird eine Aufgabe zur Sicherheit nicht ausgeführt.",
                "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            busy = true;
            cancel = false;
            liveStatus = "Starte …";
            strip.Visible = true;
            btnCancel.Enabled = true;
            btnCancel.Visible = true;
            btnAll.Enabled = false;
            btnNew.Enabled = false;
            foreach (TaskCard c in cards) c.Enabled = false;
            PerformLayout();

            List<SyncJob> jobs = Main.Jobs.Where(x => !x.Disabled).ToList(); // deaktivierte nicht
            Thread t = new Thread(delegate ()
            {
                int ok = 0, bad = 0, n = 0;
                foreach (SyncJob j in jobs)
                {
                    if (cancel) break;
                    n++;
                    string prefix = "(" + n + "/" + jobs.Count + ") " + j.Name + ": ";
                    HistoryEntry h;
                    try
                    {
                        h = Runner.Run(j, false, delegate { return cancel; },
                            delegate (string s) { liveStatus = prefix + s; });
                    }
                    catch (Exception ex) { h = Runner.Crash(j, false, ex); }
                    if (h.IsOk) ok++; else bad++;
                }
                bool wasCancelled = cancel;
                Ui_(delegate
                {
                    busy = false;
                    btnCancel.Visible = false;
                    btnAll.Enabled = true;
                    btnNew.Enabled = true;
                    foreach (TaskCard c in cards) c.Enabled = true;
                    stripText.Text = (wasCancelled ? "Abgebrochen. " : "Fertig. ") + ok + " ohne Fehler, " + bad +
                                     " mit Fehlern oder Hinweisen. Details im Verlauf.";
                    LoadHistory();
                    Main.ViewIdle();
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
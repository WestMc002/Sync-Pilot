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
        public HistoryEntry Last { get { return owner.LastOf(Job); } }
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
            Tip.SetToolTip(this, "Doppelklick: bearbeiten · Rechtsklick: weitere Aktionen · Ziehen auf eine Gruppe: verschieben");
            // Ziehen auf einen Gruppenkopf bzw. eine Kachel einer anderen Gruppe verschiebt die Aufgabe
            Point downAt = Point.Empty;
            bool down = false;
            MouseDown += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; downAt = e.Location; } };
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!down || e.Button != MouseButtons.Left || !TaskDrag.Moved(downAt, e.Location)) return;
                down = false;
                TaskDrag.Begin(this, owner, Job);
            };
            MouseUp += delegate { down = false; };
            TaskDrag.Target(this, owner, delegate { return TasksView.GroupOf(Job); });
            // Menü der Aufgabe: Rechtsklick auf die Karte (an der Mausposition)
            MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) owner.ShowJobMenu(Job, owner.LastOf(Job), this, e.Location);
            };
        }

        // Kontextmenü-Taste bzw. Umschalt+F10 auf der Karte
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Apps || keyData == (Keys.Shift | Keys.F10))
            {
                owner.ShowJobMenu(Job, owner.LastOf(Job), this, new Point(btnSync.Left, btnSync.Bottom));
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
        readonly List<Control> items = new List<Control>();   // Gruppenköpfe, Karten bzw. Zeilen in Anzeigereihenfolge
        readonly Dictionary<string, HistoryEntry> lasts = new Dictionary<string, HistoryEntry>();
        readonly HashSet<string> selected = new HashSet<string>();
        readonly Pill pAll, pSched, pErr, pOld;
        readonly FlatButton btnTiles, btnList, btnGroup, btnSel;
        readonly Label selInfo;
        readonly TaskListHeader listHead;
        string chip = "all";
        TaskRow anchorRow;
        public int SortColumn;
        public bool SortDesc;
        static readonly ToolTip BarTip = new ToolTip();
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

            // Werkzeugleiste: Filter, Ansicht, Gruppen, Auswahl
            pAll = new Pill("Alle");
            pSched = new Pill("Mit Zeitplan");
            pErr = new Pill("Fehler / Konflikte");
            pOld = new Pill("Länger nicht gelaufen");
            pAll.Checked = true;
            BarTip.SetToolTip(pSched, "Mit Zeitplan oder Start bei Ereignissen");
            BarTip.SetToolTip(pOld, "Aktive Aufgaben, die seit mehr als 7 Tagen nicht gelaufen sind");
            foreach (Pill p in new Pill[] { pAll, pSched, pErr, pOld })
            {
                Pill pp = p;
                pp.CheckedChanged += delegate
                {
                    if (!pp.Checked) return;
                    chip = pp == pSched ? "sched" : pp == pErr ? "err" : pp == pOld ? "old" : "all";
                    Rebuild();
                };
            }
            btnTiles = new FlatButton("", "\uECA5", BtnKind.Secondary);
            btnList = new FlatButton("", "\uE8FD", BtnKind.Secondary);
            btnTiles.AccessibleName = "Als Kacheln zeigen";
            btnList.AccessibleName = "Als Liste zeigen";
            BarTip.SetToolTip(btnTiles, "Kacheln");
            BarTip.SetToolTip(btnList, "Liste");
            btnTiles.Click += delegate { SetListMode(false); };
            btnList.Click += delegate { SetListMode(true); };
            btnGroup = new FlatButton("Gruppiert", "\uE8B7", BtnKind.Secondary);
            BarTip.SetToolTip(btnGroup, "Aufgaben nach Gruppe ordnen (Gruppe in „Aufgabe bearbeiten“ festlegen)");
            btnGroup.Click += delegate
            {
                if (!Main.Jobs.Any(x => !string.IsNullOrWhiteSpace(x.Group))) { MakeGroups(); return; }
                AppSettings.Current.TaskGrouped = !AppSettings.Current.TaskGrouped;
                AppSettings.Current.Save();
                Rebuild();
            };
            selInfo = Ui.Label("", Theme.Small, Theme.TextDim, Theme.Bg);
            btnSel = new FlatButton("Ausgewählte synchronisieren", Theme.GSync, BtnKind.Secondary);
            btnSel.Height = Theme.P(32);
            btnSel.FitWidth();
            btnSel.Click += delegate
            {
                List<SyncJob> sel = Main.Jobs.Where(x => selected.Contains(x.Id) && !x.Disabled).ToList();
                if (sel.Count > 0) RunJobs(sel, sel.Count == 1 ? "Die ausgewählte Aufgabe jetzt synchronisieren?" : "Die " + sel.Count + " ausgewählten Aufgaben jetzt synchronisieren?");
            };
            foreach (FlatButton b in new FlatButton[] { btnTiles, btnList, btnGroup })
            {
                b.Height = Theme.P(32);
                if (b == btnGroup) b.FitWidth(); else b.Width = b.Height;
            }
            listHead = new TaskListHeader(this);

            Controls.AddRange(new Control[] { title, subtitle, btnAll, btnNew, tOk, tWarn, tErr, strip,
                pAll, pSched, pErr, pOld, btnTiles, btnList, btnGroup, selInfo, btnSel, listHead, list, empty });

            LoadHistory();
            Rebuild();

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
            if (disposing)
            {
                timer.Stop(); timer.Dispose();
                // Bildlaufposition merken: nach "Bearbeiten" bzw. "Analysieren" geht es an derselben Stelle weiter
                try { savedScroll = -list.AutoScrollPosition.Y; } catch { }
            }
            base.Dispose(disposing);
        }

        /// <summary>Bildlaufposition der Liste über das Wechseln der Ansicht hinweg (solange das Programm läuft).</summary>
        static int savedScroll;
        bool scrollRestored;

        void RestoreScroll()
        {
            if (scrollRestored || !list.IsHandleCreated || list.Height <= 0) return;
            scrollRestored = true;
            if (savedScroll <= 0) return;
            int y = savedScroll;
            BeginInvoke((MethodInvoker)delegate
            {
                try { list.AutoScrollPosition = new Point(0, y); } catch { }
            });
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
            lasts.Clear();
            int ok = 0, warn = 0, err = 0;
            foreach (SyncJob j in Main.Jobs)
            {
                HistoryEntry h = Store.LastRun(history, j.Id);
                lasts[j.Id] = h;
                int k = StatusKind(h);
                if (k == 1) ok++;
                else if (k == 2) warn++;
                else if (k == 3) err++;
            }
            tOk.Value = ok; tWarn.Value = warn; tErr.Value = err;
            tOk.Invalidate(); tWarn.Invalidate(); tErr.Invalidate();
            if (pAll != null)
            {
                pAll.Count = Main.Jobs.Count;
                pSched.Count = Main.Jobs.Count(j => Matches(j, "sched"));
                pErr.Count = Main.Jobs.Count(j => Matches(j, "err"));
                pErr.Alert = Main.Jobs.Any(j => StatusKind(LastOf(j)) == 3) ? 2 : pErr.Count > 0 ? 1 : 0;
                pOld.Count = Main.Jobs.Count(j => Matches(j, "old"));
                foreach (TaskGroupHeader gh in items.OfType<TaskGroupHeader>()) gh.Info = GroupInfo(gh.Group);
                // Filter "Fehler" bzw. "länger nicht gelaufen" hängen vom Verlauf ab
                if (chip == "err" || chip == "old") Rebuild();
                else foreach (Control c in items) c.Invalidate();
                PerformLayout();
            }
        }

        public HistoryEntry LastOf(SyncJob job)
        {
            HistoryEntry h;
            return job != null && lasts.TryGetValue(job.Id, out h) ? h : null;
        }

        public bool IsRunningNow(SyncJob job)
        {
            return Runner.IsRunning(job.Id);
        }

        /// <summary>Filter-Plakette: alle, mit Zeitplan/Ereignis, Fehler/Konflikte, länger als 7 Tage nicht gelaufen.</summary>
        bool Matches(SyncJob j, string f)
        {
            switch (f)
            {
                case "sched": return j.Schedule != ScheduleKind.Manuell || j.HasEvents;
                case "err": return StatusKind(LastOf(j)) >= 2;
                case "old":
                    HistoryEntry h = LastOf(j);
                    return !j.Disabled && (h == null || h.Start < DateTime.Now.AddDays(-7));
                default: return true;
            }
        }

        bool MatchesSearch(SyncJob j)
        {
            return filter.Length == 0 ||
                j.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (j.Group ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (j.NameA ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (j.NameB ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                j.ShortSide(true).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                j.ShortSide(false).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                j.FolderA.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                j.FolderB.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        const string NoGroup = "Ohne Gruppe";

        public static string GroupOf(SyncJob j)
        {
            return string.IsNullOrWhiteSpace(j.Group) ? NoGroup : j.Group.Trim();
        }

        bool Grouped
        {
            get { return AppSettings.Current.TaskGrouped && Main.Jobs.Any(j => !string.IsNullOrWhiteSpace(j.Group)); }
        }

        bool ListMode { get { return AppSettings.Current.TaskList; } }

        string GroupInfo(string group)
        {
            List<SyncJob> js = Main.Jobs.Where(j => GroupOf(j) == group).ToList();
            int err = js.Count(j => StatusKind(LastOf(j)) == 3), warn = js.Count(j => StatusKind(LastOf(j)) == 2),
                never = js.Count(j => LastOf(j) == null && !j.Disabled);
            List<string> parts = new List<string>();
            if (err > 0) parts.Add(err + " mit Fehler");
            if (warn > 0) parts.Add(warn + " mit Konflikten");
            if (never > 0) parts.Add(never + " noch nie gelaufen");
            return js.Count + (js.Count == 1 ? " Aufgabe" : " Aufgaben") + " · " + (parts.Count == 0 ? "alle aktuell" : string.Join(", ", parts));
        }

        IEnumerable<SyncJob> Sorted(IEnumerable<SyncJob> jobs)
        {
            IOrderedEnumerable<SyncJob> o;
            switch (SortColumn)
            {
                case TaskColumns.Folders: o = jobs.OrderBy(j => j.ShortSide(true) + " " + j.ShortSide(false), StringComparer.CurrentCultureIgnoreCase); break;
                case TaskColumns.Status: o = jobs.OrderByDescending(j => j.Disabled ? -1 : StatusKind(LastOf(j))); break;
                case TaskColumns.Last: o = jobs.OrderByDescending(j => LastOf(j) == null ? DateTime.MinValue : LastOf(j).Start); break;
                default: o = jobs.OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase); break;
            }
            List<SyncJob> list = o.ThenBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (SortDesc) list.Reverse();
            return list;
        }

        /// <summary>Gruppenköpfe und Karten bzw. Zeilen neu anlegen (Filter, Suche, Ansicht, Sortierung, Gruppen).</summary>
        void Rebuild()
        {
            if (list == null) return;
            bool lm = ListMode, grouped = Grouped;
            list.SuspendLayout();
            foreach (Control c in items) { list.Controls.Remove(c); c.Dispose(); }
            items.Clear();
            anchorRow = null;
            List<SyncJob> shown = Main.Jobs.Where(j => Matches(j, chip) && MatchesSearch(j)).ToList();
            selected.IntersectWith(shown.Select(j => j.Id));
            if (grouped)
            {
                List<string> groups = shown.Select(GroupOf).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g == NoGroup ? 1 : 0).ThenBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
                foreach (string g in groups)
                {
                    List<SyncJob> js = shown.Where(j => string.Equals(GroupOf(j), g, StringComparison.OrdinalIgnoreCase)).ToList();
                    TaskGroupHeader h = new TaskGroupHeader(this, g, lm);
                    h.Collapsed = AppSettings.Current.CollapsedGroups.Contains(g, StringComparer.OrdinalIgnoreCase) && filter.Length == 0;
                    h.Info = GroupInfo(g);
                    h.CanRun = !busy && Main.Jobs.Any(j => GroupOf(j) == g && !j.Disabled);
                    items.Add(h);
                    if (!h.Collapsed) foreach (SyncJob j in Sorted(js)) items.Add(MakeItem(j, lm));
                }
            }
            else foreach (SyncJob j in Sorted(shown)) items.Add(MakeItem(j, lm));
            foreach (Control c in items) { c.Enabled = !busy || c is TaskGroupHeader; list.Controls.Add(c); }
            list.BackColor = lm ? Theme.Surface : Theme.Bg;
            list.ResumeLayout();
            UpdateBar();
            PerformLayout();
        }

        Control MakeItem(SyncJob j, bool lm)
        {
            if (!lm) return new TaskCard(this, j);
            TaskRow r = new TaskRow(this, j);
            r.Selected = selected.Contains(j.Id);
            return r;
        }

        void SetListMode(bool on)
        {
            if (AppSettings.Current.TaskList == on) return;
            AppSettings.Current.TaskList = on;
            AppSettings.Current.Save();
            selected.Clear();
            Rebuild();
        }

        void UpdateBar()
        {
            bool lm = ListMode;
            btnTiles.Kind = lm ? BtnKind.Secondary : BtnKind.Soft;
            btnList.Kind = lm ? BtnKind.Soft : BtnKind.Secondary;
            bool anyGroup = Main.Jobs.Any(j => !string.IsNullOrWhiteSpace(j.Group));
            btnGroup.Visible = Main.Jobs.Count > 0;
            btnGroup.Kind = anyGroup && AppSettings.Current.TaskGrouped ? BtnKind.Soft : BtnKind.Secondary;
            BarTip.SetToolTip(btnGroup, anyGroup ? "Aufgaben nach Gruppe ordnen bzw. alle untereinander zeigen"
                : "Noch keine Gruppen – Klick bildet Gruppen aus den Namen (z. B. „Sync PC <-> NAS (Dokumente)“)");
            btnTiles.Invalidate(); btnList.Invalidate(); btnGroup.Invalidate();
            int n = selected.Count;
            selInfo.Text = n > 0 ? n + " ausgewählt" : "";
            selInfo.Visible = lm && n > 0;
            btnSel.Visible = lm && n > 0;
            btnSel.Enabled = !busy;
            listHead.Visible = lm;
            tOk.Visible = tWarn.Visible = tErr.Visible = !lm;
            listHead.Invalidate();
        }

        /// <summary>
        /// Noch keine Gruppen: aus Namen wie "Sync PC <-> NAS (Dokumente)" Gruppe "NAS" und Name "Dokumente" bilden.
        /// Sonst erklären, wo man die Gruppe festlegt.
        /// </summary>
        void MakeGroups()
        {
            const string how = "Die Gruppe legst du je Aufgabe in „Aufgabe bearbeiten“ fest (Feld „Gruppe“ neben dem Namen, z. B. „NAS“).";
            List<SyncJob> cand = new List<SyncJob>();
            foreach (SyncJob j in Main.Jobs)
            {
                string g, n;
                if (string.IsNullOrWhiteSpace(j.Group) && Easy2SyncImport.SuggestGroup(j.Name, out g, out n)) cand.Add(j);
            }
            if (cand.Count == 0)
            {
                MessageBox.Show("Es gibt noch keine Gruppen.\n\n" + how, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string sg, sn;
            Easy2SyncImport.SuggestGroup(cand[0].Name, out sg, out sn);
            List<string> gs = new List<string>();
            foreach (SyncJob j in cand)
            {
                string g, n;
                Easy2SyncImport.SuggestGroup(j.Name, out g, out n);
                if (!gs.Contains(g, StringComparer.OrdinalIgnoreCase)) gs.Add(g);
            }
            DialogResult r = MessageBox.Show(cand.Count + " von " + Main.Jobs.Count + " Aufgaben lassen sich aus ihrem Namen in Gruppen ordnen (" +
                string.Join(", ", gs.Take(6)) + (gs.Count > 6 ? ", …" : "") + "), z. B.:\n\n" +
                "„" + cand[0].Name + "“\n→ Gruppe „" + sg + "“, Name „" + sn + "“\n\n" +
                "Ja = Gruppe und kurzen Namen übernehmen\nNein = nur die Gruppe, Namen bleiben\n\n" + how,
                "Sync Pilot", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            foreach (SyncJob j in cand)
            {
                string g, n;
                Easy2SyncImport.SuggestGroup(j.Name, out g, out n);
                j.Group = g;
                // kurzer Name nur, wenn es ihn in dieser Gruppe noch nicht gibt
                if (r == DialogResult.Yes && !Main.Jobs.Any(o => o != j && string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((o.Group ?? "").Trim(), g, StringComparison.OrdinalIgnoreCase)))
                    j.Name = n;
            }
            AppSettings.Current.TaskGrouped = true;
            AppSettings.Current.Save();
            Main.SaveJobs();
            Rebuild();
        }

        /// <summary>Untermenü "In Gruppe verschieben": vorhandene Gruppen, ohne Gruppe, neue Gruppe (gilt für alle ausgewählten).</summary>
        ToolStripMenuItem GroupMenu(ModernMenuBuilder mb, SyncJob job)
        {
            string[] ids = DragIds(job);
            string cur = GroupOf(job);
            ToolStripMenuItem sub = mb.Item(ids.Length > 1 ? "In Gruppe verschieben (" + ids.Length + ")" : "In Gruppe verschieben", '\uE8DE', null);
            List<string> groups = Main.Jobs.Where(j => !string.IsNullOrWhiteSpace(j.Group)).Select(j => j.Group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (string g in groups)
            {
                string gg = g;
                GlyphMenuItem it = mb.Item(gg, '\uE8B7', delegate { MoveToGroup(ids, gg); });
                it.Enabled = !busy && WouldMove(ids, gg);
                sub.DropDownItems.Add(it);
            }
            if (groups.Count > 0) sub.DropDownItems.Add(new ToolStripSeparator());
            GlyphMenuItem none = mb.Item(NoGroup, '\0', delegate { MoveToGroup(ids, NoGroup); });
            none.Enabled = !busy && WouldMove(ids, NoGroup);
            sub.DropDownItems.Add(none);
            GlyphMenuItem neu = mb.Item("Neue Gruppe \u2026", Theme.GAdd[0], delegate
            {
                using (GroupDialog d = new GroupDialog("In neue Gruppe verschieben",
                    ids.Length > 1 ? ids.Length + " ausgewählte Aufgaben werden verschoben." : "\u201e" + job.Name + "\u201c wird verschoben. Eine vorhandene Gruppe kannst du auch eintippen.",
                    groups.ToArray()))
                {
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    // gleiche Schreibweise wie eine vorhandene Gruppe verwenden
                    string target = groups.FirstOrDefault(g => string.Equals(g, d.Group, StringComparison.OrdinalIgnoreCase)) ?? d.Group;
                    if (string.Equals(target, NoGroup, StringComparison.OrdinalIgnoreCase)) target = NoGroup;
                    MoveToGroup(ids, target);
                }
            });
            neu.Enabled = !busy;
            sub.DropDownItems.Add(neu);
            mb.StyleDropDown(sub);
            return sub;
        }

        /// <summary>Quelle und Ziel einer Aufgabe tauschen; der gespeicherte Stand wird mitgedreht.</summary>
        void SwapSides(SyncJob job)
        {
            string a = job.SideTitle(true), b = job.SideTitle(false);
            string q = "Quelle und Ziel von \u201e" + job.Name + "\u201c tauschen?\n\n" +
                "Vorher:  " + a + "  \u2192  " + b + "\nNachher:  " + b + "  \u2192  " + a + "\n\n" +
                "Getauscht werden auch Gerät, Bezeichnung, Anmeldung, Überwachung, „niemals löschen“ und die Konfliktregel.";
            if (job.Mode != SyncMode.Abgleichen)
                q += "\n\nAchtung: \u201e" + (job.Mode == SyncMode.Spiegeln ? "Spiegeln" : "Sichern") + "\u201c kopiert danach in die andere Richtung" +
                     (job.Mode == SyncMode.Spiegeln ? " und löscht im neuen Ziel, was es in der neuen Quelle nicht gibt" : "") +
                     ". Bitte vor dem ersten Lauf „Analysieren“.";
            if (MessageBox.Show(q, "Sync Pilot", MessageBoxButtons.YesNo,
                    job.Mode == SyncMode.Spiegeln ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                    job.Mode == SyncMode.Spiegeln ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;
            job.SwapSides();
            try { StateStore.Swap(job.Id); }
            catch { StateStore.Reset(job.Id); } // im Zweifel neu anfangen (erster Lauf löscht nichts)
            string err = Scheduler.Apply(job);
            Main.SaveJobs();
            if (err != null)
                MessageBox.Show("Getauscht, aber der Zeitplan konnte nicht aktualisiert werden:\n\n" + err, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Main.ShowNotice("Quelle und Ziel von \u201e" + job.Name + "\u201c getauscht", true);
            BeginInvoke((MethodInvoker)delegate { Rebuild(); });
        }

        // ---------- Ziehen in andere Gruppen ----------

        public string DropGroup { get; private set; }

        public bool CanDragGroups { get { return Grouped && !busy; } }

        /// <summary>Gezogen werden alle ausgewählten Aufgaben, wenn die gezogene dazugehört, sonst nur sie.</summary>
        public string[] DragIds(SyncJob job)
        {
            if (ListMode && selected.Contains(job.Id) && selected.Count > 1) return selected.ToArray();
            return new[] { job.Id };
        }

        public bool WouldMove(string[] ids, string group)
        {
            return Main.Jobs.Any(j => ids.Contains(j.Id) && !string.Equals(GroupOf(j), group, StringComparison.OrdinalIgnoreCase));
        }

        public void SetDropGroup(string g)
        {
            if (string.Equals(DropGroup, g, StringComparison.OrdinalIgnoreCase)) return;
            DropGroup = g;
            foreach (TaskGroupHeader h in items.OfType<TaskGroupHeader>()) h.Invalidate();
        }

        /// <summary>Beim Ziehen nahe am oberen bzw. unteren Rand der Liste blättern.</summary>
        public void DragScroll(Point screen)
        {
            Point p = list.PointToClient(screen);
            int edge = Theme.P(28), step = Theme.P(24);
            int dy = p.Y < edge ? -step : p.Y > list.ClientSize.Height - edge ? step : 0;
            if (dy == 0) return;
            int y = Math.Max(0, -list.AutoScrollPosition.Y + dy);
            list.AutoScrollPosition = new Point(0, y);
        }

        public void MoveToGroup(string[] ids, string group)
        {
            string target = string.Equals(group, NoGroup, StringComparison.OrdinalIgnoreCase) ? null : group;
            List<string> skipped = new List<string>();
            int moved = 0;
            foreach (SyncJob j in Main.Jobs.Where(x => ids.Contains(x.Id)).ToList())
            {
                if (string.Equals(GroupOf(j), group, StringComparison.OrdinalIgnoreCase)) continue;
                // gleicher Name in der Zielgruppe ist nicht erlaubt
                if (Main.Jobs.Any(o => o != j && string.Equals(o.Name, j.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((o.Group ?? "").Trim(), target ?? "", StringComparison.OrdinalIgnoreCase)))
                { skipped.Add(j.Name); continue; }
                j.Group = target;
                moved++;
            }
            if (moved > 0)
            {
                Main.SaveJobs();
                // aufgeklappt lassen, damit man sieht, wo die Aufgabe gelandet ist
                AppSettings.Current.CollapsedGroups.RemoveAll(x => string.Equals(x, group, StringComparison.OrdinalIgnoreCase));
                AppSettings.Current.TaskGrouped = true;
                AppSettings.Current.Save();
                Main.ShowNotice(moved == 1 ? "In Gruppe \u201e" + group + "\u201c verschoben" : moved + " Aufgaben in Gruppe \u201e" + group + "\u201c verschoben", true);
            }
            if (skipped.Count > 0)
                MessageBox.Show("In der Gruppe \u201e" + group + "\u201c gibt es schon eine Aufgabe mit diesem Namen:\n\n" +
                    string.Join("\n", skipped.Select(n => "• " + n)) + "\n\nBitte vorher umbenennen.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            // erst nach dem Ziehen neu aufbauen (Quelle und Ziel werden dabei ersetzt)
            BeginInvoke((MethodInvoker)delegate { LoadHistory(); Rebuild(); });
        }

        public void SortBy(int col)
        {
            if (SortColumn == col) SortDesc = !SortDesc;
            else { SortColumn = col; SortDesc = false; }
            BeginInvoke((MethodInvoker)delegate { Rebuild(); });
        }

        public void ToggleGroup(string g)
        {
            List<string> c = AppSettings.Current.CollapsedGroups;
            int i = c.FindIndex(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) c.RemoveAt(i); else c.Add(g);
            AppSettings.Current.Save();
            int keep = -list.AutoScrollPosition.Y;
            BeginInvoke((MethodInvoker)delegate
            {
                Rebuild();
                list.AutoScrollPosition = new Point(0, keep);
            });
        }

        public void RunGroup(string g)
        {
            if (busy) return;
            List<SyncJob> js = Main.Jobs.Where(j => GroupOf(j) == g && !j.Disabled).ToList();
            if (js.Count == 0) return;
            RunJobs(js, "Alle " + js.Count + " aktiven Aufgaben der Gruppe \u201e" + g + "\u201c jetzt synchronisieren?");
        }

        /// <summary>Klick auf eine Zeile: auswählen; Strg = hinzufügen/entfernen, Umschalt = Bereich.</summary>
        public void RowClick(TaskRow row, Keys mods)
        {
            List<TaskRow> rows = items.OfType<TaskRow>().ToList();
            if ((mods & Keys.Shift) == Keys.Shift && anchorRow != null && rows.Contains(anchorRow))
            {
                int a = rows.IndexOf(anchorRow), b = rows.IndexOf(row);
                if ((mods & Keys.Control) != Keys.Control) selected.Clear();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) selected.Add(rows[i].Job.Id);
            }
            else if ((mods & Keys.Control) == Keys.Control)
            {
                if (!selected.Remove(row.Job.Id)) selected.Add(row.Job.Id);
                anchorRow = row;
            }
            else
            {
                selected.Clear();
                selected.Add(row.Job.Id);
                anchorRow = row;
            }
            foreach (TaskRow r in rows)
            {
                bool s = selected.Contains(r.Job.Id);
                if (r.Selected != s) { r.Selected = s; r.Invalidate(); }
            }
            UpdateBar();
            PerformLayout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ListMode && !busy)
            {
                if (keyData == (Keys.Control | Keys.A) && !(ActiveControl is TextBoxBase))
                {
                    foreach (TaskRow r in items.OfType<TaskRow>()) { selected.Add(r.Job.Id); r.Selected = true; r.Invalidate(); }
                    UpdateBar(); PerformLayout();
                    return true;
                }
                if (keyData == Keys.Escape && selected.Count > 0)
                {
                    selected.Clear();
                    foreach (TaskRow r in items.OfType<TaskRow>()) { r.Selected = false; r.Invalidate(); }
                    UpdateBar(); PerformLayout();
                    return true;
                }
                if (keyData == Keys.Down || keyData == Keys.Up)
                {
                    List<TaskRow> rows = items.OfType<TaskRow>().ToList();
                    if (rows.Count > 0)
                    {
                        TaskRow cur = rows.FirstOrDefault(r => r.Focused);
                        int i = cur == null ? 0 : rows.IndexOf(cur) + (keyData == Keys.Down ? 1 : -1);
                        i = Math.Max(0, Math.Min(rows.Count - 1, i));
                        rows[i].Focus();
                        list.ScrollControlIntoView(rows[i]);
                        RowClick(rows[i], Keys.None);
                        return true;
                    }
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
            Rebuild(); // bei einer Suche sind auch zugeklappte Gruppen offen
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (title == null || list == null) return;
            int pad = Theme.P(28), W = ClientSize.Width, H = ClientSize.Height;
            title.Location = new Point(pad - Theme.P(3), Theme.P(22));
            subtitle.Location = new Point(pad, title.Bottom + Theme.P(2));
            btnNew.Location = new Point(W - pad - btnNew.Width, Theme.P(30));
            btnAll.Location = new Point(btnNew.Left - Theme.P(8) - btnAll.Width, Theme.P(30));

            int gap = Theme.P(12), ty = subtitle.Bottom + Theme.P(20);
            int y = ty;
            if (tOk.Visible)
            {
                int tw = (W - 2 * pad - 2 * gap) / 3;
                tOk.SetBounds(pad, ty, tw, tOk.Height);
                tWarn.SetBounds(pad + tw + gap, ty, tw, tWarn.Height);
                tErr.SetBounds(pad + 2 * (tw + gap), ty, W - 2 * pad - 2 * (tw + gap), tErr.Height);
                y = tOk.Bottom + Theme.P(16);
            }

            // Werkzeugleiste: links Filter, rechts Auswahl, Gruppiert, Kacheln/Liste
            int x = pad;
            foreach (Pill p in new Pill[] { pAll, pSched, pErr, pOld })
            {
                p.Location = new Point(x, y);
                x += p.Width + Theme.P(6);
            }
            int rx = W - pad;
            btnList.Location = new Point(rx - btnList.Width, y);
            btnTiles.Location = new Point(btnList.Left - Theme.P(4) - btnTiles.Width, y);
            rx = btnTiles.Left - Theme.P(10);
            if (btnGroup.Visible) { btnGroup.Location = new Point(rx - btnGroup.Width, y); rx = btnGroup.Left - Theme.P(10); }
            if (btnSel.Visible)
            {
                btnSel.Location = new Point(rx - btnSel.Width, y);
                selInfo.Location = new Point(btnSel.Left - Theme.P(8) - selInfo.Width, y + (btnSel.Height - selInfo.Height) / 2);
            }
            y += pAll.Height + Theme.P(12);

            if (strip.Visible)
            {
                strip.SetBounds(pad, y, W - 2 * pad, strip.Height);
                y = strip.Bottom + Theme.P(12);
            }
            if (listHead.Visible)
            {
                listHead.SetBounds(pad, y, W - 2 * pad, listHead.Height);
                y = listHead.Bottom;
            }
            list.SetBounds(pad - 1, y, W - 2 * pad + 2, Math.Max(10, H - y - Theme.P(16)));
            empty.SetBounds(pad, y + Theme.P(40), W - 2 * pad, Theme.P(60));
            empty.Visible = items.Count == 0;
            empty.Text = Main.Jobs.Count == 0 ? "Noch keine Aufgaben vorhanden.\nLege mit „Neue Aufgabe“ das erste Ordnerpaar an."
                       : "Keine Aufgabe passt zu Filter bzw. Suche.";
            if (empty.Visible) empty.BringToFront();
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
            bool lm = ListMode;
            int total = 0;
            foreach (Control c in items) total += c.Height + Gap(c, lm);
            int w = list.ClientSize.Width;
            if (total > list.Height) w = list.Width - SystemInformation.VerticalScrollBarWidth - Theme.P(2);
            int y = list.AutoScrollPosition.Y;
            list.SuspendLayout();
            foreach (Control c in items)
            {
                c.SetBounds(lm ? 0 : 1, y, Math.Max(Theme.P(200), lm ? w : w - 2), c.Height);
                y += c.Height + Gap(c, lm);
            }
            list.AutoScrollMinSize = new Size(0, total);
            list.ResumeLayout();
            if (keepY > 0) list.AutoScrollPosition = new Point(0, keepY);
            RestoreScroll();
        }

        /// <summary>Abstand nach einem Eintrag: Kacheln mit Luft, Listenzeilen direkt untereinander.</summary>
        static int Gap(Control c, bool lm)
        {
            if (lm) return 0;
            return c is TaskGroupHeader ? Theme.P(4) : Theme.P(6);
        }

        // ---------- Menue einer Aufgabe ----------

        // at: Position im Koordinatensystem von anchor (Rechtsklick: an der Maus)
        public void ShowJobMenu(SyncJob job, HistoryEntry last, Control anchor, Point at)
        {
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip m = mb.CreateMenu();
            m.Items.Add(mb.Item("Bearbeiten", '\uE70F', delegate { Main.ShowEdit(job); }));
            m.Items.Add(GroupMenu(mb, job));
            GlyphMenuItem swap = mb.Item("Quelle und Ziel tauschen \u2026", '\uE8AB', delegate { SwapSides(job); });
            swap.Enabled = !busy && !Runner.IsRunning(job.Id);
            m.Items.Add(swap);
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
                using (FolderPicker d = new FolderPicker())
                {
                    d.Description = "Ordner f\u00fcr die Verkn\u00fcpfung zu \u201e" + job.Name + "\u201c";
                    d.ShowNewFolderButton = true;
                    if (d.ShowDialog(this) == DialogResult.OK) Shortcuts.Create(this, job, d.SelectedPath);
                }
            }));
            mb.StyleDropDown(link);
            m.Items.Add(link);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(mb.Item(job.SideTitle(true) + " \u00f6ffnen", MenuHelper.Folder, delegate { Ui.Explore(Paths.TryResolve(job.FolderA), false); }));
            m.Items.Add(mb.Item(job.SideTitle(false) + " \u00f6ffnen", MenuHelper.Folder, delegate { Ui.Explore(Paths.TryResolve(job.FolderB), false); }));
            GlyphMenuItem log = mb.Item("Letztes Protokoll \u00f6ffnen", MenuHelper.Document, delegate
            {
                if (last != null) Main.ShowLog(last, false);
            });
            log.Enabled = last != null && !string.IsNullOrEmpty(last.LogFile) && File.Exists(last.LogFile);
            m.Items.Add(log);
            m.Items.Add(mb.Item("Gespeicherten Stand zur\u00fccksetzen \u2026", '\uE72C', delegate
            {
                DialogResult r = MessageBox.Show("Gespeicherten Stand von \u201e" + job.Name + "\u201c zur\u00fccksetzen?\n\n" +
                    "Sync Pilot vergisst, welche Dateien beim letzten Lauf auf beiden Seiten waren. Der n\u00e4chste Lauf ist dann wie ein " +
                    "erster Abgleich: Nur auf einer Seite vorhandene Dateien werden kopiert, nichts wird gel\u00f6scht. Dateien selbst bleiben unver\u00e4ndert.",
                    "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                if (Runner.IsRunning(job.Id))
                {
                    MessageBox.Show("Die Aufgabe l\u00e4uft gerade. Bitte warten, bis sie fertig ist.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                StateStore.Reset(job.Id);
                Main.ShowNotice("Gespeicherter Stand von \u201e" + job.Name + "\u201c zur\u00fcckgesetzt", true);
            }));
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
            List<SyncJob> all = Main.Jobs.Where(x => !x.Disabled).ToList(); // deaktivierte nicht
            if (all.Count == 0) return;
            RunJobs(all, "Alle " + all.Count + " aktiven Aufgaben jetzt synchronisieren?", true);
        }

        /// <summary>Mehrere Aufgaben nacheinander synchronisieren (alle, eine Gruppe oder die ausgewählten).</summary>
        void RunJobs(List<SyncJob> jobs, string question)
        {
            RunJobs(jobs, question, false);
        }

        /// <summary>Play-Knopf in der Liste: gleich synchronisieren, ohne Vorschau und ohne Rückfrage (wie geplante Läufe).</summary>
        public void QuickRun(SyncJob job)
        {
            if (busy || job == null || job.Disabled) return;
            RunJobs(new List<SyncJob> { job }, null, false);
        }

        void RunJobs(List<SyncJob> jobs, string question, bool isAll)
        {
            if (busy || jobs.Count == 0) return;
            if (question != null)
            {
                DialogResult r = MessageBox.Show(question + "\n\n" +
                    "Konflikte werden dabei übersprungen. Bei auffällig vielen Löschungen wird eine Aufgabe zur Sicherheit nicht ausgeführt.",
                    "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            busy = true;
            cancel = false;
            liveStatus = "Starte …";
            strip.Visible = true;
            btnCancel.Enabled = true;
            btnCancel.Visible = true;
            btnAll.Enabled = false;
            btnNew.Enabled = false;
            foreach (Control c in items) { if (c is TaskGroupHeader) ((TaskGroupHeader)c).CanRun = false; else c.Enabled = false; c.Invalidate(); }
            UpdateBar();
            PerformLayout();

            Thread t = new Thread(delegate ()
            {
                int ok = 0, bad = 0, n = 0;
                HistoryEntry lastRun = null;
                foreach (SyncJob j in jobs)
                {
                    if (cancel) break;
                    n++;
                    string prefix = jobs.Count == 1 ? j.Name + ": " : "(" + n + "/" + jobs.Count + ") " + j.Name + ": ";
                    HistoryEntry h;
                    try
                    {
                        h = Runner.Run(j, false, delegate { return cancel; },
                            delegate (string s) { liveStatus = prefix + s; });
                    }
                    catch (Exception ex) { h = Runner.Crash(j, false, ex); }
                    if (h.IsOk) ok++; else bad++;
                    lastRun = h;
                }
                bool wasCancelled = cancel;
                Ui_(delegate
                {
                    busy = false;
                    btnCancel.Visible = false;
                    btnAll.Enabled = true;
                    btnNew.Enabled = true;
                    LoadHistory();
                    Rebuild();
                    if (jobs.Count == 1 && lastRun != null)
                        // einzelne Aufgabe (Play-Knopf): Ergebnis direkt nennen
                        stripText.Text = jobs[0].Name + ": " + (wasCancelled ? "Abgebrochen" : "Fertig – " + (lastRun.Result ?? "OK")) +
                            (lastRun.Conflicts > 0 ? ". Konflikte prüfen: „Analysieren“" : !lastRun.IsOk ? ". Details im Verlauf" : "");
                    else
                        stripText.Text = (wasCancelled ? "Abgebrochen. " : "Fertig. ") + ok + " ohne Fehler, " + bad +
                                         " mit Fehlern oder Hinweisen. Details im Verlauf.";
                    Main.ViewIdle();
                    // Einstellung "Nach „Alle synchronisieren“": herunterfahren bzw. schlafen legen (mit Countdown)
                    if (!wasCancelled && isAll) PowerAfter.Offer(FindForm(), AppSettings.Current.AfterAll);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
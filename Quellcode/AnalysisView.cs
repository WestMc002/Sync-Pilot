// Sync Pilot – Analyse, Vorschau und Synchronisation einer Aufgabe
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
    public class AnalysisView : BaseView
    {
        readonly SyncJob job;
        readonly bool autoSync;
        readonly FlatButton btnBack, btnRe, btnSync, btnClose, btnLog, btnCancel;
        readonly Label title, status, sum1, sum2;
        readonly Card summary, listCard, errCard;
        readonly Label errTitle, errText;
        readonly FlatButton errRetry;
        readonly Panel pillBar, footer;
        readonly Pill[] pills;
        readonly ThemedListView list;
        readonly ProgressBar progress;
        readonly System.Windows.Forms.Timer timer;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem miToB, miToA, miDelA, miDelB, miSkip, miReset, miShowA, miShowB, miOpenA, miOpenB, miCompare;

        Analysis an;
        string error;
        List<PlanItem> view = new List<PlanItem>();
        int filterIndex;
        HistoryEntry lastRun;
        string resultMsg;
        /// <summary>Nach dem Synchronisieren: Liste zeigt, was gemacht wurde (bis "Neu analysieren").</summary>
        bool showingResult;
        bool resultBad;

        volatile bool busy, cancel;
        volatile string liveStatus = "";
        volatile int progI, progN;

        public override bool Busy { get { return busy; } }
        public override void RequestCancel() { cancel = true; }

        public AnalysisView(MainForm main, SyncJob job, bool autoSync) : base(main)
        {
            this.job = job;
            this.autoSync = autoSync;

            btnBack = new FlatButton("", Theme.GBack, BtnKind.Subtle);
            btnBack.AccessibleName = "Zurück zu den Aufgaben";
            btnBack.Click += delegate { Main.ShowTasks(); };
            title = Ui.Label(job.Name, Theme.H1, Theme.Text, Theme.Bg);
            status = Ui.WrapLabel("", Theme.Body, Theme.TextDim, Theme.Bg);
            status.AutoEllipsis = true;
            btnRe = new FlatButton("Neu analysieren", Theme.GRefresh, BtnKind.Secondary);
            btnRe.Click += delegate { resultMsg = null; StartAnalysis(false); };

            summary = new Card();
            summary.Height = Theme.P(96);
            summary.Paint += PaintSummary;

            pillBar = new Panel();
            pillBar.BackColor = Theme.Bg;
            pillBar.Height = Theme.P(34);
            string[] names = { "Alle", job.Sides("→ Nach B"), job.Sides("← Nach A"), "Löschen", "Konflikte & Übersprungen" };
            pills = new Pill[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                Pill p = new Pill(names[i]);
                int idx = i;
                p.CheckedChanged += delegate { if (p.Checked) { filterIndex = idx; ApplyFilter(); } };
                pills[i] = p;
                pillBar.Controls.Add(p);
            }
            listCard = new Card();
            list = new ThemedListView();
            list.VirtualMode = true;
            list.Columns.Add("Aktion", ActionColumnWidth());
            list.Columns.Add("Datei", Theme.P(380));
            list.Columns.Add("Größe", Theme.P(90), HorizontalAlignment.Right);
            // so breit wie die Überschrift mit Bezeichnung (z. B. "Geändert Sicherung-NAS"), höchstens 240
            list.Columns.Add(job.Sides("Geändert A"), HeaderWidth(job.Sides("Geändert A"), Theme.P(130)));
            list.Columns.Add(job.Sides("Geändert B"), HeaderWidth(job.Sides("Geändert B"), Theme.P(130)));
            list.Columns.Add("Grund", Theme.P(180));
            list.FillColumn = 1;   // Datei bekommt die restliche Breite, keine waagrechte Bildlaufleiste
            list.FillMin = 200;
            list.RetrieveVirtualItem += OnRetrieve;
            list.CustomDraw = DrawCell;
            list.DoubleClick += delegate { ShowSelected(); };
            listCard.Controls.Add(list);
            listCard.Resize += delegate { list.SetBounds(1, Theme.P(6), listCard.Width - 2, listCard.Height - Theme.P(12)); };

            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            menu = mb.CreateMenu();
            miToB = mb.Item(job.Sides("Nach B kopieren"), '\uE72A', delegate { Choose(ActionKind.NachB); });
            miToA = mb.Item(job.Sides("Nach A kopieren"), '\uE72B', delegate { Choose(ActionKind.NachA); });
            miDelA = mb.Item(job.Sides("In A l\u00f6schen"), '\uE74D', delegate { Choose(ActionKind.LoeschenA); });
            miDelB = mb.Item(job.Sides("In B l\u00f6schen"), '\uE74D', delegate { Choose(ActionKind.LoeschenB); });
            miSkip = mb.Item("\u00dcberspringen", '\uE711', delegate { Choose(ActionKind.Keine); });
            miReset = mb.Item("Vorschlag wiederherstellen", '\uE7A7', delegate { RestoreProposal(); });
            // Datei mit dem zugehörigen Programm öffnen (z. B. Bild anschauen, bevor man entscheidet)
            miOpenA = mb.Item("Öffnen aus " + job.SideTitle(true), '\uE8E5', delegate { OpenFile(true); });
            miOpenB = mb.Item("Öffnen aus " + job.SideTitle(false), '\uE8E5', delegate { OpenFile(false); });
            miCompare = mb.Item("Inhalt vergleichen \u2026", '\uE8A7', delegate { CompareContent(); });
            miShowA = mb.Item("In " + job.SideTitle(true) + " anzeigen", MenuHelper.Folder, delegate { Reveal(true); });
            miShowB = mb.Item("In " + job.SideTitle(false) + " anzeigen", MenuHelper.Folder, delegate { Reveal(false); });
            menu.Items.AddRange(new ToolStripItem[] { miToB, miToA, miDelA, miDelB, miSkip, miReset, new ToolStripSeparator(), miCompare, miOpenA, miOpenB, miShowA, miShowB });
            menu.Opening += MenuOpening;
            list.ContextMenuStrip = menu;
            pills[0].Checked = true;

            footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.P(72);
            footer.BackColor = Theme.Surface2;
            footer.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };
            sum1 = Ui.WrapLabel("", Theme.Bold, Theme.Text, Theme.Surface2);
            sum2 = Ui.WrapLabel("", Theme.Small, Theme.TextDim, Theme.Surface2);
            sum1.AutoEllipsis = true;
            sum2.AutoEllipsis = true;
            btnSync = new FlatButton("Jetzt synchronisieren", Theme.GSync, BtnKind.Primary);
            btnSync.Click += delegate { StartSync(true); };
            btnClose = new FlatButton("Zurück", null, BtnKind.Secondary);
            btnClose.Click += delegate { Main.ShowTasks(); };
            btnLog = new FlatButton("Protokoll öffnen", Theme.GDoc, BtnKind.Subtle);
            btnLog.Visible = false;
            btnLog.Click += delegate { if (lastRun != null) Main.ShowLog(lastRun, false); };
            btnCancel = new FlatButton("Abbrechen", Theme.GCancel, BtnKind.Secondary);
            btnCancel.Click += delegate { cancel = true; btnCancel.Enabled = false; liveStatus = "Wird abgebrochen …"; };
            progress = new ProgressBar();
            progress.Height = Theme.P(6);
            progress.Maximum = 1000;
            footer.Controls.AddRange(new Control[] { sum1, sum2, btnSync, btnClose, btnLog, btnCancel, progress });
            footer.Resize += delegate { LayoutFooter(); };

            Controls.Add(footer);
            // Statt leerer Liste mit "0": klarer Hinweis, dass nicht analysiert werden konnte
            errCard = new Card();
            errCard.Visible = false;
            errTitle = Ui.WrapLabel("", Theme.H2, Theme.Text, Theme.Surface);
            errText = Ui.WrapLabel("", Theme.Body, Theme.TextDim, Theme.Surface);
            errRetry = new FlatButton("Erneut versuchen", "\uE72C", BtnKind.Secondary);
            errRetry.Click += delegate { resultMsg = null; StartAnalysis(false); };
            errCard.Controls.AddRange(new Control[] { errTitle, errText, errRetry });
            errCard.Paint += delegate (object s, PaintEventArgs e)
            {
                Theme.DrawGlyph(e.Graphics, Theme.GWarn, 20f, new Rectangle(Theme.P(28), Theme.P(26), Theme.P(32), Theme.P(32)),
                    Theme.Err, TextFormatFlags.HorizontalCenter);
            };
            errCard.Resize += delegate { LayoutErr(); };
            Controls.AddRange(new Control[] { btnBack, title, status, btnRe, summary, pillBar, listCard, errCard });

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 120;
            timer.Tick += OnTick;
            timer.Start();

            // Analyse starten, sobald die Ansicht ein Fenster-Handle hat (auch wenn es schon besteht)
            if (IsHandleCreated) BeginInvoke(new Action(Begin));
            else HandleCreated += delegate { Begin(); };
            UpdateUi();
        }

        bool begun;

        void Begin()
        {
            if (begun || IsDisposed) return;
            begun = true;
            // Screenshots: Beispiel-Analyse statt echter Ordner
            if (Demo.Active) AnalysisDone(Demo.CreateAnalysis(job), null, false);
            else StartAnalysis(autoSync);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); }
            base.Dispose(disposing);
        }

        // ---------- Layout ----------

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (footer == null || listCard == null) return;
            int pad = Theme.P(28), W = ClientSize.Width, H = ClientSize.Height - footer.Height;
            btnBack.Location = new Point(pad - Theme.P(6), Theme.P(26));
            title.Location = new Point(btnBack.Right + Theme.P(8), Theme.P(18));
            btnRe.Location = new Point(W - pad - btnRe.Width, Theme.P(26));
            status.SetBounds(btnBack.Right + Theme.P(10), title.Bottom, Math.Max(10, btnRe.Left - btnBack.Right - Theme.P(20)), Theme.P(22));

            summary.SetBounds(pad, status.Bottom + Theme.P(14), W - 2 * pad, summary.Height);
            pillBar.SetBounds(pad, summary.Bottom + Theme.P(14), W - 2 * pad, pillBar.Height);
            int x = 0;
            foreach (Pill p in pills)
            {
                p.Location = new Point(x, 1);
                x += p.Width + Theme.P(8);
            }
            int ly = pillBar.Bottom + Theme.P(12);
            listCard.SetBounds(pad, ly, W - 2 * pad, Math.Max(Theme.P(80), H - ly - Theme.P(16)));
            if (errCard != null) { errCard.SetBounds(pad, pillBar.Top, W - 2 * pad, errCard.Height); LayoutErr(); }
        }

        void LayoutFooter()
        {
            int pad = Theme.P(28), W = footer.Width, H = footer.Height;
            int by = (H - btnSync.Height) / 2;
            int x = W - pad - btnSync.Width;
            btnSync.Location = new Point(x, by);
            btnCancel.Location = new Point(W - pad - btnCancel.Width, by);
            x -= Theme.P(8) + btnClose.Width;
            btnClose.Location = new Point(x, by);
            x -= Theme.P(8) + btnLog.Width;
            btnLog.Location = new Point(x, by);
            int textW = Math.Max(10, x - pad - Theme.P(16));
            sum1.SetBounds(pad, Theme.P(14), textW, Theme.P(22));
            sum2.SetBounds(pad, Theme.P(38), textW, Theme.P(20));
            progress.SetBounds(pad, Theme.P(42), Math.Max(10, btnCancel.Left - pad - Theme.P(24)), progress.Height);
        }

        void PaintSummary(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.P(20), W = summary.Width, H = summary.Height;
            // Mitte so breit wie der Modus-Text (mit Bezeichnungen z. B. "Sichern Arbeits-PC → Sicherung-NAS"),
            // höchstens ein Drittel; die Pfade links und rechts werden dafür eher gekürzt
            int mid = Math.Max(Theme.P(150), Math.Min((W - 2 * pad) / 3,
                TextRenderer.MeasureText(g, job.ModeText, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix).Width + Theme.P(24)));
            int colW = (W - 2 * pad - mid) / 2;
            DrawSide(g, job.SideTitle(true).ToUpper(), Devices.Glyph(job.DeviceA, job.FolderA), job.FolderA, an == null ? null : an.FilesA.ToString("N0") + " Dateien · " + Fmt.Size(an.BytesA),
                new Rectangle(pad, 0, colW, H), TextFormatFlags.Left);
            DrawSide(g, job.SideTitle(false).ToUpper(), Devices.Glyph(job.DeviceB, job.FolderB), job.FolderB, an == null ? null : an.FilesB.ToString("N0") + " Dateien · " + Fmt.Size(an.BytesB),
                new Rectangle(W - pad - colW, 0, colW, H), TextFormatFlags.Right);
            Rectangle c = new Rectangle(pad + colW, Theme.P(14), mid, Theme.P(36));
            Theme.DrawGlyph(g, job.Mode == SyncMode.Abgleichen ? Theme.GSwitch : Theme.GForward, 18f, c, Theme.Accent, TextFormatFlags.HorizontalCenter);
            // passt es trotzdem nicht (schmales Fenster), auf zwei Zeilen umbrechen
            TextRenderer.DrawText(g, job.ModeText, Theme.Small, new Rectangle(pad + colW, Theme.P(52), mid, Theme.P(40)), Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        void DrawSide(Graphics g, string label, char glyph, string path, string stats, Rectangle r, TextFormatFlags align)
        {
            int gs = Theme.P(16), gap = Theme.P(22);
            bool right = (align & TextFormatFlags.Right) != 0;
            Image img = MenuHelper.Glyph(glyph, Theme.Text, gs);
            if (img != null)
            {
                g.DrawImage(img, right ? r.Right - gs : r.X, Theme.P(16), gs, gs);
                img.Dispose();
            }
            Rectangle lr = right ? new Rectangle(r.X, Theme.P(14), r.Width - gap, Theme.P(20)) : new Rectangle(r.X + gap, Theme.P(14), r.Width - gap, Theme.P(20));
            Theme.DrawText(g, label, Theme.Small, lr, Theme.TextDim, align);
            TextRenderer.DrawText(g, path, Theme.Mono, new Rectangle(r.X, Theme.P(36), r.Width, Theme.P(22)), Theme.Text,
                align | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            if (stats != null)
                Theme.DrawText(g, stats, Theme.Small, new Rectangle(r.X, Theme.P(60), r.Width, Theme.P(20)), Theme.TextDim, align);
        }

        // ---------- Liste ----------

        void OnRetrieve(object sender, RetrieveVirtualItemEventArgs e)
        {
            PlanItem it = e.ItemIndex < view.Count ? view[e.ItemIndex] : null;
            if (it == null) { e.Item = new ListViewItem(new string[] { "", "", "", "", "", "" }); return; }
            string size = it.IsDir ? "Ordner" : Fmt.Size((it.A ?? it.B).Size);
            e.Item = new ListViewItem(new string[]
            {
                job.Sides(Engine.ActionText(it.Chosen, it.IsDir)),
                it.IsDir ? it.Rel + "\\" : it.Rel,
                size,
                Time(it.A),
                Time(it.B),
                showingResult && it.Done ? "\u2713 Erledigt"
                : showingResult && it.Failed ? "Fehler: " + (it.Error ?? "unbekannt")
                : showingResult && (it.IsCopy || it.IsDelete || it.IsRename) ? "Nicht ausgeführt (abgebrochen)"
                : it.Chosen != it.Proposed ? "Geändert von dir (Vorschlag: " + job.Sides(Engine.ActionText(it.Proposed, it.IsDir)) + ")" : job.Sides(it.Reason)
            });
        }

        static int HeaderWidth(string text, int min)
        {
            int w = TextRenderer.MeasureText(text, Theme.Bold, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + Theme.P(28);
            return Math.Min(Theme.P(240), Math.Max(min, w));
        }

                static string Time(FileEntry e)
        {
            if (e == null) return "—";
            if (e.IsDir) return "";
            return e.Mtime.ToLocalTime().ToString("dd.MM.yy HH:mm");
        }

        /// <summary>
        /// Spalte "Aktion" so breit wie die längste mögliche Plakette (mit den Bezeichnungen der Seiten,
        /// z. B. "→ Nach Webserver"), damit sie nicht abgeschnitten wird.
        /// </summary>
        int ActionColumnWidth()
        {
            int w = Theme.P(150);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                foreach (ActionKind k in Enum.GetValues(typeof(ActionKind)))
                    foreach (bool dir in new[] { false, true })
                        w = Math.Max(w, Theme.ChipWidth(g, job.Sides(Engine.ActionText(k, dir)), Theme.Small) + Theme.P(24));
            return Math.Min(w, Theme.P(320));
        }

        static void ChipColors(ActionKind k, out Color fg, out Color bg)
        {
            switch (k)
            {
                case ActionKind.NachB: fg = Theme.AccentSoftText; bg = Theme.AccentSoft; break;
                case ActionKind.NachA: fg = Theme.Purple; bg = Theme.PurpleBg; break;
                case ActionKind.LoeschenA:
                case ActionKind.LoeschenB: fg = Theme.Err; bg = Theme.ErrBg; break;
                case ActionKind.Konflikt: fg = Theme.Warn; bg = Theme.WarnBg; break;
                case ActionKind.UmbenennenA:
                case ActionKind.UmbenennenB: fg = Theme.Ok; bg = Theme.OkBg; break;
                default: fg = Theme.Neutral; bg = Theme.NeutralBg; break;
            }
        }

        bool DrawCell(DrawListViewSubItemEventArgs e, Color bg)
        {
            if (e.ItemIndex >= view.Count) return true;
            PlanItem it = view[e.ItemIndex];
            if (e.ColumnIndex == 0)
            {
                Color fg, cbg;
                ChipColors(it.Chosen, out fg, out cbg);
                // nie über die Spalte hinaus malen (sonst je nach Maus mal ganz, mal abgeschnitten); zu lang: mit "…" kürzen
                string t = e.SubItem.Text;
                int max = e.Bounds.Width - Theme.P(16);
                while (t.Length > 4 && Theme.ChipWidth(e.Graphics, t, Theme.Small) > max) t = t.Substring(0, t.Length - 2).TrimEnd() + "…";
                Region clip = e.Graphics.Clip;
                e.Graphics.SetClip(e.Bounds);
                Theme.Chip(e.Graphics, t, Theme.Small, e.Bounds.X + Theme.P(10), e.Bounds.Y + e.Bounds.Height / 2, fg, cbg);
                e.Graphics.Clip = clip;
                return true;
            }
            if (e.ColumnIndex == 1)
            {
                Rectangle r = new Rectangle(e.Bounds.X + Theme.P(12), e.Bounds.Y, e.Bounds.Width - Theme.P(20), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Theme.Body, r, Theme.Text,
                    TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
                return true;
            }
            if (e.ColumnIndex == 5 && it.Chosen != it.Proposed)
            {
                Rectangle r = new Rectangle(e.Bounds.X + Theme.P(12), e.Bounds.Y, e.Bounds.Width - Theme.P(20), e.Bounds.Height);
                Theme.DrawText(e.Graphics, e.SubItem.Text, Theme.Body, r, Theme.Accent, TextFormatFlags.Left);
                return true;
            }
            return false;
        }

        void ApplyFilter()
        {
            if (an == null) view = new List<PlanItem>();
            else
            {
                switch (filterIndex)
                {
                    case 1: view = an.Actions.Where(x => x.Chosen == ActionKind.NachB).ToList(); break;
                    case 2: view = an.Actions.Where(x => x.Chosen == ActionKind.NachA).ToList(); break;
                    case 3: view = an.Actions.Where(x => x.IsDelete).ToList(); break;
                    case 4: view = an.Actions.Where(x => x.Chosen == ActionKind.Konflikt || x.Chosen == ActionKind.Keine).ToList(); break;
                    default: view = new List<PlanItem>(an.Actions); break;
                }
            }
            list.SelectedIndices.Clear();
            // Fehler in WinForms: Ist die Liste nach unten geblättert und wird kürzer (z. B. Filter "Konflikte"),
            // will VirtualListSize die oberste Zeile behalten und stürzt dabei ab. Deshalb erst ganz nach oben
            // und auf 0, dann die neue Länge. Die Länge selbst ist vor dem Fehler schon gesetzt.
            try
            {
                if (list.VirtualListSize > 0 && list.IsHandleCreated) list.EnsureVisible(0);
                list.VirtualListSize = 0;
                list.VirtualListSize = view.Count;
            }
            catch (NullReferenceException) { }
            catch (ArgumentOutOfRangeException) { }
            list.Stretch();
            list.Invalidate();
        }

        List<PlanItem> Selected()
        {
            List<PlanItem> r = new List<PlanItem>();
            foreach (int i in list.SelectedIndices) if (i < view.Count) r.Add(view[i]);
            return r;
        }

        void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            List<PlanItem> sel = Selected();
            if (sel.Count == 0 || busy || showingResult) { e.Cancel = true; return; }
            // nur Schreibweise verschieden: "Nach A/B" übernimmt die Schreibweise statt zu kopieren
            bool caseOnly = sel.All(x => x.CaseOnly);
            miToB.Text = job.Sides(caseOnly ? "Schreibweise von A nach B \u00fcbernehmen" : "Nach B kopieren");
            miToA.Text = job.Sides(caseOnly ? "Schreibweise von B nach A \u00fcbernehmen" : "Nach A kopieren");
            miToB.Enabled = sel.Any(x => x.CanChoose(ActionKind.NachB));
            miToA.Enabled = sel.Any(x => x.CanChoose(ActionKind.NachA));
            miDelA.Enabled = sel.Any(x => x.CanChoose(ActionKind.LoeschenA));
            miDelB.Enabled = sel.Any(x => x.CanChoose(ActionKind.LoeschenB));
            miSkip.Enabled = true;
            miReset.Enabled = sel.Any(x => x.Chosen != x.Proposed);
            miShowA.Enabled = sel.Count == 1 && sel[0].A != null && !FtpUrl.IsRemote(job.FolderA);
            miOpenA.Enabled = sel.Count == 1 && sel[0].A != null && !sel[0].IsDir && !FtpUrl.IsRemote(job.FolderA);
            miOpenB.Enabled = sel.Count == 1 && sel[0].B != null && !sel[0].IsDir && !FtpUrl.IsRemote(job.FolderB);
            miCompare.Enabled = miOpenA.Enabled && miOpenB.Enabled;
            miShowB.Enabled = sel.Count == 1 && sel[0].B != null && !FtpUrl.IsRemote(job.FolderB);
        }

        void Choose(ActionKind k)
        {
            foreach (PlanItem it in Selected())
                if (it.CanChoose(k)) it.Choose(k);
            list.Invalidate();
            UpdateUi();
        }

        void RestoreProposal()
        {
            foreach (PlanItem it in Selected()) it.Choose(it.Proposed);
            list.Invalidate();
            UpdateUi();
        }

        void Reveal(bool sideA)
        {
            List<PlanItem> sel = Selected();
            if (sel.Count != 1) return;
            Ui.Explore(Path.Combine(Paths.TryResolve(sideA ? job.FolderA : job.FolderB), sel[0].Rel), true);
        }

        string SidePath(PlanItem it, bool sideA)
        {
            FileEntry e = sideA ? it.A : it.B;
            string rel = e != null && !string.IsNullOrEmpty(e.Rel) ? e.Rel : it.Rel;
            return Path.Combine(Paths.TryResolve(sideA ? job.FolderA : job.FolderB), rel);
        }

        /// <summary>
        /// Beide Fassungen Byte für Byte vergleichen (im Hintergrund). Bei Textdateien zusätzlich die ersten
        /// abweichenden Zeilen zeigen; ist WinMerge installiert, kann man dort weitervergleichen.
        /// </summary>
        void CompareContent()
        {
            List<PlanItem> sel = Selected();
            if (sel.Count != 1) return;
            PlanItem it = sel[0];
            FileCompare.Show(this, SidePath(it, true), SidePath(it, false), job.SideTitle(true), job.SideTitle(false), Path.GetFileName(it.Rel));
        }

        void OpenFile(bool sideA)
        {
            List<PlanItem> sel = Selected();
            if (sel.Count != 1) return;
            PlanItem it = sel[0];
            FileEntry e = sideA ? it.A : it.B;
            string rel = e != null && !string.IsNullOrEmpty(e.Rel) ? e.Rel : it.Rel;
            string p = Path.Combine(Paths.TryResolve(sideA ? job.FolderA : job.FolderB), rel);
            if (!File.Exists(p))
            {
                MessageBox.Show("Die Datei gibt es dort nicht mehr:\n" + p, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Ui.Open(p);
        }

        void ShowSelected()
        {
            List<PlanItem> sel = Selected();
            if (sel.Count != 1) return;
            bool sideA = sel[0].A != null && !FtpUrl.IsRemote(job.FolderA);
            if (!sideA && (sel[0].B == null || FtpUrl.IsRemote(job.FolderB))) return;
            Reveal(sideA);
        }

        /// <summary>Alle Löschungen dieser Analyse landen im Papierkorb (nicht bei FTP-Seiten).</summary>
        bool RecycleAll()
        {
            if (!job.UseRecycleBin || an == null) return false;
            foreach (PlanItem it in an.Actions)
            {
                if (it.Chosen == ActionKind.LoeschenA && FtpUrl.IsRemote(job.FolderA)) return false;
                if (it.Chosen == ActionKind.LoeschenB && FtpUrl.IsRemote(job.FolderB)) return false;
            }
            return true;
        }

        // ---------- Zustand anzeigen ----------

        void OnTick(object sender, EventArgs e)
        {
            if (!busy) return;
            status.Text = liveStatus;
            int n = progN;
            if (n > 0)
            {
                if (progress.Style != ProgressBarStyle.Continuous) progress.Style = ProgressBarStyle.Continuous;
                progress.Value = Math.Max(0, Math.Min(1000, (int)((long)progI * 1000 / n)));
                sum1.Text = progI.ToString("N0") + " von " + n.ToString("N0") + " Aktionen";
            }
            else if (progress.Style != ProgressBarStyle.Marquee) progress.Style = ProgressBarStyle.Marquee;
        }

        void LayoutErr()
        {
            if (errCard == null) return;
            int x0 = Theme.P(76), w = Math.Max(Theme.P(100), errCard.Width - x0 - Theme.P(28));
            int hh = TextRenderer.MeasureText(errTitle.Text, Theme.H2, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.P(2);
            errTitle.SetBounds(x0, Theme.P(28), w, hh);
            int th = TextRenderer.MeasureText(errText.Text, Theme.Body, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.P(4);
            errText.SetBounds(x0, errTitle.Bottom + Theme.P(8), w, th);
            errRetry.Location = new Point(x0, errText.Bottom + Theme.P(16));
            int need = errRetry.Bottom + Theme.P(28);
            if (errCard.Height != need) errCard.Height = need;
        }

        /// <summary>"Ordner A/B" in Meldungen durch die Bezeichnungen ersetzen (z. B. "Laptop nicht erreichbar").</summary>
        string SideNames(string msg)
        {
            if (msg == null) return "";
            if (!string.IsNullOrWhiteSpace(job.NameA)) msg = msg.Replace("Ordner A", job.NameA.Trim());
            if (!string.IsNullOrWhiteSpace(job.NameB)) msg = msg.Replace("Ordner B", job.NameB.Trim());
            return msg;
        }

        void UpdateUi()
        {
            btnRe.Enabled = !busy;
            btnBack.Enabled = !busy;
            foreach (Pill p in pills) p.Enabled = !busy;
            btnSync.Visible = !busy;
            btnClose.Visible = !busy;
            btnLog.Visible = !busy && lastRun != null && !string.IsNullOrEmpty(lastRun.LogFile);
            btnCancel.Visible = busy;
            progress.Visible = busy;
            sum2.Visible = !busy;
            btnSync.Enabled = an != null && (an.CountActive > 0 || job.ScriptsAlways) && !showingResult;
            string st = an != null && an.CountActive == 0 && job.ScriptsAlways ? "Skripte ausführen" : "Jetzt synchronisieren";
            if (btnSync.Text != st) { btnSync.Text = st; btnSync.FitWidth(); PerformLayout(); }

            if (busy) { summary.Invalidate(); return; }

            if (an != null)
            {
                pills[0].Count = an.Actions.Count;
                pills[1].Count = an.Actions.Count(x => x.Chosen == ActionKind.NachB);
                pills[2].Count = an.Actions.Count(x => x.Chosen == ActionKind.NachA);
                pills[3].Count = an.Actions.Count(x => x.IsDelete);
                pills[4].Count = an.Actions.Count(x => x.Chosen == ActionKind.Konflikt || x.Chosen == ActionKind.Keine);
                pills[4].Alert = an.Actions.Any(x => x.Chosen == ActionKind.Konflikt) ? 1 : 0;
            }
            else foreach (Pill p in pills) p.Count = 0;
            PerformLayout();

            string info;
            if (error != null) info = error;
            else if (an == null) info = "";
            else
            {
                info = "Analyse abgeschlossen · " + Fmt.Duration(an.Seconds);
                if (an.ScanErrors.Count > 0) info += " · " + an.ScanErrors.Count + " Einträge nicht lesbar";
            }
            if (resultMsg != null) info = resultMsg + "  ·  " + info;
            status.Text = info;
            status.ForeColor = (error != null || resultBad) ? Theme.Err : Theme.TextDim;

            if (showingResult && lastRun != null)
            {
                sum1.Text = lastRun.Cancelled ? "Abgebrochen" : lastRun.Errors > 0 ? "Fertig – mit Fehlern" : "Fertig";
                sum2.Text = "Die Liste zeigt, was gemacht wurde. „Neu analysieren“ zeigt den aktuellen Stand.";
            }
            else if (an == null)
            {
                sum1.Text = error != null ? "Analyse nicht möglich" : "";
                sum2.Text = error != null ? "Prüfe, ob beide Ordner erreichbar sind (z. B. USB-Laufwerk oder NAS)." : "";
            }
            else if (an.CountActive == 0)
            {
                sum1.Text = an.CountUnknown > 0 ? "Bitte entscheiden: neu oder gelöscht?" :
                    an.Actions.Count == 0 ? (job.ScriptsAlways ? "Alles aktuell – die Skripte laufen trotzdem" : "Alles aktuell – nichts zu tun")
                    : "Keine Aktionen ausgewählt";
                sum2.Text = job.ScriptsAlways && an.CountUnknown == 0
                    ? "„Skripte ausführen“ startet die Befehle vor/nach dem Kopieren (Option „nur bei Änderungen“ ist aus)."
                    : Hints();
            }
            else
            {
                sum1.Text = an.CountActive.ToString("N0") + (an.CountActive == 1 ? " Aktion" : " Aktionen") +
                            " · " + Fmt.Size(an.TransferBytes) + " werden übertragen";
                sum2.Text = Hints();
            }
            // Fehler vor der Analyse: keine leere Liste mit "0" zeigen (sieht aus wie "alles aktuell")
            bool failed = error != null && an == null && !busy;
            if (errCard != null && (errCard.Visible != failed || failed))
            {
                pillBar.Visible = listCard.Visible = !failed;
                errCard.Visible = failed;
                if (failed)
                {
                    errTitle.Text = "Nicht analysiert \u2013 nichts wurde verglichen oder geändert";
                    errText.Text = SideNames(error) + "\n\nIst der Rechner bzw. das Laufwerk eingeschaltet und im Netz? " +
                        "Bei Freigaben: Stimmt der Pfad (z. B. \\\\Rechner\\Freigabe) und ist die Anmeldung hinterlegt?";
                    LayoutErr();
                    errCard.Invalidate();
                }
            }
            summary.Invalidate();
        }

        string Hints()
        {
            List<string> parts = new List<string>();
            int del = an.CountDeletes, con = an.CountConflicts, skip = an.CountSkipped, ren = an.CountRenames, unk = an.CountUnknown;
            if (unk > 0)
            {
                // Stand verloren: Einträge nur auf einer Seite – neu oder auf der anderen Seite gelöscht?
                parts.Add((an.StateNote ?? "Der gespeicherte Stand fehlt.") + " " + unk.ToString("N0") +
                    (unk == 1 ? " Eintrag liegt" : " Einträge liegen") + " nur auf einer Seite – neu oder gelöscht? " +
                    "Auswählen (Strg+A für alle) und per Rechtsklick kopieren oder löschen");
                con -= unk;
            }
            else if (an.StateNote != null) parts.Add(an.StateNote);
            if (ren > 0) parts.Add(ren.ToString("N0") + (ren == 1 ? " Umbenennung" : " Umbenennungen") + " ohne erneutes Übertragen");
            if (del > 0) parts.Add(del.ToString("N0") + (del == 1 ? " Löschung " : " Löschungen ") + (RecycleAll() ? "(in den Papierkorb)" : "(endgültig)"));
            if (con > 0) parts.Add(con.ToString("N0") + " Konflikte werden übersprungen – Rechtsklick zum Entscheiden");
            if (skip > 0) parts.Add(skip.ToString("N0") + " übersprungen");
            if (parts.Count == 0) return "Rechtsklick auf Einträge, um die Aktion zu ändern";
            return string.Join(" · ", parts);
        }

        void Ui_(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        // ---------- Analyse ----------

        void StartAnalysis(bool thenSync)
        {
            if (busy) return;
            busy = true;
            cancel = false;
            progN = 0;
            progI = 0;
            liveStatus = "Analysiere …";
            sum1.Text = "Analysiere …";
            showingResult = false;
            btnCancel.Enabled = true;
            UpdateUi();
            LayoutFooter();

            Thread t = new Thread(delegate ()
            {
                Analysis result = null;
                Exception err = null;
                try
                {
                    result = Engine.Analyze(job, delegate { return cancel; }, delegate (string s) { liveStatus = s; });
                    Runner.RecordUpToDate(result);
                }
                catch (Exception ex) { err = ex; }
                Ui_(delegate { AnalysisDone(result, err, thenSync); });
            });
            t.IsBackground = true;
            t.Start();
        }

        void AnalysisDone(Analysis result, Exception err, bool thenSync)
        {
            busy = false;
            bool wasCancel = cancel;
            an = result;
            error = null;
            if (err is OperationCanceledException) error = "Analyse abgebrochen";
            else if (err != null) error = err.Message;
            ApplyFilter();
            UpdateUi();
            if (wasCancel) { Main.ViewIdle(); return; }

            if (thenSync && an != null)
            {
                if (an.CountActive == 0 && !(job.ScriptsAlways && an.CountUnknown == 0)) { }
                else if (an.CountConflicts > 0)
                    status.Text = "Es gibt Konflikte. Bitte prüfen (Rechtsklick) und dann synchronisieren.";
                else if (Engine.SafetyCheck(an) != null)
                    status.Text = "Ungewöhnlich viele Löschungen – bitte die Liste prüfen und dann synchronisieren.";
                else
                {
                    StartSync(false);
                    return;
                }
            }
            Main.ViewIdle();
        }

        // ---------- Synchronisieren ----------

        void StartSync(bool ask)
        {
            if (busy || an == null || (an.CountActive == 0 && !job.ScriptsAlways)) return;
            string warn = Engine.SafetyCheck(an);
            if (warn != null)
            {
                DialogResult r = MessageBox.Show("Achtung: " + warn + ".\n\nTrotzdem synchronisieren?", "Sync Pilot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }
            else if (ask && !RecycleAll() && an.CountDeletes > 0)
            {
                DialogResult r = MessageBox.Show(an.CountDeletes.ToString("N0") + " Dateien werden endgültig gelöscht (nicht in den Papierkorb).\n\nFortfahren?",
                    "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            busy = true;
            cancel = false;
            progI = 0;
            progN = an.CountActive;
            liveStatus = "Synchronisiere …";
            btnCancel.Enabled = true;
            UpdateUi();
            LayoutFooter();

            Analysis current = an;
            Thread t = new Thread(delegate ()
            {
                HistoryEntry h;
                try
                {
                    h = Runner.ExecuteAnalysis(current, false, delegate { return cancel; },
                        delegate (int i, int n, string text) { progI = i; progN = n; liveStatus = "Synchronisiere … " + text; });
                }
                catch (Exception ex) { h = Runner.Crash(job, false, ex); }
                Ui_(delegate { SyncDone(h); });
            });
            t.IsBackground = true;
            t.Start();
        }

        void SyncDone(HistoryEntry h)
        {
            busy = false;
            lastRun = h;
            resultBad = !h.IsOk;
            if (h.Cancelled) resultMsg = "Abgebrochen nach " + h.Copied + " Kopien und " + h.Deleted + " Löschungen";
            else if (h.Errors > 0 && h.Copied == 0 && h.Deleted == 0 && !string.IsNullOrEmpty(h.Result) && h.Result.StartsWith("Fehler"))
                resultMsg = h.Result;
            else
                resultMsg = "Synchronisiert: " + h.Copied + " kopiert, " + (h.Moved > 0 ? h.Moved + " umbenannt, " : "") +
                            h.Deleted + " gelöscht, " + h.Errors +
                            (h.Errors == 1 ? " Fehler" : " Fehler") + " (" + Fmt.Size(h.Bytes) + ")";
            // Ergebnis stehen lassen statt sofort neu zu analysieren, damit man sieht, was gemacht wurde
            showingResult = true;
            ApplyFilter();
            UpdateUi();
            Main.ViewIdle();
        }
    }
}
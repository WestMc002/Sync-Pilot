// Sync Pilot – Protokoll eines Laufs grafisch anzeigen (statt nur im Editor)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SyncPilot
{
    public enum LogKind { Header, ToB, ToA, Delete, Rename, Conflict, Skip, Error, Warn, Script, ScriptOut, Result, Info }

    /// <summary>Eine Zeile des Protokolls, für die Anzeige zerlegt.</summary>
    public class LogEntry
    {
        public string Time = "";
        public LogKind Kind;
        public string Chip = "";
        /// <summary>Relativer Pfad (null bei Meldungen ohne Datei).</summary>
        public string Rel;
        /// <summary>Text in der Spalte "Datei / Meldung" (Pfad oder Meldung).</summary>
        public string Text = "";
        public string Detail = "";
        public string Raw = "";
        public bool IsDir;
    }

    /// <summary>Liest eine Protokolldatei (Logger) und zerlegt sie in Einträge.</summary>
    public class LogParser
    {
        public readonly List<LogEntry> Entries = new List<LogEntry>();
        public string SideA = "A", SideB = "B";
        public string PathA, PathB;
        public string Mode;
        /// <summary>Zahlen aus der Zeile "Ergebnis: …" (-1 = nicht gefunden).</summary>
        public int Copied = -1, Moved, Deleted, Conflicts, Skipped, Errors;

        static readonly Regex TimeRx = new Regex(@"^(\d\d:\d\d:\d\d)  (.*)$");
        static readonly Regex LabelRx = new Regex(@"^([^(:]+?) \((.+?)\): (.*)$");
        static readonly Regex SideRx = new Regex(@"^(.+?): (.+?)(?:  \((.+)\))?$");

        int expectSides;

        public static LogParser FromFile(string file)
        {
            List<string> lines = new List<string>();
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8, true))
            {
                string s;
                while ((s = r.ReadLine()) != null) lines.Add(s);
            }
            return FromLines(lines);
        }

        public static LogParser FromLines(IEnumerable<string> lines)
        {
            LogParser p = new LogParser();
            foreach (string l in lines) p.Add(l);
            return p;
        }

        void Add(string line)
        {
            if (line == null || line.Trim().Length == 0) return;
            LogEntry e = new LogEntry();
            e.Raw = line;
            string s = line;
            Match tm = TimeRx.Match(line);
            if (tm.Success) { e.Time = tm.Groups[1].Value; s = tm.Groups[2].Value; }
            else if (Entries.Count > 0)
            {
                // Fortsetzung einer mehrzeiligen Meldung: an die vorige Zeile anhängen
                LogEntry prev = Entries[Entries.Count - 1];
                if (prev.Kind == LogKind.Error || prev.Kind == LogKind.Warn) prev.Detail = (prev.Detail + " " + line.Trim()).Trim();
                else prev.Text = (prev.Text + " " + line.Trim()).Trim();
                prev.Raw += Environment.NewLine + line;
                return;
            }
            Classify(e, s);
            Entries.Add(e);
        }

        bool IsSideA(string label)
        {
            label = (label ?? "").Trim();
            if (label == SideA) return true;
            if (label == SideB) return false;
            return label == "A";
        }

        /// <summary>"A → B" bzw. "PC → NAS": true, wenn nach B kopiert wurde.</summary>
        bool TargetIsB(string dir)
        {
            int i = dir.LastIndexOf('\u2192');
            string target = i >= 0 ? dir.Substring(i + 1).Trim() : dir.Trim();
            if (target == SideB) return true;
            if (target == SideA) return false;
            return target != "A";
        }

        void Set(LogEntry e, LogKind k, string chip, string rel, string detail)
        {
            e.Kind = k;
            e.Chip = chip;
            e.Rel = rel;
            e.Text = rel ?? "";
            e.Detail = detail ?? "";
        }

        void Classify(LogEntry e, string s)
        {
            if (s.StartsWith("Sync Pilot "))
            {
                expectSides = 2;
                Set(e, LogKind.Header, "Start", null, null);
                int i = s.IndexOf("Aufgabe:");
                e.Text = i >= 0 ? "Aufgabe: " + s.Substring(i + 8).Trim() : s;
                e.Detail = s.Substring(0, i > 0 ? i : s.Length).Trim().TrimEnd('\u2013', '-').Trim();
                return;
            }
            if (expectSides > 0 && !s.StartsWith("Modus:"))
            {
                Match sm = SideRx.Match(s);
                if (sm.Success)
                {
                    bool first = expectSides == 2;
                    expectSides--;
                    if (first) { SideA = sm.Groups[1].Value.Trim(); PathA = sm.Groups[2].Value.Trim(); }
                    else { SideB = sm.Groups[1].Value.Trim(); PathB = sm.Groups[2].Value.Trim(); }
                    Set(e, LogKind.Header, sm.Groups[1].Value.Trim(), null, sm.Groups[3].Value);
                    e.Text = sm.Groups[2].Value.Trim();
                    return;
                }
            }
            expectSides = 0;
            if (s.StartsWith("Modus:"))
            {
                Mode = s.Substring(6).Trim();
                Set(e, LogKind.Header, "Modus", null, null);
                e.Text = Mode;
                return;
            }
            if (s.StartsWith("Skript:"))
            {
                Set(e, LogKind.ScriptOut, "Ausgabe", null, null);
                e.Text = s.Substring(7).Trim();
                return;
            }
            if (s.StartsWith("Skript"))
            {
                Set(e, LogKind.Script, "Skript", null, null);
                int i = s.IndexOf(": ");
                if (s.StartsWith("Skript beendet")) { e.Text = s; if (!s.EndsWith(" 0")) e.Kind = LogKind.Warn; }
                else if (s.StartsWith("Skript konnte") || s.StartsWith("Skript abgebrochen")) { e.Kind = LogKind.Warn; e.Chip = "Skript"; e.Text = s; }
                else if (i > 0) { e.Text = s.Substring(i + 2); e.Detail = s.Substring(0, i); }
                else e.Text = s;
                return;
            }
            if (s.StartsWith("FEHLER bei "))
            {
                string rest = s.Substring(11);
                int i = rest.IndexOf(": ");
                if (i > 0) Set(e, LogKind.Error, "Fehler", rest.Substring(0, i), rest.Substring(i + 2));
                else Set(e, LogKind.Error, "Fehler", null, rest);
                if (e.Rel == null) e.Text = rest;
                return;
            }
            if (s.StartsWith("FEHLER"))
            {
                Set(e, LogKind.Error, "Fehler", null, null);
                e.Text = s.Substring(6).TrimStart(':', ' ');
                return;
            }
            if (s.StartsWith("Ergebnis:"))
            {
                Set(e, LogKind.Result, "Ergebnis", null, null);
                e.Text = s.Substring(9).Trim();
                Copied = Num(s, "kopiert");
                Moved = Math.Max(0, Num(s, "umbenannt/verschoben"));
                Deleted = Math.Max(0, Num(s, "gelöscht"));
                Conflicts = Math.Max(0, Num(s, "Konflikte"));
                Skipped = Math.Max(0, Num(s, "übersprungen"));
                Errors = Math.Max(0, Num(s, "Fehler"));
                return;
            }
            if (s.StartsWith("Warnung:")) { Set(e, LogKind.Warn, "Warnung", null, null); e.Text = s.Substring(8).Trim(); return; }
            if (s.StartsWith("Hinweis:")) { Set(e, LogKind.Info, "Hinweis", null, null); e.Text = s.Substring(8).Trim(); return; }
            if (s.StartsWith("Vom Benutzer abgebrochen")) { Set(e, LogKind.Warn, "Abgebrochen", null, null); e.Text = s; return; }
            if (s.StartsWith("Übersprungen: ")) { Set(e, LogKind.Skip, "Übersprungen", s.Substring(14), null); return; }

            Match m = LabelRx.Match(s);
            if (m.Success)
            {
                string what = m.Groups[1].Value, label = m.Groups[2].Value, rest = m.Groups[3].Value;
                // "Gelöscht (NAS, Papierkorb)": Seite und Art trennen
                string side = label, how = "";
                int c = label.IndexOf(", ");
                if (c > 0) { side = label.Substring(0, c); how = label.Substring(c + 2); }
                switch (what)
                {
                    case "Kopiert":
                    case "Ordner angelegt":
                    {
                        bool toB = TargetIsB(label);
                        string target = toB ? SideB : SideA;
                        Set(e, toB ? LogKind.ToB : LogKind.ToA, (toB ? "\u2192 Nach " : "\u2190 Nach ") + target, rest, null);
                        if (what != "Kopiert") { e.IsDir = true; e.Detail = "Ordner angelegt"; }
                        return;
                    }
                    case "Gelöscht":
                    case "Ordner gelöscht":
                        Set(e, LogKind.Delete, "Gelöscht in " + side, rest, how);
                        e.IsDir = what != "Gelöscht";
                        if (e.IsDir) e.Detail = "Ordner" + (how.Length > 0 ? ", " + how : "");
                        return;
                    case "Schon nicht mehr vorhanden":
                        Set(e, LogKind.Skip, "Schon weg", rest, "War in " + side + " schon gelöscht");
                        return;
                    case "Ordner nicht leer oder nicht vorhanden, bleibt":
                        Set(e, LogKind.Skip, "Ordner bleibt", rest, "In " + side + " nicht leer oder nicht vorhanden");
                        e.IsDir = true;
                        return;
                    case "Umbenannt/verschoben":
                    case "Ordner umbenannt/verschoben":
                    case "Schreibweise angeglichen":
                    {
                        int a = rest.IndexOf(" \u2192 ");
                        string from = a > 0 ? rest.Substring(0, a) : rest;
                        string to = a > 0 ? rest.Substring(a + 3) : rest;
                        if (what == "Schreibweise angeglichen")
                        {
                            int p = from.LastIndexOf('\\');
                            to = (p >= 0 ? from.Substring(0, p + 1) : "") + to;
                        }
                        Set(e, LogKind.Rename, (what == "Schreibweise angeglichen" ? "Schreibweise in " : "Umbenannt in ") + side, to, "vorher: " + from);
                        e.IsDir = what.StartsWith("Ordner");
                        return;
                    }
                    case "Konflikt, übersprungen":
                        Set(e, LogKind.Conflict, "Konflikt", rest, label);
                        return;
                    case "Bisherige Fassung aufbewahrt":
                        Set(e, LogKind.Info, "Fassung aufbewahrt", null, "in " + side);
                        e.Text = rest;
                        return;
                }
            }
            Set(e, LogKind.Info, "Info", null, null);
            e.Text = s;
        }

        static int Num(string s, string word)
        {
            Match m = Regex.Match(s, @"(\d+)\s+" + Regex.Escape(word));
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }
    }

    /// <summary>
    /// Protokoll eines Laufs: Kopf mit Ergebnis und Kacheln, Filter, Suche und Liste aller Aktionen.
    /// Geöffnet aus dem Verlauf (Doppelklick, Rechtsklick) oder in den Aufgaben über "Letztes Protokoll öffnen".
    /// </summary>
    public class LogView : BaseView
    {
        readonly HistoryEntry h;
        readonly bool fromHistory;
        readonly SyncJob job;
        readonly FlatButton btnBack, btnText, btnFolder, btnAnalyse;
        readonly Label title, status, empty;
        readonly Card summary, listCard;
        readonly Panel pillBar;
        readonly Pill[] pills;
        readonly InputBox search;
        readonly ThemedListView list;
        readonly System.Windows.Forms.Timer searchTimer;
        readonly ToolStripMenuItem miOpenA, miOpenB, miCompare, miShowA, miShowB, miCopy;
        readonly Rectangle[] tiles = new Rectangle[4];

        LogParser log;
        string loadError;
        List<LogEntry> view = new List<LogEntry>();
        int filterIndex;
        int hoverTile = -1;

        static readonly int[] TilePill = { 1, 3, 5, 6 };

        public override string NavKey { get { return "log"; } }
        public override string NavHighlight { get { return fromHistory ? "history" : "tasks"; } }

        public LogView(MainForm main, HistoryEntry entry, bool fromHistory) : base(main)
        {
            h = entry;
            this.fromHistory = fromHistory;
            job = main.Jobs.FirstOrDefault(j => j.Id == entry.JobId);

            btnBack = new FlatButton("", Theme.GBack, BtnKind.Subtle);
            btnBack.AccessibleName = fromHistory ? "Zurück zum Verlauf" : "Zurück zu den Aufgaben";
            btnBack.Click += delegate { GoBack(); };
            title = Ui.Label("Protokoll · " + entry.JobName, Theme.H1, Theme.Text, Theme.Bg);
            title.AutoSize = false;
            title.AutoEllipsis = true;
            status = Ui.WrapLabel("", Theme.Body, Theme.TextDim, Theme.Bg);
            status.AutoEllipsis = true;

            btnText = new FlatButton("Als Text öffnen", Theme.GDoc, BtnKind.Secondary);
            btnText.Click += delegate { if (HasFile) Ui.Open(h.LogFile); };
            btnFolder = new FlatButton("Im Ordner zeigen", Theme.GFolder, BtnKind.Subtle);
            btnFolder.Click += delegate { if (HasFile) Ui.Explore(h.LogFile, true); };
            btnAnalyse = new FlatButton("Neu analysieren", Theme.GRefresh, BtnKind.Secondary);
            btnAnalyse.Click += delegate { if (job != null) Main.ShowAnalysis(job, false); };
            btnText.Enabled = btnFolder.Enabled = HasFile || Demo.Active;
            btnAnalyse.Enabled = job != null;

            summary = new Card();
            summary.Height = Theme.P(104);
            summary.Paint += PaintSummary;
            summary.MouseMove += delegate (object s, MouseEventArgs e)
            {
                int t = TileAt(e.Location);
                if (t != hoverTile) { hoverTile = t; summary.Cursor = t >= 0 ? Cursors.Hand : Cursors.Default; summary.Invalidate(); }
            };
            summary.MouseLeave += delegate { if (hoverTile >= 0) { hoverTile = -1; summary.Cursor = Cursors.Default; summary.Invalidate(); } };
            // Klick auf eine Kachel: passenden Filter wählen
            summary.MouseClick += delegate (object s, MouseEventArgs e)
            {
                int t = TileAt(e.Location);
                if (t < 0) return;
                Pill p = pills[TilePill[t]];
                if (!p.Visible) p = pills[0];
                p.Checked = true;
            };

            pillBar = new Panel();
            pillBar.BackColor = Theme.Bg;
            pillBar.Height = Theme.P(36);
            string[] names = { "Alle", "\u2192 Nach B", "\u2190 Nach A", "Gelöscht", "Umbenannt", "Konflikte & Übersprungen", "Fehler", "Skript & Hinweise" };
            pills = new Pill[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                Pill p = new Pill(names[i]);
                int idx = i;
                p.CheckedChanged += delegate { if (p.Checked) { filterIndex = idx; ApplyFilter(); } };
                pills[i] = p;
                pillBar.Controls.Add(p);
            }
            search = new InputBox(false, false);
            search.Cue = "Datei oder Meldung suchen";
            search.Width = Theme.P(240);
            searchTimer = new System.Windows.Forms.Timer();
            searchTimer.Interval = 250;
            searchTimer.Tick += delegate { searchTimer.Stop(); ApplyFilter(); };
            search.Box.TextChanged += delegate { searchTimer.Stop(); searchTimer.Start(); };
            search.Box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { search.Text = ""; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) { list.Focus(); e.SuppressKeyPress = true; }
            };
            pillBar.Controls.Add(search);

            listCard = new Card();
            list = new ThemedListView();
            list.VirtualMode = true;
            list.Columns.Add("Zeit", Theme.P(84));
            list.Columns.Add("Aktion", Theme.P(150));
            list.Columns.Add("Datei / Meldung", Theme.P(420));
            list.Columns.Add("Details", Theme.P(280));
            list.FillColumn = 2;
            list.FillMin = 200;
            list.RetrieveVirtualItem += OnRetrieve;
            list.CustomDraw = DrawCell;
            list.DoubleClick += delegate { RevealBest(); };
            list.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C) { CopyLines(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { RevealBest(); e.SuppressKeyPress = true; }
            };
            listCard.Controls.Add(list);
            empty = Ui.WrapLabel("", Theme.Body, Theme.TextDim, Theme.Surface);
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.Visible = false;
            listCard.Controls.Add(empty);
            empty.BringToFront();
            listCard.Resize += delegate
            {
                list.SetBounds(1, Theme.P(6), listCard.Width - 2, listCard.Height - Theme.P(12));
                empty.SetBounds(1, Theme.P(60), listCard.Width - 2, Theme.P(40));
            };

            // Rechtsklick: Datei öffnen, vergleichen, im Ordner zeigen
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip menu = mb.CreateMenu();
            miOpenA = mb.Item("Öffnen aus A", (char)0xE8E5, delegate { OpenFile(true); });
            miOpenB = mb.Item("Öffnen aus B", (char)0xE8E5, delegate { OpenFile(false); });
            miCompare = mb.Item("Inhalt vergleichen \u2026", (char)0xE8A7, delegate { Compare(); });
            miShowA = mb.Item("In A anzeigen", MenuHelper.Folder, delegate { Reveal(true); });
            miShowB = mb.Item("In B anzeigen", MenuHelper.Folder, delegate { Reveal(false); });
            miCopy = mb.Item("Zeilen kopieren", (char)0xE8C8, delegate { CopyLines(); });
            menu.Items.AddRange(new ToolStripItem[] { miOpenA, miOpenB, miCompare, miShowA, miShowB, new ToolStripSeparator(), miCopy });
            menu.Opening += MenuOpening;
            list.ContextMenuStrip = menu;

            Controls.AddRange(new Control[] { btnBack, title, status, btnText, btnFolder, btnAnalyse, summary, pillBar, listCard });
            UpdatePills();
            pills[0].Checked = true;

            if (Demo.Active)
            {
                // Screenshots: Beispielprotokoll statt einer Datei
                log = LogParser.FromLines(Demo.SampleLog(job, h));
                Loaded();
            }
            else if (!HasFile)
            {
                loadError = "Zu diesem Lauf gibt es keine Protokolldatei mehr (ältere Protokolle werden nach der eingestellten Zeit gelöscht).";
                Loaded();
            }
            else
            {
                status.Text = "Protokoll wird gelesen \u2026";
                string file = h.LogFile;
                Thread t = new Thread(delegate ()
                {
                    LogParser p = null;
                    string err = null;
                    try { p = LogParser.FromFile(file); }
                    catch (Exception ex) { err = "Das Protokoll ließ sich nicht lesen: " + ex.Message; }
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            log = p;
                            loadError = err;
                            Loaded();
                        });
                    }
                    catch { }
                });
                t.IsBackground = true;
                // erst mit Fenster-Handle starten (BeginInvoke braucht es), und nur einmal
                bool started = false;
                if (IsHandleCreated) { started = true; t.Start(); }
                else HandleCreated += delegate { if (!started) { started = true; t.Start(); } };
            }
        }

        bool HasFile { get { return h != null && !string.IsNullOrEmpty(h.LogFile) && File.Exists(h.LogFile); } }

        string NameA { get { return log != null ? log.SideA : job != null ? job.SideName(true) : "A"; } }
        string NameB { get { return log != null ? log.SideB : job != null ? job.SideName(false) : "B"; } }

        void GoBack()
        {
            if (fromHistory) { HistoryView.RestoreNext = true; Main.Navigate(new HistoryView(Main)); }
            else Main.ShowTasks();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { search.Box.Focus(); search.Box.SelectAll(); return true; }
            if (keyData == (Keys.Alt | Keys.Left)) { GoBack(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { searchTimer.Stop(); searchTimer.Dispose(); }
            base.Dispose(disposing);
        }

        void Loaded()
        {
            if (log != null)
            {
                pills[1].Text = "\u2192 Nach " + log.SideB;
                pills[2].Text = "\u2190 Nach " + log.SideA;
                miOpenA.Text = "Öffnen aus " + log.SideA;
                miOpenB.Text = "Öffnen aus " + log.SideB;
                miShowA.Text = "In " + log.SideA + " anzeigen";
                miShowB.Text = "In " + log.SideB + " anzeigen";
                // Spalte "Aktion" so breit wie die längste Plakette
                int w = Theme.P(110);
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    foreach (string c in log.Entries.Select(x => x.Chip).Distinct())
                        w = Math.Max(w, Theme.ChipWidth(g, c, Theme.Small) + Theme.P(24));
                list.Columns[1].Width = Math.Min(w, Theme.P(300));
            }
            UpdatePills();
            UpdateStatus();
            ApplyFilter();
            summary.Invalidate();
            PerformLayout();
        }

        void UpdateStatus()
        {
            if (loadError != null) { status.Text = loadError; return; }
            List<string> parts = new List<string>();
            parts.Add(Fmt.When(h.Start));
            if (h.Seconds > 0) parts.Add("Dauer " + Fmt.Duration(h.Seconds));
            parts.Add(h.Scheduled ? "Zeitplan" : "Manuell");
            if (log != null) parts.Add(log.SideA + " \u2192 " + log.SideB);
            status.Text = string.Join("  ·  ", parts.ToArray());
        }

        // ---------- Filter ----------

        static bool InFilter(LogEntry e, int f)
        {
            switch (f)
            {
                case 1: return e.Kind == LogKind.ToB;
                case 2: return e.Kind == LogKind.ToA;
                case 3: return e.Kind == LogKind.Delete;
                case 4: return e.Kind == LogKind.Rename;
                case 5: return e.Kind == LogKind.Conflict || e.Kind == LogKind.Skip;
                case 6: return e.Kind == LogKind.Error;
                case 7: return e.Kind == LogKind.Header || e.Kind == LogKind.Script || e.Kind == LogKind.ScriptOut ||
                               e.Kind == LogKind.Warn || e.Kind == LogKind.Info || e.Kind == LogKind.Result;
                default: return e.Kind != LogKind.ScriptOut;   // Ausgabe der Skripte nur unter "Skript & Hinweise"
            }
        }

        void UpdatePills()
        {
            List<LogEntry> all = log != null ? log.Entries : new List<LogEntry>();
            for (int i = 0; i < pills.Length; i++)
            {
                int n = 0;
                foreach (LogEntry e in all) if (InFilter(e, i)) n++;
                pills[i].Count = n;
                // Filter ohne Einträge ausblenden (Alle und Fehler bleiben immer sichtbar)
                pills[i].Visible = i == 0 || i == 6 || n > 0 || pills[i].Checked;
            }
            pills[6].Alert = pills[6].Count > 0 ? 2 : 0;
            PerformLayout();
        }

        void ApplyFilter()
        {
            string q = search.Text.Trim();
            List<LogEntry> all = log != null ? log.Entries : new List<LogEntry>();
            view = all.Where(e => InFilter(e, filterIndex) &&
                (q.Length == 0 || e.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 e.Detail.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 e.Chip.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            list.SelectedIndices.Clear();
            // wie in der Analyse: erst nach oben und auf 0, sonst stürzt VirtualListSize beim Verkürzen ab
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
            if (loadError != null) empty.Text = "Kein Protokoll vorhanden.";
            else if (log == null) empty.Text = "Wird gelesen \u2026";
            else if (q.Length > 0) empty.Text = "Nichts gefunden für \u201e" + q + "\u201c.";
            else empty.Text = filterIndex == 6 ? "Keine Fehler \u2013 alles in Ordnung." : "Keine Einträge.";
            empty.Visible = view.Count == 0;
        }

        // ---------- Layout und Zeichnen ----------

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (listCard == null || pills == null || search == null) return;
            int pad = Theme.P(28), W = ClientSize.Width, H = ClientSize.Height;
            btnBack.Location = new Point(pad - Theme.P(6), Theme.P(26));
            int bx = W - pad - btnAnalyse.Width;
            btnAnalyse.Location = new Point(bx, Theme.P(26));
            bx -= Theme.P(8) + btnText.Width;
            btnText.Location = new Point(bx, Theme.P(26));
            bx -= Theme.P(8) + btnFolder.Width;
            btnFolder.Location = new Point(bx, Theme.P(26));
            int tw = Math.Max(Theme.P(60), bx - btnBack.Right - Theme.P(24));
            title.SetBounds(btnBack.Right + Theme.P(8), Theme.P(18), tw, title.PreferredHeight);
            status.SetBounds(btnBack.Right + Theme.P(10), title.Bottom, Math.Max(10, tw), Theme.P(22));

            summary.SetBounds(pad, status.Bottom + Theme.P(14), W - 2 * pad, summary.Height);
            // Filter links, Suche rechts; passen nicht beide in eine Zeile, kommt die Suche in eine eigene Zeile darunter
            int bw = W - 2 * pad, sw = Math.Min(Theme.P(260), Math.Max(Theme.P(180), bw / 4));
            int pillsW = 0;
            foreach (Pill p in pills) if (p.Visible) pillsW += p.Width + Theme.P(8);
            bool twoRows = pillsW + Theme.P(8) > bw - sw;
            int rowH = Theme.P(36);
            pillBar.SetBounds(pad, summary.Bottom + Theme.P(14), bw, twoRows ? rowH + Theme.P(8) + search.Height : rowH);
            if (twoRows) search.SetBounds(0, rowH + Theme.P(8), Math.Min(bw, Theme.P(360)), search.Height);
            else search.SetBounds(bw - sw, 0, sw, search.Height);
            int x = 0;
            foreach (Pill p in pills)
            {
                if (!p.Visible) continue;
                p.Location = new Point(x, 1);
                x += p.Width + Theme.P(8);
            }
            int ly = pillBar.Bottom + Theme.P(12);
            listCard.SetBounds(pad, ly, W - 2 * pad, Math.Max(Theme.P(80), H - ly - Theme.P(16)));
        }

        int TileAt(Point p)
        {
            for (int i = 0; i < tiles.Length; i++) if (tiles[i].Contains(p)) return i;
            return -1;
        }

        void PaintSummary(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.P(20), W = summary.Width, H = summary.Height;
            int left = Math.Max(Theme.P(220), Math.Min(Theme.P(360), W / 3));

            // links: Ergebnis als Plakette, darunter der Modus
            int k = TasksView.StatusKind(h);
            Color fg = k == 3 ? Theme.Err : k == 2 ? Theme.Warn : Theme.Ok;
            Color cbg = k == 3 ? Theme.ErrBg : k == 2 ? Theme.WarnBg : Theme.OkBg;
            Theme.DrawText(g, "ERGEBNIS", Theme.Small, new Rectangle(pad, Theme.P(12), left - pad, Theme.P(20)), Theme.TextDim, TextFormatFlags.Left);
            string res = Fmt.Result(h);
            int maxW = left - pad - Theme.P(8);
            if (Theme.ChipWidth(g, res, Theme.Small) > maxW)
            {
                while (res.Length > 1 && Theme.ChipWidth(g, res + "\u2026", Theme.Small) > maxW) res = res.Substring(0, res.Length - 1);
                res = res.TrimEnd() + "\u2026";
            }
            Theme.Chip(g, res, Theme.Small, pad, Theme.P(46), fg, cbg);
            string mode = log != null && log.Mode != null ? log.Mode : job != null ? job.ModeText : "";
            TextRenderer.DrawText(g, mode, Theme.Small, new Rectangle(pad, Theme.P(64), left - pad - Theme.P(8), Theme.P(34)), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // rechts: vier Kacheln
            int copied, moved, deleted, conflicts, skipped, errors;
            if (log != null && log.Copied >= 0)
            {
                copied = log.Copied; moved = log.Moved; deleted = log.Deleted;
                conflicts = log.Conflicts; skipped = log.Skipped; errors = log.Errors;
            }
            else
            {
                copied = h.Copied; moved = h.Moved; deleted = h.Deleted;
                conflicts = h.Conflicts; skipped = h.Skipped; errors = h.Errors;
            }
            string[] labels = { "Kopiert", "Gelöscht", "Konflikte", "Fehler" };
            int[] vals = { copied, deleted, conflicts, errors };
            string[] subs =
            {
                h.Bytes > 0 && !Demo.Active ? Fmt.Size(h.Bytes) : "",
                moved > 0 ? moved.ToString("N0") + " umbenannt" : "",
                skipped > 0 ? "+ " + skipped.ToString("N0") + " übersprungen" : "",
                ""
            };
            Color[] cols = { Theme.Text, Theme.Text, conflicts > 0 ? Theme.Warn : Theme.Text, errors > 0 ? Theme.Err : Theme.Text };
            int gap = Theme.P(10);
            int tx = left + Theme.P(8), tAll = W - pad - tx;
            int tW = (tAll - 3 * gap) / 4;
            for (int i = 0; i < 4; i++)
            {
                Rectangle r = new Rectangle(tx + i * (tW + gap), Theme.P(12), tW, H - Theme.P(24));
                tiles[i] = r;
                Color tileBg = i == 3 && errors > 0 ? Theme.Blend(Theme.Surface, Theme.ErrBg, 0.7f)
                    : i == 2 && conflicts > 0 ? Theme.Blend(Theme.Surface, Theme.WarnBg, 0.7f) : Theme.Surface2;
                if (hoverTile == i) tileBg = Theme.Blend(tileBg, Theme.Accent, 0.12f);
                Theme.FillRound(g, r, Theme.P(8), tileBg);
                Theme.DrawText(g, labels[i], Theme.Small, new Rectangle(r.X + Theme.P(14), r.Y + Theme.P(6), r.Width - Theme.P(20), Theme.P(20)),
                    Theme.TextDim, TextFormatFlags.Left);
                Theme.DrawText(g, log == null && loadError == null ? "\u2013" : vals[i].ToString("N0"), Theme.H1,
                    new Rectangle(r.X + Theme.P(12), r.Y + Theme.P(26), r.Width - Theme.P(20), Theme.P(34)), cols[i], TextFormatFlags.Left);
                if (subs[i].Length > 0)
                    Theme.DrawText(g, subs[i], Theme.Small, new Rectangle(r.X + Theme.P(14), r.Bottom - Theme.P(22), r.Width - Theme.P(20), Theme.P(18)),
                        Theme.TextDim, TextFormatFlags.Left);
            }
        }

        void OnRetrieve(object sender, RetrieveVirtualItemEventArgs e)
        {
            LogEntry it = e.ItemIndex < view.Count ? view[e.ItemIndex] : null;
            if (it == null) { e.Item = new ListViewItem(new string[] { "", "", "", "" }); return; }
            e.Item = new ListViewItem(new string[] { it.Time, it.Chip, it.Rel != null && it.IsDir ? it.Text + "\\" : it.Text, it.Detail });
        }

        static void ChipColors(LogKind k, out Color fg, out Color bg)
        {
            switch (k)
            {
                case LogKind.ToB: fg = Theme.AccentSoftText; bg = Theme.AccentSoft; break;
                case LogKind.ToA: fg = Theme.Purple; bg = Theme.PurpleBg; break;
                case LogKind.Delete:
                case LogKind.Error: fg = Theme.Err; bg = Theme.ErrBg; break;
                case LogKind.Conflict:
                case LogKind.Warn: fg = Theme.Warn; bg = Theme.WarnBg; break;
                case LogKind.Rename:
                case LogKind.Result: fg = Theme.Ok; bg = Theme.OkBg; break;
                default: fg = Theme.Neutral; bg = Theme.NeutralBg; break;
            }
        }

        bool DrawCell(DrawListViewSubItemEventArgs e, Color bg)
        {
            if (e.ItemIndex >= view.Count) return true;
            LogEntry it = view[e.ItemIndex];
            bool err = it.Kind == LogKind.Error;
            // Fehlerzeilen rot hinterlegt (außer markiert)
            if (err && !e.Item.Selected)
                using (SolidBrush b = new SolidBrush(Theme.Blend(Theme.Surface, Theme.ErrBg, 0.75f)))
                    e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height - 1);
            Rectangle r = new Rectangle(e.Bounds.X + Theme.P(12), e.Bounds.Y, e.Bounds.Width - Theme.P(20), e.Bounds.Height);
            switch (e.ColumnIndex)
            {
                case 0:
                    if (e.Item.Selected)
                        using (SolidBrush ab = new SolidBrush(Theme.Accent))
                            e.Graphics.FillRectangle(ab, e.Bounds.X, e.Bounds.Y + Theme.P(8), Theme.P(3), Math.Max(2, e.Bounds.Height - Theme.P(16)));
                    Theme.DrawText(e.Graphics, it.Time, Theme.Body, r, Theme.TextDim, TextFormatFlags.Left);
                    return true;
                case 1:
                {
                    Color fg, cbg;
                    ChipColors(it.Kind, out fg, out cbg);
                    string t = it.Chip;
                    int max = e.Bounds.Width - Theme.P(16);
                    while (t.Length > 4 && Theme.ChipWidth(e.Graphics, t, Theme.Small) > max) t = t.Substring(0, t.Length - 2).TrimEnd() + "\u2026";
                    Region clip = e.Graphics.Clip;
                    e.Graphics.SetClip(e.Bounds);
                    Theme.Chip(e.Graphics, t, Theme.Small, e.Bounds.X + Theme.P(10), e.Bounds.Y + e.Bounds.Height / 2, fg, cbg);
                    e.Graphics.Clip = clip;
                    return true;
                }
                case 2:
                {
                    Font f = it.Kind == LogKind.ScriptOut || it.Kind == LogKind.Script ? Theme.Mono : Theme.Body;
                    Color c = it.Rel != null || it.Kind == LogKind.Header ? Theme.Text : Theme.TextDim;
                    if (err) c = Theme.Text;
                    TextFormatFlags ell = it.Rel != null ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis;
                    TextRenderer.DrawText(e.Graphics, e.SubItem.Text, f, r, c,
                        ell | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
                    return true;
                }
                default:
                    Theme.DrawText(e.Graphics, it.Detail, Theme.Body, r, err ? Theme.Err : Theme.TextDim, TextFormatFlags.Left);
                    return true;
            }
        }

        // ---------- Aktionen ----------

        List<LogEntry> Selected()
        {
            List<LogEntry> r = new List<LogEntry>();
            foreach (int i in list.SelectedIndices) if (i < view.Count) r.Add(view[i]);
            return r;
        }

        LogEntry One()
        {
            List<LogEntry> sel = Selected();
            return sel.Count == 1 ? sel[0] : null;
        }

        /// <summary>Wurzelordner einer Seite: wie im Protokoll (Stand des Laufs), sonst aus der Aufgabe.</summary>
        string Root(bool sideA)
        {
            string p = log != null ? (sideA ? log.PathA : log.PathB) : null;
            if (string.IsNullOrEmpty(p) && job != null) p = sideA ? job.FolderA : job.FolderB;
            if (string.IsNullOrEmpty(p) || FtpUrl.IsRemote(p)) return null;
            try { return Paths.TryResolve(p); } catch { return null; }
        }

        string FullPath(LogEntry e, bool sideA)
        {
            if (e == null || string.IsNullOrEmpty(e.Rel)) return null;
            string root = Root(sideA);
            if (root == null) return null;
            try { return Path.Combine(root, e.Rel); } catch { return null; }
        }

        static bool FileThere(string p) { return p != null && File.Exists(p); }
        static bool Exists(string p) { return p != null && (File.Exists(p) || Directory.Exists(p)); }

        void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            List<LogEntry> sel = Selected();
            if (sel.Count == 0) { e.Cancel = true; return; }
            LogEntry it = sel.Count == 1 ? sel[0] : null;
            string pa = FullPath(it, true), pb = FullPath(it, false);
            miOpenA.Enabled = FileThere(pa);
            miOpenB.Enabled = FileThere(pb);
            miCompare.Enabled = miOpenA.Enabled && miOpenB.Enabled;
            miShowA.Enabled = Exists(pa);
            miShowB.Enabled = Exists(pb);
            miCopy.Text = sel.Count == 1 ? "Zeile kopieren" : sel.Count.ToString("N0") + " Zeilen kopieren";
        }

        void OpenFile(bool sideA)
        {
            string p = FullPath(One(), sideA);
            if (FileThere(p)) Ui.Open(p);
        }

        void Reveal(bool sideA)
        {
            string p = FullPath(One(), sideA);
            if (Exists(p)) Ui.Explore(p, true);
        }

        /// <summary>Doppelklick: dort zeigen, wo die Datei jetzt liegt (zuerst am Ziel der Aktion).</summary>
        void RevealBest()
        {
            LogEntry it = One();
            if (it == null || it.Rel == null) return;
            bool first = it.Kind != LogKind.ToA;
            string p = FullPath(it, first);
            if (!Exists(p)) p = FullPath(it, !first);
            if (Exists(p)) Ui.Explore(p, true);
            else
            {
                // gelöscht: wenigstens den übergeordneten Ordner zeigen
                string parent = p != null ? Path.GetDirectoryName(p) : null;
                if (parent != null && Directory.Exists(parent)) Ui.Explore(parent, false);
            }
        }

        void Compare()
        {
            LogEntry it = One();
            string pa = FullPath(it, true), pb = FullPath(it, false);
            if (pa == null || pb == null) return;
            FileCompare.Show(this, pa, pb, NameA, NameB, Path.GetFileName(it.Rel));
        }

        void CopyLines()
        {
            List<LogEntry> sel = Selected();
            if (sel.Count == 0) return;
            try { Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => x.Raw).ToArray())); }
            catch { }
        }
    }
}

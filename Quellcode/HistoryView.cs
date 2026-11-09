// Sync Pilot – Verlauf aller Läufe
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SyncPilot
{
    public class HistoryView : BaseView
    {
        readonly Label title, subtitle, empty;
        readonly FlatButton btnFolder, btnClear;
        readonly Card listCard;
        readonly ThemedListView list;
        List<HistoryEntry> entries;

        public override string NavKey { get { return "history"; } }

        /// <summary>Beim nächsten Öffnen an die gemerkte Stelle scrollen (gesetzt von "Zurück" im Protokoll).</summary>
        public static bool RestoreNext;
        static string savedTop, savedSel;

        static string Key(HistoryEntry h) { return h == null ? null : h.JobId + "|" + h.Start.Ticks; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && list != null && list.Items.Count > 0)
            {
                try
                {
                    ListViewItem top = list.TopItem;
                    savedTop = top != null ? Key(top.Tag as HistoryEntry) : null;
                    savedSel = list.SelectedItems.Count > 0 ? Key(list.SelectedItems[0].Tag as HistoryEntry) : null;
                }
                catch { }
            }
            base.Dispose(disposing);
        }

        void RestorePosition()
        {
            if (!RestoreNext) return;
            RestoreNext = false;
            try
            {
                ListViewItem top = null, sel = null;
                foreach (ListViewItem it in list.Items)
                {
                    string k = Key(it.Tag as HistoryEntry);
                    if (k == savedTop) top = it;
                    if (k == savedSel) sel = it;
                }
                if (sel != null) { sel.Selected = true; sel.Focused = true; }
                if (top != null)
                {
                    // zuerst ans Ende der sichtbaren Seite, dann die oberste Zeile setzen (TopItem allein greift nicht immer)
                    list.EnsureVisible(Math.Min(list.Items.Count - 1, top.Index + 30));
                    list.TopItem = top;
                    list.TopItem = top;
                }
                else if (sel != null) sel.EnsureVisible();
            }
            catch { }
        }

        public HistoryView(MainForm main) : base(main)
        {
            title = Ui.Label("Verlauf", Theme.H1, Theme.Text, Theme.Bg);
            subtitle = Ui.Label("Alle Läufe mit Ergebnis – Doppelklick zeigt das Protokoll", Theme.Body, Theme.TextDim, Theme.Bg);
            btnFolder = new FlatButton("Protokollordner", Theme.GFolder, BtnKind.Secondary);
            btnClear = new FlatButton("Verlauf leeren", null, BtnKind.Subtle);
            btnFolder.Click += delegate
            {
                Directory.CreateDirectory(Store.LogDir);
                Ui.Explore(Store.LogDir, false);
            };
            btnClear.Click += delegate
            {
                if (MessageBox.Show("Den Verlauf leeren? Die Protokolldateien bleiben erhalten.", "Sync Pilot",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { Store.ClearHistory(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Sync Pilot"); }
                Reload();
            };

            listCard = new Card();
            list = new ThemedListView();
            // Zeitpunkt mit Uhr-Symbol bei Läufen per Zeitplan/Ereignis (wie "Zuletzt" in der Aufgabenliste);
            // die frühere Spalte "Start" (Zeitplan/Manuell) entfällt dafür, damit keine waagrechte Leiste entsteht
            list.Columns.Add("Zeitpunkt", Theme.P(170));
            list.Columns.Add("Ordner", Theme.P(220));
            list.Columns.Add("Aufgabe", Theme.P(170));
            list.Columns.Add("Ergebnis", Theme.P(120));
            list.Columns.Add("Kopiert", Theme.P(76), HorizontalAlignment.Right);
            list.Columns.Add("Gelöscht", Theme.P(82), HorizontalAlignment.Right);
            list.Columns.Add("Konflikte", Theme.P(86), HorizontalAlignment.Right);
            list.Columns.Add("Fehler", Theme.P(74), HorizontalAlignment.Right);
            list.Columns.Add("Dauer", Theme.P(96), HorizontalAlignment.Right);
            list.FillColumn = 1;   // Ordner bekommt die restliche Breite, keine waagrechte Bildlaufleiste
            list.FillMin = 200;
            // Ordner: beim Darüberfahren die vollständigen Pfade
            list.TipText = delegate (int row, int col)
            {
                if (row < 0 || row >= list.Items.Count) return null;
                if (col == 0)
                {
                    HistoryEntry he = list.Items[row].Tag as HistoryEntry;
                    return he == null ? null : (he.Scheduled ? "Per Zeitplan oder Ereignis gestartet" : "Von Hand gestartet");
                }
                if (col != 1) return null;
                SyncJob j = JobOf(list.Items[row].Tag as HistoryEntry);
                if (j == null) return null;
                return j.SideTitle(true) + ": " + j.FolderA + "\n" + j.SideTitle(false) + ": " + j.FolderB;
            };
            list.CustomDraw = DrawCell;
            list.DoubleClick += delegate { OpenSelected(); };
            list.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) OpenSelected(); };
            // Rechtsklick: Protokoll öffnen, im Ordner zeigen, Aufgabe analysieren bzw. bearbeiten
            list.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                ListViewItem hit = list.GetItemAt(e.X, e.Y);
                if (hit == null) return;
                if (!hit.Selected) { list.SelectedItems.Clear(); hit.Selected = true; }
                ShowMenu(hit.Tag as HistoryEntry, e.Location);
            };
            listCard.Controls.Add(list);
            listCard.Resize += delegate { list.SetBounds(1, Theme.P(6), listCard.Width - 2, listCard.Height - Theme.P(12)); };

            empty = Ui.WrapLabel("Noch keine Läufe.", Theme.Body, Theme.TextDim, Theme.Surface);
            empty.TextAlign = ContentAlignment.MiddleCenter;
            listCard.Controls.Add(empty);
            empty.BringToFront();

            Controls.AddRange(new Control[] { title, subtitle, btnFolder, btnClear, listCard });
            Reload();
        }

        void Reload()
        {
            // neueste zuerst nach Startzeit (ein langer Lauf endet später, gehört aber an seine Startzeit)
            entries = Store.LoadHistory().OrderByDescending(x => x.Start).ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (HistoryEntry h in entries)
            {
                SyncJob job = JobOf(h);
                ListViewItem it = new ListViewItem(new string[]
                {
                    Fmt.When(h.Start),
                    job == null ? "\u2014" : job.ShortSide(true) + (job.Mode == SyncMode.Abgleichen ? " \u21C4 " : " \u2192 ") + job.ShortSide(false),
                    h.JobName,
                    Fmt.Result(h),
                    h.Copied.ToString("N0"),
                    h.Deleted.ToString("N0"),
                    h.Conflicts.ToString("N0"),
                    h.Errors.ToString("N0"),
                    Fmt.Duration(h.Seconds)
                });
                it.Tag = h;
                list.Items.Add(it);
            }
            list.EndUpdate();
            // Ordner bekommt den restlichen Platz (mindestens 160); abgeschnittene Einträge zeigt der Hinweis beim Darüberfahren
            list.FillMin = 160;
            list.Stretch();
            empty.Visible = entries.Count == 0;
            if (RestoreNext)
            {
                if (list.IsHandleCreated) BeginInvoke((MethodInvoker)RestorePosition);
                else list.HandleCreated += delegate { BeginInvoke((MethodInvoker)RestorePosition); };
            }
        }

        SyncJob JobOf(HistoryEntry h)
        {
            if (h == null) return null;
            return Main.Jobs.FirstOrDefault(j => j.Id == h.JobId);
        }

        bool DrawCell(DrawListViewSubItemEventArgs e, Color bg)
        {
            HistoryEntry h = e.Item.Tag as HistoryEntry;
            if (h == null) return false;
            if (e.ColumnIndex == 0)
            {
                // Uhr = per Zeitplan oder Ereignis gestartet, sonst von Hand
                int x = e.Bounds.X + Theme.P(12);
                if (h.Scheduled)
                {
                    Theme.DrawGlyph(e.Graphics, "\uE823", 9f, new Rectangle(x, e.Bounds.Y, Theme.P(18), e.Bounds.Height), Theme.AccentSoftText, TextFormatFlags.Left);
                    x += Theme.P(20);
                }
                Theme.DrawText(e.Graphics, e.SubItem.Text, Theme.Body, new Rectangle(x, e.Bounds.Y, Math.Max(0, e.Bounds.Right - x - Theme.P(8)), e.Bounds.Height),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return true;
            }
            if (e.ColumnIndex != 3) return false;
            int k = TasksView.StatusKind(h);
            Color fg = k == 3 ? Theme.Err : k == 2 ? Theme.Warn : Theme.Ok;
            Color cbg = k == 3 ? Theme.ErrBg : k == 2 ? Theme.WarnBg : Theme.OkBg;
            string text = e.SubItem.Text;
            // Text kürzen, bis die Plakette in die Spalte passt (statt mitten im Wort abgeschnitten)
            int maxW = e.Bounds.Width - Theme.P(10) - Theme.P(8);
            if (Theme.ChipWidth(e.Graphics, text, Theme.Small) > maxW)
            {
                while (text.Length > 1 && Theme.ChipWidth(e.Graphics, text + "…", Theme.Small) > maxW) text = text.Substring(0, text.Length - 1);
                text = text.TrimEnd() + "…";
            }
            using (Region old = e.Graphics.Clip)
            {
                e.Graphics.SetClip(e.Bounds);
                Theme.Chip(e.Graphics, text, Theme.Small, e.Bounds.X + Theme.P(10), e.Bounds.Y + e.Bounds.Height / 2, fg, cbg);
                e.Graphics.Clip = old;
            }
            return true;
        }

        void ShowMenu(HistoryEntry h, Point at)
        {
            if (h == null) return;
            bool hasLog = !string.IsNullOrEmpty(h.LogFile) && File.Exists(h.LogFile);
            SyncJob job = Main.Jobs.FirstOrDefault(j => j.Id == h.JobId);
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip m = mb.CreateMenu();
            GlyphMenuItem open = mb.Item("Protokoll anzeigen", '\uE8A5', delegate { Main.ShowLog(h, true); });
            open.Enabled = hasLog;
            GlyphMenuItem text = mb.Item("Als Text öffnen", MenuHelper.Document, delegate { Ui.Open(h.LogFile); });
            text.Enabled = hasLog;
            GlyphMenuItem show = mb.Item("Protokoll im Ordner anzeigen", MenuHelper.Folder, delegate { Ui.Explore(h.LogFile, true); });
            show.Enabled = hasLog;
            m.Items.Add(open);
            m.Items.Add(text);
            m.Items.Add(show);
            m.Items.Add(new ToolStripSeparator());
            GlyphMenuItem an = mb.Item("Aufgabe analysieren", '\uE721', delegate { Main.ShowAnalysis(job, false); });
            GlyphMenuItem ed = mb.Item("Aufgabe bearbeiten", '\uE70F', delegate { Main.ShowEdit(job); });
            an.Enabled = ed.Enabled = job != null;
            m.Items.Add(an);
            m.Items.Add(ed);
            m.Show(list, at);
        }

        void OpenSelected()
        {
            if (list.SelectedItems.Count != 1) return;
            HistoryEntry h = list.SelectedItems[0].Tag as HistoryEntry;
            if (h == null) return;
            if (string.IsNullOrEmpty(h.LogFile) || !File.Exists(h.LogFile))
            {
                MessageBox.Show("Zu diesem Lauf gibt es keine Protokolldatei mehr.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Main.ShowLog(h, true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (title == null || empty == null) return;
            int pad = Theme.P(28), W = ClientSize.Width, H = ClientSize.Height;
            title.Location = new Point(pad - Theme.P(3), Theme.P(22));
            subtitle.Location = new Point(pad, title.Bottom + Theme.P(2));
            btnFolder.Location = new Point(W - pad - btnFolder.Width, Theme.P(30));
            btnClear.Location = new Point(btnFolder.Left - Theme.P(8) - btnClear.Width, Theme.P(30));
            int y = subtitle.Bottom + Theme.P(20);
            listCard.SetBounds(pad, y, W - 2 * pad, Math.Max(Theme.P(80), H - y - Theme.P(16)));
            empty.SetBounds(1, Theme.P(60), listCard.Width - 2, Theme.P(40));
        }
    }
}
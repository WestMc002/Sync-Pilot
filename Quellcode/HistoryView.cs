// Sync Pilot – Verlauf aller Läufe
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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

        public HistoryView(MainForm main) : base(main)
        {
            title = Ui.Label("Verlauf", Theme.H1, Theme.Text, Theme.Bg);
            subtitle = Ui.Label("Alle Läufe mit Ergebnis – Doppelklick öffnet das Protokoll", Theme.Body, Theme.TextDim, Theme.Bg);
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
            list.Columns.Add("Zeitpunkt", Theme.P(150));
            list.Columns.Add("Aufgabe", Theme.P(180));
            list.Columns.Add("Ergebnis", Theme.P(220));
            list.Columns.Add("Kopiert", Theme.P(76), HorizontalAlignment.Right);
            list.Columns.Add("Gelöscht", Theme.P(82), HorizontalAlignment.Right);
            list.Columns.Add("Konflikte", Theme.P(86), HorizontalAlignment.Right);
            list.Columns.Add("Fehler", Theme.P(74), HorizontalAlignment.Right);
            list.Columns.Add("Dauer", Theme.P(84), HorizontalAlignment.Right);
            list.Columns.Add("Start", Theme.P(92));
            list.FillColumn = 2;   // Ergebnis bekommt die restliche Breite, keine waagrechte Bildlaufleiste
            list.CustomDraw = DrawCell;
            list.DoubleClick += delegate { OpenSelected(); };
            list.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) OpenSelected(); };
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
            entries = Store.LoadHistory();
            entries.Reverse();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (HistoryEntry h in entries)
            {
                ListViewItem it = new ListViewItem(new string[]
                {
                    Fmt.When(h.Start),
                    h.JobName,
                    Fmt.Result(h),
                    h.Copied.ToString("N0"),
                    h.Deleted.ToString("N0"),
                    h.Conflicts.ToString("N0"),
                    h.Errors.ToString("N0"),
                    Fmt.Duration(h.Seconds),
                    h.Scheduled ? "Zeitplan" : "Manuell"
                });
                it.Tag = h;
                list.Items.Add(it);
            }
            list.EndUpdate();
            list.Stretch();
            empty.Visible = entries.Count == 0;
        }

        bool DrawCell(DrawListViewSubItemEventArgs e, Color bg)
        {
            if (e.ColumnIndex != 2) return false;
            HistoryEntry h = e.Item.Tag as HistoryEntry;
            if (h == null) return false;
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
            Ui.Open(h.LogFile);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (title == null || empty == null) return;
            int pad = Theme.P(32), W = ClientSize.Width, H = ClientSize.Height;
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
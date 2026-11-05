// Sync Pilot – Uhrzeit mit Wochentagen für den Zeitplan
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SyncPilot
{
    public class TimeSlotDialog : FixedDialog
    {
        readonly Pill[] days = new Pill[7];
        readonly InputBox time;

        public TimeSlot Slot { get; private set; }

        public TimeSlotDialog(string title, TimeSlot slot) : base(title)
        {
            int W = Theme.P(480), pad = Theme.P(24), y = Theme.P(20), g = Theme.P(8);

            Label lDays = Ui.Label("An diesen Tagen", Theme.Bold, Theme.Text, Theme.Bg);
            lDays.Location = new Point(pad, y);
            y += Theme.P(30);
            int x = pad;
            for (int i = 0; i < 7; i++)
            {
                Pill p = new Pill(TimeSlot.DayShort[i], false);
                p.AutoCheck = false; // Mehrfachauswahl statt Optionsfeld
                p.Checked = slot.Has(i);
                p.AccessibleName = new[] { "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag" }[i];
                p.Width = Math.Max(p.Width, Theme.P(48));
                p.Location = new Point(x, y);
                p.Click += delegate { p.Checked = !p.Checked; };
                x += p.Width + Theme.P(6);
                days[i] = p;
                Controls.Add(p);
            }
            y += Theme.P(32) + Theme.P(10);

            FlatButton all = new FlatButton("Täglich", null, BtnKind.Subtle);
            FlatButton work = new FlatButton("Mo–Fr", null, BtnKind.Subtle);
            FlatButton weekend = new FlatButton("Wochenende", null, BtnKind.Subtle);
            int bx = pad;
            foreach (FlatButton b in new[] { all, work, weekend })
            {
                b.Height = Theme.P(30);
                b.FitWidth();
                b.Location = new Point(bx, y);
                bx += b.Width + Theme.P(4);
                Controls.Add(b);
            }
            all.Click += delegate { SetDays(127); };
            work.Click += delegate { SetDays(31); };
            weekend.Click += delegate { SetDays(96); };
            y += Theme.P(30) + Theme.P(18);

            Label lTime = Ui.Label("Uhrzeit", Theme.Bold, Theme.Text, Theme.Bg);
            lTime.Location = new Point(pad, y);
            y += Theme.P(26);
            time = new InputBox(false, false);
            time.BackColor = Theme.Bg;
            time.Box.AccessibleName = "Uhrzeit";
            time.Text = slot.At;
            time.SetBounds(pad, y, Theme.P(110), time.Height);
            Label hint = Ui.Label("z. B. 23:10", Theme.Small, Theme.TextDim, Theme.Bg);
            hint.Location = new Point(time.Right + g, y + Theme.P(10));
            y += time.Height + Theme.P(24);

            FlatButton ok = new FlatButton("OK", Theme.GCheck, BtnKind.Primary);
            FlatButton cancel = new FlatButton("Abbrechen", null, BtnKind.Secondary);
            ok.Location = new Point(W - pad - ok.Width, y);
            cancel.Location = new Point(ok.Left - g - cancel.Width, y);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { Accept(); };
            AcceptButton = ok;
            CancelButton = cancel;
            y += ok.Height + Theme.P(20);

            Controls.AddRange(new Control[] { lDays, lTime, time, hint, cancel, ok });
            ClientSize = new Size(Math.Max(W, x + pad), y);
        }

        void SetDays(int mask)
        {
            for (int i = 0; i < 7; i++) days[i].Checked = (mask & (1 << i)) != 0;
        }

        void Accept()
        {
            int mask = 0;
            for (int i = 0; i < 7; i++) if (days[i].Checked) mask |= 1 << i;
            if (mask == 0)
            {
                MessageBox.Show(this, "Bitte mindestens einen Tag auswählen.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string at;
            if (!TimeSlot.TryParseTime(time.Text, out at))
            {
                MessageBox.Show(this, "Bitte eine Uhrzeit wie 23:10 eingeben.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                time.Box.Focus();
                return;
            }
            Slot = new TimeSlot(mask, at);
            DialogResult = DialogResult.OK;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            time.Box.Focus();
            time.Box.SelectAll();
        }
    }
}

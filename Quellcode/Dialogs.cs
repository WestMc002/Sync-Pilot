using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>
    /// Meldungen und Rückfragen im Sync-Pilot-Stil (hell/dunkel, gleiche Schaltflächen wie im Programm).
    /// Hat bewusst denselben Namen und dieselben Show-Aufrufe wie System.Windows.Forms.MessageBox: im Namensraum
    /// SyncPilot wird damit überall automatisch dieses Fenster verwendet.
    /// </summary>
    public static class MessageBox
    {
        public static DialogResult Show(string text) { return Show(null, text, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption) { return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) { return Show(null, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def) { return Show(null, text, caption, buttons, icon, def); }
        public static DialogResult Show(IWin32Window owner, string text) { return Show(owner, text, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption) { return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) { return Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            // Vor dem Laden des Designs (sehr früher Fehler) bleibt es beim Windows-Fenster
            if (Theme.Body == null) return System.Windows.Forms.MessageBox.Show(owner, text, caption, buttons, icon, def);
            if (owner == null) owner = DefaultOwner();
            using (MsgDialog d = new MsgDialog(text, caption, buttons, icon, def, null))
            {
                if (owner != null) d.StartPosition = FormStartPosition.CenterParent;
                // Programmfenster mit nach vorn holen, damit die Meldung nicht allein über anderen Programmen steht
                Form of = owner as Form;
                if (of != null && !of.IsDisposed && of.Visible) { try { of.Activate(); } catch { } }
                DialogResult r = owner != null ? d.ShowDialog(owner) : d.ShowDialog();
                // danach wieder aktivieren (sonst springt Windows ggf. zum vorherigen Programm)
                if (of != null && !of.IsDisposed && of.Visible) { try { of.Activate(); } catch { } }
                return d.Map(r);
            }
        }

        /// <summary>
        /// Besitzer für Meldungen ohne Angabe: das aktive Fenster; ist keines aktiv (z. B. weil das Programm während
        /// einer Netzwerkprüfung kurz "nicht reagiert" hat), das sichtbare Hauptfenster. Ohne Besitzer würde das
        /// Hauptfenster nach der Meldung hinter anderen Programmen verschwinden.
        /// </summary>
        public static IWin32Window DefaultOwner()
        {
            Form a = Form.ActiveForm;
            if (a != null && !a.InvokeRequired && a.Visible) return a;
            foreach (Form f in Application.OpenForms)
                if (f != null && !f.IsDisposed && !f.InvokeRequired && f.Visible && f.WindowState != FormWindowState.Minimized &&
                    f.Owner == null && !(f is MsgDialog))
                    return f;
            return null;
        }
    }

    /// <summary>Fenster für Meldungen und Rückfragen; optional mit Kästchen (z. B. „Nicht mehr fragen“).</summary>
    public class MsgDialog : FixedDialog
    {
        readonly CheckBox check;
        readonly MessageBoxButtons buttons;

        public bool Checked { get { return check != null && check.Checked; } }

        public MsgDialog(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
                         MessageBoxDefaultButton def, string checkText)
            : base(string.IsNullOrEmpty(caption) ? "Sync Pilot" : caption)
        {
            this.buttons = buttons;
            text = text ?? "";
            int pad = Theme.P(24), iconW = icon == MessageBoxIcon.None ? 0 : Theme.P(44);
            TextFormatFlags tf = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            // Breite nach Text: mindestens 420, höchstens 600 (lange Zeilen werden umbrochen)
            // (Fensterbreite = Text + Symbol + Ränder, daher hier Textbreite = Fensterbreite - Ränder - Symbol)
            int minTw = Theme.P(420) - 2 * pad - iconW, maxTw = Theme.P(600) - 2 * pad - iconW;
            int natural = TextRenderer.MeasureText(text, Theme.Body, new Size(maxTw, 0), tf).Width;
            int tw = Math.Max(minTw, Math.Min(maxTw, natural + Theme.P(8)));
            int W = pad + iconW + tw + pad, x0 = pad + iconW;

            if (icon != MessageBoxIcon.None)
            {
                Color c; string sym;
                switch (icon)
                {
                    case MessageBoxIcon.Warning: c = Theme.Warn; sym = "!"; break;
                    case MessageBoxIcon.Error: c = Theme.Err; sym = "×"; break;
                    case MessageBoxIcon.Question: c = Theme.Accent; sym = "?"; break;
                    default: c = Theme.Accent; sym = "i"; break;
                }
                Panel ic = new Panel();
                ic.SetBounds(pad, Theme.P(22), Theme.P(32), Theme.P(32));
                ic.BackColor = Theme.Bg;
                ic.Paint += delegate (object s, PaintEventArgs e)
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(c)) e.Graphics.FillEllipse(b, 0, 0, ic.Width - 1, ic.Height - 1);
                    TextRenderer.DrawText(e.Graphics, sym, Theme.Bold, new Rectangle(0, 0, ic.Width, ic.Height), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                };
                Controls.Add(ic);
            }

            int th = TextRenderer.MeasureText(text, Theme.Body, new Size(tw, 0), tf).Height + Theme.P(4);
            Label l = Ui.WrapLabel(text, Theme.Body, Theme.Text, Theme.Bg);
            l.SetBounds(x0, Theme.P(26), tw, th);
            Controls.Add(l);
            int y = Math.Max(l.Bottom, Theme.P(54)) + Theme.P(18);

            if (!string.IsNullOrEmpty(checkText))
            {
                check = new CheckBox();
                check.Text = checkText;
                check.Font = Theme.Body;
                check.ForeColor = Theme.Text;
                check.BackColor = Theme.Bg;
                check.AutoSize = true;
                check.Location = new Point(x0, y);
                Controls.Add(check);
                y = check.Bottom + Theme.P(18);
            }

            // Schaltflächen wie bei Windows: Ja | Nein | Abbrechen, rechtsbündig; die Vorgabe ist hervorgehoben
            string[] labels; DialogResult[] results;
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel: labels = new[] { "OK", "Abbrechen" }; results = new[] { DialogResult.OK, DialogResult.Cancel }; break;
                case MessageBoxButtons.YesNo: labels = new[] { "Ja", "Nein" }; results = new[] { DialogResult.Yes, DialogResult.No }; break;
                case MessageBoxButtons.YesNoCancel: labels = new[] { "Ja", "Nein", "Abbrechen" }; results = new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }; break;
                case MessageBoxButtons.RetryCancel: labels = new[] { "Wiederholen", "Abbrechen" }; results = new[] { DialogResult.Retry, DialogResult.Cancel }; break;
                default: labels = new[] { "OK" }; results = new[] { DialogResult.OK }; break;
            }
            int defIndex = def == MessageBoxDefaultButton.Button3 ? 2 : def == MessageBoxDefaultButton.Button2 ? 1 : 0;
            if (defIndex >= labels.Length) defIndex = 0;
            FlatButton[] btns = new FlatButton[labels.Length];
            int bx = W - pad;
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                FlatButton b = new FlatButton(labels[i], null, i == defIndex ? BtnKind.Primary : BtnKind.Secondary);
                b.Width = Math.Max(b.Width, Theme.P(110));
                b.Location = new Point(bx - b.Width, y);
                bx = b.Left - Theme.P(8);
                b.DialogResult = results[i];
                btns[i] = b;
                Controls.Add(b);
            }
            // Fenster breiter, falls die Schaltflächen nicht passen
            if (bx < pad)
            {
                int shift = pad - bx;
                W += shift;
                foreach (FlatButton b in btns) b.Left += shift;
            }
            AcceptButton = btns[defIndex];
            CancelButton = btns[labels.Length - 1];
            ClientSize = new Size(W, btns[0].Bottom + Theme.P(20));
            Shown += delegate { Activate(); btns[defIndex].Focus(); };
        }

        /// <summary>Schließen mit X bzw. Esc: bei Ja/Nein als "Nein" werten (wie bei Windows).</summary>
        public DialogResult Map(DialogResult r)
        {
            if (r == DialogResult.Cancel || r == DialogResult.None)
            {
                if (buttons == MessageBoxButtons.YesNo) return DialogResult.No;
                if (buttons == MessageBoxButtons.OK) return DialogResult.OK;
            }
            return r;
        }
    }
}

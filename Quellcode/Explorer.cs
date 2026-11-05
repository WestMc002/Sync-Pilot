// Sync Pilot – Explorer-Kontextmenü: Ordner A wählen, Ordner B wählen, Richtung festlegen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SyncPilot
{
    /// <summary>
    /// Eintrag „Sync Pilot“ im Rechtsklickmenü von Ordnern, Laufwerken und dem Ordnerhintergrund.
    /// Ablauf: Rechtsklick auf Ordner A → „Als Ordner A wählen“ → Rechtsklick auf Ordner B →
    /// „Mit „A“ abgleichen …“ → in Sync Pilot die Richtung wählen.
    /// Die Menütexte stehen in HKCU und werden nach jeder Auswahl neu geschrieben.
    /// </summary>
    public static class ExplorerMenu
    {
        const string StateKey = @"Software\Sync Pilot\Explorer";
        static readonly string[] MenuKeys =
        {
            @"Software\Classes\Directory\shell\SyncPilot",
            @"Software\Classes\Directory\Background\shell\SyncPilot",
            @"Software\Classes\Drive\shell\SyncPilot"
        };
        // %1 = angeklickter Ordner, %V = Ordner, in dessen leerem Bereich geklickt wurde
        static readonly string[] MenuArgs = { "%1", "%V", "%1" };

        /// <summary>Signal an die laufende Oberfläche: ein Ordnerpaar liegt bereit.</summary>
        public const string EventName = "Local\\SyncPilot_Explorer";

        /// <summary>Menü passend zur Einstellung einrichten bzw. entfernen (immer mit dem aktuellen Programmpfad).</summary>
        public static void Apply()
        {
            if (Demo.Active) return;
            if (AppSettings.Current.ExplorerEntry) Write(ReadState("A"));
            else Remove();
        }

        /// <summary>Menü ausschalten und eine gemerkte Auswahl vergessen.</summary>
        public static void Disable()
        {
            Remove();
            try { Registry.CurrentUser.DeleteSubKeyTree(StateKey, false); } catch { }
        }

        static void Remove()
        {
            foreach (string k in MenuKeys)
                try { Registry.CurrentUser.DeleteSubKeyTree(k, false); } catch { }
        }

        static void Write(string pendingA)
        {
            string exe = Application.ExecutablePath;
            for (int i = 0; i < MenuKeys.Length; i++)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(MenuKeys[i], false);
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(MenuKeys[i]))
                    {
                        k.SetValue("MUIVerb", "Sync Pilot");
                        k.SetValue("Icon", "\"" + exe + "\",0");
                        k.SetValue("SubCommands", "");
                        if (pendingA != null)
                            Item(k, "1_b", "Mit \u201e" + Short(pendingA).Replace("&", "&&") + "\u201c abgleichen \u2026",
                                Cmd(exe, "/ordner-b", MenuArgs[i]));
                        Item(k, "2_a", pendingA == null ? "Als Ordner A w\u00e4hlen" : "Stattdessen als Ordner A w\u00e4hlen",
                            Cmd(exe, "/ordner-a", MenuArgs[i]));
                        if (pendingA != null)
                            Item(k, "3_x", "Auswahl aufheben", Cmd(exe, "/ordner-x", null));
                    }
                }
                catch { }
            }
        }

        static string Cmd(string exe, string verb, string arg)
        {
            return "\"" + exe + "\" " + verb + (arg != null ? " \"" + arg + "\"" : "");
        }

        static void Item(RegistryKey parent, string id, string text, string command)
        {
            using (RegistryKey k = parent.CreateSubKey(@"shell\" + id))
            {
                k.SetValue("MUIVerb", text);
                using (RegistryKey c = k.CreateSubKey("command")) c.SetValue("", command);
            }
        }

        static string ReadState(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(StateKey))
                    return k == null ? null : k.GetValue(name) as string;
            }
            catch { return null; }
        }

        static void WriteState(string name, string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(StateKey))
                {
                    if (value == null) k.DeleteValue(name, false);
                    else k.SetValue(name, value);
                }
            }
            catch { }
        }

        /// <summary>Ordnerpfad aus der Kommandozeile. Explorer übergibt Laufwerke als "C:\", was als C:" ankommt.</summary>
        public static string ArgPath(string[] args)
        {
            if (args.Length < 2) return null;
            string p = string.Join(" ", args, 1, args.Length - 1).Trim().Replace('"', '\\');
            if (p.Length == 0) return null;
            try { p = Path.GetFullPath(p); }
            catch { return null; }
            if (p.Length > 3) p = p.TrimEnd('\\');
            return p;
        }

        /// <summary>Kurzname für Menü und Hinweise: Ordnername bzw. "C:\" bei Laufwerken.</summary>
        public static string Short(string path)
        {
            string n = Path.GetFileName((path ?? "").TrimEnd('\\'));
            if (string.IsNullOrEmpty(n)) n = path ?? "";
            if (n.Length > 40) n = n.Substring(0, 39) + "\u2026";
            return n;
        }

        public static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").TrimEnd('\\'), (b ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static bool Inside(string inner, string outer)
        {
            string a = inner.TrimEnd('\\') + "\\", b = outer.TrimEnd('\\') + "\\";
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }

        static bool CheckFolder(string path)
        {
            if (path != null && Directory.Exists(path)) return true;
            MessageBox.Show("Diesen Ort kann Sync Pilot nicht verwenden" + (path != null ? ":\n" + path : ".") +
                "\n\nM\u00f6glich sind Ordner und Laufwerke, aber keine Bibliotheken oder Bereiche wie \u201eDieser PC\u201c.",
                "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        /// <summary>"/ordner-a": Ordner merken, Menü umstellen und kurz Bescheid geben.</summary>
        public static void PickA(string path)
        {
            if (!CheckFolder(path)) return;
            WriteState("B", null);
            WriteState("A", path);
            Write(path);
            ExplorerToast.ShowAndWait("Ordner A: " + Short(path),
                "Jetzt mit der rechten Maustaste auf Ordner B klicken und \u201eSync Pilot\u201c \u203a \u201eMit \u201e" +
                Short(path) + "\u201c abgleichen \u2026\u201c w\u00e4hlen.");
        }

        /// <summary>"/ordner-x": gemerkten Ordner A vergessen.</summary>
        public static void Clear()
        {
            WriteState("A", null);
            WriteState("B", null);
            Write(null);
        }

        /// <summary>"/ordner-b": Ordner B für die Oberfläche hinterlegen. false = nichts weiter zu tun.</summary>
        public static bool PickB(string path)
        {
            if (!CheckFolder(path)) return false;
            string a = ReadState("A");
            if (a == null) { PickA(path); return false; }
            if (!Directory.Exists(a))
            {
                MessageBox.Show("Ordner A ist nicht mehr erreichbar:\n" + a + "\n\nBitte Ordner A neu w\u00e4hlen.",
                    "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Clear();
                return false;
            }
            if (Same(a, path))
            {
                MessageBox.Show("Das ist derselbe Ordner wie Ordner A. Bitte einen anderen Ordner als B w\u00e4hlen.",
                    "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (Inside(a, path) || Inside(path, a))
            {
                MessageBox.Show("Ein Ordner darf nicht im anderen liegen:\n\nA: " + a + "\nB: " + path,
                    "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            WriteState("B", path);
            return true;
        }

        /// <summary>Bereitliegendes Ordnerpaar abholen (danach ist die Auswahl zurückgesetzt).</summary>
        public static bool TakePair(out string a, out string b)
        {
            a = ReadState("A");
            b = ReadState("B");
            if (a == null || b == null) { a = b = null; return false; }
            WriteState("A", null);
            WriteState("B", null);
            Apply();
            return true;
        }
    }

    /// <summary>Kleiner Hinweis unten rechts, der nach ein paar Sekunden von selbst verschwindet (nimmt nicht den Fokus).</summary>
    public class ExplorerToast : Form
    {
        readonly string head, text;
        readonly Timer timer;

        public static void ShowAndWait(string head, string text)
        {
            using (ExplorerToast t = new ExplorerToast(head, text)) Application.Run(t);
        }

        ExplorerToast(string head, string text)
        {
            this.head = head;
            this.text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Surface;
            Font = Theme.Body;
            Text = "Sync Pilot";
            AccessibleName = head;
            int w = Theme.P(380);
            int textH = TextRenderer.MeasureText(text, Theme.Small, new Size(w - Theme.P(72), 1000),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            Size = new Size(w, Theme.P(16 + 28 + 14) + textH);
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - Theme.P(16), wa.Bottom - Height - Theme.P(16));
            timer = new Timer();
            timer.Interval = 7000;
            timer.Tick += delegate { Close(); };
            Click += delegate { Close(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { } // runde Ecken (Windows 11)
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            int pad = Theme.P(16), x = pad + Theme.P(40);
            Theme.DrawGlyph(g, Theme.GCheck, 15f, new Rectangle(pad, pad, Theme.P(28), Theme.P(28)), Theme.Accent, TextFormatFlags.Left);
            Theme.DrawText(g, head, Theme.Bold, new Rectangle(x, pad, Width - x - pad, Theme.P(26)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(x, pad + Theme.P(28), Width - x - pad, Height - pad - Theme.P(28)),
                Theme.TextDim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }

    /// <summary>Zwei im Explorer gewählte Ordner: Richtung festlegen und gleich analysieren.</summary>
    public class PairDialog : FixedDialog
    {
        string a, b;
        readonly Label pathA, pathB;
        readonly ModeCard mSync, mBackup, mMirror;
        readonly InputBox name;
        bool nameEdited, updating;

        public PairDialog(string folderA, string folderB) : base("Zwei Ordner abgleichen")
        {
            a = folderA;
            b = folderB;
            int W = Theme.P(640), pad = Theme.P(24), w = W - 2 * pad, gap = Theme.P(12), y = Theme.P(20);

            FlatButton swap = new FlatButton("Tauschen", "\uE8CB", BtnKind.Secondary);
            swap.AccessibleName = "Ordner A und B tauschen";
            int pw = w - swap.Width - Theme.P(16);
            Label capA = Ui.Label("Ordner A", Theme.Small, Theme.TextDim, Theme.Bg);
            capA.Location = new Point(pad, y);
            pathA = PathLabel(pad, y + Theme.P(20), pw);
            Label capB = Ui.Label("Ordner B", Theme.Small, Theme.TextDim, Theme.Bg);
            capB.Location = new Point(pad, y + Theme.P(54));
            pathB = PathLabel(pad, y + Theme.P(74), pw);
            swap.Location = new Point(W - pad - swap.Width, y + Theme.P(50) - swap.Height / 2);
            swap.Click += delegate
            {
                string t = a; a = b; b = t;
                Refresh_();
            };
            y += Theme.P(118);

            Label lMode = Ui.Label("Richtung", Theme.Bold, Theme.Text, Theme.Bg);
            lMode.Location = new Point(pad, y);
            y += Theme.P(30);
            mSync = new ModeCard(Theme.GSwitch, "Abgleichen", "\u00c4nderungen in beide Richtungen \u00fcbernehmen");
            mBackup = new ModeCard(Theme.GForward, "Sichern", "Neue und ge\u00e4nderte Dateien von A nach B");
            mMirror = new ModeCard(Theme.GForward, "Spiegeln", "B wird exakt wie A, auch L\u00f6schungen");
            int cw = (w - 2 * gap) / 3;
            mSync.SetBounds(pad, y, cw, mSync.Height);
            mBackup.SetBounds(pad + cw + gap, y, cw, mBackup.Height);
            mMirror.SetBounds(pad + 2 * (cw + gap), y, w - 2 * (cw + gap), mMirror.Height);
            mSync.Checked = true;
            foreach (ModeCard m in new ModeCard[] { mSync, mBackup, mMirror })
                m.CheckedChanged += delegate { if (!updating) Refresh_(); };
            y += mSync.Height + Theme.P(18);

            Label lName = Ui.Label("Name der Aufgabe", Theme.Body, Theme.Text, Theme.Bg);
            lName.Location = new Point(pad, y);
            name = new InputBox(false, false);
            name.BackColor = Theme.Bg;
            name.Box.AccessibleName = "Name der Aufgabe";
            name.SetBounds(pad, y + Theme.P(22), w, name.Height);
            name.Box.TextChanged += delegate { if (!updating) nameEdited = true; };
            y += Theme.P(22) + name.Height + Theme.P(14);

            Label note = Ui.WrapLabel("Sync Pilot legt daf\u00fcr eine Aufgabe an und analysiert zuerst nur. Kopiert oder gel\u00f6scht wird erst, " +
                "wenn du in der Vorschau auf \u201eSynchronisieren\u201c klickst. \u201eWeitere Optionen\u201c \u00f6ffnet die Aufgabe mit Filtern und Zeitplan.",
                Theme.Small, Theme.TextDim, Theme.Bg);
            note.SetBounds(pad, y, w, Theme.P(40));
            y += Theme.P(50);

            FlatButton ok = new FlatButton("Analysieren", Theme.GPlay, BtnKind.Primary);
            FlatButton more = new FlatButton("Weitere Optionen \u2026", null, BtnKind.Secondary);
            FlatButton cancel = new FlatButton("Abbrechen", null, BtnKind.Secondary);
            ok.Location = new Point(W - pad - ok.Width, y);
            more.Location = new Point(ok.Left - Theme.P(8) - more.Width, y);
            cancel.Location = new Point(more.Left - Theme.P(8) - cancel.Width, y);
            ok.DialogResult = DialogResult.Yes;
            more.DialogResult = DialogResult.No;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;
            y += ok.Height + Theme.P(20);

            Controls.AddRange(new Control[] { capA, pathA, capB, pathB, swap, lMode, mSync, mBackup, mMirror, lName, name, note, cancel, more, ok });
            ClientSize = new Size(W, y);
            Refresh_();
        }

        Label PathLabel(int x, int y, int width)
        {
            Label l = Ui.Label("", Theme.Body, Theme.Text, Theme.Bg);
            l.AutoSize = false;
            l.AutoEllipsis = true;
            l.SetBounds(x, y, width, Theme.P(24));
            return l;
        }

        SyncMode Mode
        {
            get { return mMirror.Checked ? SyncMode.Spiegeln : mBackup.Checked ? SyncMode.Sichern : SyncMode.Abgleichen; }
        }

        void Refresh_()
        {
            updating = true;
            pathA.Text = a;
            pathB.Text = b;
            if (!nameEdited) name.Text = DefaultName();
            updating = false;
        }

        string DefaultName()
        {
            return ExplorerMenu.Short(a) + (Mode == SyncMode.Abgleichen ? " \u21c4 " : " \u2192 ") + ExplorerMenu.Short(b);
        }

        /// <summary>Neue Aufgabe mit den gewählten Ordnern (Name eindeutig, Standard-Filter).</summary>
        public SyncJob CreateJob(List<SyncJob> existing)
        {
            SyncJob j = new SyncJob();
            string n = name.Text.Trim();
            if (n.Length == 0) n = DefaultName();
            string unique = n;
            for (int i = 2; existing.Exists(x => string.Equals(x.Name, unique, StringComparison.OrdinalIgnoreCase)); i++)
                unique = n + " (" + i + ")";
            j.Name = unique;
            j.FolderA = a;
            j.FolderB = b;
            j.Mode = Mode;
            j.ExcludeFiles.AddRange(EditView.DefaultExcludes);
            return j;
        }
    }
}

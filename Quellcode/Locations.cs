// Sync Pilot – Orte: Variablen in Pfaden, Laufwerksnamen, Anmeldung an Netzwerkfreigaben, Geräte-Icons
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Pfade mit Platzhaltern auflösen: %VARIABLE% und %DRIVELETTER:Laufwerksname%.</summary>
    public static class Paths
    {
        static readonly Regex DriveRx = new Regex(@"%DRIVELETTER:([^%]+)%", RegexOptions.IgnoreCase);

        /// <summary>Löst alle Platzhalter auf. Wirft SyncException, wenn ein Laufwerk nicht angeschlossen ist.</summary>
        public static string Resolve(string path)
        {
            string p = (path ?? "").Trim().Trim('"');
            p = DriveRx.Replace(p, delegate (Match m)
            {
                string label = m.Groups[1].Value.Trim();
                string letter = FindDrive(label);
                if (letter == null)
                    throw new SyncException("Laufwerk „" + label + "“ ist nicht angeschlossen.");
                return letter;
            });
            p = Environment.ExpandEnvironmentVariables(p);
            return p;
        }

        /// <summary>Wie Resolve, liefert aber bei Fehlern den unveränderten Text.</summary>
        public static string TryResolve(string path)
        {
            try { return Resolve(path); }
            catch { return path ?? ""; }
        }

        public static bool HasPlaceholders(string path)
        {
            return (path ?? "").IndexOf('%') >= 0;
        }

        /// <summary>Laufwerksbuchstabe (ohne Doppelpunkt) des Laufwerks mit dieser Bezeichnung, sonst null.</summary>
        public static string FindDrive(string label)
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && string.Equals(d.VolumeLabel, label, StringComparison.OrdinalIgnoreCase))
                        return d.Name.Substring(0, 1);
                }
                catch { }
            }
            return null;
        }

        /// <summary>\\Server\Freigabe aus einem UNC-Pfad, sonst null.</summary>
        public static string ShareRoot(string path)
        {
            if (path == null || !path.StartsWith("\\\\")) return null;
            string[] parts = path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            return "\\\\" + parts[0] + "\\" + parts[1];
        }
    }

    /// <summary>Kennwörter verschlüsselt speichern (Windows-Datenschutz, nur für das eigene Benutzerkonto lesbar).</summary>
    public static class Secret
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sync Pilot");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>Anmeldung an einer Netzwerkfreigabe mit eigenem Benutzer (temporär, bis zur Abmeldung von Windows).</summary>
    public static class NetShare
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class NETRESOURCE
        {
            public int dwScope;
            public int dwType;
            public int dwDisplayType;
            public int dwUsage;
            public string lpLocalName;
            public string lpRemoteName;
            public string lpComment;
            public string lpProvider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        static extern int WNetAddConnection2(NETRESOURCE nr, string password, string user, int flags);

        const int RESOURCETYPE_DISK = 1, CONNECT_TEMPORARY = 4;
        const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

        /// <summary>Meldet an, wenn für einen UNC-Pfad ein Benutzer hinterlegt ist. Wirft SyncException bei Fehlern.</summary>
        public static void Connect(string path, string user, string protectedPassword)
        {
            if (string.IsNullOrEmpty(user)) return;
            string root = Paths.ShareRoot(path);
            if (root == null) return;
            NETRESOURCE nr = new NETRESOURCE();
            nr.dwType = RESOURCETYPE_DISK;
            nr.lpRemoteName = root;
            int rc = WNetAddConnection2(nr, Secret.Unprotect(protectedPassword), user, CONNECT_TEMPORARY);
            if (rc == 0 || rc == ERROR_SESSION_CREDENTIAL_CONFLICT) return; // 1219: bereits mit anderem Konto verbunden, diese Verbindung wird genutzt
            throw new SyncException("Anmeldung an " + root + " fehlgeschlagen: " + new Win32Exception(rc).Message);
        }
    }

    /// <summary>Gerätearten für die Icons der Ordner.</summary>
    public static class Devices
    {
        public static readonly string[] Keys = { "Auto", "Ordner", "PC", "Laptop", "Server", "NAS", "USB", "Festplatte", "Web", "Cloud", "Smartphone" };
        static readonly string[] Names = { "Automatisch", "Ordner", "PC", "Laptop", "Server", "NAS / Netzlaufwerk", "USB-Stick", "Externe Festplatte", "Web / FTP", "Cloud", "Smartphone" };
        static readonly char[] Glyphs = { '', '', '', '', '', '', '', '', '', '', '' };

        static int IndexOf(string key)
        {
            for (int i = 0; i < Keys.Length; i++)
                if (string.Equals(Keys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        public static string Name(string key) { return Names[IndexOf(key)]; }

        /// <summary>Symbol für ein Gerät; bei "Auto" aus dem Pfad ermittelt.</summary>
        public static char Glyph(string key, string path)
        {
            int i = IndexOf(key);
            if (i == 0) i = IndexOf(Detect(path));
            return Glyphs[i];
        }

        public static char GlyphOf(string key) { return Glyphs[IndexOf(key)]; }

        /// <summary>Geräteart aus dem Pfad erraten (für "Automatisch").</summary>
        public static string Detect(string path)
        {
            string p = (path ?? "").Trim();
            if (p.StartsWith("ftp", StringComparison.OrdinalIgnoreCase) || p.StartsWith("sftp", StringComparison.OrdinalIgnoreCase)) return "Web";
            if (p.StartsWith("\\\\")) return "NAS";
            if (p.IndexOf("%DRIVELETTER:", StringComparison.OrdinalIgnoreCase) >= 0) return "USB";
            try
            {
                string r = Paths.TryResolve(p);
                if (r.Length >= 2 && r[1] == ':')
                {
                    DriveInfo d = new DriveInfo(r.Substring(0, 1));
                    if (d.DriveType == DriveType.Removable) return "USB";
                    if (d.DriveType == DriveType.Network) return "NAS";
                }
            }
            catch { }
            return "PC";
        }
    }

    /// <summary>Geräteauswahl im Windows-11-Stil (geschlossen nur das Icon, Menü mit Icon und Name).</summary>
    public class DeviceCombo : ChoiceBox
    {
        public Func<string> PathForAuto;

        public DeviceCombo()
        {
            AccessibleName = "Gerät";
            IconOnly = true;
            foreach (string k in Devices.Keys) Items.Add(k);
            TextFor = delegate (int i) { return Devices.Name((string)Items[i]); };
            GlyphFor = delegate (int i)
            {
                string key = (string)Items[i];
                return key == "Auto" ? Devices.Glyph("Auto", PathForAuto != null ? PathForAuto() : "") : Devices.GlyphOf(key);
            };
            SelectedIndex = 0;
        }

        public string Key
        {
            get { return SelectedItem as string ?? "Auto"; }
            set
            {
                int i = Items.IndexOf(value ?? "Auto");
                SelectedIndex = i >= 0 ? i : 0;
            }
        }
    }

    /// <summary>Dialog: Benutzername und Kennwort für einen Netzwerkordner, mit Prüfen-Knopf.</summary>
    public class CredentialDialog : Form
    {
        readonly InputBox user, pass;
        readonly CheckBox show;
        readonly Label result;
        readonly Func<string> pathOf;
        public string User { get { return user.Text.Trim(); } }
        public string ProtectedPassword;
        /// <summary>FTPS: bestätigter Fingerabdruck des Server-Zertifikats.</summary>
        public string Cert;

        public CredentialDialog(string title, string currentUser, string protectedPassword, string cert, Func<string> pathOf)
        {
            this.pathOf = pathOf;
            ProtectedPassword = protectedPassword ?? "";
            Cert = cert;
            Text = title;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            int W = Theme.P(420), p = Theme.P(24), w = W - 2 * p, y = Theme.P(20);

            Label info = Ui.WrapLabel("F\u00fcr FTP- und SFTP-Server (ftp://, ftps://, ftpsim://, sftp://) und f\u00fcr Netzwerkfreigaben (\\\\Server\\Freigabe), die einen anderen " +
                "Benutzer brauchen als dein Windows-Konto. Das Kennwort wird verschl\u00fcsselt gespeichert und ist nur mit deinem Windows-Konto lesbar.",
                Theme.Small, Theme.TextDim, Theme.Bg);
            info.SetBounds(p, y, w, Theme.P(70));
            y += Theme.P(78);
            Label l1 = Ui.Label("Benutzername (z. B. NAS\\admin oder admin)", Theme.Body, Theme.Text, Theme.Bg);
            l1.Location = new Point(p, y);
            user = new InputBox(false, false);
            user.BackColor = Theme.Bg;
            user.Text = currentUser ?? "";
            user.Box.AccessibleName = "Benutzername";
            user.SetBounds(p, y + Theme.P(22), w, user.Height);
            y += Theme.P(70);
            Label l2 = Ui.Label("Kennwort", Theme.Body, Theme.Text, Theme.Bg);
            l2.Location = new Point(p, y);
            pass = new InputBox(false, false);
            pass.BackColor = Theme.Bg;
            pass.Box.UseSystemPasswordChar = true;
            pass.Box.AccessibleName = "Kennwort";
            pass.Text = Secret.Unprotect(ProtectedPassword);
            pass.SetBounds(p, y + Theme.P(22), w, pass.Height);
            y += Theme.P(66);
            show = new CheckBox();
            show.Text = "Kennwort anzeigen";
            show.AutoSize = true;
            show.ForeColor = Theme.Text;
            show.BackColor = Theme.Bg;
            show.Location = new Point(p, y);
            show.CheckedChanged += delegate { pass.Box.UseSystemPasswordChar = !show.Checked; };
            y += Theme.P(34);

            FlatButton test = new FlatButton("Verbindung prüfen", Theme.GRefresh, BtnKind.Secondary);
            test.Location = new Point(p, y);
            test.Click += delegate { Test(); };
            result = Ui.WrapLabel("", Theme.Small, Theme.TextDim, Theme.Bg);
            result.SetBounds(p, y + test.Height + Theme.P(6), w, Theme.P(40));
            y += test.Height + Theme.P(54);

            FlatButton ok = new FlatButton("OK", null, BtnKind.Primary);
            FlatButton cancel = new FlatButton("Abbrechen", null, BtnKind.Secondary);
            FlatButton clear = new FlatButton("Entfernen", null, BtnKind.Subtle);
            ok.Width = Math.Max(ok.Width, Theme.P(90));
            ok.Location = new Point(W - p - ok.Width, y);
            cancel.Location = new Point(ok.Left - Theme.P(10) - cancel.Width, y);
            clear.Location = new Point(p - Theme.P(8), y);
            ok.Click += delegate { Store_(); DialogResult = DialogResult.OK; };
            cancel.DialogResult = DialogResult.Cancel;
            clear.Click += delegate { user.Text = ""; pass.Text = ""; Cert = null; Store_(); DialogResult = DialogResult.OK; };
            AcceptButton = ok;
            CancelButton = cancel;
            y += ok.Height + Theme.P(20);

            Controls.AddRange(new Control[] { info, l1, user, l2, pass, show, test, result, ok, cancel, clear });
            ClientSize = new Size(W, y);
        }

        void Store_()
        {
            ProtectedPassword = Secret.Protect(pass.Text);
        }

        void Test()
        {
            result.ForeColor = Theme.TextDim;
            result.Text = "Pr\u00fcfe \u2026";
            result.Refresh();
            Cursor = Cursors.WaitCursor;
            try
            {
                string raw = pathOf();
                Store_();
                string where = null;
                if (FtpUrl.IsRemote(raw)) where = TestFtp(raw);
                else
                {
                    string path = Paths.Resolve(raw);
                    if (Paths.ShareRoot(path) == null)
                        throw new SyncException("Bitte zuerst einen Netzwerkpfad (\\\\Server\\Freigabe) oder eine FTP-Adresse eintragen.");
                    NetShare.Connect(path, User, ProtectedPassword);
                    if (!Directory.Exists(path)) throw new SyncException("Angemeldet, aber der Ordner wurde nicht gefunden: " + path);
                }
                result.ForeColor = Theme.Ok;
                result.Text = where != null ? "Verbindung erfolgreich \u2013 Ordner auf dem Server: " + where : "Verbindung erfolgreich.";
            }
            catch (Exception ex)
            {
                result.ForeColor = Theme.Err;
                result.Text = ex.Message;
            }
            finally { Cursor = Cursors.Default; }
        }

        /// <summary>Prüft ein FTP- bzw. SFTP-Ziel. Liefert bei SFTP den Ordner auf dem Server, sonst null.</summary>
        string TestFtp(string address)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                SyncJob t = new SyncJob();
                t.FolderA = address;
                t.UserA = User;
                t.PassA = ProtectedPassword;
                t.CertA = Cert;
                try
                {
                    using (IFileSide s = Sides.Open(t, true))
                    {
                        s.CheckRoot();
                        SftpSide sf = s as SftpSide;
                        return sf != null ? sf.RootPath : null;
                    }
                }
                catch (SshHostKeyException ke)
                {
                    if (attempt > 0) throw;
                    string text = ke.Changed
                        ? "ACHTUNG: Der Schl\u00fcssel des Servers hat sich ge\u00e4ndert!\n\nBisher best\u00e4tigt:\n" + Cert + "\n\nJetzt:\n" + ke.Fingerprint +
                          "\n\nDas ist normal, wenn der Server neu eingerichtet wurde. Sonst kann sich jemand als dein Server ausgeben. " +
                          "Den neuen Schl\u00fcssel nur best\u00e4tigen, wenn die \u00c4nderung bekannt ist."
                        : "Erste Verbindung zu diesem SFTP-Server.\n\nSchl\u00fcsseltyp: " + ke.KeyType + "\nFingerabdruck:\n" + ke.Fingerprint +
                          "\n\nDiesem Server vertrauen? (Zum Vergleich auf dem Server: ssh-keygen -lf auf die Schl\u00fcsseldatei, " +
                          "bei vielen NAS steht der Fingerabdruck auch in den SSH-Einstellungen.)";
                    DialogResult r = MessageBox.Show(this, text, "Sync Pilot", MessageBoxButtons.YesNo,
                        ke.Changed ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes) throw;
                    Cert = ke.Fingerprint;
                }
                catch (FtpCertificateException ce)
                {
                    if (attempt > 0) throw;
                    DialogResult r = MessageBox.Show(this,
                        "Das Zertifikat des Servers ist nicht offiziell best\u00e4tigt (z. B. selbst signiert oder auf einen anderen Namen ausgestellt).\n\n" +
                        "Ausgestellt f\u00fcr: " + ce.Subject + "\nAussteller: " + ce.Issuer + "\nG\u00fcltig bis: " + ce.ValidUntil +
                        "\nFingerabdruck: " + ce.Thumbprint + "\n\nDiesem Zertifikat f\u00fcr diese Aufgabe vertrauen?",
                        "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes) throw;
                    Cert = ce.Thumbprint;
                }
            }
            return null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size;
            MaximumSize = Size;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0084)
            {
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1;
            }
        }
    }
}
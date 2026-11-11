// Sync Pilot – Einstellungen (Hintergrund, Explorer, Handy-Zugriff)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>
    /// Einstellungen. Handy-Zugriff in zwei Spalten: links Steuerung, HTTPS und Internet,
    /// rechts QR-Code (meldet ohne Kennwort an), Link, Kurzanleitung, Status und Windows-Firewall.
    /// Änderungen gelten sofort (wie in den Windows-Einstellungen).
    /// </summary>
    public class SettingsView : BaseView
    {
        readonly Panel scroll;
        // Alles liegt auf einer Inhaltsfläche: Windows blättert nur diese eine Fläche, ihre Höhe ist der Bildlaufbereich.
        // (Vorher lagen die Karten direkt im Bildlauf und wurden mit der Bildlaufposition verrechnet – dabei konnte
        // der Inhalt nach oben „wegrutschen“, unten blieb eine große leere Fläche.)
        readonly Panel content;
        readonly Label title;
        readonly Card cBack, cExplorer, cLogs, cPhone, cTls, cNet, cOpen, cImport;
        readonly ChoiceBox cbLogDays, cbAfterAll;
        static readonly string[] AfterAllKeys = { "Nichts", "Herunterfahren", "Ruhezustand", "Energiesparen" };
        static readonly int[] LogDayChoices = { 7, 14, 30, 90, 180, 365, 0 };
        readonly ToggleSwitch tBackground, tAutostart, tAskBg, tAdmin, tExplorer, tWeb, tHttps, tInternet;

        // Steuerung per Handy
        readonly InputBox port, address;
        readonly Label lPort, lAddress, addressHint, lPass, passHint;
        readonly FlatButton btnPass, btnLogoutAll;

        // HTTPS
        readonly Pill pFile, pStore;
        readonly Label lCert, lKey, lCertPass, lName, storeHint, certStatus;
        readonly InputBox certFile, keyFile, certPass, certName;
        readonly FlatButton browseCert, browseKey, btnCertApply;

        // Internet
        readonly Label netHint;

        // Auf dem Handy öffnen
        readonly QrBox qr;
        readonly Label url, qrHint, steps, lStatus, status, lFirewall, firewall;
        readonly FlatButton btnCopy, btnFirewall;
        readonly System.Windows.Forms.Timer qrTimer;
        string qrBase;
        int firewallRun, certRun;

        bool loading = true;

        // Änderungen werden gesammelt und erst mit "Speichern" übernommen (wie Wake on LAN)
        readonly FlatButton btnSave, btnCancel;
        readonly Label saveMsg;
        string pendingPass;      // neues Kennwort (verschlüsselt), gilt erst beim Speichern
        bool pendingLogoutAll;   // "Alle Handys abmelden" erst beim Speichern

        public override string NavKey { get { return "settings"; } }
        public override string PageTitle { get { return "Einstellungen"; } }
        protected override string ExtraState() { return (pendingPass ?? "") + "|" + pendingLogoutAll; }

        public SettingsView(MainForm main) : base(main)
        {
            TrackChanges();
            AppSettings s = AppSettings.Current;
            scroll = new ScrollPanel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Theme.Bg;
            scroll.HandleCreated += delegate { Theme.DarkScroll(scroll); };
            scroll.Resize += delegate { LayoutAll(); };
            Controls.Add(scroll);
            content = new Panel();
            content.BackColor = Theme.Bg;
            content.Location = Point.Empty;
            scroll.Controls.Add(content);

            title = Ui.Label("Einstellungen", Theme.H1, Theme.Text, Theme.Bg);
            content.Controls.Add(title);

            // ---------- Hintergrund ----------
            cBack = Section("Hintergrund");
            tBackground = Row(cBack, 0, "Im Infobereich weiterlaufen",
                "Beim Schließen des Fensters läuft Sync Pilot unten rechts im Infobereich weiter – nötig für den Handy-Zugriff.");
            tAutostart = Row(cBack, 1, "Mit Windows starten",
                "Startet nach der Anmeldung unsichtbar im Infobereich.");
            // Rückfrage beim Speichern von Aufgaben mit Ereignissen (dort per „Nicht mehr fragen“ abschaltbar)
            tAskBg = Row(cBack, 2, "Bei Aufgaben mit Ereignissen nachfragen",
                "Fragt beim Speichern, ob Sync Pilot im Hintergrund und mit Windows laufen soll, solange das noch aus ist.");
            tAdmin = Row(cBack, 3, "Immer als Administrator starten",
                (Elevation.IsAdmin ? "Läuft gerade als Administrator. " : "Läuft gerade ohne. ") +
                "Windows fragt bei jedem Start nach; Ereignisse und Handy-Aufträge laufen dann ebenso.");
            tBackground.Checked = s.Background;
            tAutostart.Checked = s.Autostart;
            tAskBg.Checked = !s.NoAskBackground;
            tAdmin.Checked = s.AlwaysAdmin;
            cBack.Height = Theme.P(56 + 4 * 64 + 12);

            // ---------- Explorer ----------
            cExplorer = Section("Explorer");
            tExplorer = Row(cExplorer, 0, "Im Rechtsklickmenü von Ordnern",
                "Rechtsklick auf Ordner A › Sync Pilot › „Als Ordner A wählen“, dann Rechtsklick auf Ordner B › „Mit … abgleichen“. " +
                "Unter Windows 11 bei „Weitere Optionen anzeigen“.");
            tExplorer.Checked = s.ExplorerEntry;
            cExplorer.Height = Theme.P(56 + 64 + 12);

            // ---------- Aufgaben übernehmen ----------
            cImport = Section("Aufgaben exportieren und importieren");
            Label lExp = Ui.Label("Aufgaben exportieren", Theme.Body, Theme.Text, Theme.Surface);
            Label lExpDesc = Ui.WrapLabel("Speichert alle Aufgaben in einer Datei – als Sicherung oder für einen anderen PC. " +
                "Kennwörter werden nicht mitgespeichert.", Theme.Small, Theme.TextDim, Theme.Surface);
            FlatButton btnExp = new FlatButton("Exportieren \u2026", "\uEDE1", BtnKind.Secondary);
            btnExp.Click += delegate { ExportJobs(); };
            Label lInp = Ui.Label("Aufgaben importieren", Theme.Body, Theme.Text, Theme.Surface);
            Label lInpDesc = Ui.WrapLabel("Liest eine exportierte Datei ein. Vorhandene Aufgaben bleiben unverändert; " +
                "Aufgaben mit gleichem Namen in derselben Gruppe werden ausgelassen.", Theme.Small, Theme.TextDim, Theme.Surface);
            FlatButton btnInp = new FlatButton("Importieren \u2026", "\uE8B5", BtnKind.Secondary);
            btnInp.Click += delegate { ImportJobs(); };
            cImport.Controls.AddRange(new Control[] { lExp, lExpDesc, btnExp, lInp, lInpDesc, btnInp });
            Label lImp = Ui.Label("Aus Easy2Sync", Theme.Body, Theme.Text, Theme.Surface);
            Label lImpDesc = Ui.WrapLabel("Übernimmt Name, Ordner, Richtung, Filter, Bezeichnungen, Sperrdatei und Befehle aus der Datei " +
                "easy2sync.sdb. Kennwörter, Zeitplan und Ordnerüberwachung bitte danach selbst eintragen.", Theme.Small, Theme.TextDim, Theme.Surface);
            FlatButton btnImp = new FlatButton("Datei auswählen \u2026", Theme.GFolder, BtnKind.Secondary);
            btnImp.Click += delegate { ImportEasy2Sync(); };
            cImport.Controls.AddRange(new Control[] { lImp, lImpDesc, btnImp });
            cImport.Height = Theme.P(56 + 3 * 64 + 12);
            cImport.Resize += delegate
            {
                int y = Theme.P(52);
                // gleiche Knopfbreite, damit die drei Knöpfe bündig untereinander stehen
                int bw = Math.Max(btnImp.Width, Math.Max(btnExp.Width, btnInp.Width));
                btnExp.Width = btnInp.Width = btnImp.Width = bw;
                btnExp.Location = new Point(cImport.Width - Theme.P(20) - bw, y + (Theme.P(64) - btnExp.Height) / 2);
                lExp.Location = new Point(Theme.P(20), y + Theme.P(10));
                lExpDesc.SetBounds(Theme.P(20), y + Theme.P(30), Math.Max(10, btnExp.Left - Theme.P(32)), Theme.Small.Height * 2 + Theme.P(2));
                y += Theme.P(64);
                btnInp.Location = new Point(cImport.Width - Theme.P(20) - bw, y + (Theme.P(64) - btnInp.Height) / 2);
                lInp.Location = new Point(Theme.P(20), y + Theme.P(10));
                lInpDesc.SetBounds(Theme.P(20), y + Theme.P(30), Math.Max(10, btnInp.Left - Theme.P(32)), Theme.Small.Height * 2 + Theme.P(2));
                y += Theme.P(64);
                btnImp.Location = new Point(cImport.Width - Theme.P(20) - btnImp.Width, y + (Theme.P(64) - btnImp.Height) / 2);
                lImp.Location = new Point(Theme.P(20), y + Theme.P(10));
                lImpDesc.SetBounds(Theme.P(20), y + Theme.P(30), Math.Max(10, btnImp.Left - Theme.P(32)), Theme.Small.Height * 2 + Theme.P(2));
            };

            // ---------- Protokolle ----------
            cLogs = Section("Protokolle und Abschluss");
            Label lLogs = Ui.Label("Protokolle aufbewahren", Theme.Body, Theme.Text, Theme.Surface);
            Label lLogsDesc = Ui.WrapLabel("Ältere Protokolle werden automatisch gelöscht, der Verlauf bleibt. " +
                "Automatische Läufe ohne Änderungen schreiben kein Protokoll.", Theme.Small, Theme.TextDim, Theme.Surface);
            cbLogDays = Ui.Combo();
            cbLogDays.AccessibleName = "Protokolle aufbewahren";
            foreach (int d in LogDayChoices) cbLogDays.Items.Add(d == 0 ? "Unbegrenzt" : d + " Tage");
            int li = Array.IndexOf(LogDayChoices, s.LogDays);
            if (li < 0) { cbLogDays.Items.Add(s.LogDays + " Tage"); li = cbLogDays.Items.Count - 1; }
            cbLogDays.SelectedIndex = li;
            cbLogDays.SelectedIndexChanged += delegate { MarkDirty(); };
            Label lAfter = Ui.Label("Nach „Alle synchronisieren“", Theme.Body, Theme.Text, Theme.Surface);
            Label lAfterDesc = Ui.WrapLabel("Den PC danach ausschalten oder schlafen legen – mit 60 Sekunden Countdown zum Abbrechen. " +
                "Nicht nach einem Abbruch.", Theme.Small, Theme.TextDim, Theme.Surface);
            cbAfterAll = Ui.Combo();
            cbAfterAll.AccessibleName = "Nach Alle synchronisieren";
            cbAfterAll.Items.AddRange(new object[] { "Nichts tun", "Herunterfahren", "Ruhezustand", "Energiesparmodus" });
            cbAfterAll.SelectedIndex = Math.Max(0, Array.IndexOf(AfterAllKeys, s.AfterAll ?? "Nichts"));
            cbAfterAll.SelectedIndexChanged += delegate { MarkDirty(); };
            cLogs.Controls.AddRange(new Control[] { lLogs, lLogsDesc, cbLogDays, lAfter, lAfterDesc, cbAfterAll });
            cLogs.Height = Theme.P(56 + 2 * 64 + 12);
            cLogs.Paint += delegate (object o, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border))
                    e.Graphics.DrawLine(p, Theme.P(20), Theme.P(52 + 64), cLogs.Width - Theme.P(20), Theme.P(52 + 64));
            };
            cLogs.Resize += delegate
            {
                int y = Theme.P(52), cw = Theme.P(160);
                foreach (object[] row in new object[][] { new object[] { lLogs, lLogsDesc, cbLogDays }, new object[] { lAfter, lAfterDesc, cbAfterAll } })
                {
                    ChoiceBox cb = (ChoiceBox)row[2];
                    cb.SetBounds(cLogs.Width - Theme.P(20) - cw, y + (Theme.P(64) - cb.Height) / 2, cw, cb.Height);
                    ((Label)row[0]).Location = new Point(Theme.P(20), y + Theme.P(10));
                    ((Label)row[1]).SetBounds(Theme.P(20), y + Theme.P(30), Math.Max(10, cb.Left - Theme.P(32)), Theme.Small.Height * 2 + Theme.P(2));
                    y += Theme.P(64);
                }
                cLogs.Invalidate();
            };

            // ---------- Steuerung per Handy ----------
            cPhone = Section("Steuerung per Handy");
            tWeb = Row(cPhone, 0, "Webseite für Handy und Tablet",
                "Aufgaben ansehen, analysieren, synchronisieren und Konflikte entscheiden – im Browser des Handys.");
            tWeb.Checked = s.WebEnabled;
            lPort = FieldLabel(cPhone, "Port");
            port = Field(cPhone, "Port", false);
            port.Text = s.WebPort.ToString();
            lAddress = FieldLabel(cPhone, "Adresse für das Handy");
            address = Field(cPhone, "Adresse für das Handy", false);
            address.Text = s.WebHost ?? "";
            addressHint = Hint(cPhone, "Rechnername, IP-Adresse oder Domain/DynDNS-Name (wie im Zertifikat). Leer = automatisch. " +
                "Daraus entstehen Link und QR-Code.");
            lPass = FieldLabel(cPhone, "Kennwort");
            btnPass = new FlatButton("Festlegen …", "", BtnKind.Secondary);
            btnPass.Click += delegate { SetPassword(MinPassword()); };
            btnLogoutAll = new FlatButton("Alle Handys abmelden", null, BtnKind.Subtle);
            btnLogoutAll.Click += delegate
            {
                pendingLogoutAll = true;
                MarkDirty();
                UpdateState();
            };
            passHint = Hint(cPhone, "");
            cPhone.Controls.AddRange(new Control[] { btnPass, btnLogoutAll });
            port.Box.TextChanged += delegate { MarkDirty(); };
            address.Box.TextChanged += delegate { MarkDirty(); };

            // ---------- Verschlüsselung ----------
            cTls = Section("Verschlüsselung (HTTPS)");
            tHttps = Row(cTls, 0, "Verschlüsselt über HTTPS",
                "Mit eigenem Zertifikat, z. B. von Let's Encrypt. Nötig für den Zugriff aus dem Internet.");
            tHttps.Checked = s.WebHttps;
            pFile = new Pill("Zertifikatsdatei", false);
            pStore = new Pill("Windows-Zertifikatspeicher", false);
            pFile.Checked = s.CertMode == "Datei";
            pStore.Checked = !pFile.Checked;
            pFile.CheckedChanged += delegate { if (pFile.Checked) { UpdateCertMode(); LayoutAll(); MarkDirty(); } };
            pStore.CheckedChanged += delegate { if (pStore.Checked) { UpdateCertMode(); LayoutAll(); MarkDirty(); } };
            lCert = FieldLabel(cTls, "Zertifikat (.crt, .pem, .cer, .pfx, .p12)");
            certFile = Field(cTls, "Zertifikatsdatei", false);
            certFile.Text = s.CertFile ?? "";
            browseCert = new FlatButton("", Theme.GFolder, BtnKind.Secondary);
            browseCert.AccessibleName = "Zertifikat auswählen";
            browseCert.Click += delegate { ChooseFile(certFile, "Zertifikat auswählen", "Zertifikate (*.crt;*.pem;*.cer;*.pfx;*.p12)|*.crt;*.pem;*.cer;*.pfx;*.p12|Alle Dateien (*.*)|*.*", true); };
            lKey = FieldLabel(cTls, "Privater Schlüssel (.key, .pem) – nicht bei .pfx/.p12");
            keyFile = Field(cTls, "Privater Schlüssel", false);
            keyFile.Text = s.CertKeyFile ?? "";
            browseKey = new FlatButton("", Theme.GFolder, BtnKind.Secondary);
            browseKey.AccessibleName = "Privaten Schlüssel auswählen";
            browseKey.Click += delegate { ChooseFile(keyFile, "Privaten Schlüssel auswählen", "Schlüssel (*.key;*.pem)|*.key;*.pem|Alle Dateien (*.*)|*.*", false); };
            lCertPass = FieldLabel(cTls, "Kennwort (nur .pfx/.p12)");
            certPass = Field(cTls, "Kennwort der PFX-Datei", false);
            certPass.Box.UseSystemPasswordChar = true;
            certPass.Text = Secret.Unprotect(s.CertPass);
            lName = FieldLabel(cTls, "Domain im Zertifikat (z. B. sync.meinedomain.de)");
            certName = Field(cTls, "Domain im Zertifikat", false);
            certName.Text = s.CertName ?? "";
            storeHint = Hint(cTls, "Z. B. von win-acme abgelegt und automatisch verlängert – Sync Pilot nimmt immer das neueste gültige Zertifikat.");
            btnCertApply = new FlatButton("Prüfen und übernehmen", "", BtnKind.Secondary);
            btnCertApply.Click += delegate { ApplyCertificate(); };
            certStatus = Hint(cTls, "");
            cTls.Controls.AddRange(new Control[] { pFile, pStore, browseCert, browseKey, btnCertApply });

            // ---------- Internet ----------
            cNet = Section("Zugriff aus dem Internet");
            tInternet = Row(cNet, 0, "Auch aus dem Internet erreichbar",
                "Portweiterleitung im Router auf diesen PC. Nur mit HTTPS und einem Kennwort ab 12 Zeichen.");
            tInternet.Checked = s.WebInternet && s.WebHttps;
            netHint = Hint(cNet, "Schutz: Nach 5 falschen Kennwörtern ist eine Adresse 10 Minuten gesperrt, nach 15 in einer Stunde die Anmeldung " +
                "von außen 1 Stunde – im Heimnetz geht es weiter. Aus: Die Seite nimmt nur Verbindungen aus dem Heimnetz und per VPN an.");

            // ---------- Auf dem Handy öffnen ----------
            cOpen = Section("Auf dem Handy öffnen");
            qr = new QrBox();
            qr.AccessibleName = "QR-Code zum Anmelden am Handy";
            url = Ui.Label("", Theme.Bold, Theme.AccentSoftText, Theme.Surface);
            url.AutoSize = false;
            url.AutoEllipsis = true;
            url.TextAlign = ContentAlignment.MiddleCenter;
            btnCopy = new FlatButton("Link kopieren", "", BtnKind.Secondary);
            btnCopy.Click += delegate
            {
                try { Clipboard.SetText(WebServer.PhoneUrl() + "/"); SetCopyText("Kopiert"); } catch { }
            };
            qrHint = Hint(cOpen, "Der QR-Code meldet das Handy ohne Kennwort an. Er gilt nur einmal, nur solange er hier zu sehen ist (höchstens 10 Minuten) und wird von selbst erneuert.");
            steps = Hint(cOpen, "1. QR-Code mit der Handy-Kamera scannen (Handy im WLAN, per VPN oder – wenn eingeschaltet – aus dem Internet).\n" +
                "2. Das Handy ist sofort angemeldet und bleibt es 30 Tage. Ohne Kamera: Link öffnen und Kennwort eingeben.\n" +
                "3. Im Browsermenü „Zum Startbildschirm hinzufügen“ bzw. „App installieren“ – Sync Pilot öffnet sich dann wie eine App.");
            lStatus = Ui.Label("Status", Theme.Bold, Theme.Text, Theme.Surface);
            status = Hint(cOpen, "");
            lFirewall = Ui.Label("Windows-Firewall", Theme.Bold, Theme.Text, Theme.Surface);
            firewall = Hint(cOpen, "");
            btnFirewall = new FlatButton("Freigeben …", "", BtnKind.Secondary);
            btnFirewall.Click += delegate { AllowFirewall(); };
            cOpen.Controls.AddRange(new Control[] { qr, url, btnCopy, lStatus, lFirewall, btnFirewall });

            qrTimer = new System.Windows.Forms.Timer();
            qrTimer.Interval = 9 * 60 * 1000; // vor Ablauf des 10-Minuten-Codes erneuern
            qrTimer.Tick += delegate { qrBase = null; UpdateQr(); };
            qrTimer.Start();

            foreach (Card c in new Card[] { cPhone, cTls, cNet, cOpen }) c.Resize += delegate { LayoutAll(); };

            tBackground.CheckedChanged += delegate { MarkDirty(); };
            tAutostart.CheckedChanged += delegate { MarkDirty(); };
            tAskBg.CheckedChanged += delegate { MarkDirty(); };
            tAdmin.CheckedChanged += delegate { MarkDirty(); };
            tExplorer.CheckedChanged += delegate { MarkDirty(); };
            foreach (InputBox b in new InputBox[] { certFile, keyFile, certPass, certName })
                b.Box.TextChanged += delegate { MarkDirty(); };

            // ---------- Speichern / Abbrechen (rechts unter den Karten) ----------
            btnSave = new FlatButton("Speichern", null, BtnKind.Primary);
            btnCancel = new FlatButton("Abbrechen", null, BtnKind.Secondary);
            saveMsg = Ui.WrapLabel("", Theme.Body, Theme.TextDim, Theme.Bg);
            btnSave.Click += delegate { Save(); };
            btnCancel.Click += delegate { Discard(); };
            content.Controls.AddRange(new Control[] { btnSave, btnCancel, saveMsg });
            tWeb.CheckedChanged += delegate { WebToggled(); };
            tHttps.CheckedChanged += delegate { HttpsToggled(); };
            tInternet.CheckedChanged += delegate { InternetToggled(); };
            UpdateCertMode();
            loading = false;
            UpdateState();
            CheckFirewall();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                qrTimer.Dispose();
                WebAuth.CancelPairCode(); // QR-Code gilt nur, solange er zu sehen ist
            }
            base.Dispose(disposing);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (qrTimer == null) return;
            if (!Visible)
            {
                // Fenster geschlossen bzw. im Infobereich: angezeigten Code ungültig machen, beim Wiederanzeigen neuer Code
                WebAuth.CancelPairCode();
                qrBase = null;
            }
            else UpdateQr();
        }

        // =============== Aufbau ===============

        /// <summary>Aufgaben aus einer easy2sync.sdb lesen, Liste zeigen und nach Rückfrage anlegen (nur manuell, ohne Zeitplan).</summary>
        void ExportJobs()
        {
            if (Main.Jobs.Count == 0)
            {
                MessageBox.Show("Es gibt noch keine Aufgaben zum Exportieren.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string file;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Aufgaben exportieren";
                d.Filter = "Sync-Pilot-Aufgaben (*.xml)|*.xml";
                d.DefaultExt = "xml";
                d.FileName = "Sync Pilot Aufgaben " + DateTime.Now.ToString("yyyy-MM-dd") + ".xml";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                file = d.FileName;
            }
            try { Store.ExportJobs(Main.Jobs, file); }
            catch (Exception ex)
            {
                MessageBox.Show("Die Datei konnte nicht geschrieben werden:\n" + ex.Message, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int withPass = Main.Jobs.Count(j => !string.IsNullOrEmpty(j.PassA) || !string.IsNullOrEmpty(j.PassB));
            MessageBox.Show(Main.Jobs.Count + (Main.Jobs.Count == 1 ? " Aufgabe wurde" : " Aufgaben wurden") + " exportiert." +
                (withPass > 0 ? "\n\nKennwörter sind nicht enthalten – bei " + withPass + (withPass == 1 ? " Aufgabe" : " Aufgaben") +
                    " nach dem Importieren neu eintragen." : ""),
                "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ImportJobs()
        {
            string file;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Exportierte Aufgaben auswählen";
                d.Filter = "Sync-Pilot-Aufgaben (*.xml)|*.xml|Alle Dateien (*.*)|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                file = d.FileName;
            }
            List<SyncJob> found;
            try { found = Store.ReadExport(file); }
            catch (Exception)
            {
                MessageBox.Show("Die Datei ist keine Aufgabenliste von Sync Pilot oder nicht lesbar.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // Doppelt ist, was es mit gleichem Namen in derselben Gruppe schon gibt (auch innerhalb der Datei)
            HashSet<string> names = new HashSet<string>(Main.Jobs.Select(x => (x.Group ?? "").Trim() + "\n" + x.Name), StringComparer.OrdinalIgnoreCase);
            List<SyncJob> fresh = new List<SyncJob>();
            foreach (SyncJob j in found)
                if (!string.IsNullOrWhiteSpace(j.Name) && names.Add((j.Group ?? "").Trim() + "\n" + j.Name)) fresh.Add(j);
            int dup = found.Count - fresh.Count;
            if (fresh.Count == 0)
            {
                MessageBox.Show(found.Count == 0 ? "In der Datei wurden keine Aufgaben gefunden."
                    : "Alle " + found.Count + " Aufgaben gibt es in Sync Pilot schon (gleicher Name und gleiche Gruppe).", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(fresh.Count + (fresh.Count == 1 ? " Aufgabe" : " Aufgaben") + " gefunden" +
                (dup > 0 ? " (" + dup + " mit schon vorhandenem Namen werden ausgelassen)" : "") + ":");
            sb.AppendLine();
            foreach (SyncJob j in fresh.Take(15))
                sb.AppendLine("• " + (string.IsNullOrWhiteSpace(j.Group) ? "" : j.Group + " › ") + j.Name + "  –  " + j.ModeText);
            if (fresh.Count > 15) sb.AppendLine("• … und " + (fresh.Count - 15) + " weitere");
            sb.AppendLine();
            sb.AppendLine("Der erste Lauf gleicht ab wie beim ersten Mal: Was nur auf einer Seite liegt, wird kopiert, " +
                "nichts wird gelöscht. Bitte vorher „Analysieren“. Kennwörter bitte neu eintragen.");

            int auto = fresh.Count(j => !j.Disabled && (j.Schedule != ScheduleKind.Manuell || j.HasEvents));
            bool asDisabled = false;
            if (auto > 0)
            {
                DialogResult r = MessageBox.Show(sb.ToString() + "\n" + auto + (auto == 1 ? " Aufgabe startet" : " Aufgaben starten") +
                    " von selbst (Zeitplan oder Ereignis).\n\n" +
                    "Ja = so übernehmen, Zeitplan und Ereignisse sind gleich aktiv\n" +
                    "Nein = diese Aufgaben deaktiviert übernehmen und später einzeln einschalten",
                    "Sync Pilot", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r == DialogResult.Cancel) return;
                asDisabled = r == DialogResult.No;
            }
            else if (MessageBox.Show(sb.ToString() + "\nJetzt übernehmen?", "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            HashSet<string> ids = new HashSet<string>(Main.Jobs.Select(x => x.Id ?? ""), StringComparer.OrdinalIgnoreCase);
            List<string> errors = new List<string>();
            int disabledNow = 0;
            foreach (SyncJob j in fresh)
            {
                // die ID bleibt, wenn sie frei ist (Aufrufe mit /run gehen dann weiter), sonst eine neue
                if (string.IsNullOrEmpty(j.Id) || !ids.Add(j.Id)) { j.Id = Guid.NewGuid().ToString("N"); ids.Add(j.Id); }
                if (asDisabled && !j.Disabled && (j.Schedule != ScheduleKind.Manuell || j.HasEvents)) { j.Disabled = true; disabledNow++; }
                Main.Jobs.Add(j);
                StateStore.Reset(j.Id); // neu anfangen, nicht nach einem verlorenen Stand fragen
                if (!j.Disabled && j.Schedule != ScheduleKind.Manuell)
                {
                    string err = Scheduler.Apply(j);
                    if (err != null) errors.Add(j.Name + ": " + err);
                }
            }
            Main.SaveJobs();
            string msg = fresh.Count + (fresh.Count == 1 ? " Aufgabe wurde" : " Aufgaben wurden") + " importiert.";
            if (disabledNow > 0)
                msg += "\n\n" + disabledNow + (disabledNow == 1 ? " davon ist" : " davon sind") + " deaktiviert" +
                    (AppSettings.Current.HideDisabled ? " und gerade ausgeblendet – in der Aufgabenliste über das Auge einblenden." : ".");
            if (errors.Count > 0)
                msg += "\n\nZeitplan nicht eingerichtet:\n" + string.Join("\n", errors.Take(10).Select(x => "• " + x));
            MessageBox.Show(msg, "Sync Pilot", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        void ImportEasy2Sync()
        {
            string file;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Aufgabenliste von Easy2Sync auswählen";
                d.Filter = "Easy2Sync-Aufgaben (*.sdb)|*.sdb|Alle Dateien (*.*)|*.*";
                d.FileName = "easy2sync.sdb";
                try
                {
                    string guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Easy2Sync für Dateien");
                    if (Directory.Exists(guess)) d.InitialDirectory = guess;
                }
                catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                file = d.FileName;
            }
            List<ImportedJob> found;
            Cursor = Cursors.WaitCursor;
            try { found = Easy2SyncImport.Read(file); }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show("Die Datei konnte nicht gelesen werden:\n" + ex.Message, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Cursor = Cursors.Default;
            // nicht lesbare Aufgaben nicht still verschlucken
            string skippedText = Easy2SyncImport.LastSkipped.Count == 0 ? "" :
                "\n\nNicht übernommen:\n" + string.Join("\n", Easy2SyncImport.LastSkipped.Take(10).Select(x => "• " + x)) +
                (Easy2SyncImport.LastSkipped.Count > 10 ? "\n• … und " + (Easy2SyncImport.LastSkipped.Count - 10) + " weitere" : "");

            // Gruppen und kurze Namen vorschlagen ("Sync PC <-> NAS (Dokumente)" -> Gruppe "NAS", Name "Dokumente")
            int canGroup = 0;
            foreach (ImportedJob ij in found)
            {
                string g, n;
                if (string.IsNullOrWhiteSpace(ij.Job.Group) && Easy2SyncImport.SuggestGroup(ij.Job.Name, out g, out n)) canGroup++;
            }
            if (canGroup > 0)
            {
                ImportedJob sample = found.First(x => { string g0, n0; return Easy2SyncImport.SuggestGroup(x.Job.Name, out g0, out n0); });
                string sg, sn;
                Easy2SyncImport.SuggestGroup(sample.Job.Name, out sg, out sn);
                DialogResult gr = MessageBox.Show(canGroup + " von " + found.Count + " Aufgaben lassen sich nach Gruppen ordnen und kürzer benennen, z. B.:\n\n" +
                    "„" + sample.Job.Name + "“\n→ Gruppe „" + sg + "“, Name „" + sn + "“\n\n" +
                    "Ja = mit Gruppen und kurzen Namen übernehmen\nNein = Namen unverändert übernehmen",
                    "Sync Pilot", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (gr == DialogResult.Cancel) return;
                if (gr == DialogResult.Yes)
                    foreach (ImportedJob ij in found)
                    {
                        string g, n;
                        if (!string.IsNullOrWhiteSpace(ij.Job.Group) || !Easy2SyncImport.SuggestGroup(ij.Job.Name, out g, out n)) continue;
                        ij.Job.Group = g;
                        ij.Job.Name = n;
                    }
            }

            // Doppelt ist, was es mit gleichem Namen in derselben Gruppe schon gibt (auch innerhalb der Datei)
            HashSet<string> names = new HashSet<string>(Main.Jobs.Select(x => (x.Group ?? "").Trim() + "\n" + x.Name), StringComparer.OrdinalIgnoreCase);
            List<ImportedJob> fresh = new List<ImportedJob>();
            foreach (ImportedJob ij in found)
                if (names.Add((ij.Job.Group ?? "").Trim() + "\n" + ij.Job.Name)) fresh.Add(ij);
            int dup = found.Count - fresh.Count;
            if (fresh.Count == 0)
            {
                MessageBox.Show(found.Count == 0 ? "In der Datei wurden keine Aufgaben gefunden."
                    : "Alle " + found.Count + " Aufgaben gibt es in Sync Pilot schon (gleicher Name und gleiche Gruppe)." + skippedText, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(fresh.Count + (fresh.Count == 1 ? " Aufgabe" : " Aufgaben") + " gefunden" +
                (dup > 0 ? " (" + dup + " mit schon vorhandenem Namen werden ausgelassen)" : "") + ":");
            sb.AppendLine();
            foreach (ImportedJob ij in fresh.Take(15))
                sb.AppendLine("• " + (string.IsNullOrWhiteSpace(ij.Job.Group) ? "" : ij.Job.Group + " › ") + ij.Job.Name + "  –  " + ij.Job.ModeText);
            if (fresh.Count > 15) sb.AppendLine("• … und " + (fresh.Count - 15) + " weitere");
            sb.AppendLine();
            sb.AppendLine("Übernommen werden sie ohne Zeitplan (nur manuell). Der erste Lauf gleicht ab wie beim ersten Mal: " +
                "Was nur auf einer Seite liegt, wird kopiert, nichts wird gelöscht. Bitte vorher „Analysieren“.");
            if (MessageBox.Show(sb.ToString() + skippedText + "\n\nJetzt übernehmen?", "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            List<string> notes = new List<string>();
            foreach (ImportedJob ij in fresh)
            {
                Main.Jobs.Add(ij.Job);
                StateStore.Reset(ij.Job.Id); // neu anfangen, nicht nach einem verlorenen Stand fragen
                List<string> imp = ij.Notes.Where(n => n.StartsWith("Anmeldung")).ToList();
                foreach (string n in imp) notes.Add(ij.Job.Name + ": " + n);
            }
            Main.SaveJobs();
            string msg = fresh.Count + (fresh.Count == 1 ? " Aufgabe wurde" : " Aufgaben wurden") + " übernommen.";
            if (notes.Count > 0) msg += "\n\nBitte nachtragen:\n" + string.Join("\n", notes.Select(n => "• " + n));
            msg += "\n\nZeitplan und Ordnerüberwachung bitte je Aufgabe neu einstellen.";
            MessageBox.Show(msg, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        Card Section(string heading)
        {
            Card c = new Card();
            Label h = Ui.Label(heading, Theme.H2, Theme.Text, Theme.Surface);
            h.Location = new Point(Theme.P(20), Theme.P(16));
            c.Controls.Add(h);
            content.Controls.Add(c);
            return c;
        }

        ToggleSwitch Row(Card c, int index, string titleText, string desc)
        {
            int y = Theme.P(52) + index * Theme.P(64);
            Label t = Ui.Label(titleText, Theme.Body, Theme.Text, Theme.Surface);
            t.Location = new Point(Theme.P(20), y + Theme.P(10));
            Label d = Ui.WrapLabel(desc, Theme.Small, Theme.TextDim, Theme.Surface);
            ToggleSwitch tg = new ToggleSwitch();
            tg.AccessibleName = titleText;
            t.Click += delegate { if (tg.Enabled) tg.Checked = !tg.Checked; };
            c.Controls.AddRange(new Control[] { t, d, tg });
            c.Resize += delegate
            {
                tg.Location = new Point(c.Width - Theme.P(20) - tg.Width, y + (Theme.P(64) - tg.Height) / 2);
                d.SetBounds(Theme.P(20), y + Theme.P(30), Math.Max(10, tg.Left - Theme.P(32)), Theme.P(32));
            };
            return tg;
        }

        static Label FieldLabel(Card c, string text)
        {
            Label l = Ui.Label(text, Theme.Body, Theme.Text, Theme.Surface);
            l.AutoSize = false;
            l.AutoEllipsis = true;
            c.Controls.Add(l);
            return l;
        }

        static InputBox Field(Card c, string accessibleName, bool mono)
        {
            InputBox b = new InputBox(mono, false);
            b.BackColor = Theme.Surface;
            b.Box.AccessibleName = accessibleName;
            c.Controls.Add(b);
            return b;
        }

        static Label Hint(Card c, string text)
        {
            Label l = Ui.WrapLabel(text, Theme.Small, Theme.TextDim, Theme.Surface);
            c.Controls.Add(l);
            return l;
        }

        /// <summary>Höhe eines Hinweistexts bei gegebener Breite.</summary>
        static int TextHeight(Label l, int width)
        {
            if (string.IsNullOrEmpty(l.Text)) return 0;
            return TextRenderer.MeasureText(l.Text, l.Font, new Size(Math.Max(10, width), 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.P(2);
        }

        /// <summary>Hinweis an Position y setzen, liefert das neue y (darunter).</summary>
        static int PlaceHint(Label l, int x, int y, int w)
        {
            int h = TextHeight(l, w);
            l.SetBounds(x, y, w, Math.Max(1, h));
            l.Visible = h > 0;
            return h > 0 ? y + h + Theme.P(10) : y;
        }

        // =============== Anordnung ===============

        bool laying;

        void LayoutAll()
        {
            if (cOpen == null || laying) return;
            laying = true;
            try
            {
                int pad = Theme.P(28), gap = Theme.P(16);
                // feste Breite ohne Bildlaufleiste: sonst springt beim Ein-/Ausblenden der Leiste auch eine waagrechte Leiste ein
                int cw = Math.Max(Theme.P(420) + 2 * pad, scroll.Width - SystemInformation.VerticalScrollBarWidth);
                int W = Math.Max(Theme.P(420), Math.Min(cw - 2 * pad, Theme.P(1200)));
                int y0 = 0;
                title.Location = new Point(pad - Theme.P(3), y0 + Theme.P(20));
                int y = y0 + Theme.P(84);
                cBack.SetBounds(pad, y, W, cBack.Height);
                y += cBack.Height + gap;
                cExplorer.SetBounds(pad, y, W, cExplorer.Height);
                y += cExplorer.Height + gap;
                cImport.SetBounds(pad, y, W, cImport.Height);
                y += cImport.Height + gap;
                cLogs.SetBounds(pad, y, W, cLogs.Height);
                y += cLogs.Height + gap;

                bool two = W >= Theme.P(900);
                int leftW = two ? (W - gap) * 58 / 100 : W;
                int rightW = two ? W - gap - leftW : W;
                int ly = y;
                cPhone.SetBounds(pad, ly, leftW, LayoutPhone(leftW));
                ly += cPhone.Height + gap;
                cTls.SetBounds(pad, ly, leftW, LayoutTls(leftW));
                ly += cTls.Height + gap;
                cNet.SetBounds(pad, ly, leftW, LayoutNet(leftW));
                ly += cNet.Height;
                int openH = LayoutOpen(rightW);
                if (two)
                {
                    // rechte Spalte unten bündig mit der linken
                    cOpen.SetBounds(pad + leftW + gap, y, rightW, Math.Max(openH, ly - y));
                    y = Math.Max(ly, cOpen.Bottom);
                }
                else
                {
                    cOpen.SetBounds(pad, ly + gap, rightW, openH);
                    y = cOpen.Bottom;
                }
                // "Abbrechen" links, "Speichern" (primär) ganz rechts, 10 Abstand; links davon die Fehlerzeile
                y += gap + Theme.P(4);
                btnSave.Location = new Point(pad + W - btnSave.Width, y);
                btnCancel.Location = new Point(btnSave.Left - Theme.P(10) - btnCancel.Width, y);
                saveMsg.SetBounds(pad, y, Math.Max(Theme.P(40), btnCancel.Left - pad - Theme.P(12)), btnSave.Height);
                y += btnSave.Height;
                content.Size = new Size(cw, y + Theme.P(24));
            }
            finally { laying = false; }
        }

        int LayoutPhone(int W)
        {
            int p = Theme.P(20), w = W - 2 * p, g = Theme.P(12);
            int y = Theme.P(52 + 64 + 8);
            int portW = Theme.P(96);
            lPort.SetBounds(p, y, portW, Theme.P(22));
            lAddress.SetBounds(p + portW + g, y, w - portW - g, Theme.P(22));
            y += Theme.P(24);
            port.SetBounds(p, y, portW, port.Height);
            address.SetBounds(p + portW + g, y, w - portW - g, address.Height);
            y += address.Height + Theme.P(8);
            y = PlaceHint(addressHint, p, y, w);
            lPass.SetBounds(p, y, w, Theme.P(22));
            y += Theme.P(24);
            btnPass.Location = new Point(p, y);
            btnLogoutAll.Location = new Point(btnPass.Right + Theme.P(8), y);
            y += btnPass.Height + Theme.P(8);
            y = PlaceHint(passHint, p, y, w);
            return y + Theme.P(8);
        }

        int LayoutTls(int W)
        {
            int p = Theme.P(20), w = W - 2 * p, g = Theme.P(8);
            int y = Theme.P(52 + 64 + 8);
            pFile.Location = new Point(p, y);
            pStore.Location = new Point(pFile.Right + g, y);
            y += pFile.Height + Theme.P(14);
            int bw = Theme.P(40);
            if (pFile.Checked)
            {
                lCert.SetBounds(p, y, w, Theme.P(22));
                y += Theme.P(24);
                certFile.SetBounds(p, y, w - bw - g, certFile.Height);
                browseCert.SetBounds(p + w - bw, y, bw, certFile.Height);
                y += certFile.Height + Theme.P(12);
                lKey.SetBounds(p, y, w, Theme.P(22));
                y += Theme.P(24);
                keyFile.SetBounds(p, y, w - bw - g, keyFile.Height);
                browseKey.SetBounds(p + w - bw, y, bw, keyFile.Height);
                y += keyFile.Height + Theme.P(12);
                lCertPass.SetBounds(p, y, w, Theme.P(22));
                y += Theme.P(24);
                int pw = Math.Min(Theme.P(200), w - btnCertApply.Width - g);
                certPass.SetBounds(p, y, pw, certPass.Height);
                btnCertApply.Location = new Point(p + pw + g, y + (certPass.Height - btnCertApply.Height) / 2);
                y += certPass.Height + Theme.P(10);
            }
            else
            {
                lName.SetBounds(p, y, w, Theme.P(22));
                y += Theme.P(24);
                int nw = w - btnCertApply.Width - g;
                certName.SetBounds(p, y, nw, certName.Height);
                btnCertApply.Location = new Point(p + nw + g, y + (certName.Height - btnCertApply.Height) / 2);
                y += certName.Height + Theme.P(10);
                y = PlaceHint(storeHint, p, y, w);
            }
            y = PlaceHint(certStatus, p, y, w);
            return y + Theme.P(8);
        }

        int LayoutNet(int W)
        {
            int p = Theme.P(20), w = W - 2 * p;
            int y = Theme.P(52 + 64 + 4);
            y = PlaceHint(netHint, p, y, w);
            return y + Theme.P(8);
        }

        int LayoutOpen(int W)
        {
            int p = Theme.P(20), w = W - 2 * p;
            int y = Theme.P(52);
            int q = Math.Min(Theme.P(220), w);
            qr.SetBounds((W - q) / 2, y, q, q);
            y += q + Theme.P(10);
            url.SetBounds(p, y, w, Theme.P(24));
            y += Theme.P(28);
            btnCopy.Location = new Point((W - btnCopy.Width) / 2, y);
            y += btnCopy.Height + Theme.P(12);
            y = PlaceHint(qrHint, p, y, w);
            y = PlaceHint(steps, p, y, w);
            lStatus.Location = new Point(p, y);
            y += Theme.P(24);
            y = PlaceHint(status, p, y, w);
            lFirewall.Location = new Point(p, y);
            y += Theme.P(24);
            y = PlaceHint(firewall, p, y, w);
            btnFirewall.Location = new Point(p, y);
            y += btnFirewall.Height + Theme.P(16);
            return y;
        }

        void SetCopyText(string text)
        {
            if (btnCopy.Text == text) return;
            btnCopy.Text = text;
            btnCopy.FitWidth();
            LayoutAll();
        }

        // =============== Hintergrund ===============

        // Änderung auf der Seite: eine alte Fehlermeldung verschwindet (übernommen wird erst mit "Speichern")
        void MarkDirty()
        {
            if (loading || saveMsg == null) return;
            if (saveMsg.Text.Length > 0) { saveMsg.Text = ""; }
        }

        // Enter = Speichern, Esc = Abbrechen (wie AcceptButton/CancelButton); Enter auf einem Knopf löst diesen aus
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Discard(); return true; }
            if (keyData == Keys.Enter)
            {
                Control a = ActiveDeepest();
                bool multi = a is TextBox && ((TextBox)a).Multiline;
                if (!(a is ButtonBase) && !(a is ChoiceBox) && !multi) { Save(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        Control ActiveDeepest()
        {
            Control c = ActiveControl;
            while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
            Form f = FindForm();
            if (f != null && f.ActiveControl != null)
            {
                c = f.ActiveControl;
                while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
            }
            return c;
        }

        // "Abbrechen": Seite aus den gespeicherten Einstellungen neu aufbauen (Änderungen verwerfen)
        /// <summary>Nach dem Speichern wieder an dieselbe Stelle blättern.</summary>
        public void ScrollTo(int y)
        {
            if (y <= 0) return;
            BeginInvoke(new Action(delegate
            {
                try { scroll.AutoScrollPosition = new Point(0, y); } catch { }
            }));
        }

        void Discard()
        {
            DropChanges(); // Abbrechen verwirft ohne Rückfrage
            BeginInvoke(new Action(delegate { Main.Navigate(new SettingsView(Main)); }));
        }

        // =============== Steuerung per Handy ===============

        int MinPassword() { return tInternet.Checked ? 12 : 8; }

        void SetLoading(ToggleSwitch t, bool value)
        {
            loading = true;
            t.Checked = value;
            loading = false;
        }

        void WebToggled()
        {
            if (loading) return;
            // ohne Kennwort geht es nicht: gleich festlegen (gilt erst beim Speichern)
            if (tWeb.Checked && !HasPassword() && !SetPassword(MinPassword()))
            {
                SetLoading(tWeb, false);
                return;
            }
            // ohne Hintergrundbetrieb wäre die Seite weg, sobald das Fenster zu ist
            if (tWeb.Checked && !tBackground.Checked) tBackground.Checked = true;
            MarkDirty();
            UpdateState();
        }

        /// <summary>Kennwort-Dialog; das neue Kennwort wird erst beim Speichern übernommen.</summary>
        bool SetPassword(int minLength)
        {
            using (PasswordDialog d = new PasswordDialog(HasPassword(), minLength))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                pendingPass = Secret.Protect(d.Password);
            }
            MarkDirty();
            UpdateState();
            return true;
        }

        int PendingPasswordLength()
        {
            return Secret.Unprotect(pendingPass ?? AppSettings.Current.WebPass).Length;
        }

        bool HasPassword() { return pendingPass != null || AppSettings.Current.HasWebPassword; }

        // =============== Speichern ===============

        void Fail(string text, Control focus)
        {
            saveMsg.Text = text;
            saveMsg.ForeColor = Theme.Err;
            LayoutAll();
            if (focus != null) focus.Focus();
        }

        /// <summary>Alle gesammelten Änderungen übernehmen; den Webserver nur neu starten, wenn nötig.</summary>
        void Save()
        {
            AppSettings s = AppSettings.Current;
            int p;
            if (!int.TryParse(port.Text.Trim(), out p) || p < 1024 || p > 65535) { Fail("Der Port muss zwischen 1024 und 65535 liegen.", port.Box); return; }
            string a = address.Text.Trim();
            if (a.Contains("/") || a.Contains(" ") || a.Contains("://"))
            { Fail("Bei der Adresse bitte nur den Namen bzw. die IP eintragen, ohne http:// und ohne Pfad.", address.Box); return; }
            if (tWeb.Checked && !HasPassword()) { Fail("Bitte unter „Festlegen …“ ein Kennwort festlegen.", btnPass); return; }
            if (tInternet.Checked && !tHttps.Checked) { Fail("Zugriff aus dem Internet geht nur mit HTTPS.", tHttps); return; }
            if (tInternet.Checked && PendingPasswordLength() < 12)
            { Fail("Für den Zugriff aus dem Internet braucht das Kennwort mindestens 12 Zeichen – bitte unter „Ändern …“ ein neues festlegen.", btnPass); return; }

            string mode = pFile.Checked ? "Datei" : "Speicher";
            string cf = certFile.Text.Trim(), kf = CertLoader.IsPfx(cf) ? "" : keyFile.Text.Trim(), name = certName.Text.Trim();
            bool passOfCertChanged = certPass.Text != Secret.Unprotect(s.CertPass);
            string protectedCertPass = passOfCertChanged ? Secret.Protect(certPass.Text) : s.CertPass;
            bool certChanged = mode != (s.CertMode ?? "Speicher") || cf != (s.CertFile ?? "") || kf != (s.CertKeyFile ?? "") ||
                               name != (s.CertName ?? "") || passOfCertChanged;
            List<string> names = null;
            if (tHttps.Checked && (certChanged || !s.WebHttps))
            {
                // Zertifikat vor dem Übernehmen prüfen (wie Wake on LAN)
                if (mode == "Datei" ? cf.Length == 0 : name.Length == 0)
                {
                    Fail(mode == "Datei" ? "Bitte die Zertifikatsdatei auswählen." : "Bitte die Domain des Zertifikats eintragen.",
                        mode == "Datei" ? certFile.Box : certName.Box);
                    return;
                }
                Cursor = Cursors.WaitCursor;
                try
                {
                    using (X509Certificate2 c = CertProvider.Load(mode, name, cf, kf, protectedCertPass, CertLoader.TestKeyName))
                    {
                        names = CertProvider.DnsNames(c);
                        if (c.NotAfter < DateTime.Now) { Fail("Das Zertifikat ist abgelaufen (" + c.NotAfter.ToString("dd.MM.yyyy") + ").", null); return; }
                    }
                }
                catch (Exception ex) { Fail(ex.Message, null); return; }
                finally { Cursor = Cursors.Default; CertLoader.DeleteTestKey(); }
            }

            int logDays = cbLogDays.SelectedIndex >= 0 && cbLogDays.SelectedIndex < LogDayChoices.Length ? LogDayChoices[cbLogDays.SelectedIndex] : s.LogDays;
            bool explorerChanged = tExplorer.Checked != s.ExplorerEntry;
            bool logsChanged = logDays != s.LogDays;
            bool passChanged = pendingPass != null;

            s.Background = tBackground.Checked;
            s.Autostart = tAutostart.Checked;
            s.NoAskBackground = !tAskBg.Checked;
            bool adminNow = tAdmin.Checked && !s.AlwaysAdmin && !Elevation.IsAdmin;
            s.AlwaysAdmin = tAdmin.Checked;
            s.ExplorerEntry = tExplorer.Checked;
            s.LogDays = logDays;
            s.AfterAll = AfterAllKeys[Math.Max(0, Math.Min(AfterAllKeys.Length - 1, cbAfterAll.SelectedIndex))];
            s.WebEnabled = tWeb.Checked;
            s.WebPort = p;
            // Adresse aus dem Zertifikat übernehmen, wenn keine eingetragen ist
            if (a.Length == 0 && names != null && names.Count > 0 && !names[0].StartsWith("*")) a = names[0];
            s.WebHost = a;
            s.WebHttps = tHttps.Checked;
            s.WebInternet = tInternet.Checked;
            if (certChanged)
            {
                s.CertMode = mode;
                s.CertFile = cf;
                s.CertKeyFile = kf;
                s.CertPass = protectedCertPass;
                s.CertName = name;
            }
            if (passChanged) s.WebPass = pendingPass;
            try { s.Save(); }
            catch (Exception ex) { Fail("Speichern fehlgeschlagen: " + ex.Message, null); return; }

            s.ApplyAutostart();
            Main.UpdateTray();
            if (explorerChanged) { if (s.ExplorerEntry) ExplorerMenu.Apply(); else ExplorerMenu.Disable(); }
            if (logsChanged) ThreadPool.QueueUserWorkItem(delegate { Store.CleanupLogs(true); });
            if (passChanged || pendingLogoutAll) WebAuth.LogoutAll();
            if (certChanged)
            {
                CertProvider.Reset();
                if (s.WebHttps) WebServer.Stop(); // neues Zertifikat laden
            }
            WebServer.Apply(); // startet nur neu, wenn sich Ein/Aus, Port, HTTPS oder das Zertifikat geändert haben

            pendingPass = null;
            pendingLogoutAll = false;
            loading = true;
            address.Text = s.WebHost ?? "";
            loading = false;
            RememberState(); // gespeichert: keine Rückfrage beim Neuaufbau oder Neustart
            // kurze Meldung oben im Hauptfenster
            string done = s.WebEnabled && WebServer.Running ? "Gespeichert · Steuerung per Handy: " + WebServer.PhoneUrl() + "/"
                        : s.WebEnabled ? "Gespeichert · Steuerung per Handy: nicht gestartet – " + (WebServer.LastError ?? "unbekannter Fehler")
                        : "Gespeichert";
            MainForm main = Main;
            if (adminNow && MessageBox.Show("Sync Pilot jetzt als Administrator neu starten?", "Sync Pilot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                BeginInvoke(new Action(delegate { main.RestartAsAdmin(null); }));
                return;
            }
            // Speichern schließt die Seite nicht: frisch mit den gespeicherten Werten anzeigen (gleiche Bildlaufposition),
            // oben die kurze grüne Meldung
            int keepScroll = scroll.VerticalScroll.Value;
            BeginInvoke(new Action(delegate
            {
                SettingsView fresh = new SettingsView(main);
                main.Navigate(fresh);
                fresh.ScrollTo(keepScroll);
                main.ShowNotice(done, !s.WebEnabled || WebServer.Running);
            }));
        }

        // =============== HTTPS ===============

        void UpdateCertMode()
        {
            bool file = pFile.Checked;
            foreach (Control c in new Control[] { lCert, certFile, browseCert, lKey, keyFile, browseKey, lCertPass, certPass })
                c.Visible = file;
            foreach (Control c in new Control[] { lName, certName, storeHint }) c.Visible = !file;
            certStatus.Text = "";
        }

        void ChooseFile(InputBox box, string titleText, string filter, bool isCert)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = titleText;
                d.Filter = filter;
                d.CheckFileExists = true;
                try
                {
                    string cur = box.Text.Trim().Length > 0 ? box.Text.Trim() : certFile.Text.Trim();
                    string dir = cur.Length > 0 ? Path.GetDirectoryName(cur) : null;
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) d.InitialDirectory = dir;
                }
                catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                box.Text = d.FileName;
                if (isCert)
                {
                    // passenden Schlüssel daneben vorschlagen, bei .pfx/.p12 keinen
                    if (CertLoader.IsPfx(d.FileName)) keyFile.Text = "";
                    else if (keyFile.Text.Trim().Length == 0 || !File.Exists(keyFile.Text.Trim())) keyFile.Text = CertLoader.GuessKeyFile(d.FileName);
                }
                certStatus.Text = "";
                LayoutAll();
            }
        }

        /// <summary>Zertifikat im Hintergrund prüfen (übernommen wird es erst mit "Speichern").</summary>
        void ApplyCertificate()
        {
            string mode = pFile.Checked ? "Datei" : "Speicher";
            string cf = certFile.Text.Trim(), kf = CertLoader.IsPfx(certFile.Text.Trim()) ? "" : keyFile.Text.Trim();
            string protectedPass = Secret.Protect(certPass.Text);
            string name = certName.Text.Trim();
            if (mode == "Datei" ? cf.Length == 0 : name.Length == 0)
            {
                ShowCertStatus(mode == "Datei" ? "Bitte die Zertifikatsdatei auswählen." : "Bitte die Domain des Zertifikats eintragen.", Theme.Err);
                return;
            }
            int run = ++certRun;
            btnCertApply.Enabled = false;
            ShowCertStatus("Prüfe Zertifikat …", Theme.TextDim);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                bool ok = false, warn = false;
                List<string> names = null;
                try
                {
                    using (X509Certificate2 c = CertProvider.Load(mode, name, cf, kf, protectedPass, CertLoader.TestKeyName))
                    {
                        names = CertProvider.DnsNames(c);
                        text = (names.Count > 0 ? string.Join(", ", names) : c.Subject) + " · gültig bis " + c.NotAfter.ToString("dd.MM.yyyy");
                        ok = true;
                        if (c.NotAfter < DateTime.Now.AddDays(14)) { text += " – läuft bald ab"; warn = true; }
                    }
                }
                catch (Exception ex) { text = ex.Message; }
                finally { CertLoader.DeleteTestKey(); }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (run != certRun) return;
                        btnCertApply.Enabled = true;
                        if (!ok)
                        {
                            ShowCertStatus(text, Theme.Err);
                            return;
                        }
                        // Adresse aus dem Zertifikat vorschlagen, wenn bisher keine eingetragen ist
                        if (address.Text.Trim().Length == 0 && names != null && names.Count > 0 && !names[0].StartsWith("*"))
                            address.Text = names[0];
                        ShowCertStatus("Geprüft: " + text + " – wird mit „Speichern“ übernommen.", warn ? Theme.Warn : Theme.Ok);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        void ShowCertStatus(string text, Color color)
        {
            certStatus.Text = text;
            certStatus.ForeColor = color;
            LayoutAll();
        }

        bool CertConfigured()
        {
            AppSettings s = AppSettings.Current;
            return s.CertMode == "Datei" ? !string.IsNullOrEmpty(s.CertFile) : !string.IsNullOrEmpty(s.CertName);
        }

        void HttpsToggled()
        {
            if (loading) return;
            // aus dem Internet nur mit HTTPS
            if (!tHttps.Checked && tInternet.Checked) SetLoading(tInternet, false);
            MarkDirty();
            UpdateState();
        }

        // =============== Internet ===============

        void InternetToggled()
        {
            if (loading) return;
            if (tInternet.Checked)
            {
                if (!tHttps.Checked)
                {
                    SetLoading(tInternet, false);
                    return;
                }
                if (PendingPasswordLength() < 12)
                {
                    DialogResult r = MessageBox.Show("Für den Zugriff aus dem Internet braucht das Kennwort mindestens 12 Zeichen. Jetzt ein neues festlegen?\n\n" +
                        "Alle angemeldeten Handys werden beim Speichern abgemeldet (neu anmelden per QR-Code).", "Sync Pilot",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes || !SetPassword(12))
                    {
                        SetLoading(tInternet, false);
                        return;
                    }
                }
            }
            MarkDirty();
            UpdateState();
        }

        // =============== QR-Code, Status, Firewall ===============

        void UpdateQr()
        {
            if (!WebServer.Running)
            {
                qrBase = null;
                qr.Text = "";
                qr.Message = AppSettings.Current.WebEnabled ? "Nicht gestartet" : "Ausgeschaltet";
                return;
            }
            string b = WebServer.PhoneUrl();
            if (b == qrBase) return;
            qrBase = b;
            try
            {
                qr.Text = b + "/#pair=" + WebAuth.NewPairCode();
                qr.Message = null;
            }
            catch (ArgumentException)
            {
                qr.Text = "";
                qr.Message = "Adresse zu lang für einen QR-Code";
            }
        }

        void UpdateState()
        {
            AppSettings s = AppSettings.Current;
            bool on = s.WebEnabled;
            btnPass.Text = HasPassword() ? "Ändern …" : "Festlegen …";
            btnPass.FitWidth();
            btnLogoutAll.Enabled = s.HasWebPassword && !pendingLogoutAll;
            if (pendingPass != null)
                passHint.Text = "Das neue Kennwort wird beim Speichern übernommen – angemeldete Handys müssen sich dann neu anmelden.";
            else if (pendingLogoutAll)
                passHint.Text = "Alle Handys werden beim Speichern abgemeldet und müssen sich neu anmelden.";
            else
                passHint.Text = "Mindestens " + MinPassword() + " Zeichen, am besten Buchstaben, Ziffern und Sonderzeichen gemischt. " +
                    "Nur nötig, wenn das Handy keine Kamera hat – sonst meldet der QR-Code an.";
            tInternet.Enabled = tHttps.Checked;
            pFile.Enabled = pStore.Enabled = true;

            UpdateQr();
            string link = WebServer.PhoneUrl() + "/";
            url.Text = on ? link : "";
            btnCopy.Enabled = on && WebServer.Running;
            SetCopyText("Link kopieren");

            if (!on)
            {
                status.Text = "Ausgeschaltet.";
                status.ForeColor = Theme.TextDim;
            }
            else if (WebServer.Running)
            {
                status.Text = (WebServer.Https ? "Läuft verschlüsselt (HTTPS)" : "Läuft") +
                    (s.WebInternet && WebServer.Https ? " – auch aus dem Internet erreichbar." : " – nur im Heimnetz und per VPN erreichbar.");
                status.ForeColor = Theme.Ok;
                DateTime locked = WebAuth.ExternalLockedUntil;
                if (s.WebInternet && locked > DateTime.UtcNow)
                {
                    status.Text = "Anmeldung aus dem Internet wegen vieler Fehlversuche gesperrt bis " + locked.ToLocalTime().ToString("HH:mm") +
                        " Uhr. Im Heimnetz geht es weiter.";
                    status.ForeColor = Theme.Warn;
                }
            }
            else
            {
                status.Text = "Nicht gestartet: " + (WebServer.LastError ?? (s.HasWebPassword ? "unbekannter Fehler" : "kein Kennwort festgelegt"));
                status.ForeColor = Theme.Err;
            }
            LayoutAll();
        }

        void CheckFirewall()
        {
            int p = AppSettings.Current.WebPort;
            int run = ++firewallRun;
            firewall.Text = "Prüfe …";
            firewall.ForeColor = Theme.TextDim;
            btnFirewall.Enabled = false;
            LayoutAll();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string problem = Firewall.Problem(p);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (run != firewallRun) return;
                        firewall.Text = problem == null ? "Port " + p + " ist freigegeben." : problem + ".";
                        firewall.ForeColor = problem == null ? Theme.Ok : Theme.Warn;
                        btnFirewall.Enabled = problem != null;
                        LayoutAll();
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        void AllowFirewall()
        {
            string error = Firewall.Allow(AppSettings.Current.WebPort);
            if (error != null) MessageBox.Show(error, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            CheckFirewall();
        }
    }

    /// <summary>QR-Code als Bild (dunkle Module auf weißem Grund, auch im Dunkelmodus); ohne Inhalt ein Hinweistext.</summary>
    public sealed class QrBox : Control
    {
        QrCode code;
        string message;

        public QrBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
        }

        /// <summary>Hinweis statt QR-Code (z. B. "Ausgeschaltet").</summary>
        public string Message
        {
            get { return message; }
            set { message = value; Invalidate(); }
        }

        /// <summary>Inhalt des QR-Codes. Wirft ArgumentException, wenn er zu lang ist.</summary>
        public override string Text
        {
            get { return base.Text; }
            set
            {
                QrCode c = string.IsNullOrEmpty(value) ? null : new QrCode(value);
                code = c;
                base.Text = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.P(8), Color.White);
            Theme.DrawRound(g, r, Theme.P(8), Theme.Border, 1f);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            if (code == null)
            {
                TextRenderer.DrawText(g, message ?? "", Font, ClientRectangle, Color.FromArgb(140, 140, 140),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int modules = code.Size + 8; // 4 Module Ruhezone ringsum
            int cell = Math.Max(1, Math.Min(Width, Height) / modules);
            int total = cell * modules;
            int ox = (Width - total) / 2 + 4 * cell, oy = (Height - total) / 2 + 4 * cell;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 20, 20)))
                for (int y = 0; y < code.Size; y++)
                    for (int x = 0; x < code.Size; x++)
                        if (code[x, y]) g.FillRectangle(b, ox + x * cell, oy + y * cell, cell, cell);
        }
    }

    /// <summary>Kennwort für den Handy-Zugriff festlegen (zweimal eingeben, mindestens 8 bzw. 12 Zeichen).</summary>
    public class PasswordDialog : Form
    {
        readonly InputBox p1, p2;
        readonly Label error;
        public string Password { get { return p1.Text; } }

        public PasswordDialog(bool change, int minLength)
        {
            Text = change ? "Kennwort ändern" : "Kennwort festlegen";
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
            int W = Theme.P(400), pad = Theme.P(24), w = W - 2 * pad, y = Theme.P(20);
            Label info = Ui.WrapLabel("Mit diesem Kennwort meldest du dich am Handy an (falls du nicht den QR-Code scannst). Mindestens " + minLength + " Zeichen. " +
                (change ? "Alle bisher angemeldeten Geräte werden abgemeldet." : ""), Theme.Small, Theme.TextDim, Theme.Bg);
            int ih = TextRenderer.MeasureText(info.Text, Theme.Small, new Size(w, 0), TextFormatFlags.WordBreak).Height;
            info.SetBounds(pad, y, w, ih);
            y += ih + Theme.P(8);
            Label l1 = Ui.Label("Kennwort", Theme.Body, Theme.Text, Theme.Bg);
            l1.Location = new Point(pad, y);
            p1 = new InputBox(false, false);
            p1.BackColor = Theme.Bg;
            p1.Box.UseSystemPasswordChar = true;
            p1.Box.AccessibleName = "Kennwort";
            p1.SetBounds(pad, y + Theme.P(22), w, p1.Height);
            y += Theme.P(70);
            Label l2 = Ui.Label("Kennwort wiederholen", Theme.Body, Theme.Text, Theme.Bg);
            l2.Location = new Point(pad, y);
            p2 = new InputBox(false, false);
            p2.BackColor = Theme.Bg;
            p2.Box.UseSystemPasswordChar = true;
            p2.Box.AccessibleName = "Kennwort wiederholen";
            p2.SetBounds(pad, y + Theme.P(22), w, p2.Height);
            y += Theme.P(66);
            error = Ui.WrapLabel("", Theme.Small, Theme.Err, Theme.Bg);
            error.SetBounds(pad, y, w, Theme.P(22));
            y += Theme.P(30);
            FlatButton ok = new FlatButton("OK", null, BtnKind.Primary);
            FlatButton cancel = new FlatButton("Abbrechen", null, BtnKind.Secondary);
            ok.Width = Math.Max(ok.Width, Theme.P(90));
            ok.Location = new Point(W - pad - ok.Width, y);
            cancel.Location = new Point(ok.Left - Theme.P(10) - cancel.Width, y);
            ok.Click += delegate
            {
                if (p1.Text.Length < minLength) { error.Text = "Mindestens " + minLength + " Zeichen."; return; }
                if (p1.Text != p2.Text) { error.Text = "Die beiden Eingaben sind nicht gleich."; return; }
                DialogResult = DialogResult.OK;
            };
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;
            y += ok.Height + Theme.P(20);
            Controls.AddRange(new Control[] { info, l1, p1, l2, p2, error, ok, cancel });
            ClientSize = new Size(W, y);
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

    /// <summary>Gemeinsame Grundlage der kleinen Dialoge: normaler Rahmen, feste Größe, dunkle Titelleiste.</summary>
    public class FixedDialog : Form
    {
        public FixedDialog(string title)
        {
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

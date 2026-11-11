// Sync Pilot – Aufgabe anlegen und bearbeiten
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
    public class EditView : BaseView
    {
        readonly SyncJob original;
        readonly bool isNew;
        public override string PageTitle { get { return isNew ? "Neue Aufgabe" : "Aufgabe bearbeiten"; } }
        protected override string ExtraState()
        {
            return string.Join(";", times.Select(t => t.Days + "@" + t.At).ToArray()) + "|" + customInterval + "|" +
                   userA + "|" + passA + "|" + userB + "|" + passB + "|" + certA + "|" + certB;
        }

        readonly Panel scroll, footer;
        readonly Label title;
        readonly Card cFolders, cMode, cExcl, cOpts, cSched, cEvt, cPro;
        readonly InputBox name, folderA, folderB, exFiles, exDirs, inFiles, inDirs, nameA, nameB, groupBox;
        // Für Profis
        readonly ChoiceBox cbConflict;
        readonly ToggleSwitch tNoDelA, tNoDelB, tScriptsChanged, tScriptAbort;
        readonly InputBox lockFile, scriptBefore, scriptAfter;
        readonly Label lNoDelA, lNoDelB, lWatchA, lWatchB;
        static readonly ConflictRule[] Rules = { ConflictRule.Fragen, ConflictRule.Neuer, ConflictRule.AGewinnt, ConflictRule.BGewinnt };
        readonly FlatButton btnSwap, browseA, browseB, btnCancel, btnSaveAnalyze, btnSave, credA, credB, groupPick;
        readonly DeviceCombo devA, devB;
        string userA, passA, userB, passB, certA, certB;
        readonly ModeCard mSync, mBackup, mMirror;
        readonly ToggleSwitch tDeletes, tRecycle, tHidden, tContent, tActive, tDst, tRights, tVersions, tSubdirs;
        readonly InputBox minSize, maxSize;
        readonly Label lDeletesDesc;
        readonly ChoiceBox cbSchedule, cbInterval, cbEvtGap;
        readonly ToggleSwitch tEvtChanges;
        static readonly int[] EvtGaps = { 0, 15, 30, 60, 120, 240, 480, 720, 1440 };
        readonly Label lWhen, lTimes, lInterval, lSchedInfo;
        readonly Panel timesFrame;
        readonly ListBox lbTimes;
        readonly FlatButton btnTimeAdd, btnTimeEdit, btnTimeDel;
        readonly List<TimeSlot> times;
        readonly ToggleSwitch tCatchUp, tOffline, tAdmin;
        readonly Label lCatchUp, lCatchUpDesc, lOffline, lOfflineDesc;
        static readonly ScheduleKind[] SchedKinds = { ScheduleKind.Manuell, ScheduleKind.Uhrzeiten, ScheduleKind.Intervall, ScheduleKind.BeiAnmeldung };
        static readonly int[] Intervals = { 5, 10, 15, 30, 60, 120, 180, 240, 360, 480, 720 };
        // Ereignisse
        readonly ToggleSwitch tWatchA, tWatchB;
        readonly InputBox watchDelay, trigFile, availFolder, availHost, availDrive, availInterval, availDelay;
        int customInterval; // Intervall aus einer älteren bzw. von Hand geänderten Aufgabe, das nicht in der Liste steht

        public static readonly string[] DefaultExcludes = { "Thumbs.db", "desktop.ini", "~$*", "*.tmp" };

        public EditView(MainForm main, SyncJob job) : this(main, job, job == null) { }

        /// <summary>asNew: job ist nur eine Vorlage (z. B. aus dem Explorer) und wird als neue Aufgabe gespeichert.</summary>
        public EditView(MainForm main, SyncJob job, bool asNew) : base(main)
        {
            TrackChanges();
            isNew = asNew;
            original = asNew ? null : job;
            SyncJob j = job != null ? job : new SyncJob();
            if (job == null) j.ExcludeFiles.AddRange(DefaultExcludes);
            j.Upgrade();

            scroll = new ScrollPanel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Theme.Bg;
            scroll.HandleCreated += delegate { Theme.DarkScroll(scroll); };
            scroll.Resize += delegate { LayoutSections(); };

            footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.P(68);
            footer.BackColor = Theme.Surface2;
            footer.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };
            btnCancel = new FlatButton("Abbrechen", null, BtnKind.Secondary);
            btnSaveAnalyze = new FlatButton("Speichern & analysieren", null, BtnKind.Secondary);
            btnSave = new FlatButton("Speichern", Theme.GCheck, BtnKind.Primary);
            btnCancel.Click += delegate { DropChanges(); Main.ShowTasks(); }; // Abbrechen verwirft ohne Rückfrage
            btnSaveAnalyze.Click += delegate { SyncJob saved = Save(); if (saved != null) { RememberState(); Main.ShowAnalysis(saved, false); } };
            btnSave.Click += delegate { if (Save() != null) { RememberState(); Main.ShowTasks(); } };
            footer.Controls.AddRange(new Control[] { btnCancel, btnSaveAnalyze, btnSave });
            footer.Resize += delegate
            {
                // Globale Richtlinien: "Abbrechen" links, "Speichern" (primär) ganz rechts, 10 Abstand
                int y = (footer.Height - btnSave.Height) / 2, x = footer.Width - Theme.P(28) - btnSave.Width;
                btnSave.Location = new Point(x, y);
                x -= Theme.P(10) + btnSaveAnalyze.Width;
                btnSaveAnalyze.Location = new Point(x, y);
                x -= Theme.P(10) + btnCancel.Width;
                btnCancel.Location = new Point(x, y);
            };

            title = Ui.Label(isNew ? "Neue Aufgabe" : "Aufgabe bearbeiten", Theme.H1, Theme.Text, Theme.Bg);
            scroll.Controls.Add(title);

            // --- Ordner ---
            cFolders = Section("Ordner");
            name = new InputBox(false, false);
            name.Text = j.Name;
            folderA = new InputBox(true, false);
            folderA.Text = j.FolderA;
            folderB = new InputBox(true, false);
            folderB.Text = j.FolderB;
            browseA = new FlatButton("Durchsuchen \u2026", Theme.GFolder, BtnKind.Secondary);
            browseB = new FlatButton("Durchsuchen \u2026", Theme.GFolder, BtnKind.Secondary);
            browseA.Click += delegate { Browse(folderA, SideTitle(true) + " ausw\u00e4hlen"); };
            browseB.Click += delegate { Browse(folderB, SideTitle(false) + " ausw\u00e4hlen"); };
            userA = j.UserA; passA = j.PassA; userB = j.UserB; passB = j.PassB; certA = j.CertA; certB = j.CertB;
            credA = new FlatButton("Anmeldung", "\uE72E", BtnKind.Secondary);
            credB = new FlatButton("Anmeldung", "\uE72E", BtnKind.Secondary);
            credA.Click += delegate { EditCredentials(true); };
            credB.Click += delegate { EditCredentials(false); };
            devA = new DeviceCombo();
            devB = new DeviceCombo();
            devA.Key = j.DeviceA;
            devB.Key = j.DeviceB;
            devA.AccessibleName = "Ger\u00e4t f\u00fcr Ordner A";
            devB.AccessibleName = "Ger\u00e4t f\u00fcr Ordner B";
            devA.PathForAuto = delegate { return folderA.Text; };
            devB.PathForAuto = delegate { return folderB.Text; };
            folderA.Box.TextChanged += delegate { devA.Invalidate(); };
            folderB.Box.TextChanged += delegate { devB.Invalidate(); };
            UpdateCredButtons();
            Label lName = FieldLabel("Name der Aufgabe", name);
            // Gruppe: ordnet die Aufgabenliste (z. B. nach Rechner oder NAS); Vorschläge aus vorhandenen Gruppen
            groupBox = new InputBox(false, false);
            groupBox.Text = j.Group ?? "";
            groupBox.Cue = "z. B. NAS";
            AutoCompleteStringCollection groups = new AutoCompleteStringCollection();
            groups.AddRange(Main.Jobs.Where(x => !string.IsNullOrWhiteSpace(x.Group)).Select(x => x.Group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray());
            groupBox.Box.AutoCompleteCustomSource = groups;
            groupBox.Box.AutoCompleteSource = AutoCompleteSource.CustomSource;
            groupBox.Box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            Label lGroup = FieldLabel("Gruppe (optional)", groupBox);
            // Pfeil rechts neben dem Feld: vorhandene Gruppen zum Auswählen (Eintippen bleibt möglich)
            groupPick = new FlatButton("", "\uE70D", BtnKind.Secondary);
            groupPick.AccessibleName = "Vorhandene Gruppe auswählen";
            groupPick.Click += delegate { PickGroup(); };
            Label lA = FieldLabel("Ordner A (Quelle)", folderA);
            Label lB = FieldLabel("Ordner B (Ziel bzw. Gegenst\u00fcck)", folderB);
            nameA = new InputBox(false, false);
            nameB = new InputBox(false, false);
            nameA.Text = j.NameA ?? "";
            nameB.Text = j.NameB ?? "";
            nameA.Cue = "z. B. PC";
            nameB.Cue = "z. B. NAS";
            Label lNA = FieldLabel("Bezeichnung für A", nameA);
            Label lNB = FieldLabel("Bezeichnung für B", nameB);
            Label lNamesHint = Ui.WrapLabel("Erscheint statt „A“ und „B“ in der Analyse, im Protokoll und auf dem Handy – z. B. „Nach NAS“.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            nameA.Box.TextChanged += delegate { lA.Text = "Ordner A (Quelle)" + (nameA.Text.Trim().Length > 0 ? " · " + nameA.Text.Trim() : ""); };
            nameB.Box.TextChanged += delegate { lB.Text = "Ordner B (Ziel bzw. Gegenst\u00fcck)" + (nameB.Text.Trim().Length > 0 ? " · " + nameB.Text.Trim() : ""); };
            if (nameA.Text.Trim().Length > 0) lA.Text += " · " + nameA.Text.Trim();
            if (nameB.Text.Trim().Length > 0) lB.Text += " · " + nameB.Text.Trim();
            Label pathHint = Ui.WrapLabel("Auch m\u00f6glich:  \\\\Server\\Freigabe  \u00b7  %USERPROFILE%\\Dokumente  \u00b7  " +
                "%DRIVELETTER:MeinUsbStick%:\\Sicherung  \u00b7  ftp://server/pfad  \u00b7  ftps://\u2026 (FTPS explizit)  \u00b7  ftpsim://\u2026 (implizit)  \u00b7  sftp://\u2026",
                Theme.Small, Theme.TextDim, Theme.Surface);
            // Quelle und Ziel tauschen (mit allem, was zu einer Seite gehört)
            btnSwap = new FlatButton("A und B tauschen", "\uE8AB", BtnKind.Subtle);
            btnSwap.Height = Theme.P(26);
            btnSwap.FitWidth();
            btnSwap.Click += delegate { SwapForm(); };
            cFolders.Controls.Add(btnSwap);
            cFolders.Controls.AddRange(new Control[] { lName, name, lGroup, groupBox, groupPick, lA, devA, folderA, browseA, credA, lB, devB, folderB, browseB, credB, pathHint,
                lNA, nameA, lNB, nameB, lNamesHint });
            cFolders.Height = Theme.P(340);
            cFolders.Resize += delegate
            {
                int p = Theme.P(20), w = cFolders.Width - 2 * p, y = Theme.P(56), g = Theme.P(8);
                int gw = Math.Max(Theme.P(140), (w - Theme.P(16)) * 35 / 100), nw = w - gw - Theme.P(16);
                lName.Location = new Point(p, y);
                name.SetBounds(p, y + Theme.P(22), nw, name.Height);
                lGroup.Location = new Point(p + nw + Theme.P(16), y);
                int pw = groupBox.Height;
                groupBox.SetBounds(p + nw + Theme.P(16), y + Theme.P(22), gw - pw - Theme.P(6), groupBox.Height);
                groupPick.SetBounds(groupBox.Right + Theme.P(6), groupBox.Top, pw, groupBox.Height);
                y += Theme.P(74);
                RowLayout(lA, devA, folderA, browseA, credA, p, w, y, g);
                y += Theme.P(74);
                RowLayout(lB, devB, folderB, browseB, credB, p, w, y, g);
                btnSwap.Location = new Point(p + w - btnSwap.Width, y - Theme.P(6)); // endet über den Feldern darunter
                y += Theme.P(66);
                // Hinweis so hoch wie nötig (bei schmalem Fenster drei Zeilen), Karte wächst mit
                int hh = TextRenderer.MeasureText(pathHint.Text, pathHint.Font, new Size(Math.Max(10, w), 0),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.P(4);
                pathHint.SetBounds(p, y, w, hh);
                y += hh + Theme.P(16);
                int half = (w - Theme.P(16)) / 2;
                lNA.Location = new Point(p, y);
                lNB.Location = new Point(p + half + Theme.P(16), y);
                nameA.SetBounds(p, y + Theme.P(22), half, nameA.Height);
                nameB.SetBounds(p + half + Theme.P(16), y + Theme.P(22), half, nameB.Height);
                y += Theme.P(22) + nameA.Height + Theme.P(6);
                int nh = TextRenderer.MeasureText(lNamesHint.Text, lNamesHint.Font, new Size(Math.Max(10, w), 0),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.P(4);
                lNamesHint.SetBounds(p, y, w, nh);
                int need = y + nh + Theme.P(20);
                if (cFolders.Height != need) cFolders.Height = need;
            };

            // --- Richtung ---
            cMode = Section("Richtung");
            mSync = new ModeCard(Theme.GSwitch, "Abgleichen", "Änderungen in beide Richtungen übernehmen");
            mBackup = new ModeCard(Theme.GForward, "Sichern", "Neue und geänderte Dateien von A nach B");
            mMirror = new ModeCard(Theme.GForward, "Spiegeln", "B wird exakt wie A, auch Löschungen");
            mSync.Checked = j.Mode == SyncMode.Abgleichen;
            mBackup.Checked = j.Mode == SyncMode.Sichern;
            mMirror.Checked = j.Mode == SyncMode.Spiegeln;
            foreach (ModeCard m in new ModeCard[] { mSync, mBackup, mMirror })
            {
                m.CheckedChanged += delegate { UpdateModeDependent(); };
                cMode.Controls.Add(m);
            }
            cMode.Height = Theme.P(200);
            cMode.Resize += delegate { LayoutModeCards(); };

            // --- Filter (Negativ- und Positivliste wie bei Easy2Sync) ---
            cExcl = Section("Filter");
            Label hint = Ui.WrapLabel("Ein Muster pro Zeile: Name (Thumbs.db, *.tmp, .well-known) oder fester Pfad ab dem Startordner " +
                "(\\writable\\cache). * und ? sind Platzhalter.", Theme.Small, Theme.TextDim, Theme.Surface);
            Label hEx = Ui.Label("Nicht abgleichen (Negativliste)", Theme.Bold, Theme.Text, Theme.Surface);
            Label hIn = Ui.Label("Nur diese abgleichen (Positivliste)", Theme.Bold, Theme.Text, Theme.Surface);
            Label hInHint = Ui.WrapLabel("Leer = alles. Sonst werden NUR passende Dateien bzw. Ordner (mit allem darin) abgeglichen.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            Label lExF = Ui.Label("Dateien, die so heißen", Theme.Small, Theme.TextDim, Theme.Surface);
            Label lExD = Ui.Label("Ordner, die so heißen", Theme.Small, Theme.TextDim, Theme.Surface);
            Label lInF = Ui.Label("Dateien, die so heißen", Theme.Small, Theme.TextDim, Theme.Surface);
            Label lInD = Ui.Label("Ordner, die so heißen", Theme.Small, Theme.TextDim, Theme.Surface);
            exFiles = ListBox_(j.ExcludeFiles, "Nicht abgleichen: Dateien");
            exDirs = ListBox_(j.ExcludeDirs, "Nicht abgleichen: Ordner");
            inFiles = ListBox_(j.IncludeFiles, "Nur diese abgleichen: Dateien");
            inDirs = ListBox_(j.IncludeDirs, "Nur diese abgleichen: Ordner");
            FlatButton[] picks =
            {
                PickButton(exFiles, false, "Nicht abgleichen: Dateien"), PickButton(exDirs, true, "Nicht abgleichen: Ordner"),
                PickButton(inFiles, false, "Nur diese abgleichen: Dateien"), PickButton(inDirs, true, "Nur diese abgleichen: Ordner")
            };
            Label hSize = Ui.Label("Dateigröße", Theme.Bold, Theme.Text, Theme.Surface);
            Label lMin = Ui.Label("Nicht abgleichen, wenn kleiner als", Theme.Small, Theme.TextDim, Theme.Surface);
            Label lMax = Ui.Label("oder größer als", Theme.Small, Theme.TextDim, Theme.Surface);
            minSize = new InputBox(false, false);
            maxSize = new InputBox(false, false);
            minSize.Box.AccessibleName = "Nicht abgleichen, wenn kleiner als";
            maxSize.Box.AccessibleName = "Nicht abgleichen, wenn größer als";
            minSize.Text = j.MinSize > 0 ? Fmt.SizeInput(j.MinSize) : "";
            maxSize.Text = j.MaxSize > 0 ? Fmt.SizeInput(j.MaxSize) : "";
            Label lSizeHint = Ui.Label("Leer = keine Grenze. Z. B. 500 KB, 2 MB, 1,5 GB oder Bytes.", Theme.Small, Theme.TextDim, Theme.Surface);
            cExcl.Controls.AddRange(new Control[] { hint, hEx, lExF, lExD, exFiles, exDirs, hIn, hInHint, lInF, lInD, inFiles, inDirs,
                hSize, lMin, lMax, minSize, maxSize, lSizeHint });
            cExcl.Controls.AddRange(picks);
            cExcl.Height = Theme.P(52 + 40 + 2 * (28 + 22 + 110 + 12) + 28 + 16 + 26 + 22 + 36 + 26 + 8);
            cExcl.Resize += delegate
            {
                int p = Theme.P(20), w = cExcl.Width - 2 * p, g = Theme.P(12), half = (w - g) / 2;
                int y = Theme.P(52);
                hint.SetBounds(p, y, w, Theme.P(36));
                y += Theme.P(40);
                hEx.Location = new Point(p, y);
                y += Theme.P(28);
                lExF.Location = new Point(p, y);
                lExD.Location = new Point(p + half + g, y);
                y += Theme.P(22);
                exFiles.SetBounds(p, y, half, exFiles.Height);
                exDirs.SetBounds(p + half + g, y, w - half - g, exDirs.Height);
                y += exFiles.Height + Theme.P(16);
                hIn.Location = new Point(p, y);
                y += Theme.P(26);
                hInHint.SetBounds(p, y, w, Theme.P(20));
                y += Theme.P(26);
                lInF.Location = new Point(p, y);
                lInD.Location = new Point(p + half + g, y);
                y += Theme.P(22);
                inFiles.SetBounds(p, y, half, inFiles.Height);
                inDirs.SetBounds(p + half + g, y, w - half - g, inDirs.Height);
                y += inFiles.Height + Theme.P(16);
                // Knopf "…" rechts über jeder Liste (wie bei Easy2Sync): Namen oder Pfade aus Ordner A/B auswählen
                InputBox[] boxes = { exFiles, exDirs, inFiles, inDirs };
                for (int i = 0; i < 4; i++)
                    picks[i].Location = new Point(boxes[i].Right - picks[i].Width, boxes[i].Top - picks[i].Height - Theme.P(2));
                hSize.Location = new Point(p, y);
                y += Theme.P(28);
                lMin.Location = new Point(p, y);
                lMax.Location = new Point(p + half + g, y);
                y += Theme.P(22);
                minSize.SetBounds(p, y, Math.Min(half, Theme.P(200)), minSize.Height);
                maxSize.SetBounds(p + half + g, y, Math.Min(w - half - g, Theme.P(200)), maxSize.Height);
                y += minSize.Height + Theme.P(6);
                lSizeHint.Location = new Point(p, y);
            };

            // --- Optionen ---
            cOpts = Section("Optionen");
            tDeletes = new ToggleSwitch();
            tRecycle = new ToggleSwitch();
            tHidden = new ToggleSwitch();
            tContent = new ToggleSwitch();
            tActive = new ToggleSwitch();
            tActive.Checked = !j.Disabled;
            tDeletes.Checked = j.PropagateDeletes;
            tRecycle.Checked = j.UseRecycleBin;
            tHidden.Checked = j.IncludeHidden;
            tContent.Checked = j.CompareContent;
            tDst = new ToggleSwitch();
            tRights = new ToggleSwitch();
            tVersions = new ToggleSwitch();
            tDst.Checked = j.IgnoreDstOffset;
            tRights.Checked = j.CopyRights;
            tVersions.Checked = j.KeepVersions;
            tSubdirs = new ToggleSwitch();
            tSubdirs.Checked = j.IncludeSubdirs;
            lDeletesDesc = null;
            const int Rows = 9, RowH = 74;
            Label[] rowsTitle = new Label[Rows];
            Label[] rowsDesc = new Label[Rows];
            ToggleSwitch[] toggles = { tActive, tSubdirs, tDeletes, tRecycle, tVersions, tHidden, tContent, tDst, tRights };
            string[] titles = { "Aufgabe aktiv", "Unterordner einbeziehen", "Löschungen übernehmen", "In den Papierkorb", "Ältere Versionen aufbewahren", "Versteckte Dateien",
                "Inhalt vergleichen", "Sommerzeit-Versatz ignorieren", "NTFS-Rechte übertragen" };
            string[] descs =
            {
                "Aus: läuft nicht nach Zeitplan und nicht bei „Alle synchronisieren“",
                "Aus: nur die Dateien direkt im gewählten Ordner abgleichen, ohne Unterordner",
                "Was auf einer Seite gelöscht wurde, wird auch auf der anderen entfernt",
                "Gelöschte Dateien landen im Papierkorb statt endgültig weg zu sein",
                "Überschriebene und gelöschte Dateien kommen vorher in den Ordner „" + Engine.VersionsDir + "“ derselben Seite",
                "Auch versteckte und Systemdateien abgleichen",
                "Bei gleicher Größe, aber anderem Datum den Inhalt prüfen (langsamer)",
                "Gleiche Größe und genau 1 Stunde Zeitunterschied gilt als gleich (FAT-Laufwerke, USB-Sticks, manche NAS)",
                "Zugriffsrechte von Dateien und Ordnern mitkopieren – nur zwischen Windows-Laufwerken und -Freigaben"
            };
            for (int i = 0; i < Rows; i++)
            {
                rowsTitle[i] = Ui.Label(titles[i], Theme.Body, Theme.Text, Theme.Surface);
                rowsDesc[i] = Ui.WrapLabel(descs[i], Theme.Small, Theme.TextDim, Theme.Surface);
                toggles[i].AccessibleName = titles[i];
                cOpts.Controls.Add(rowsTitle[i]);
                cOpts.Controls.Add(rowsDesc[i]);
                cOpts.Controls.Add(toggles[i]);
                Label lt = rowsTitle[i];
                ToggleSwitch tg = toggles[i];
                lt.Click += delegate { if (tg.Enabled) tg.Checked = !tg.Checked; };
            }
            lDeletesDesc = rowsDesc[2];
            cOpts.Height = Theme.P(56 + Rows * RowH + 8);
            cOpts.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border))
                    for (int i = 1; i < Rows; i++)
                    {
                        int y = Theme.P(52) + i * Theme.P(RowH);
                        e.Graphics.DrawLine(p, Theme.P(20), y, cOpts.Width - Theme.P(20), y);
                    }
            };
            cOpts.Resize += delegate
            {
                int p = Theme.P(20);
                for (int i = 0; i < Rows; i++)
                {
                    int y = Theme.P(52) + i * Theme.P(RowH);
                    toggles[i].Location = new Point(cOpts.Width - p - toggles[i].Width, y + (Theme.P(RowH) - toggles[i].Height) / 2);
                    int tw = toggles[i].Left - p - Theme.P(12);
                    // Beschreibung hat Platz für zwei Zeilen (vorher wurde die zweite Zeile abgeschnitten)
                    rowsTitle[i].Location = new Point(p, y + Theme.P(8));
                    rowsDesc[i].SetBounds(p, y + Theme.P(28), Math.Max(10, tw), Theme.Small.Height * 2 + Theme.P(2));
                }
                cOpts.Invalidate();
            };

            // --- Zeitplan (Windows-Aufgabenplanung bzw. Autostart, läuft auch ohne geöffnetes Programm) ---
            cSched = Section("Zeitplan");
            lWhen = Ui.Label("Automatisch ausführen", Theme.Body, Theme.Text, Theme.Surface);
            cbSchedule = Ui.Combo();
            cbSchedule.Items.AddRange(new object[] { "Nur manuell", "Zu festen Uhrzeiten", "Alle x Minuten bzw. Stunden", "Bei der Windows-Anmeldung" });
            cbSchedule.SelectedIndex = Math.Max(0, Array.IndexOf(SchedKinds, j.Schedule));
            cbSchedule.AccessibleName = "Automatisch ausführen";
            cbSchedule.SelectedIndexChanged += delegate
            {
                // erste Uhrzeit gleich vorschlagen
                if (SelectedKind == ScheduleKind.Uhrzeiten && times.Count == 0) { times.Add(new TimeSlot(127, "22:00")); FillTimes(0); }
                UpdateModeDependent();
            };

            times = (j.Times ?? new List<TimeSlot>()).Select(t => new TimeSlot(t.Days, t.At)).ToList();
            lTimes = Ui.Label("Uhrzeiten", Theme.Body, Theme.Text, Theme.Surface);
            timesFrame = new Panel();
            timesFrame.BackColor = Theme.Surface;
            timesFrame.Paint += delegate (object o, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, timesFrame.Width - 1, timesFrame.Height - 1);
                Theme.FillRound(e.Graphics, r, Theme.P(5), Theme.Input);
                Theme.DrawRound(e.Graphics, r, Theme.P(5), Theme.ControlBorder, 1f);
            };
            lbTimes = new ListBox();
            lbTimes.BorderStyle = BorderStyle.None;
            lbTimes.BackColor = Theme.Input;
            lbTimes.ForeColor = Theme.Text;
            lbTimes.Font = Theme.Body;
            lbTimes.IntegralHeight = false;
            lbTimes.DrawMode = DrawMode.OwnerDrawFixed;
            lbTimes.ItemHeight = Theme.P(24);
            lbTimes.AccessibleName = "Uhrzeiten";
            lbTimes.DrawItem += delegate (object o, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (SolidBrush b = new SolidBrush(sel ? Theme.AccentSoft : Theme.Input)) e.Graphics.FillRectangle(b, e.Bounds);
                Theme.DrawText(e.Graphics, lbTimes.Items[e.Index].ToString(), Theme.Body,
                    new Rectangle(e.Bounds.X + Theme.P(6), e.Bounds.Y, e.Bounds.Width - Theme.P(6), e.Bounds.Height),
                    sel ? Theme.AccentSoftText : Theme.Text, TextFormatFlags.Left);
            };
            lbTimes.HandleCreated += delegate { Theme.DarkScroll(lbTimes); };
            lbTimes.SelectedIndexChanged += delegate { UpdateTimeButtons(); };
            lbTimes.DoubleClick += delegate { EditTime(); };
            lbTimes.KeyDown += delegate (object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) { DeleteTime(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { EditTime(); e.Handled = true; }
            };
            timesFrame.Controls.Add(lbTimes);
            timesFrame.Resize += delegate { lbTimes.SetBounds(Theme.P(4), Theme.P(4), timesFrame.Width - Theme.P(8), timesFrame.Height - Theme.P(8)); };
            btnTimeAdd = new FlatButton("Hinzufügen \u2026", null, BtnKind.Secondary);
            btnTimeEdit = new FlatButton("Bearbeiten \u2026", null, BtnKind.Secondary);
            btnTimeDel = new FlatButton("Löschen", null, BtnKind.Secondary);
            btnTimeAdd.Click += delegate { AddTime(); };
            btnTimeEdit.Click += delegate { EditTime(); };
            btnTimeDel.Click += delegate { DeleteTime(); };
            FillTimes(-1);

            lInterval = Ui.Label("Intervall", Theme.Body, Theme.Text, Theme.Surface);
            cbInterval = Ui.Combo();
            cbInterval.AccessibleName = "Intervall";
            foreach (int m in Intervals) cbInterval.Items.Add(SyncJob.IntervalText(m));
            int iv = Array.IndexOf(Intervals, j.IntervalMinutes);
            if (iv < 0) { cbInterval.Items.Add(SyncJob.IntervalText(j.IntervalMinutes)); iv = cbInterval.Items.Count - 1; }
            cbInterval.SelectedIndex = iv;
            customInterval = Array.IndexOf(Intervals, j.IntervalMinutes) < 0 ? j.IntervalMinutes : 0;

            tCatchUp = new ToggleSwitch();
            tCatchUp.Checked = j.CatchUp;
            tCatchUp.AccessibleName = "Verpassten Termin nachholen";
            lCatchUp = Ui.Label("Verpassten Termin nachholen", Theme.Body, Theme.Text, Theme.Surface);
            lCatchUpDesc = Ui.WrapLabel("War der PC zur geplanten Zeit aus, läuft die Aufgabe, sobald er wieder an ist", Theme.Small, Theme.TextDim, Theme.Surface);
            tOffline = new ToggleSwitch();
            tOffline.Checked = j.SkipOffline;
            tOffline.AccessibleName = "Ohne Verbindung still auslassen";
            lOffline = Ui.Label("Ohne Verbindung still auslassen", Theme.Body, Theme.Text, Theme.Surface);
            lOfflineDesc = Ui.WrapLabel("Fehlt Laufwerk, Freigabe oder Server, fällt ein automatischer Lauf ohne Fehlermeldung aus", Theme.Small, Theme.TextDim, Theme.Surface);
            lCatchUp.Click += delegate { tCatchUp.Checked = !tCatchUp.Checked; };
            lOffline.Click += delegate { tOffline.Checked = !tOffline.Checked; };
            lSchedInfo = Ui.WrapLabel("Läuft ohne Fenster über die Windows-Aufgabenplanung bzw. den Autostart, auch wenn Sync Pilot geschlossen ist. " +
                "Konflikte werden dabei übersprungen, das Ergebnis steht im Verlauf.", Theme.Small, Theme.TextDim, Theme.Surface);
            cSched.Controls.AddRange(new Control[] { lWhen, cbSchedule, lTimes, timesFrame, btnTimeAdd, btnTimeEdit, btnTimeDel,
                lInterval, cbInterval, tCatchUp, lCatchUp, lCatchUpDesc, tOffline, lOffline, lOfflineDesc, lSchedInfo });
            cSched.Resize += delegate { LayoutSched(); };

            // --- Bei Ereignissen starten (nur solange Sync Pilot läuft, auch im Infobereich) ---
            cEvt = Section("Bei Ereignissen starten");
            Label lEvtHint = Ui.WrapLabel("Funktioniert, solange Sync Pilot läuft – auch nur im Infobereich.", Theme.Small, Theme.TextDim, Theme.Surface);
            Label lGap = Ui.Label("Höchstens alle", Theme.Body, Theme.Text, Theme.Surface);
            cbEvtGap = Ui.Combo();
            cbEvtGap.AccessibleName = "Durch Ereignisse höchstens alle";
            foreach (int m in EvtGaps) cbEvtGap.Items.Add(SyncJob.GapText(m));
            int gi = Array.IndexOf(EvtGaps, j.EventMinGap);
            if (gi < 0) { cbEvtGap.Items.Add(SyncJob.GapText(j.EventMinGap)); gi = cbEvtGap.Items.Count - 1; }
            cbEvtGap.SelectedIndex = gi;
            Label lGapHint = Ui.WrapLabel("Kommt ein Ereignis früher (seit dem letzten Lauf), wird es gesammelt und danach nachgeholt.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            tEvtChanges = new ToggleSwitch();
            tEvtChanges.Checked = j.EventOnlyChanges;
            tEvtChanges.AccessibleName = "Nur wenn sich etwas geändert hat";
            Label lEvtChanges = Ui.Label("Nur wenn sich etwas geändert hat", Theme.Body, Theme.Text, Theme.Surface);
            lEvtChanges.Click += delegate { tEvtChanges.Checked = !tEvtChanges.Checked; };
            Label lEvtChangesDesc = Ui.WrapLabel("Erst prüfen; ist auf keiner Seite etwas zu tun, still auslassen – kein Eintrag im Verlauf, keine Skripte.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            Label hWatch = Ui.Label("Änderungen im Ordner", Theme.Bold, Theme.Text, Theme.Surface);
            tWatchA = new ToggleSwitch();
            tWatchB = new ToggleSwitch();
            tWatchA.Checked = j.WatchA;
            tWatchB.Checked = j.WatchB;
            tWatchA.AccessibleName = "Ordner A überwachen";
            tWatchB.AccessibleName = "Ordner B überwachen";
            lWatchA = Ui.Label("Ordner A überwachen", Theme.Body, Theme.Text, Theme.Surface);
            lWatchB = Ui.Label("Ordner B überwachen", Theme.Body, Theme.Text, Theme.Surface);
            lWatchA.Click += delegate { if (tWatchA.Enabled) tWatchA.Checked = !tWatchA.Checked; };
            lWatchB.Click += delegate { if (tWatchB.Enabled) tWatchB.Checked = !tWatchB.Checked; };
            Label lDelay = Ui.Label("Starten nach", Theme.Body, Theme.Text, Theme.Surface);
            watchDelay = NumberBox(j.WatchDelay, "Starten nach Sekunden ohne Änderung");
            Label lDelayUnit = Ui.Label("Sekunden ohne weitere Änderung", Theme.Small, Theme.TextDim, Theme.Surface);
            Label lWatchHint = Ui.WrapLabel("Nicht für FTP/SFTP. Bei Netzwerkfreigaben weniger zuverlässig als bei lokalen Ordnern.",
                Theme.Small, Theme.TextDim, Theme.Surface);

            Label hFile = Ui.Label("Trigger-Datei", Theme.Bold, Theme.Text, Theme.Surface);
            Label lFileHint = Ui.WrapLabel("Existiert diese Datei, wird sie gelöscht und die Aufgabe gestartet – z. B. von einem anderen PC aus angelegt.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            trigFile = new InputBox(true, false);
            trigFile.Text = j.TriggerFile ?? "";
            trigFile.Box.AccessibleName = "Trigger-Datei";
            trigFile.Cue = "z. B. \\\\NAS\\Daten\\jetzt-sichern.txt";
            FlatButton btnFile = SmallBrowse("Trigger-Datei auswählen");
            btnFile.Click += delegate
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.Title = "Trigger-Datei (muss noch nicht existieren)";
                    d.OverwritePrompt = false;
                    d.CheckFileExists = false;
                    d.CheckPathExists = true;
                    d.Filter = "Alle Dateien (*.*)|*.*";
                    d.FileName = trigFile.Text.Trim().Length > 0 ? Path.GetFileName(trigFile.Text.Trim()) : "sync-starten.txt";
                    try { string dir = Path.GetDirectoryName(Paths.TryResolve(trigFile.Text.Trim())); if (Directory.Exists(dir)) d.InitialDirectory = dir; } catch { }
                    if (d.ShowDialog(this) == DialogResult.OK) trigFile.Text = d.FileName;
                }
            };

            Label hAvail = Ui.Label("Wenn etwas verfügbar wird", Theme.Bold, Theme.Text, Theme.Surface);
            Label lAvailHint = Ui.WrapLabel("Starten, sobald einer davon (wieder) erreichbar ist – z. B. wenn der NAS hochfährt oder der USB-Stick eingesteckt wird.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            Label lAvF = Ui.Label("Ordner", Theme.Body, Theme.Text, Theme.Surface);
            Label lAvH = Ui.Label("Computer / IP", Theme.Body, Theme.Text, Theme.Surface);
            Label lAvD = Ui.Label("Laufwerksname", Theme.Body, Theme.Text, Theme.Surface);
            availFolder = new InputBox(true, false);
            availFolder.Text = j.AvailFolder ?? "";
            availFolder.Box.AccessibleName = "Verfügbar wird: Ordner";
            availFolder.Cue = "z. B. \\\\Laptop\\Freigabe";
            availHost = new InputBox(false, false);
            availHost.Text = j.AvailHost ?? "";
            availHost.Box.AccessibleName = "Verfügbar wird: Computername oder IP";
            availHost.Cue = "z. B. Laptop oder 192.168.1.20";
            availDrive = new InputBox(false, false);
            availDrive.Text = j.AvailDrive ?? "";
            availDrive.Box.AccessibleName = "Verfügbar wird: Laufwerksname";
            availDrive.Cue = "Name, kein Laufwerksbuchstabe";
            FlatButton btnAvF = SmallBrowse("Ordner auswählen");
            btnAvF.Click += delegate
            {
                string avStart = Paths.TryResolve(availFolder.Text.Trim());
                string avPicked = FolderPicker.Pick(this, "Ordner, dessen Verfügbarkeit die Aufgabe startet", Directory.Exists(avStart) ? avStart : null);
                if (avPicked != null) availFolder.Text = avPicked;
            };
            FlatButton btnAvD = SmallBrowse("Laufwerk auswählen");
            btnAvD.Click += delegate { PickDrive(btnAvD); };
            Label lCheck = Ui.Label("Prüfen alle", Theme.Body, Theme.Text, Theme.Surface);
            availInterval = NumberBox(j.AvailInterval, "Prüfen alle Sekunden");
            Label lCheckUnit = Ui.Label("Sekunden", Theme.Small, Theme.TextDim, Theme.Surface);
            Label lWait = Ui.Label("Dann warten", Theme.Body, Theme.Text, Theme.Surface);
            availDelay = NumberBox(j.AvailDelay, "Wartezeit vor dem Abgleich in Sekunden");
            Label lWaitUnit = Ui.Label("Sekunden bis zum Start", Theme.Small, Theme.TextDim, Theme.Surface);

            cEvt.Controls.AddRange(new Control[] { lEvtHint, lGap, cbEvtGap, lGapHint, tEvtChanges, lEvtChanges, lEvtChangesDesc, hWatch, tWatchA, lWatchA, tWatchB, lWatchB, lDelay, watchDelay, lDelayUnit, lWatchHint,
                hFile, lFileHint, trigFile, btnFile, hAvail, lAvailHint, lAvF, availFolder, btnAvF, lAvH, availHost, lAvD, availDrive, btnAvD,
                lCheck, availInterval, lCheckUnit, lWait, availDelay, lWaitUnit });
            cEvt.Height = Theme.P(700);
            cEvt.Resize += delegate
            {
                int p = Theme.P(20), w = cEvt.Width - 2 * p, lw = Theme.P(120), g = Theme.P(8);
                int y = Theme.P(52);
                lEvtHint.SetBounds(p, y, w, Theme.P(22));
                y += Theme.P(30);
                lGap.Location = new Point(p, y + Theme.P(8));
                cbEvtGap.SetBounds(p + lw, y, Math.Min(Theme.P(200), w - lw), cbEvtGap.Height);
                y += Math.Max(Theme.P(36), cbEvtGap.Height) + Theme.P(4);
                lGapHint.SetBounds(p, y, w, Theme.P(36));
                y += Theme.P(40);
                tEvtChanges.Location = new Point(p + w - tEvtChanges.Width, y + (Theme.P(36) - tEvtChanges.Height) / 2);
                lEvtChanges.Location = new Point(p, y + Theme.P(8));
                y += Theme.P(36);
                lEvtChangesDesc.SetBounds(p, y, w - tEvtChanges.Width - g, Theme.P(36));
                y += Theme.P(40 + 12);
                hWatch.Location = new Point(p, y);
                y += Theme.P(30);
                foreach (object[] row in new object[][] { new object[] { lWatchA, tWatchA }, new object[] { lWatchB, tWatchB } })
                {
                    ToggleSwitch tg = (ToggleSwitch)row[1];
                    tg.Location = new Point(p + w - tg.Width, y + (Theme.P(36) - tg.Height) / 2);
                    ((Label)row[0]).Location = new Point(p, y + Theme.P(8));
                    y += Theme.P(36);
                }
                lDelay.Location = new Point(p, y + Theme.P(8));
                watchDelay.SetBounds(p + lw, y, Theme.P(90), watchDelay.Height);
                lDelayUnit.Location = new Point(watchDelay.Right + g, y + Theme.P(10));
                y += Theme.P(44);
                lWatchHint.SetBounds(p, y, w, Theme.P(36));
                y += Theme.P(40 + 16);
                hFile.Location = new Point(p, y);
                y += Theme.P(30);
                lFileHint.SetBounds(p, y, w, Theme.P(36));
                y += Theme.P(40);
                btnFile.Location = new Point(p + w - btnFile.Width, y + (trigFile.Height - btnFile.Height) / 2);
                trigFile.SetBounds(p, y, btnFile.Left - g - p, trigFile.Height);
                y += Theme.P(44 + 16);
                hAvail.Location = new Point(p, y);
                y += Theme.P(30);
                lAvailHint.SetBounds(p, y, w, Theme.P(36));
                y += Theme.P(40);
                foreach (object[] row in new object[][] { new object[] { lAvF, availFolder, btnAvF }, new object[] { lAvH, availHost, null },
                    new object[] { lAvD, availDrive, btnAvD } })
                {
                    InputBox box = (InputBox)row[1];
                    FlatButton b = row[2] as FlatButton;
                    ((Label)row[0]).Location = new Point(p, y + Theme.P(8));
                    int right = p + w;
                    if (b != null) { b.Location = new Point(right - b.Width, y + (box.Height - b.Height) / 2); right = b.Left - g; }
                    else right -= Theme.P(36) + g;
                    box.SetBounds(p + lw, y, Math.Max(Theme.P(60), right - p - lw), box.Height);
                    y += Theme.P(44);
                }
                // je eine Zeile: "Prüfen alle [10] Sekunden", "Dann warten [10] Sekunden bis zum Start"
                lCheck.Location = new Point(p, y + Theme.P(8));
                availInterval.SetBounds(p + lw, y, Theme.P(90), availInterval.Height);
                lCheckUnit.Location = new Point(availInterval.Right + g, y + Theme.P(10));
                y += Theme.P(44);
                lWait.Location = new Point(p, y + Theme.P(8));
                availDelay.SetBounds(p + lw, y, Theme.P(90), availDelay.Height);
                lWaitUnit.Location = new Point(availDelay.Right + g, y + Theme.P(10));
                y += Theme.P(44 + 8);
                if (cEvt.Height != y) { cEvt.Height = y; LayoutSections(); }
            };
            folderA.Box.TextChanged += delegate { UpdateWatchEnabled(); };
            folderB.Box.TextChanged += delegate { UpdateWatchEnabled(); };
            UpdateWatchEnabled();

            // --- Für Profis: Konflikte, nie löschen, Sperrdatei, Skripte ---
            cPro = Section("Für Profis");
            Label hConf = Ui.Label("Datei auf beiden Seiten geändert", Theme.Bold, Theme.Text, Theme.Surface);
            Label lConfHint = Ui.WrapLabel("Mit einer Regel wird nicht mehr nachgefragt. Wurde eine Datei auf einer Seite gelöscht und auf der anderen geändert, bleibt dabei immer die geänderte Datei erhalten.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            cbConflict = Ui.Combo();
            cbConflict.Items.AddRange(new object[] { "Immer nachfragen (Konflikt)", "Neuere Datei gewinnt", "A gewinnt", "B gewinnt" });
            cbConflict.SelectedIndex = Math.Max(0, Array.IndexOf(Rules, j.Conflicts));
            cbConflict.AccessibleName = "Datei auf beiden Seiten geändert";

            Label hNoDel = Ui.Label("Niemals löschen", Theme.Bold, Theme.Text, Theme.Surface);
            tNoDelA = new ToggleSwitch();
            tNoDelB = new ToggleSwitch();
            tNoDelA.Checked = j.NeverDeleteA;
            tNoDelB.Checked = j.NeverDeleteB;
            lNoDelA = Ui.Label("In A nie etwas löschen", Theme.Body, Theme.Text, Theme.Surface);
            lNoDelB = Ui.Label("In B nie etwas löschen", Theme.Body, Theme.Text, Theme.Surface);
            tNoDelA.AccessibleName = lNoDelA.Text;
            tNoDelB.AccessibleName = lNoDelB.Text;
            lNoDelA.Click += delegate { tNoDelA.Checked = !tNoDelA.Checked; };
            lNoDelB.Click += delegate { tNoDelB.Checked = !tNoDelB.Checked; };

            Label hLock = Ui.Label("Sperrdatei", Theme.Bold, Theme.Text, Theme.Surface);
            Label lLockHint = Ui.WrapLabel("Gegen gleichzeitigen Abgleich derselben Ordner von mehreren PCs: Solange die Datei existiert, wartet ein " +
                "automatischer Lauf (bis 30 Minuten), ein manueller bricht ab. Älter als " + LockFiles.StaleHours + " Stunden gilt sie als verwaist.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            lockFile = new InputBox(true, false);
            lockFile.Text = j.LockFile ?? "";
            lockFile.Box.AccessibleName = "Sperrdatei";
            lockFile.Cue = "z. B. \\\\NAS\\Temp\\Sync Bilder.ini";
            FlatButton btnLock = SmallBrowse("Sperrdatei auswählen");
            btnLock.Click += delegate { PickFile(lockFile, "Sperrdatei (muss noch nicht existieren)", "Sync " + name.Text.Trim() + ".ini", false); };

            Label hScript = Ui.Label("Befehl vor und nach dem Kopieren", Theme.Bold, Theme.Text, Theme.Surface);
            Label lScriptHint = Ui.WrapLabel("Wird mit cmd.exe ausgeführt, die Ausgabe steht im Protokoll – z. B. net stop mysql oder ein Skript (.bat, .cmd). " +
                "Variablen: %SYNCPILOT_A%, %SYNCPILOT_B%, %SYNCPILOT_AUFGABE%, danach %SYNCPILOT_ERGEBNIS%.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            Label lBefore = Ui.Label("Vorher", Theme.Body, Theme.Text, Theme.Surface);
            Label lAfter = Ui.Label("Nachher", Theme.Body, Theme.Text, Theme.Surface);
            scriptBefore = new InputBox(true, false);
            scriptAfter = new InputBox(true, false);
            scriptBefore.Text = j.ScriptBefore ?? "";
            scriptAfter.Text = j.ScriptAfter ?? "";
            scriptBefore.Box.AccessibleName = "Befehl vor dem Kopieren";
            scriptAfter.Box.AccessibleName = "Befehl nach dem Kopieren";
            scriptBefore.Cue = "z. B. net stop mysql";
            scriptAfter.Cue = "z. B. \"C:\\Skripte\\Fertig.bat\"";
            FlatButton btnBefore = SmallBrowse("Skript für vorher auswählen");
            FlatButton btnAfter = SmallBrowse("Skript für nachher auswählen");
            btnBefore.Click += delegate { PickFile(scriptBefore, "Skript vor dem Kopieren", null, true); };
            btnAfter.Click += delegate { PickFile(scriptAfter, "Skript nach dem Kopieren", null, true); };
            tScriptsChanged = new ToggleSwitch();
            tScriptAbort = new ToggleSwitch();
            tAdmin = new ToggleSwitch();
            tAdmin.Checked = j.RunAsAdmin;
            Label lAdmin = Ui.Label("Mit Administratorrechten ausf\u00fchren", Theme.Body, Theme.Text, Theme.Surface);
            tAdmin.AccessibleName = lAdmin.Text;
            lAdmin.Click += delegate { tAdmin.Checked = !tAdmin.Checked; };
            tScriptsChanged.Checked = j.ScriptsOnlyWithChanges;
            tScriptAbort.Checked = j.ScriptAbortOnError;
            Label lScriptsChanged = Ui.Label("Nur, wenn es etwas zu tun gibt", Theme.Body, Theme.Text, Theme.Surface);
            Label lScriptAbort = Ui.Label("Nicht synchronisieren, wenn „Vorher“ einen Fehler meldet", Theme.Body, Theme.Text, Theme.Surface);
            tScriptsChanged.AccessibleName = lScriptsChanged.Text;
            tScriptAbort.AccessibleName = lScriptAbort.Text;
            lScriptsChanged.Click += delegate { tScriptsChanged.Checked = !tScriptsChanged.Checked; };
            lScriptAbort.Click += delegate { tScriptAbort.Checked = !tScriptAbort.Checked; };
            Label lAfterHint = Ui.WrapLabel("„Nachher“ läuft auch nach Fehlern oder einem Abbruch – z. B. um einen vorher beendeten Dienst wieder zu starten. " +
                "Befehle, die Administratorrechte brauchen (net stop), funktionieren nur mit „Mit Administratorrechten ausführen“: " +
                "Der Zeitplan läuft dann ohne Nachfrage mit höchsten Rechten (Windows fragt einmal beim Speichern), " +
                "manuell startet Sync Pilot als Administrator neu. Als Administrator sind verbundene Netzlaufwerke (z. B. Z:) oft nicht sichtbar – dann \\\\Server\\Freigabe verwenden.",
                Theme.Small, Theme.TextDim, Theme.Surface);

            // Aufruf über die Kommandozeile (z. B. aus einem eigenen Skript): mit ID, eindeutig und unabhängig vom Namen
            Label hCmd = Ui.Label("Aufruf über die Kommandozeile", Theme.Bold, Theme.Text, Theme.Surface);
            Label lCmdHint = Ui.WrapLabel(original != null
                ? "Startet die Aufgabe ohne Fenster und wartet, bis sie fertig ist. Die ID (" + original.Id + ") bleibt auch beim Umbenennen gleich; " +
                  "statt der ID gehen auch der Name oder „Gruppe\\Name“. Rückgabewert 0 = OK, 1 = Fehler, 4 = deaktiviert, 5 = offline ausgelassen."
                : "Die ID wird beim Speichern vergeben. Danach steht hier der Aufruf zum Kopieren.",
                Theme.Small, Theme.TextDim, Theme.Surface);
            InputBox cmdBox = new InputBox(true, false);
            cmdBox.Box.ReadOnly = true;
            cmdBox.Box.BackColor = Theme.Input;
            cmdBox.Text = original != null ? Scheduler.Command(original) : "";
            cmdBox.Box.AccessibleName = "Aufruf über die Kommandozeile";
            FlatButton btnCmd = new FlatButton("", "\uE8C8", BtnKind.Secondary);
            btnCmd.Width = Theme.P(36);
            btnCmd.AccessibleName = "Aufruf kopieren";
            btnCmd.Enabled = original != null;
            btnCmd.Click += delegate { try { Clipboard.SetText(cmdBox.Text); } catch { } };
            ModernToolTip cmdTip = new ModernToolTip();
            cmdTip.SetToolTip(btnCmd, "Aufruf in die Zwischenablage kopieren");

            nameA.Box.TextChanged += delegate { UpdateSideLabels(); };
            nameB.Box.TextChanged += delegate { UpdateSideLabels(); };
            UpdateSideLabels();

            cPro.Controls.AddRange(new Control[] { hConf, lConfHint, cbConflict, hNoDel, tNoDelA, lNoDelA, tNoDelB, lNoDelB,
                hLock, lLockHint, lockFile, btnLock, hScript, lScriptHint, lBefore, scriptBefore, btnBefore, lAfter, scriptAfter, btnAfter,
                tScriptsChanged, lScriptsChanged, tScriptAbort, lScriptAbort, tAdmin, lAdmin, lAfterHint, hCmd, cmdBox, btnCmd, lCmdHint });
            cPro.Height = Theme.P(800);
            cPro.Resize += delegate
            {
                int p = Theme.P(20), w = cPro.Width - 2 * p, g = Theme.P(8), lw = Theme.P(80);
                int y = Theme.P(56);
                Func<Label, int, int> wrap = delegate (Label wl, int wy)
                {
                    int wh = TextRenderer.MeasureText(wl.Text, wl.Font, new Size(Math.Max(10, w), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.P(4);
                    wl.SetBounds(p, wy, w, wh);
                    return wy + wh;
                };
                hConf.Location = new Point(p, y);
                y += Theme.P(28);
                cbConflict.SetBounds(p, y, w, cbConflict.Height);
                y += cbConflict.Height + Theme.P(6);
                y = wrap(lConfHint, y) + Theme.P(16);

                hNoDel.Location = new Point(p, y);
                y += Theme.P(30);
                foreach (object[] row in new object[][] { new object[] { lNoDelA, tNoDelA }, new object[] { lNoDelB, tNoDelB } })
                {
                    ToggleSwitch tg = (ToggleSwitch)row[1];
                    tg.Location = new Point(p + w - tg.Width, y + (Theme.P(36) - tg.Height) / 2);
                    ((Label)row[0]).Location = new Point(p, y + Theme.P(8));
                    y += Theme.P(36);
                }
                y += Theme.P(16);

                hLock.Location = new Point(p, y);
                y += Theme.P(28);
                btnLock.Location = new Point(p + w - btnLock.Width, y + (lockFile.Height - btnLock.Height) / 2);
                lockFile.SetBounds(p, y, btnLock.Left - g - p, lockFile.Height);
                y += lockFile.Height + Theme.P(6);
                y = wrap(lLockHint, y) + Theme.P(16);

                hScript.Location = new Point(p, y);
                y += Theme.P(28);
                foreach (object[] row in new object[][] { new object[] { lBefore, scriptBefore, btnBefore }, new object[] { lAfter, scriptAfter, btnAfter } })
                {
                    InputBox box = (InputBox)row[1];
                    FlatButton b = (FlatButton)row[2];
                    ((Label)row[0]).Location = new Point(p, y + Theme.P(8));
                    b.Location = new Point(p + w - b.Width, y + (box.Height - b.Height) / 2);
                    box.SetBounds(p + lw, y, Math.Max(Theme.P(60), b.Left - g - p - lw), box.Height);
                    y += box.Height + Theme.P(8);
                }
                y = wrap(lScriptHint, y) + Theme.P(8);
                foreach (object[] row in new object[][] { new object[] { lScriptsChanged, tScriptsChanged }, new object[] { lScriptAbort, tScriptAbort }, new object[] { lAdmin, tAdmin } })
                {
                    ToggleSwitch tg = (ToggleSwitch)row[1];
                    tg.Location = new Point(p + w - tg.Width, y + (Theme.P(36) - tg.Height) / 2);
                    Label lab = (Label)row[0];
                    lab.AutoSize = false;
                    lab.SetBounds(p, y + Theme.P(8), Math.Max(10, tg.Left - p - g), Theme.P(22));
                    y += Theme.P(36);
                }
                y = wrap(lAfterHint, y + Theme.P(4)) + Theme.P(16);

                hCmd.Location = new Point(p, y);
                y += Theme.P(28);
                btnCmd.Height = cmdBox.Height;
                btnCmd.Location = new Point(p + w - btnCmd.Width, y);
                cmdBox.SetBounds(p, y, btnCmd.Left - g - p, cmdBox.Height);
                y += cmdBox.Height + Theme.P(6);
                y = wrap(lCmdHint, y) + Theme.P(20);
                if (cPro.Height != y) { cPro.Height = y; LayoutSections(); }
            };

            Controls.Add(scroll);
            Controls.Add(footer);
            scroll.BringToFront();
            UpdateModeDependent();
        }

        Card Section(string heading)
        {
            Card c = new Card();
            Label h = Ui.Label(heading, Theme.H2, Theme.Text, Theme.Surface);
            h.Location = new Point(Theme.P(20), Theme.P(16));
            c.Controls.Add(h);
            scroll.Controls.Add(c);
            return c;
        }

        static Label FieldLabel(string text, InputBox box)
        {
            Label l = Ui.Label(text, Theme.Body, Theme.Text, Theme.Surface);
            box.Box.AccessibleName = text;
            return l;
        }

        /// <summary>Richtungs-Karten so hoch wie die längste Beschreibung (mit langen Bezeichnungen drei Zeilen).</summary>
        void LayoutModeCards()
        {
            if (cMode == null || mMirror == null) return;
            int p = Theme.P(20), gap = Theme.P(10), w = (cMode.Width - 2 * p - 2 * gap) / 3, y = Theme.P(56);
            int textW = Math.Max(10, w - 2 * Theme.P(14)), descH = 0;
            foreach (ModeCard m in new ModeCard[] { mSync, mBackup, mMirror })
                descH = Math.Max(descH, TextRenderer.MeasureText(m.Desc ?? "", Theme.Small, new Size(textW, 0),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
            int h = Math.Max(Theme.P(124), Theme.P(72) + descH + Theme.P(14));
            mSync.SetBounds(p, y, w, h);
            mBackup.SetBounds(p + w + gap, y, w, h);
            mMirror.SetBounds(p + 2 * (w + gap), y, cMode.Width - 2 * p - 2 * (w + gap), h);
            int need = y + h + Theme.P(20);
            if (cMode.Height != need) { cMode.Height = need; LayoutSections(); }
        }

        void LayoutSections()
        {
            if (cSched == null || cEvt == null || cPro == null || title == null) return;
            int pad = Theme.P(28), gap = Theme.P(16);
            int W = Math.Min(scroll.ClientSize.Width - 2 * pad, Theme.P(1180));
            int y0 = scroll.AutoScrollPosition.Y;
            title.Location = new Point(pad - Theme.P(3), y0 + Theme.P(20));
            int top = y0 + Theme.P(84);
            if (W >= Theme.P(860))
            {
                int lw = (int)((W - gap) * 0.6), rw = W - gap - lw, x2 = pad + lw + gap;
                int y = top;
                foreach (Card c in new Card[] { cFolders, cMode, cExcl, cPro }) { c.SetBounds(pad, y, lw, c.Height); y += c.Height + gap; }
                int y2 = top;
                foreach (Card c in new Card[] { cOpts, cSched, cEvt }) { c.SetBounds(x2, y2, rw, c.Height); y2 += c.Height + gap; }
                scroll.AutoScrollMinSize = new Size(0, Math.Max(y, y2) - y0 + Theme.P(8));
            }
            else
            {
                int y = top;
                foreach (Card c in new Card[] { cFolders, cMode, cOpts, cExcl, cSched, cEvt, cPro }) { c.SetBounds(pad, y, Math.Max(Theme.P(300), W), c.Height); y += c.Height + gap; }
                scroll.AutoScrollMinSize = new Size(0, y - y0 + Theme.P(8));
            }
        }

        void UpdateModeDependent()
        {
            bool mirror = mMirror.Checked;
            tDeletes.Enabled = !mirror;
            if (mirror) tDeletes.Checked = true;
            if (lDeletesDesc != null)
                lDeletesDesc.Text = mirror ? Sd("Beim Spiegeln wird in B immer gelöscht, was in A fehlt")
                    : mBackup.Checked ? Sd("In A gelöschte Dateien auch in der Sicherung B entfernen")
                    : "Was auf einer Seite gelöscht wurde, wird auch auf der anderen entfernt";
            LayoutSched();
        }

        ScheduleKind SelectedKind
        {
            get { return SchedKinds[Math.Max(0, Math.Min(SchedKinds.Length - 1, cbSchedule.SelectedIndex))]; }
        }

        // Zeitplan-Karte: je nach Art nur die passenden Felder zeigen, Höhe anpassen
        void LayoutSched()
        {
            if (cSched == null || lSchedInfo == null) return;
            ScheduleKind k = SelectedKind;
            bool fixedTimes = k == ScheduleKind.Uhrzeiten, interval = k == ScheduleKind.Intervall;
            foreach (Control c in new Control[] { lTimes, timesFrame, btnTimeAdd, btnTimeEdit, btnTimeDel }) c.Visible = fixedTimes;
            lInterval.Visible = cbInterval.Visible = interval;
            tCatchUp.Visible = lCatchUp.Visible = lCatchUpDesc.Visible = fixedTimes || interval;
            int p = Theme.P(20), w = cSched.Width - 2 * p, g = Theme.P(8);
            int y = Theme.P(56);
            lWhen.Location = new Point(p, y);
            cbSchedule.SetBounds(p, y + Theme.P(22), w, cbSchedule.Height);
            y += Theme.P(22) + cbSchedule.Height + Theme.P(14);
            if (fixedTimes)
            {
                lTimes.Location = new Point(p, y);
                y += Theme.P(22);
                int bw = Math.Max(btnTimeAdd.Width, Math.Max(btnTimeEdit.Width, btnTimeDel.Width));
                int bx = p + w - bw;
                btnTimeAdd.SetBounds(bx, y, bw, btnTimeAdd.Height);
                btnTimeEdit.SetBounds(bx, y + btnTimeAdd.Height + Theme.P(6), bw, btnTimeEdit.Height);
                btnTimeDel.SetBounds(bx, btnTimeEdit.Bottom + Theme.P(6), bw, btnTimeDel.Height);
                int fh = btnTimeDel.Bottom - y;
                timesFrame.SetBounds(p, y, Math.Max(Theme.P(80), bx - g - p), fh);
                y += fh + Theme.P(14);
            }
            if (interval)
            {
                lInterval.Location = new Point(p, y);
                cbInterval.SetBounds(p, y + Theme.P(22), Math.Min(w, Theme.P(220)), cbInterval.Height);
                y += Theme.P(22) + cbInterval.Height + Theme.P(14);
            }
            foreach (object[] row in new object[][] { new object[] { tCatchUp, lCatchUp, lCatchUpDesc }, new object[] { tOffline, lOffline, lOfflineDesc } })
            {
                ToggleSwitch tg = (ToggleSwitch)row[0];
                if (tg == tCatchUp && !(fixedTimes || interval)) continue; // (Visible gilt erst, wenn die Seite angezeigt wird)
                tg.Location = new Point(p + w - tg.Width, y + (Theme.P(74) - tg.Height) / 2);
                int tw = tg.Left - p - Theme.P(12);
                ((Label)row[1]).Location = new Point(p, y + Theme.P(6));
                ((Label)row[2]).SetBounds(p, y + Theme.P(28), Math.Max(10, tw), Theme.Small.Height * 2 + Theme.P(2));
                y += Theme.P(74);
            }
            lSchedInfo.SetBounds(p, y + Theme.P(4), w, Theme.P(52));
            y += Theme.P(4 + 52 + 12);
            if (cSched.Height != y) { cSched.Height = y; LayoutSections(); }
        }

        void FillTimes(int select)
        {
            lbTimes.BeginUpdate();
            lbTimes.Items.Clear();
            foreach (TimeSlot t in times) lbTimes.Items.Add(t.ToString());
            lbTimes.EndUpdate();
            if (select >= 0 && select < lbTimes.Items.Count) lbTimes.SelectedIndex = select;
            UpdateTimeButtons();
        }

        void UpdateTimeButtons()
        {
            btnTimeEdit.Enabled = btnTimeDel.Enabled = lbTimes.SelectedIndex >= 0;
        }

        void AddTime()
        {
            using (TimeSlotDialog d = new TimeSlotDialog("Uhrzeit hinzufügen", new TimeSlot(127, "22:00")))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                times.Add(d.Slot);
                times.Sort((a, b) => string.CompareOrdinal(a.At, b.At));
                FillTimes(times.IndexOf(d.Slot));
            }
        }

        void EditTime()
        {
            int i = lbTimes.SelectedIndex;
            if (i < 0) return;
            using (TimeSlotDialog d = new TimeSlotDialog("Uhrzeit bearbeiten", times[i]))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                times[i] = d.Slot;
                times.Sort((a, b) => string.CompareOrdinal(a.At, b.At));
                FillTimes(times.IndexOf(d.Slot));
            }
        }

        void DeleteTime()
        {
            int i = lbTimes.SelectedIndex;
            if (i < 0) return;
            times.RemoveAt(i);
            FillTimes(Math.Min(i, times.Count - 1));
        }

        /// <summary>Bezeichnungen der Ordner in den Beschriftungen und der Konfliktauswahl übernehmen.</summary>
        void UpdateSideLabels()
        {
            if (cbConflict == null) return;
            string na = nameA.Text.Trim().Length > 0 ? nameA.Text.Trim() : "A", nb = nameB.Text.Trim().Length > 0 ? nameB.Text.Trim() : "B";
            lNoDelA.Text = "In " + na + " nie etwas löschen";
            lNoDelB.Text = "In " + nb + " nie etwas löschen";
            int sel = cbConflict.SelectedIndex;
            cbConflict.Items[2] = na + " gewinnt";
            cbConflict.Items[3] = nb + " gewinnt";
            cbConflict.SelectedIndex = sel;
            cbConflict.Invalidate();
            // Überwachung, Richtungen und Löschen ebenfalls mit den Bezeichnungen
            lWatchA.Text = SideTitle(true) + " überwachen";
            lWatchB.Text = SideTitle(false) + " überwachen";
            tWatchA.AccessibleName = lWatchA.Text;
            tWatchB.AccessibleName = lWatchB.Text;
            mBackup.Desc = Sd("Neue und geänderte Dateien von A nach B");
            mMirror.Desc = Sd("B wird exakt wie A, auch Löschungen");
            mBackup.Invalidate(); mMirror.Invalidate();
            LayoutModeCards();
            UpdateModeDependent();
            if (cEvt != null) cEvt.PerformLayout();
        }

        /// <summary>Eigene Bezeichnung der Seite, sonst "Ordner A" bzw. "Ordner B".</summary>
        string SideTitle(bool a)
        {
            string n = (a ? nameA : nameB).Text.Trim();
            return n.Length > 0 ? n : (a ? "Ordner A" : "Ordner B");
        }

        /// <summary>A und B im Text durch die eingetragenen Bezeichnungen ersetzen (wie in Analyse und Protokoll).</summary>
        string Sd(string text)
        {
            SyncJob t = new SyncJob();
            t.NameA = nameA.Text.Trim().Length > 0 ? nameA.Text.Trim() : null;
            t.NameB = nameB.Text.Trim().Length > 0 ? nameB.Text.Trim() : null;
            return t.Sides(text);
        }

        /// <summary>Datei auswählen (Skript bzw. Sperrdatei) und ins Feld übernehmen; Skripte mit Leerzeichen in Anführungszeichen.</summary>
        void PickFile(InputBox box, string title, string suggest, bool script)
        {
            using (FileDialog d = script ? (FileDialog)new OpenFileDialog() : new SaveFileDialog())
            {
                d.Title = title;
                d.CheckPathExists = true;
                if (script)
                {
                    d.Filter = "Skripte (*.bat;*.cmd;*.exe;*.ps1)|*.bat;*.cmd;*.exe;*.ps1|Alle Dateien (*.*)|*.*";
                    d.CheckFileExists = true;
                }
                else
                {
                    d.Filter = "Alle Dateien (*.*)|*.*";
                    d.CheckFileExists = false;
                    ((SaveFileDialog)d).OverwritePrompt = false;
                    d.FileName = Fmt.SafeFileName(suggest ?? "Sync.ini");
                }
                string cur = box.Text.Trim().Trim('"');
                try { string dir = Path.GetDirectoryName(Paths.TryResolve(cur)); if (Directory.Exists(dir)) d.InitialDirectory = dir; } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string f = d.FileName;
                if (script && f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                    box.Text = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + f + "\"";
                else box.Text = script && f.Contains(" ") ? "\"" + f + "\"" : f;
            }
        }

        static InputBox NumberBox(int value, string accessibleName)
        {
            InputBox b = new InputBox(false, false);
            b.Text = value.ToString();
            b.Box.AccessibleName = accessibleName;
            return b;
        }

        static FlatButton SmallBrowse(string accessibleName)
        {
            FlatButton b = new FlatButton("\u2026", null, BtnKind.Secondary);
            b.Width = Theme.P(36);
            b.AccessibleName = accessibleName;
            return b;
        }

        // Laufwerksnamen der gerade angeschlossenen Laufwerke zur Auswahl
        /// <summary>Menü mit allen vorhandenen Gruppen; die aktuelle ist markiert, „Keine Gruppe“ leert das Feld.</summary>
        void PickGroup()
        {
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip m = mb.CreateMenu();
            string cur = groupBox.Text.Trim();
            List<string> all = Main.Jobs.Where(x => !string.IsNullOrWhiteSpace(x.Group)).Select(x => x.Group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (string grp in all)
            {
                string gname = grp;
                bool on = string.Equals(gname, cur, StringComparison.OrdinalIgnoreCase);
                m.Items.Add(mb.Item(gname, on ? '\uE73E' : MenuHelper.Folder, delegate { groupBox.Text = gname; groupBox.Box.Focus(); }));
            }
            if (all.Count == 0) m.Items.Add(mb.Info("Noch keine Gruppen – einfach einen Namen eintippen"));
            else
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(mb.Item("Keine Gruppe", '\uE711', delegate { groupBox.Text = ""; groupBox.Box.Focus(); }));
            }
            m.Show(groupPick, new Point(groupPick.Width - m.PreferredSize.Width, groupPick.Height));
        }

        void PickDrive(Control anchor)
        {
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip m = mb.CreateMenu();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                string label;
                try { if (!d.IsReady || string.IsNullOrEmpty(d.VolumeLabel)) continue; label = d.VolumeLabel; }
                catch { continue; }
                string l = label;
                m.Items.Add(mb.Item(label + "  (" + d.Name.TrimEnd('\\') + ")", d.DriveType == DriveType.Removable ? '\uE88E' : '\uEDA2',
                    delegate { availDrive.Text = l; }));
            }
            if (m.Items.Count == 0) m.Items.Add(mb.Info("Kein Laufwerk mit Namen gefunden"));
            m.Show(anchor, new Point(0, anchor.Height));
        }

        // Überwachen geht nur bei Windows-Ordnern (lokal oder Freigabe), nicht bei FTP/SFTP
        void UpdateWatchEnabled()
        {
            bool ra = FtpUrl.IsRemote(folderA.Text.Trim()), rb = FtpUrl.IsRemote(folderB.Text.Trim());
            tWatchA.Enabled = !ra;
            tWatchB.Enabled = !rb;
            if (ra) tWatchA.Checked = false;
            if (rb) tWatchB.Checked = false;
        }

        void Browse(InputBox box, string desc)
        {
            if (FtpUrl.IsRemote(box.Text))
            {
                MessageBox.Show("FTP- und SFTP-Ziele bitte direkt als Adresse eintragen, z. B. ftps://server.de/httpdocs oder sftp://nas//volume1/backup.\n" +
                    "Benutzer und Kennwort stehen unter \u201eAnmeldung\u201c.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (FolderPicker d = new FolderPicker())
            {
                d.Description = desc;
                d.ShowNewFolderButton = true;
                if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    box.Text = d.SelectedPath;
                    if (name.Text.Trim().Length == 0 && box == folderA)
                        name.Text = Path.GetFileName(d.SelectedPath.TrimEnd('\\'));
                }
            }
        }

        // Enter = Speichern, Esc = Abbrechen (Globale Richtlinien). Enter bleibt in mehrzeiligen Feldern, Listen,
        // Knöpfen und Feldern mit Vorschlagsliste (Gruppe) beim jeweiligen Steuerelement.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DropChanges(); Main.ShowTasks(); return true; }
            if (keyData == Keys.Enter)
            {
                Control c = this;
                while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
                while (c != null && !c.Focused && c.Controls.Count > 0)
                {
                    Control f = null;
                    foreach (Control ch in c.Controls) if (ch.ContainsFocus) { f = ch; break; }
                    if (f == null) break;
                    c = f;
                }
                TextBox tb = c as TextBox;
                bool own = c is ButtonBase || c is FlatButton || c is ListView || c is ListBox || c is ComboBox ||
                           (tb != null && (tb.Multiline || tb.AutoCompleteMode != AutoCompleteMode.None));
                if (!own) { btnSave.PerformClick(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>Im Formular A und B tauschen; beim Speichern wird der gespeicherte Stand mitgedreht.</summary>
        void SwapForm()
        {
            string t = folderA.Text; folderA.Text = folderB.Text; folderB.Text = t;
            t = devA.Key; devA.Key = devB.Key; devB.Key = t;
            t = nameA.Text; nameA.Text = nameB.Text; nameB.Text = t;
            t = userA; userA = userB; userB = t;
            t = passA; passA = passB; passB = t;
            t = certA; certA = certB; certB = t;
            bool b = tNoDelA.Checked; tNoDelA.Checked = tNoDelB.Checked; tNoDelB.Checked = b;
            b = tWatchA.Checked; tWatchA.Checked = tWatchB.Checked; tWatchB.Checked = b;
            int ci = cbConflict.SelectedIndex;
            if (ci == 2) cbConflict.SelectedIndex = 3;
            else if (ci == 3) cbConflict.SelectedIndex = 2;
            devA.Invalidate(); devB.Invalidate();
            UpdateCredButtons();
            Main.ShowNotice("A und B getauscht \u2013 wird beim Speichern übernommen", true);
        }

        static void RowLayout(Label l, DeviceCombo dev, InputBox box, FlatButton browse, FlatButton cred, int p, int w, int y, int g)
        {
            int top = y + Theme.P(22);
            l.Location = new Point(p, y);
            int dw = Theme.P(60);
            dev.SetBounds(p, top + (box.Height - dev.Height) / 2, dw, dev.Height);
            int right = p + w;
            cred.Location = new Point(right - cred.Width, top);
            browse.Location = new Point(cred.Left - g - browse.Width, top);
            box.SetBounds(p + dw + g, top, Math.Max(Theme.P(80), browse.Left - g - (p + dw + g)), box.Height);
        }

        void UpdateCredButtons()
        {
            credA.Kind = string.IsNullOrEmpty(userA) && string.IsNullOrEmpty(passA) ? BtnKind.Secondary : BtnKind.Soft;
            credB.Kind = string.IsNullOrEmpty(userB) && string.IsNullOrEmpty(passB) ? BtnKind.Secondary : BtnKind.Soft;
            credA.Invalidate();
            credB.Invalidate();
        }

        void EditCredentials(bool sideA)
        {
            InputBox box = sideA ? folderA : folderB;
            using (CredentialDialog d = new CredentialDialog("Anmeldung f\u00fcr " + SideTitle(sideA),
                sideA ? userA : userB, sideA ? passA : passB, sideA ? certA : certB, delegate { return box.Text; }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                bool none = string.IsNullOrEmpty(d.User) && !FtpUrl.IsRemote(box.Text);
                if (sideA) { userA = d.User; passA = none ? "" : d.ProtectedPassword; certA = d.Cert; }
                else { userB = d.User; passB = none ? "" : d.ProtectedPassword; certB = d.Cert; }
            }
            UpdateCredButtons();
        }

        static string Normalize(string p)
        {
            p = (p ?? "").Trim().Trim('"');
            if (p.Length == 0) return p;
            if (FtpUrl.IsRemote(p)) return p.TrimEnd('/');
            if (Paths.HasPlaceholders(p)) return p.Length > 3 ? p.TrimEnd('\\') : p;
            p = Path.GetFullPath(p);
            if (p.Length > 3) p = p.TrimEnd('\\');
            return p;
        }

        static bool Reachable(string path, string user, string pass, string cert)
        {
            try
            {
                if (FtpUrl.IsRemote(path))
                {
                    SyncJob t = new SyncJob();
                    t.FolderA = path; t.UserA = user; t.PassA = pass; t.CertA = cert;
                    using (IFileSide s = Sides.Open(t, true)) s.CheckRoot();
                    return true;
                }
                string r = Paths.Resolve(path);
                NetShare.Connect(r, user, pass);
                return Directory.Exists(r);
            }
            catch { return false; }
        }

        /// <summary>
        /// Lokaler Ordner oder Freigabe, die selbst fehlt, deren Oberordner (Laufwerk, Freigabe) aber erreichbar ist:
        /// der tiefste vorhandene Oberordner; sonst null (Rechner aus, Freigabe fehlt, Laufwerk nicht angeschlossen, FTP).
        /// </summary>
        static string ExistingParent(string path)
        {
            try
            {
                if (FtpUrl.IsRemote(path)) return null;
                string p = Paths.Resolve(path).TrimEnd('\\');
                if (Directory.Exists(p)) return null;
                while (true)
                {
                    string parent = Path.GetDirectoryName(p);
                    if (string.IsNullOrEmpty(parent)) return null;
                    if (Directory.Exists(parent)) return parent;
                    p = parent.TrimEnd('\\');
                }
            }
            catch { return null; }
        }

        /// <summary>Knopf "…" über einer Filterliste: Namen oder Pfade per Auswahl aus Ordner A bzw. B übernehmen.</summary>
        FlatButton PickButton(InputBox box, bool dirs, string what)
        {
            FlatButton b = new FlatButton("\u2026", null, BtnKind.Secondary);
            b.Height = Theme.P(24);
            b.FitWidth();
            b.AccessibleName = what + " – aus Ordner A oder B auswählen";
            b.Click += delegate
            {
                ModernMenuBuilder mb = new ModernMenuBuilder(this);
                ContextMenuStrip m = mb.CreateMenu();
                string kind = dirs ? "Ordner" : "Datei";
                foreach (bool withPath in new bool[] { false, true })
                {
                    GlyphMenuItem head = mb.Item(withPath ? kind + "name + Pfad hinzufügen" : "Nur " + kind + "namen hinzufügen", '\0', null);
                    foreach (bool sideA in new bool[] { true, false })
                    {
                        bool wp = withPath, sa = sideA;
                        string folder = (sideA ? folderA : folderB).Text.Trim();
                        ToolStripMenuItem it = mb.Item("In " + SideTitle(sideA) + " suchen …", MenuHelper.Folder,
                            delegate { PickInto(box, dirs, wp, sa); });
                        // Auswählen geht nur in Windows-Ordnern, nicht auf FTP/SFTP-Servern
                        it.Enabled = folder.Length > 0 && !FtpUrl.IsRemote(folder);
                        head.DropDownItems.Add(it);
                    }
                    mb.StyleDropDown(head);
                    m.Items.Add(head);
                }
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(mb.Item("Was ist der Unterschied?", '\uE897', delegate
                {
                    MessageBox.Show("Nur Name: gilt für " + (dirs ? "jeden Ordner" : "jede Datei") + " mit diesem Namen, egal in welchem Unterordner " +
                        "(z. B. " + (dirs ? ".well-known" : "Thumbs.db") + ").\n\nName + Pfad: gilt nur für " + (dirs ? "genau diesen Ordner" : "genau diese Datei") +
                        " ab dem Startordner der Aufgabe (z. B. " + (dirs ? "\\writable\\cache" : "\\config\\local.php") + ").",
                        "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
                m.Show(b, new Point(0, b.Height));
            };
            return b;
        }

        void PickInto(InputBox box, bool dirs, bool withPath, bool sideA)
        {
            string root;
            try { root = Paths.Resolve((sideA ? folderA : folderB).Text.Trim()).TrimEnd('\\'); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!Directory.Exists(root))
            {
                MessageBox.Show(SideTitle(sideA) + " ist nicht erreichbar:\n" + root, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            List<string> picked = new List<string>();
            if (dirs)
            {
                string dir = FolderPicker.Pick(this, "Ordner innerhalb von " + root + " auswählen", root);
                if (dir == null) return;
                picked.Add(dir);
            }
            else
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "Dateien innerhalb von " + root + " auswählen";
                    d.InitialDirectory = root;
                    d.Multiselect = true;
                    d.Filter = "Alle Dateien (*.*)|*.*";
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    picked.AddRange(d.FileNames);
                }
            }
            List<string> lines = Lines(box);
            foreach (string full in picked)
            {
                string f = full.TrimEnd('\\');
                bool inside = f.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
                if (withPath && !inside)
                {
                    MessageBox.Show("Liegt nicht in Ordner " + (sideA ? "A" : "B") + ":\n" + f, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }
                string entry = withPath ? f.Substring(root.Length) : Path.GetFileName(f);
                if (entry.Length == 0) continue;
                if (!lines.Exists(x => string.Equals(x, entry, StringComparison.OrdinalIgnoreCase))) lines.Add(entry);
            }
            box.Text = string.Join(Environment.NewLine, lines);
        }

        static InputBox ListBox_(List<string> values, string accessibleName)
        {
            InputBox b = new InputBox(true, true);
            b.Height = Theme.P(110);
            b.Box.AccessibleName = accessibleName;
            b.Text = string.Join(Environment.NewLine, values ?? new List<string>());
            return b;
        }

        /// <summary>
        /// Zeilen einer Liste. Leerzeilen bleiben zur Gliederung erhalten (mehrere hintereinander werden zu einer,
        /// am Anfang und Ende entfallen sie); beim Abgleich zählen sie nicht – ebenso Zeilen mit # als Kommentar.
        /// </summary>
        static List<string> Lines(InputBox b)
        {
            List<string> r = new List<string>();
            foreach (string raw in b.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string x = raw.Trim();
                if (x.Length == 0 && (r.Count == 0 || r[r.Count - 1].Length == 0)) continue;
                r.Add(x);
            }
            while (r.Count > 0 && r[r.Count - 1].Length == 0) r.RemoveAt(r.Count - 1);
            return r;
        }

        static string SftpKeyHint(string side)
        {
            return "Ordner " + side + " liegt auf einem SFTP-Server. Bitte einmal unter \u201eAnmeldung\u201c Benutzer und Kennwort eintragen und " +
                "\u201eVerbindung pr\u00fcfen\u201c klicken, um den Schl\u00fcssel des Servers zu best\u00e4tigen.";
        }

        static bool Inside(string inner, string outer)
        {
            string a = inner.TrimEnd('\\') + "\\", b = outer.TrimEnd('\\') + "\\";
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }

        void Fail(string msg, Control focus)
        {
            MessageBox.Show(msg, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (focus != null) focus.Focus();
        }

        SyncJob Save()
        {
            string n = name.Text.Trim();
            if (n.Length == 0) { Fail("Bitte einen Namen für die Aufgabe eingeben.", name.Box); return null; }
            foreach (SyncJob other in Main.Jobs)
                if (other != original && string.Equals(other.Name, n, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((other.Group ?? "").Trim(), groupBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                { Fail(groupBox.Text.Trim().Length > 0 ? "In dieser Gruppe gibt es schon eine Aufgabe mit diesem Namen."
                       : "Es gibt schon eine Aufgabe mit diesem Namen.", name.Box); return null; }

            string a, b;
            try
            {
                a = Normalize(folderA.Text);
                b = Normalize(folderB.Text);
            }
            catch (Exception ex) { Fail("Ungültiger Pfad: " + ex.Message, folderA.Box); return null; }
            if (a.Length == 0 || b.Length == 0) { Fail("Bitte beide Ordner angeben.", a.Length == 0 ? folderA.Box : folderB.Box); return null; }
            string ra = Paths.TryResolve(a), rb = Paths.TryResolve(b);
            if (string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase)) { Fail("Ordner A und Ordner B dürfen nicht gleich sein.", folderB.Box); return null; }
            if (FtpUrl.IsRemote(a)) { try { FtpUrl.Parse(a); } catch (Exception ex) { Fail(ex.Message, folderA.Box); return null; } }
            if (FtpUrl.IsRemote(b)) { try { FtpUrl.Parse(b); } catch (Exception ex) { Fail(ex.Message, folderB.Box); return null; } }
            if (!FtpUrl.IsRemote(a) && !FtpUrl.IsRemote(b) && (Inside(ra, rb) || Inside(rb, ra))) { Fail("Ein Ordner darf nicht im anderen liegen.", folderB.Box); return null; }
            if (FtpUrl.IsSftp(a) && string.IsNullOrEmpty(certA)) { Fail(SftpKeyHint("A"), credA); return null; }
            if (FtpUrl.IsSftp(b) && string.IsNullOrEmpty(certB)) { Fail(SftpKeyHint("B"), credB); return null; }
            long minS, maxS;
            if (!Fmt.TryParseSize(minSize.Text, out minS)) { Fail("Dateigröße „kleiner als“: bitte z. B. 500 KB oder 2 MB eingeben.", minSize.Box); return null; }
            if (!Fmt.TryParseSize(maxSize.Text, out maxS)) { Fail("Dateigröße „größer als“: bitte z. B. 500 KB oder 2 MB eingeben.", maxSize.Box); return null; }
            if (maxS > 0 && minS > maxS) { Fail("Die untere Dateigröße ist größer als die obere.", minSize.Box); return null; }
            ScheduleKind kind = SelectedKind;
            if (kind == ScheduleKind.Uhrzeiten && times.Count == 0) { Fail("Bitte mindestens eine Uhrzeit hinzufügen.", btnTimeAdd); return null; }
            int wDelay, aInterval, aDelay;
            if (!int.TryParse(watchDelay.Text.Trim(), out wDelay) || wDelay < 0 || wDelay > 86400)
            { Fail("„Starten nach“: bitte eine Zahl von 0 bis 86400 Sekunden eingeben.", watchDelay.Box); return null; }
            if (!int.TryParse(availInterval.Text.Trim(), out aInterval) || aInterval < 1 || aInterval > 3600)
            { Fail("„Prüfen alle“: bitte eine Zahl von 1 bis 3600 Sekunden eingeben.", availInterval.Box); return null; }
            if (!int.TryParse(availDelay.Text.Trim(), out aDelay) || aDelay < 0 || aDelay > 3600)
            { Fail("Wartezeit: bitte eine Zahl von 0 bis 3600 Sekunden eingeben.", availDelay.Box); return null; }
            string drv = availDrive.Text.Trim().TrimEnd(':', '\\');
            if (drv.Length == 1 && char.IsLetter(drv[0]))
            { Fail("Beim Laufwerk bitte den Namen (z. B. MeinUsbStick) statt des Buchstabens eingeben – der Buchstabe kann sich ändern.", availDrive.Box); return null; }

            List<string> missing = new List<string>();
            // Ziel erreichbar, nur der Ordner selbst fehlt noch: anbieten, ihn anzulegen
            List<string> create = new List<string>(), createText = new List<string>();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (bool sideA in new bool[] { true, false })
                {
                    string path = sideA ? a : b, rp = sideA ? ra : rb;
                    if (Reachable(path, sideA ? userA : userB, sideA ? passA : passB, sideA ? certA : certB)) continue;
                    string parent = ExistingParent(path);
                    if (parent != null)
                    {
                        create.Add(Paths.Resolve(path));
                        createText.Add(SideTitle(sideA) + ": " + rp);
                    }
                    else missing.Add(SideTitle(sideA) + ": " + rp);
                }
            }
            finally { Cursor = Cursors.Default; }
            if (create.Count > 0)
            {
                bool one = create.Count == 1;
                DialogResult r = MessageBox.Show((one ? "Diesen Ordner gibt es noch nicht:\n" : "Diese Ordner gibt es noch nicht:\n") +
                    string.Join("\n", createText) + "\n\n" + "Jetzt anlegen?" +
                    " (Bei „Nein“ wird die Aufgabe trotzdem gespeichert; der Ordner muss dann vor dem ersten Lauf da sein.)",
                    "Sync Pilot", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return null;
                if (r == DialogResult.Yes)
                {
                    foreach (string dir in create)
                    {
                        try { Directory.CreateDirectory(dir); }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Der Ordner ließ sich nicht anlegen:\n" + dir + "\n\n" + ex.Message, "Sync Pilot",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return null;
                        }
                    }
                }
            }
            if (missing.Count > 0)
            {
                DialogResult r = MessageBox.Show("Folgende Ordner sind gerade nicht erreichbar:\n" + string.Join("\n", missing) +
                    "\n\nTrotzdem speichern? (Sinnvoll z. B. bei USB-Laufwerken, die nicht immer angeschlossen sind.)",
                    "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return null;
            }

            SyncJob j = isNew ? new SyncJob() : original;
            bool foldersChanged = !isNew && (!string.Equals(j.FolderA, a, StringComparison.OrdinalIgnoreCase) ||
                                             !string.Equals(j.FolderB, b, StringComparison.OrdinalIgnoreCase));
            bool swapped = foldersChanged && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(j.FolderA, b, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(j.FolderB, a, StringComparison.OrdinalIgnoreCase);
            j.Name = n;
            j.FolderA = a;
            j.FolderB = b;
            j.DeviceA = devA.Key;
            j.DeviceB = devB.Key;
            j.UserA = string.IsNullOrEmpty(userA) ? null : userA;
            j.PassA = string.IsNullOrEmpty(userA) ? null : passA;
            j.UserB = string.IsNullOrEmpty(userB) ? null : userB;
            j.PassB = string.IsNullOrEmpty(userB) ? null : passB;
            // FTP: auch ohne Benutzername (anonym) ein Kennwort zulassen
            if (FtpUrl.IsRemote(a) && string.IsNullOrEmpty(userA)) j.PassA = string.IsNullOrEmpty(passA) ? null : passA;
            if (FtpUrl.IsRemote(b) && string.IsNullOrEmpty(userB)) j.PassB = string.IsNullOrEmpty(passB) ? null : passB;
            // FTPS: bestätigtes Zertifikat, SFTP: bestätigter Server-Schlüssel
            j.CertA = FtpUrl.IsRemote(a) ? certA : null;
            j.CertB = FtpUrl.IsRemote(b) ? certB : null;
            j.Mode = mMirror.Checked ? SyncMode.Spiegeln : mBackup.Checked ? SyncMode.Sichern : SyncMode.Abgleichen;
            j.PropagateDeletes = tDeletes.Checked;
            j.UseRecycleBin = tRecycle.Checked;
            j.IncludeHidden = tHidden.Checked;
            j.CompareContent = tContent.Checked;
            j.IgnoreDstOffset = tDst.Checked;
            j.IncludeSubdirs = tSubdirs.Checked;
            j.CopyRights = tRights.Checked;
            j.KeepVersions = tVersions.Checked;
            j.NameA = nameA.Text.Trim().Length > 0 ? nameA.Text.Trim() : null;
            j.Group = groupBox.Text.Trim().Length > 0 ? groupBox.Text.Trim() : null;
            j.NameB = nameB.Text.Trim().Length > 0 ? nameB.Text.Trim() : null;
            j.Conflicts = Rules[Math.Max(0, Math.Min(Rules.Length - 1, cbConflict.SelectedIndex))];
            j.NeverDeleteA = tNoDelA.Checked;
            j.NeverDeleteB = tNoDelB.Checked;
            j.LockFile = lockFile.Text.Trim().Length > 0 ? lockFile.Text.Trim().Trim('"') : null;
            j.ScriptBefore = scriptBefore.Text.Trim().Length > 0 ? scriptBefore.Text.Trim() : null;
            j.ScriptAfter = scriptAfter.Text.Trim().Length > 0 ? scriptAfter.Text.Trim() : null;
            j.ScriptsOnlyWithChanges = tScriptsChanged.Checked;
            j.ScriptAbortOnError = tScriptAbort.Checked;
            j.RunAsAdmin = tAdmin.Checked;
            j.Excludes = new List<string>();
            j.ExcludeFiles = Lines(exFiles);
            j.ExcludeDirs = Lines(exDirs);
            j.IncludeFiles = Lines(inFiles);
            j.IncludeDirs = Lines(inDirs);
            j.MinSize = minS;
            j.MaxSize = maxS;
            j.Disabled = !tActive.Checked;
            j.Schedule = kind;
            j.Times = times.Select(t => new TimeSlot(t.Days, t.At)).ToList();
            int ix = cbInterval.SelectedIndex;
            j.IntervalMinutes = ix >= 0 && ix < Intervals.Length ? Intervals[ix] : (customInterval > 0 ? customInterval : 60);
            j.CatchUp = tCatchUp.Checked;
            j.SkipOffline = tOffline.Checked;
            j.WatchA = tWatchA.Checked;
            j.WatchB = tWatchB.Checked;
            j.WatchDelay = wDelay;
            j.TriggerFile = trigFile.Text.Trim().Length > 0 ? trigFile.Text.Trim().Trim('"') : null;
            j.AvailFolder = availFolder.Text.Trim().Length > 0 ? availFolder.Text.Trim().Trim('"') : null;
            j.AvailHost = availHost.Text.Trim().Length > 0 ? availHost.Text.Trim() : null;
            j.AvailDrive = drv.Length > 0 ? drv : null;
            j.AvailInterval = aInterval;
            j.AvailDelay = aDelay;
            {
                int gsel = cbEvtGap.SelectedIndex;
                j.EventOnlyChanges = tEvtChanges.Checked;
                j.EventMinGap = gsel >= 0 && gsel < EvtGaps.Length ? EvtGaps[gsel] : (original != null ? original.EventMinGap : 0);
            }

            if (swapped)
            {
                // nur getauscht: Stand mitdrehen statt neu anfangen
                try { StateStore.Swap(j.Id); }
                catch { StateStore.Reset(j.Id); }
            }
            else if (foldersChanged) StateStore.Reset(j.Id); // neues Ordnerpaar: neu anfangen, nicht nach verlorenem Stand fragen
            if (isNew) Main.Jobs.Add(j);

            string err = Scheduler.Apply(j);
            Main.SaveJobs();
            if (err != null)
                MessageBox.Show("Die Aufgabe wurde gespeichert, aber der Zeitplan konnte nicht eingerichtet werden:\n\n" + err,
                    "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // Ereignisse brauchen das laufende Programm: anbieten, im Infobereich weiterzulaufen und mit Windows zu starten
            AppSettings st = AppSettings.Current;
            if (j.HasEvents && !j.Disabled && !Demo.Active && !st.NoAskBackground && (!st.Background || !st.Autostart))
            {
                DialogResult r;
                using (MsgDialog d = new MsgDialog("Starten bei Ereignissen funktioniert nur, solange Sync Pilot läuft.\n\n" +
                    "Soll Sync Pilot mit Windows starten und beim Schließen des Fensters im Infobereich weiterlaufen?",
                    "Sync Pilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, "Nicht mehr fragen"))
                {
                    r = d.Map(d.ShowDialog(FindForm()));
                    if (d.Checked) { st.NoAskBackground = true; st.Save(); }
                }
                if (r == DialogResult.Yes)
                {
                    st.Background = true;
                    st.Autostart = true;
                    st.Save();
                    st.ApplyAutostart();
                    Main.UpdateTray();
                }
            }
            return j;
        }
    }
}
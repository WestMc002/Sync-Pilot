// Sync Pilot – Datenmodell und Speicherung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Serialization;

namespace SyncPilot
{
    public enum SyncMode { Abgleichen, Sichern, Spiegeln }

    /// <summary>Was bei einem Konflikt passiert (Datei auf beiden Seiten geändert bzw. auf einer Seite geändert, auf der anderen gelöscht).</summary>
    public enum ConflictRule { Fragen, Neuer, AGewinnt, BGewinnt }

    /// <summary>Taeglich und Stuendlich nur noch zum Einlesen älterer Aufgaben (siehe SyncJob.Upgrade).</summary>
    public enum ScheduleKind { Manuell, Taeglich, Stuendlich, BeiAnmeldung, Uhrzeiten, Intervall }

    /// <summary>Feste Uhrzeit an bestimmten Wochentagen (Bit 0 = Montag … Bit 6 = Sonntag).</summary>
    public class TimeSlot
    {
        [XmlAttribute("Tage")] public int Days = 127;
        [XmlAttribute("Uhrzeit")] public string At = "22:00";

        public static readonly string[] DayShort = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };

        public TimeSlot() { }
        public TimeSlot(int days, string at) { Days = days; At = at; }

        public bool Has(int day) { return (Days & (1 << day)) != 0; }

        /// <summary>z. B. "Täglich 23:10", "Mo–Fr 12:00", "Mo, Mi, Fr 08:30"</summary>
        public override string ToString()
        {
            return DaysText(Days) + " " + At;
        }

        public static string DaysText(int days)
        {
            days &= 127;
            if (days == 127) return "Täglich";
            if (days == 31) return "Mo–Fr";
            if (days == 96) return "Sa, So";
            // zusammenhängende Folge von mindestens drei Tagen als Bereich
            List<int> on = new List<int>();
            for (int i = 0; i < 7; i++) if ((days & (1 << i)) != 0) on.Add(i);
            if (on.Count >= 3 && on[on.Count - 1] - on[0] == on.Count - 1) return DayShort[on[0]] + "–" + DayShort[on[on.Count - 1]];
            return string.Join(", ", on.Select(i => DayShort[i]).ToArray());
        }

        /// <summary>"8:5" oder "08.05" in "08:05" umwandeln; false bei ungültiger Uhrzeit.</summary>
        public static bool TryParseTime(string text, out string at)
        {
            at = null;
            string t = (text ?? "").Trim();
            if (t.EndsWith("Uhr", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 3).Trim();
            t = t.Replace('.', ':').Replace(',', ':').Replace(' ', ':').Replace('h', ':').Replace('H', ':').TrimEnd(':');
            // nur Ziffern: "2330" -> 23:30, "930" -> 09:30, "23" -> 23:00, "7" -> 07:00
            if (t.Length >= 3 && t.Length <= 4 && t.All(char.IsDigit)) t = t.Substring(0, t.Length - 2) + ":" + t.Substring(t.Length - 2);
            string[] parts = t.Split(':');
            int h, m = 0;
            if (parts.Length < 1 || parts.Length > 2 || !int.TryParse(parts[0], out h)) return false;
            if (parts.Length == 2 && !int.TryParse(parts[1], out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            at = h.ToString("00") + ":" + m.ToString("00");
            return true;
        }
    }

    public class SyncJob
    {
        public string Id;
        public string Name;
        /// <summary>Gruppe in der Aufgabenliste (z. B. "NAS"); leer = ohne Gruppe.</summary>
        public string Group;
        public string FolderA;
        public string FolderB;
        /// <summary>Geräte-Icon je Ordner (siehe Devices.Keys, "Auto" = aus dem Pfad).</summary>
        public string DeviceA = "Auto";
        public string DeviceB = "Auto";
        /// <summary>Eigene Bezeichnung für Ordner A bzw. B (z. B. "PC" und "NAS"); leer = "A" bzw. "B".</summary>
        public string NameA;
        public string NameB;
        /// <summary>Anmeldung an Netzwerkfreigaben; Kennwort mit Secret.Protect verschlüsselt.</summary>
        public string UserA;
        public string PassA;
        public string UserB;
        public string PassB;
        /// <summary>FTPS: bestätigter Fingerabdruck eines nicht offiziell signierten Server-Zertifikats.</summary>
        public string CertA;
        public string CertB;
        public SyncMode Mode;
        public bool PropagateDeletes = true;
        public bool UseRecycleBin = true;
        public bool IncludeHidden;
        public bool CompareContent;
        /// <summary>Gleiche Größe und genau 1 Stunde Zeitunterschied gilt als gleich (Sommerzeit auf FAT-Laufwerken, NAS).</summary>
        public bool IgnoreDstOffset = true;
        /// <summary>Unterordner einbeziehen (aus: nur die Dateien direkt im gewählten Ordner, wie Easy2Sync ohne „inklusive Unterverzeichnisse“).</summary>
        public bool IncludeSubdirs = true;
        /// <summary>NTFS-Zugriffsrechte von Dateien und Ordnern mitkopieren (nur lokale Laufwerke und Windows-Freigaben).</summary>
        public bool CopyRights;
        /// <summary>Überschriebene und gelöschte Dateien vorher in den Ordner "_Sync Pilot Versionen" der jeweiligen Seite verschieben.</summary>
        public bool KeepVersions;
        /// <summary>Auf dieser Seite nie etwas löschen.</summary>
        public bool NeverDeleteA;
        public bool NeverDeleteB;
        public ConflictRule Conflicts;
        /// <summary>Sperrdatei gegen gleichzeitigen Abgleich von einem anderen PC (leer = aus).</summary>
        public string LockFile;
        /// <summary>Befehl bzw. Skript vor und nach dem Kopieren (leer = aus).</summary>
        public string ScriptBefore;
        public string ScriptAfter;
        /// <summary>Skripte nur ausführen, wenn es etwas zu kopieren, umzubenennen oder zu löschen gibt.</summary>
        public bool ScriptsOnlyWithChanges = true;
        /// <summary>Meldet das Skript vor dem Kopieren einen Fehler (Rückgabewert ungleich 0), nicht synchronisieren.</summary>
        public bool ScriptAbortOnError;
        /// <summary>Mit Administratorrechten ausführen (Zeitplan: höchste Rechte; manuell: Sync Pilot als Administrator neu starten).</summary>
        public bool RunAsAdmin;
        /// <summary>Nur noch zum Einlesen älterer Aufgaben (gemischte Liste, Ordner mit "\" am Ende); siehe Upgrade().</summary>
        public List<string> Excludes = new List<string>();
        /// <summary>Negativliste: Dateien bzw. Ordner mit diesem Namen oder Pfad nicht abgleichen.</summary>
        public List<string> ExcludeFiles = new List<string>();
        public List<string> ExcludeDirs = new List<string>();
        /// <summary>Positivliste: wenn nicht leer, NUR passende Dateien bzw. Ordner (mit allem darin) abgleichen.</summary>
        public List<string> IncludeFiles = new List<string>();
        public List<string> IncludeDirs = new List<string>();
        /// <summary>Dateigröße: kleinere bzw. größere Dateien nicht abgleichen (Bytes, 0 = aus).</summary>
        public long MinSize;
        public long MaxSize;
        /// <summary>Deaktiviert: läuft nicht nach Zeitplan und nicht bei "Alle synchronisieren".</summary>
        public bool Disabled;
        public ScheduleKind Schedule;
        /// <summary>Nur noch zum Einlesen älterer Aufgaben ("Täglich um …").</summary>
        public string ScheduleTime = "22:00";
        /// <summary>Zeitplan "Zu festen Uhrzeiten".</summary>
        [XmlArray("Uhrzeiten"), XmlArrayItem("Zeit")]
        public List<TimeSlot> Times = new List<TimeSlot>();
        /// <summary>Zeitplan "Alle x Minuten/Stunden".</summary>
        public int IntervalMinutes = 60;
        /// <summary>Verpassten Termin nachholen, sobald der PC wieder läuft.</summary>
        public bool CatchUp = true;
        /// <summary>Automatische Läufe ohne Fehlermeldung auslassen, wenn ein Ordner (Laufwerk, Freigabe, Server) fehlt.</summary>
        public bool SkipOffline = true;
        public string ScheduledTask;

        // ----- Ereignisse (nur solange Sync Pilot läuft, auch im Infobereich) -----
        /// <summary>Bei Änderungen in Ordner A bzw. B starten, sobald WatchDelay Sekunden lang nichts mehr passiert ist.</summary>
        public bool WatchA;
        public bool WatchB;
        public int WatchDelay = 60;
        /// <summary>Existiert diese Datei, wird sie gelöscht und die Aufgabe gestartet.</summary>
        public string TriggerFile;
        /// <summary>Starten, sobald einer davon verfügbar wird: Ordner, Computername/IP, Laufwerksbezeichnung.</summary>
        public string AvailFolder;
        public string AvailHost;
        public string AvailDrive;
        public int AvailInterval = 10;
        public int AvailDelay = 10;
        /// <summary>Durch Ereignisse höchstens alle x Minuten starten (0 = ohne Begrenzung); frühere Anlässe werden danach nachgeholt.</summary>
        public int EventMinGap;
        /// <summary>Durch Ereignisse gestartet und nichts zu tun: still auslassen (kein Eintrag im Verlauf, kein Protokoll, keine Skripte).</summary>
        public bool EventOnlyChanges = true;

        public static string GapText(int minutes)
        {
            if (minutes <= 0) return "Ohne Begrenzung";
            if (minutes < 60) return minutes + " Minuten";
            if (minutes % 60 != 0) return minutes + " Minuten";
            return minutes == 60 ? "1 Stunde" : (minutes / 60) + " Stunden";
        }

        /// <summary>Beim Einlesen umgestellter Zeitplan: muss in der Aufgabenplanung neu eingerichtet werden.</summary>
        [XmlIgnore] public bool NeedsReapply;

        public SyncJob()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "";
            FolderA = "";
            FolderB = "";
        }

        public SyncJob Clone()
        {
            SyncJob j = (SyncJob)MemberwiseClone();
            j.Excludes = new List<string>(Excludes ?? new List<string>());
            j.ExcludeFiles = new List<string>(ExcludeFiles ?? new List<string>());
            j.ExcludeDirs = new List<string>(ExcludeDirs ?? new List<string>());
            j.IncludeFiles = new List<string>(IncludeFiles ?? new List<string>());
            j.IncludeDirs = new List<string>(IncludeDirs ?? new List<string>());
            j.Times = (Times ?? new List<TimeSlot>()).Select(t => new TimeSlot(t.Days, t.At)).ToList();
            return j;
        }

        /// <summary>
        /// Ältere Aufgaben: die gemischte Ausnahmeliste auf Dateien und Ordner aufteilen. "name\" war ein Ordner;
        /// ein Muster ohne "\" am Ende galt bisher für beides und wird deshalb – wenn es nicht nach einer Datei aussieht –
        /// auch als Ordner übernommen.
        /// </summary>
        public void Upgrade()
        {
            if (ExcludeFiles == null) ExcludeFiles = new List<string>();
            if (ExcludeDirs == null) ExcludeDirs = new List<string>();
            if (IncludeFiles == null) IncludeFiles = new List<string>();
            if (IncludeDirs == null) IncludeDirs = new List<string>();
            if (Excludes == null) Excludes = new List<string>();
            if (Times == null) Times = new List<TimeSlot>();
            // früher: "ohne Unterordner" als Ausnahme "*" bei den Ordnern – jetzt eigener Schalter
            if (ExcludeDirs.RemoveAll(x => x != null && x.Trim() == "*") > 0) IncludeSubdirs = false;
            // alter Zeitplan: "Täglich um …" = feste Uhrzeit an allen Tagen, "Stündlich" = alle 60 Minuten
            if (Schedule == ScheduleKind.Taeglich)
            {
                string at;
                if (!TimeSlot.TryParseTime(ScheduleTime, out at)) at = "22:00";
                Times.Clear();
                Times.Add(new TimeSlot(127, at));
                Schedule = ScheduleKind.Uhrzeiten;
                NeedsReapply = true;
            }
            else if (Schedule == ScheduleKind.Stuendlich)
            {
                IntervalMinutes = 60;
                Schedule = ScheduleKind.Intervall;
                NeedsReapply = true;
            }
            if (IntervalMinutes < 1) IntervalMinutes = 60;
            if (WatchDelay < 0) WatchDelay = 60;
            if (AvailInterval < 1) AvailInterval = 10;
            if (AvailDelay < 0) AvailDelay = 10;
            foreach (string raw in Excludes)
            {
                string p = (raw ?? "").Trim();
                if (p.Length == 0) continue;
                if (p.EndsWith("\\") || p.EndsWith("/"))
                {
                    AddOnce(ExcludeDirs, p.TrimEnd('\\', '/'));
                    continue;
                }
                AddOnce(ExcludeFiles, p);
                string last = p.Substring(p.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                bool looksLikeFile = last.Contains(".") && !last.StartsWith(".") || last.StartsWith("~$");
                if (!looksLikeFile) AddOnce(ExcludeDirs, p);
            }
            Excludes.Clear();
        }

        static void AddOnce(List<string> list, string p)
        {
            if (p.Length > 0 && !list.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) list.Add(p);
        }

        public string ModeText
        {
            get
            {
                switch (Mode)
                {
                    case SyncMode.Sichern: return Sides("Sichern A → B");
                    case SyncMode.Spiegeln: return Sides("Spiegeln A → B");
                    default: return Sides("Abgleichen A ⇄ B");
                }
            }
        }

        /// <summary>Bezeichnung von Ordner A bzw. B: eigener Name oder "A"/"B".</summary>
        /// <summary>Quelle und Ziel tauschen: Ordner, Gerät, Bezeichnung, Anmeldung, Überwachung, "niemals löschen" und Konfliktregel.</summary>
        public void SwapSides()
        {
            string t;
            t = FolderA; FolderA = FolderB; FolderB = t;
            t = DeviceA; DeviceA = DeviceB; DeviceB = t;
            t = NameA; NameA = NameB; NameB = t;
            t = UserA; UserA = UserB; UserB = t;
            t = PassA; PassA = PassB; PassB = t;
            t = CertA; CertA = CertB; CertB = t;
            bool b;
            b = NeverDeleteA; NeverDeleteA = NeverDeleteB; NeverDeleteB = b;
            b = WatchA; WatchA = WatchB; WatchB = b;
            if (Conflicts == ConflictRule.AGewinnt) Conflicts = ConflictRule.BGewinnt;
            else if (Conflicts == ConflictRule.BGewinnt) Conflicts = ConflictRule.AGewinnt;
        }

        /// <summary>Skripte laufen auch ohne Änderungen (Option „nur bei Änderungen“ aus) – dann lohnt ein Lauf immer.</summary>
        public bool ScriptsAlways
        {
            get
            {
                return !ScriptsOnlyWithChanges &&
                    (!string.IsNullOrWhiteSpace(ScriptBefore) || !string.IsNullOrWhiteSpace(ScriptAfter));
            }
        }

        /// <summary>
        /// Kurzer Name einer Seite für Listen: eigene Bezeichnung, sonst aus dem Pfad gebildet –
        /// Freigabe/FTP/SFTP: Rechner- bzw. Servername, USB-Stick (%DRIVELETTER:Name%): sein Name,
        /// lokal: dieser PC mit Laufwerk, z. B. "PC (F:)".
        /// </summary>
        public string ShortSide(bool a)
        {
            string n = a ? NameA : NameB;
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            return ShortOfPath(a ? FolderA : FolderB);
        }

        static string hostName;

        public static string ShortOfPath(string path)
        {
            string p = (path ?? "").Trim();
            if (p.Length == 0) return "?";
            try
            {
                if (FtpUrl.IsRemote(p)) return FtpUrl.Parse(p).Host;
            }
            catch { }
            if (p.StartsWith("\\\\"))
            {
                string rest = p.Substring(2);
                int i = rest.IndexOf('\\');
                return i > 0 ? rest.Substring(0, i) : rest;
            }
            System.Text.RegularExpressions.Match usb = System.Text.RegularExpressions.Regex.Match(p, @"%DRIVELETTER:([^%]+)%",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (usb.Success) return usb.Groups[1].Value;
            if (hostName == null)
            {
                try { hostName = System.Net.Dns.GetHostName(); } catch { }
                if (string.IsNullOrEmpty(hostName)) hostName = Environment.MachineName;
            }
            string me = Demo.Active ? "PC" : hostName; // Screenshots ohne den echten Rechnernamen
            if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0])) return me + " (" + char.ToUpperInvariant(p[0]) + ":)";
            return me;
        }

        public string SideName(bool a)
        {
            string n = a ? NameA : NameB;
            return string.IsNullOrWhiteSpace(n) ? (a ? "A" : "B") : n.Trim();
        }

        /// <summary>Überschrift einer Seite: "Ordner A" bzw. der eigene Name.</summary>
        public string SideTitle(bool a)
        {
            string n = a ? NameA : NameB;
            return string.IsNullOrWhiteSpace(n) ? (a ? "Ordner A" : "Ordner B") : n.Trim();
        }

        static readonly System.Text.RegularExpressions.Regex SideWord = new System.Text.RegularExpressions.Regex(@"(?<![\w\\:])([AB])(?![\w:])");

        /// <summary>Setzt in einem Anzeigetext ("Nach B", "Neu in A", "A → B") die eigenen Bezeichnungen der Ordner ein.</summary>
        public string Sides(string text)
        {
            if (string.IsNullOrEmpty(text) || (string.IsNullOrWhiteSpace(NameA) && string.IsNullOrWhiteSpace(NameB))) return text;
            return SideWord.Replace(text, delegate (System.Text.RegularExpressions.Match m) { return SideName(m.Value == "A"); });
        }

        public bool HasWatch { get { return WatchA || WatchB || !string.IsNullOrWhiteSpace(TriggerFile); } }

        public bool HasAvail
        {
            get { return !string.IsNullOrWhiteSpace(AvailFolder) || !string.IsNullOrWhiteSpace(AvailHost) || !string.IsNullOrWhiteSpace(AvailDrive); }
        }

        /// <summary>Startet bei Ereignissen (Ordnerüberwachung, Trigger-Datei, Verfügbarkeit) – braucht das laufende Programm.</summary>
        public bool HasEvents { get { return HasWatch || HasAvail; } }

        public static string IntervalText(int minutes)
        {
            if (minutes % 60 == 0) return minutes == 60 ? "Stündlich" : "Alle " + (minutes / 60) + " Stunden";
            return "Alle " + minutes + " Minuten";
        }

        public string ScheduleText
        {
            get
            {
                List<string> parts = new List<string>();
                switch (Schedule)
                {
                    case ScheduleKind.Taeglich: parts.Add("Täglich " + ScheduleTime); break;
                    case ScheduleKind.Stuendlich: parts.Add("Stündlich"); break;
                    case ScheduleKind.BeiAnmeldung: parts.Add("Bei Anmeldung"); break;
                    case ScheduleKind.Intervall: parts.Add(IntervalText(IntervalMinutes)); break;
                    case ScheduleKind.Uhrzeiten:
                        if (Times == null || Times.Count == 0) break;
                        parts.Add(Times.Count == 1 ? Times[0].ToString() : Times.Count + " feste Uhrzeiten");
                        break;
                }
                if (WatchA || WatchB) parts.Add("Bei Änderung");
                if (!string.IsNullOrWhiteSpace(TriggerFile)) parts.Add("Trigger-Datei");
                if (HasAvail) parts.Add("Wenn verfügbar");
                return parts.Count == 0 ? "Nur manuell" : string.Join(" · ", parts.ToArray());
            }
        }
    }

    public class HistoryEntry
    {
        public DateTime Start;
        public string JobId;
        public string JobName;
        public string Result;
        public int Copied;
        public int Moved;
        public int Deleted;
        public int Conflicts;
        public int Skipped;
        public int Errors;
        public long Bytes;
        public double Seconds;
        public bool Scheduled;
        public bool Cancelled;
        public string LogFile;

        public bool IsOk { get { return Errors == 0 && !Cancelled; } }
    }

    [XmlRoot("Aufgaben")]
    public class JobFile
    {
        [XmlElement("Aufgabe")]
        public List<SyncJob> Jobs = new List<SyncJob>();
    }

    [XmlRoot("Verlauf")]
    public class HistoryFile
    {
        [XmlElement("Lauf")]
        public List<HistoryEntry> Entries = new List<HistoryEntry>();
    }

    public static class Store
    {
        /// <summary>Laufzeitdaten unter %LOCALAPPDATA%\Sync Pilot (Globale Richtlinien; bis 1.12 %APPDATA%, siehe Migrate).</summary>
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sync Pilot"); }
        }

        static string OldDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sync Pilot"); }
        }

        /// <summary>
        /// Einmalig beim Start: Daten aus dem alten Ort (%APPDATA%, servergespeichertes Profil) nach %LOCALAPPDATA%
        /// übernehmen. Verschieben, sonst kopieren; am alten Ort bleibt dann nur ein Hinweis. Kennwörter (DPAPI,
        /// aktueller Benutzer) bleiben lesbar.
        /// </summary>
        public static void Migrate()
        {
            try
            {
                using (System.Threading.Mutex mx = new System.Threading.Mutex(false, "Local\\SyncPilot_Migrate"))
                {
                    bool own = false;
                    try { own = mx.WaitOne(30000); }
                    catch (System.Threading.AbandonedMutexException) { own = true; }
                    try
                    {
                        string from = OldDir, to = Dir;
                        if (!Directory.Exists(from) || File.Exists(Path.Combine(to, "aufgaben.xml")) ||
                            File.Exists(Path.Combine(to, "einstellungen.xml")) ||
                            File.Exists(Path.Combine(from, "Verschoben.txt")))
                            return;
                        if (!Directory.Exists(to))
                        {
                            try
                            {
                                Directory.Move(from, to);
                                return;
                            }
                            catch { } // z. B. Datei gerade offen oder anderes Laufwerk: kopieren
                        }
                        CopyDir(from, to);
                        File.WriteAllText(Path.Combine(from, "Verschoben.txt"),
                            "Die Daten von Sync Pilot liegen jetzt unter %LOCALAPPDATA%\\Sync Pilot (" + to + ").\r\n" +
                            "Dieser Ordner wird nicht mehr verwendet und kann gelöscht werden.\r\n", System.Text.Encoding.UTF8);
                    }
                    finally { if (own) mx.ReleaseMutex(); }
                }
            }
            catch { } // im Zweifel neu anfangen statt nicht zu starten
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                string t = Path.Combine(to, Path.GetFileName(f));
                if (!File.Exists(t)) File.Copy(f, t);
            }
            foreach (string d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
        public static string LogDir { get { return Path.Combine(Dir, "Protokolle"); } }

        static string CleanupMarker { get { return Path.Combine(LogDir, ".aufgeraeumt"); } }
        static readonly object cleanupLock = new object();

        /// <summary>
        /// Protokolle löschen, die älter sind als in den Einstellungen festgelegt (Standard 30 Tage).
        /// Ohne force höchstens alle 12 Stunden (wird bei jedem neuen Protokoll aufgerufen).
        /// </summary>
        public static void CleanupLogs(bool force)
        {
            lock (cleanupLock)
            {
                try
                {
                    int days = AppSettings.Current.LogDays;
                    if (days <= 0 || !Directory.Exists(LogDir)) return;
                    if (!force && File.Exists(CleanupMarker) &&
                        DateTime.UtcNow - File.GetLastWriteTimeUtc(CleanupMarker) < TimeSpan.FromHours(12)) return;
                    DateTime limit = DateTime.UtcNow.AddDays(-days);
                    foreach (string f in Directory.GetFiles(LogDir, "*.log"))
                    {
                        try { if (File.GetLastWriteTimeUtc(f) < limit) File.Delete(f); }
                        catch { } // gerade geöffnet o. Ä.: beim nächsten Mal
                    }
                    File.WriteAllText(CleanupMarker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    try { File.SetAttributes(CleanupMarker, FileAttributes.Hidden); } catch { }
                }
                catch { }
            }
        }
        public static string StateDir { get { return Path.Combine(Dir, "Status"); } }
        static string JobsFile { get { return Path.Combine(Dir, "aufgaben.xml"); } }
        static string HistoryPath { get { return Path.Combine(Dir, "verlauf.xml"); } }

        const int MaxHistory = 1000;

        static void Locked(Action a)
        {
            using (Mutex m = Ipc.Mutex(false, "Local\\SyncPilot_Store"))
            {
                bool own = false;
                try
                {
                    try { own = m.WaitOne(10000); }
                    catch (AbandonedMutexException) { own = true; }
                    if (!own) throw new IOException("Die Datendateien sind gerade gesperrt. Bitte später erneut versuchen.");
                    a();
                }
                finally
                {
                    if (own) m.ReleaseMutex();
                }
            }
        }

        static T Read<T>(string path) where T : new()
        {
            if (!File.Exists(path)) return new T();
            XmlSerializer xs = new XmlSerializer(typeof(T));
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return (T)xs.Deserialize(fs);
            }
        }

        static void Write<T>(string path, T data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            XmlSerializer xs = new XmlSerializer(typeof(T));
            using (StreamWriter sw = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                xs.Serialize(sw, data);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static List<SyncJob> LoadJobs()
        {
            if (Demo.Active) return Demo.Jobs();
            List<SyncJob> list = null;
            Locked(delegate { list = Read<JobFile>(JobsFile).Jobs; });
            foreach (SyncJob j in list)
            {
                j.Upgrade();
                if (string.IsNullOrEmpty(j.Id)) j.Id = Guid.NewGuid().ToString("N");
            }
            return list;
        }

        public static void SaveJobs(List<SyncJob> jobs)
        {
            if (Demo.Active) return;
            JobFile f = new JobFile();
            f.Jobs = jobs;
            Locked(delegate { Write(JobsFile, f); });
        }

        /// <summary>
        /// Aufgaben in eine Datei exportieren (Sicherung, anderer PC). Kennwörter bleiben weg: Sie sind an den
        /// Windows-Benutzer gebunden (DPAPI) und wären woanders nicht lesbar.
        /// </summary>
        public static void ExportJobs(List<SyncJob> jobs, string file)
        {
            JobFile f = new JobFile();
            foreach (SyncJob j in jobs)
            {
                SyncJob c = j.Clone();
                c.PassA = null;
                c.PassB = null;
                c.ScheduledTask = null;
                f.Jobs.Add(c);
            }
            Write(file, f);
        }

        /// <summary>Exportierte Aufgaben lesen (ohne Kennwörter und ohne Verweis auf eine geplante Aufgabe).</summary>
        public static List<SyncJob> ReadExport(string file)
        {
            List<SyncJob> list = Read<JobFile>(file).Jobs;
            foreach (SyncJob j in list)
            {
                j.Upgrade();
                j.PassA = null;
                j.PassB = null;
                j.ScheduledTask = null;
            }
            return list;
        }

        public static List<HistoryEntry> LoadHistory()
        {
            if (Demo.Active) return Demo.History();
            List<HistoryEntry> list = new List<HistoryEntry>();
            try { Locked(delegate { list = Read<HistoryFile>(HistoryPath).Entries; }); }
            catch { }
            return list;
        }

        public static DateTime HistoryStamp()
        {
            if (Demo.Active) return DateTime.MinValue;
            try { return File.Exists(HistoryPath) ? File.GetLastWriteTimeUtc(HistoryPath) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        public static void AddHistory(HistoryEntry e)
        {
            if (Demo.Active) return;
            try
            {
                Locked(delegate
                {
                    HistoryFile f;
                    try { f = Read<HistoryFile>(HistoryPath); }
                    catch { f = new HistoryFile(); }
                    f.Entries.Add(e);
                    if (f.Entries.Count > MaxHistory)
                        f.Entries.RemoveRange(0, f.Entries.Count - MaxHistory);
                    Write(HistoryPath, f);
                });
            }
            catch { }
        }

        public static void ClearHistory()
        {
            if (Demo.Active) return;
            Locked(delegate { Write(HistoryPath, new HistoryFile()); });
        }

        public static HistoryEntry LastRun(List<HistoryEntry> history, string jobId)
        {
            for (int i = history.Count - 1; i >= 0; i--)
                if (history[i].JobId == jobId) return history[i];
            return null;
        }
    }

    public static class Fmt
    {
        /// <summary>Größe zum Bearbeiten: glatte Werte als KB/MB/GB, sonst Bytes.</summary>
        public static string SizeInput(long bytes)
        {
            string[] units = { "GB", "MB", "KB" };
            long[] f = { 1L << 30, 1L << 20, 1L << 10 };
            for (int i = 0; i < 3; i++)
                if (bytes >= f[i] && (bytes * 10) % f[i] == 0)
                    return (bytes / (double)f[i]).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " " + units[i];
            return bytes.ToString();
        }

        /// <summary>"500 KB", "2 MB", "1,5 GB", "123" (Bytes). Leer = 0. false bei ungültiger Eingabe.</summary>
        public static bool TryParseSize(string text, out long bytes)
        {
            bytes = 0;
            string t = (text ?? "").Trim().ToUpperInvariant().Replace(" ", "");
            if (t.Length == 0) return true;
            long factor = 1;
            string[] units = { "GB", "MB", "KB", "G", "M", "K", "BYTES", "BYTE", "B" };
            long[] f = { 1L << 30, 1L << 20, 1L << 10, 1L << 30, 1L << 20, 1L << 10, 1, 1, 1 };
            for (int i = 0; i < units.Length; i++)
                if (t.EndsWith(units[i])) { factor = f[i]; t = t.Substring(0, t.Length - units[i].Length); break; }
            double v;
            if (!double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) || v < 0) return false;
            if (double.IsNaN(v) || double.IsInfinity(v) || v * factor > long.MaxValue / 2) return false;
            bytes = (long)Math.Round(v * factor);
            return true;
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes / 1024.0;
            if (v < 1024) return v.ToString("0") + " KB";
            v /= 1024;
            if (v < 1024) return v.ToString(v < 10 ? "0.0" : "0") + " MB";
            v /= 1024;
            return v.ToString("0.0") + " GB";
        }

        public static string When(DateTime t)
        {
            DateTime today = DateTime.Today;
            if (t.Date == today) return "Heute, " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "Gestern, " + t.ToString("HH:mm");
            return t.ToString("dd.MM.yyyy, HH:mm");
        }

        public static string Duration(double seconds)
        {
            if (seconds < 60) return seconds.ToString("0.0") + " Sek.";
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours < 1) return ((int)ts.TotalMinutes) + " Min. " + ts.Seconds + " Sek.";
            return ((int)ts.TotalHours) + " Std. " + ts.Minutes + " Min.";
        }

        public static string SafeFileName(string s)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in s ?? "")
                sb.Append(bad.Contains(c) ? '_' : c);
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "Aufgabe" : r;
        }

        public static string Result(HistoryEntry h)
        {
            if (h.Cancelled) return "Abgebrochen";
            if (!string.IsNullOrEmpty(h.Result)) return h.Result;
            return "OK";
        }
    }
}
// Sync Pilot – Analyse und Ausführung des Abgleichs
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SyncPilot
{
    public class SyncException : Exception
    {
        public SyncException(string msg) : base(msg) { }
    }

    public class FileEntry
    {
        public string Rel;
        public bool IsDir;
        public long Size;
        public DateTime Mtime;
        /// <summary>Genauigkeit der Zeit in Sekunden (0 = genau; FTP-Server ohne MLSD liefern nur Minuten bzw. Tage).</summary>
        public int Precision;
        /// <summary>Gespeicherter Stand im alten Format: Schreibweise auf dieser Seite nicht bekannt.</summary>
        public bool CaseUnknown;
    }

    /// <summary>UmbenennenA/B: Datei oder Ordner auf der Seite A bzw. B umbenennen/verschieben (statt löschen und neu kopieren).</summary>
    public enum ActionKind { Keine, NachB, NachA, LoeschenA, LoeschenB, Konflikt, UmbenennenA, UmbenennenB }

    public class PlanItem
    {
        public string Rel;
        public bool IsDir;
        public FileEntry A;
        public FileEntry B;
        /// <summary>Stand von A bzw. B nach dem letzten Abgleich (null = damals nicht auf beiden Seiten).</summary>
        public FileEntry LastA;
        public FileEntry LastB;
        /// <summary>Nur auf einer Seite vorhanden, gespeicherter Stand verloren: unklar, ob neu oder gelöscht.</summary>
        public bool Unknown;
        /// <summary>Neuer Stand auf der Zielseite nach dem Kopieren (für den gespeicherten Stand).</summary>
        public FileEntry ResultA;
        public FileEntry ResultB;
        public ActionKind Proposed;
        public ActionKind Chosen;
        public string Reason = "";
        /// <summary>"Niemals löschen" für Seite A bzw. B (Löschen lässt sich dann auch per Rechtsklick nicht wählen).</summary>
        public bool NoDeleteA, NoDeleteB;
        public bool Unreadable;
        public bool Done;
        public bool Failed;
        /// <summary>Fehlermeldung, falls die Aktion beim Synchronisieren fehlgeschlagen ist.</summary>
        public string Error;
        /// <summary>Umbenennen/Verschieben: bisheriger Pfad auf der Zielseite.</summary>
        public string From;
        /// <summary>Zugehöriger Eintrag am alten Pfad (für den gespeicherten Stand nach dem Umbenennen).</summary>
        public PlanItem Counterpart;
        /// <summary>Wird durch diese Umbenennung erledigt (alter Pfad bzw. Inhalt eines umbenannten Ordners).</summary>
        public PlanItem Mover;
        /// <summary>Vorschlag ohne Umbenennung – gilt, wenn statt Umbenennen kopiert wird.</summary>
        public ActionKind Before;
        /// <summary>Nur Groß-/Kleinschreibung des Namens angleichen (From = bisheriger Pfad, Rel = neue Schreibweise).</summary>
        public bool CaseOnly;

        public bool IsDelete { get { return Chosen == ActionKind.LoeschenA || Chosen == ActionKind.LoeschenB; } }
        public bool IsRename { get { return Chosen == ActionKind.UmbenennenA || Chosen == ActionKind.UmbenennenB; } }
        public bool IsCopy { get { return Chosen == ActionKind.NachA || Chosen == ActionKind.NachB; } }

        public long TransferSize
        {
            get
            {
                if (IsDir) return 0;
                if (Chosen == ActionKind.NachB && A != null) return A.Size;
                if (Chosen == ActionKind.NachA && B != null) return B.Size;
                return 0;
            }
        }

        public bool CanChoose(ActionKind k)
        {
            // Umbenennung: nur annehmen oder überspringen (sonst liefen verdeckte Löschungen am alten Pfad mit)
            if (From != null) return k == Proposed || k == ActionKind.Keine;
            switch (k)
            {
                case ActionKind.NachB: return A != null;
                case ActionKind.NachA: return B != null;
                case ActionKind.LoeschenA: return A != null && !NoDeleteA;
                case ActionKind.LoeschenB: return B != null && !NoDeleteB;
                case ActionKind.UmbenennenA:
                case ActionKind.UmbenennenB: return From != null && Proposed == k;
                default: return true;
            }
        }
    }

    public class Analysis
    {
        public SyncJob Job;
        /// <summary>Gespeicherter Stand verloren: nur einseitig vorhandene Einträge werden gefragt statt kopiert.</summary>
        public bool StateLost;
        /// <summary>Hinweis zum gespeicherten Stand (beschädigt, fehlt, Sicherung verwendet) oder null.</summary>
        public string StateNote;
        public int CountUnknown { get { return All.Count(x => x.Unknown && x.Chosen == ActionKind.Konflikt); } }
        public List<PlanItem> All = new List<PlanItem>();
        public List<PlanItem> Actions = new List<PlanItem>();
        public List<string> ScanErrors = new List<string>();
        public int FilesA, FilesB;
        public long BytesA, BytesB;
        public double Seconds;

        public int CountCopies { get { return Actions.Count(x => !x.IsDir && x.IsCopy); } }
        public int CountRenames { get { return Actions.Count(x => x.IsRename); } }
        public int CountDeletes { get { return Actions.Count(x => !x.IsDir && x.IsDelete); } }
        public int CountConflicts { get { return Actions.Count(x => x.Chosen == ActionKind.Konflikt); } }
        public int CountSkipped { get { return Actions.Count(x => x.Chosen == ActionKind.Keine); } }
        public int CountActive { get { return Actions.Count(x => x.Chosen != ActionKind.Keine && x.Chosen != ActionKind.Konflikt); } }
        public long TransferBytes { get { return Actions.Sum(x => x.TransferSize); } }
    }

    /// <summary>
    /// Filter einer Aufgabe (wie bei Easy2Sync): Negativliste und Positivliste, je für Dateien und für Ordner.
    /// Muster: Name (Thumbs.db, *.tmp, .well-known) oder fester Pfad ab dem Startordner (\writable\cache);
    /// * und ? sind Platzhalter, Groß-/Kleinschreibung egal.
    /// </summary>
    public class Matcher
    {
        class Rule
        {
            public Regex Rx;
            public bool FullPath;
        }

        readonly List<Rule> exFiles, exDirs, inFiles, inDirs;

        public static readonly string[] AlwaysDirs = { "$RECYCLE.BIN", "System Volume Information" };

        public Matcher(SyncJob job)
        {
            job.Upgrade();
            exFiles = Rules(job.ExcludeFiles);
            exDirs = Rules(job.ExcludeDirs.Concat(AlwaysDirs).Concat(job.IncludeSubdirs ? new string[0] : new[] { "*" }));
            inFiles = Rules(job.IncludeFiles);
            inDirs = Rules(job.IncludeDirs);
        }

        static List<Rule> Rules(IEnumerable<string> patterns)
        {
            List<Rule> list = new List<Rule>();
            foreach (string raw in patterns)
            {
                string p = (raw ?? "").Trim().Replace('/', '\\');
                if (p.Length == 0 || p.StartsWith("#")) continue;
                Rule r = new Rule();
                p = p.TrimEnd('\\');
                if (p.StartsWith("\\")) { r.FullPath = true; p = p.TrimStart('\\'); }
                if (p.Contains("\\")) r.FullPath = true;
                if (p.Length == 0) continue;
                string rx = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                r.Rx = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                list.Add(r);
            }
            return list;
        }

        static bool Match(List<Rule> rules, string rel, string name)
        {
            foreach (Rule r in rules)
                if (r.Rx.IsMatch(r.FullPath ? rel : name)) return true;
            return false;
        }

        static string NameOf(string rel)
        {
            int i = rel.LastIndexOf('\\');
            return i < 0 ? rel : rel.Substring(i + 1);
        }

        /// <summary>Negativliste: beim Einlesen überspringen (bei Ordnern samt Inhalt).</summary>
        public bool Excluded(string rel, string name, bool isDir)
        {
            // Ordner mit älteren Versionen gehört nicht zum Abgleich (auch wenn die Option ausgeschaltet wurde)
            if (isDir && string.Equals(rel, Engine.VersionsDir, StringComparison.OrdinalIgnoreCase)) return true;
            return Match(isDir ? exDirs : exFiles, rel, name);
        }

        public bool HasIncludes { get { return inFiles.Count > 0 || inDirs.Count > 0; } }

        /// <summary>Liegt der Pfad in einem Ordner der Positivliste (oder ist er selbst einer)?</summary>
        bool InIncludedDir(string dirRel)
        {
            if (dirRel.Length == 0) return false;
            string[] parts = dirRel.Split('\\');
            string cur = "";
            foreach (string part in parts)
            {
                cur = cur.Length == 0 ? part : cur + "\\" + part;
                if (Match(inDirs, cur, part)) return true;
            }
            return false;
        }

        /// <summary>Positivliste anwenden: nur passende Dateien/Ordner behalten, dazu die Ordner, in denen sie liegen.</summary>
        public Dictionary<string, FileEntry> ApplyIncludes(Dictionary<string, FileEntry> all)
        {
            if (!HasIncludes) return all;
            Dictionary<string, FileEntry> keep = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (FileEntry e in all.Values)
            {
                bool ok;
                if (e.IsDir) ok = inDirs.Count > 0 && InIncludedDir(e.Rel) && inFiles.Count == 0;
                else
                {
                    int i = e.Rel.LastIndexOf('\\');
                    string dir = i < 0 ? "" : e.Rel.Substring(0, i);
                    ok = (inDirs.Count == 0 || InIncludedDir(dir)) && (inFiles.Count == 0 || Match(inFiles, e.Rel, NameOf(e.Rel)));
                }
                if (!ok) continue;
                keep[e.Rel] = e;
                // übergeordnete Ordner mitnehmen, damit sie angelegt werden
                string p = e.Rel;
                for (int k = p.LastIndexOf('\\'); k > 0; k = p.LastIndexOf('\\'))
                {
                    p = p.Substring(0, k);
                    FileEntry d;
                    if (keep.ContainsKey(p)) break;
                    if (all.TryGetValue(p, out d)) keep[p] = d;
                }
            }
            return keep;
        }
    }

    /// <summary>
    /// Stand des letzten Abgleichs je Aufgabe: für jeden Pfad Größe und Zeit auf Seite A und auf Seite B.
    /// Zeilen: "D\tpfad\tpfadB", "F2\tpfad\tgrößeA\tzeitA\tgrößeB\tzeitB\tpfadB" (pfadB = Schreibweise in B, leer = wie in A;
    /// fehlt bei älteren Ständen). Ältere Form "F\tpfad\tgröße\tzeit" gilt für beide Seiten.
    /// </summary>
    /// <summary>Wie der gespeicherte Stand einer Aufgabe gelesen werden konnte.</summary>
    public enum StateLoad { Ok, Missing, Backup, Lost }

    /// <summary>
    /// Stand der letzten Synchronisation je Aufgabe (Status\&lt;ID&gt;.state). Daran erkennt der Abgleich Löschungen,
    /// Änderungen und Umbenennungen. Kopf- und Schlusszeile mit Anzahl erkennen eine beschädigte Datei; die vorige
    /// Fassung bleibt als .state.bak erhalten.
    /// </summary>
    public static class StateStore
    {
        const string Header = "SYNCPILOT-STATUS\t2";

        static string FileFor(string id) { return Path.Combine(Store.StateDir, id + ".state"); }
        static string BackupFor(string id) { return FileFor(id) + ".bak"; }

        static long L(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        static FileEntry F(string rel, long size, long ticks)
        {
            return new FileEntry { Rel = rel, Size = size, Mtime = new DateTime(ticks, DateTimeKind.Utc) };
        }

        public static Dictionary<string, FileEntry[]> Load(string id)
        {
            StateLoad st;
            HashSet<string> ask;
            return Load(id, out st, out ask);
        }

        /// <summary>
        /// Liest den Stand. Beschädigt oder fehlend: die vorige Fassung (.bak). ask = Einträge, über die nach einem
        /// verlorenen Stand noch nicht entschieden wurde (werden weiter als Frage gezeigt statt kopiert).
        /// </summary>
        public static Dictionary<string, FileEntry[]> Load(string id, out StateLoad status, out HashSet<string> ask)
        {
            Dictionary<string, FileEntry[]> d;
            if (TryRead(FileFor(id), out d, out ask)) { status = d == null ? StateLoad.Missing : StateLoad.Ok; if (d != null) return d; }
            else status = StateLoad.Lost;
            // Hauptdatei fehlt oder ist beschädigt: vorige Fassung versuchen
            Dictionary<string, FileEntry[]> bak;
            HashSet<string> bakAsk;
            if (TryRead(BackupFor(id), out bak, out bakAsk) && bak != null)
            {
                status = StateLoad.Backup;
                ask = bakAsk;
                return bak;
            }
            if (File.Exists(BackupFor(id))) status = StateLoad.Lost;
            ask = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, FileEntry[]>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>false = beschädigt; true mit d == null = nicht vorhanden.</summary>
        static bool TryRead(string f, out Dictionary<string, FileEntry[]> d, out HashSet<string> ask)
        {
            d = null;
            ask = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(f)) return true;
            Dictionary<string, FileEntry[]> r = new Dictionary<string, FileEntry[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                bool first = true, v2 = false, ended = false;
                int count = 0;
                foreach (string line in File.ReadLines(f, Encoding.UTF8))
                {
                    if (first)
                    {
                        first = false;
                        if (line == Header) { v2 = true; continue; }
                    }
                    if (ended) return false; // nach der Schlusszeile darf nichts mehr kommen
                    string[] p = line.Split('\t');
                    if (v2 && p.Length == 2 && p[0] == "ENDE")
                    {
                        if (L(p[1]) != count) return false;
                        ended = true;
                        continue;
                    }
                    count++;
                    if (p.Length >= 2 && p[0] == "D")
                    {
                        // ab 1.31: dritte Spalte = Schreibweise in B, falls anders als in A
                        FileEntry e = new FileEntry { Rel = p[1], IsDir = true };
                        FileEntry eb = p.Length >= 3 && p[2].Length > 0 ? new FileEntry { Rel = p[2], IsDir = true }
                                                                        : new FileEntry { Rel = p[1], IsDir = true, CaseUnknown = p.Length < 3 };
                        r[p[1]] = new FileEntry[] { e, eb };
                    }
                    else if (p.Length >= 2 && p[0] == "?") ask.Add(p[1]);
                    else if (p.Length >= 6 && p[0] == "F2")
                    {
                        long sa = L(p[2]), ta = L(p[3]), sb = L(p[4]), tb = L(p[5]);
                        if (sa < 0 || ta < 0 || sb < 0 || tb < 0) { if (v2) return false; continue; }
                        FileEntry fb = F(p.Length >= 7 && p[6].Length > 0 ? p[6] : p[1], sb, tb);
                        if (p.Length < 7) fb.CaseUnknown = true; // altes Format: Schreibweise in B nicht gespeichert
                        r[p[1]] = new FileEntry[] { F(p[1], sa, ta), fb };
                    }
                    else if (p.Length >= 4 && p[0] == "F")
                    {
                        long size = L(p[2]), ticks = L(p[3]);
                        if (size < 0 || ticks < 0) { if (v2) return false; continue; }
                        FileEntry e = F(p[1], size, ticks);
                        r[p[1]] = new FileEntry[] { e, e };
                    }
                    else if (v2 || line.Length > 0) return false; // unbekannte Zeile: beschädigt
                }
                if (first) return false;           // leere Datei
                if (v2 && !ended) return false;    // abgeschnitten
            }
            catch { return false; }
            d = r;
            return true;
        }

        /// <summary>Speichert Paare [Stand A, Stand B] und offene Fragen; die bisherige Fassung bleibt als .bak.</summary>
        public static void Save(string id, IEnumerable<FileEntry[]> entries, IEnumerable<string> ask)
        {
            Directory.CreateDirectory(Store.StateDir);
            string f = FileFor(id);
            string tmp = f + ".tmp";
            CultureInfo ci = CultureInfo.InvariantCulture;
            int count = 0;
            using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.WriteLine(Header);
                foreach (FileEntry[] e in entries)
                {
                    // Schreibweise in B nur, wenn sie von A abweicht (ältere Versionen lesen die Zeile weiterhin)
                    string relB = e[1].Rel != null && !string.Equals(e[1].Rel, e[0].Rel, StringComparison.Ordinal) ? e[1].Rel : "";
                    if (e[0].IsDir) w.WriteLine("D\t" + e[0].Rel + "\t" + relB);
                    else w.WriteLine("F2\t" + e[0].Rel + "\t" + e[0].Size.ToString(ci) + "\t" + e[0].Mtime.Ticks.ToString(ci) +
                                     "\t" + e[1].Size.ToString(ci) + "\t" + e[1].Mtime.Ticks.ToString(ci) + "\t" + relB);
                    count++;
                }
                if (ask != null)
                    foreach (string rel in ask) { w.WriteLine("?\t" + rel); count++; }
                w.WriteLine("ENDE\t" + count.ToString(ci));
                w.Flush();
                w.BaseStream.Flush();
            }
            // nur eine heile Fassung als Sicherung behalten
            Dictionary<string, FileEntry[]> cur;
            HashSet<string> curAsk;
            bool curOk = TryRead(f, out cur, out curAsk) && cur != null;
            if (curOk) File.Replace(tmp, f, BackupFor(id));
            else
            {
                if (File.Exists(f)) File.Delete(f);
                File.Move(tmp, f);
            }
        }

        public static void Save(string id, IEnumerable<FileEntry[]> entries) { Save(id, entries, null); }

        /// <summary>Aufgabe gelöscht: Stand samt Sicherung entfernen.</summary>
        public static void Delete(string id)
        {
            foreach (string f in new[] { FileFor(id), BackupFor(id) })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        /// <summary>Ordner geändert: neu anfangen (leerer Stand, damit nicht nach einem verlorenen Stand gefragt wird).</summary>
        /// <summary>Quelle und Ziel getauscht: Stand je Datei mit vertauschten Seiten weiterführen (nichts gilt als neu).</summary>
        public static void Swap(string id)
        {
            StateLoad st;
            HashSet<string> ask;
            Dictionary<string, FileEntry[]> cur = Load(id, out st, out ask);
            List<FileEntry[]> swapped = new List<FileEntry[]>();
            foreach (FileEntry[] e in cur.Values) swapped.Add(new FileEntry[] { e[1], e[0] });
            Save(id, swapped, ask);
        }

        public static void Reset(string id)
        {
            Delete(id);
            try { Save(id, new FileEntry[0][]); } catch { }
        }

        /// <summary>Wurde die Aufgabe schon einmal synchronisiert (laut Verlauf)?</summary>
        public static bool SyncedBefore(string id)
        {
            try
            {
                foreach (HistoryEntry h in Store.LoadHistory())
                    if (h.JobId == id && !h.Cancelled && (h.Errors == 0 || h.Copied + h.Moved + h.Deleted > 0)) return true;
            }
            catch { }
            return false;
        }
    }

    public static class Recycle
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        public static void Send(string path)
        {
            SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
            op.wFunc = FO_DELETE;
            op.pFrom = path + "\0\0";
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
            int rc = SHFileOperation(ref op);
            if (rc != 0) throw new IOException("Papierkorb-Fehler 0x" + rc.ToString("X"));
            if (op.fAnyOperationsAborted) throw new IOException("Löschen wurde abgebrochen");
        }
    }

    public class Logger : IDisposable
    {
        readonly StreamWriter w;
        public readonly string FilePath;

        public Logger(string jobName)
        {
            Directory.CreateDirectory(Store.LogDir);
            Store.CleanupLogs(false); // alte Protokolle aufräumen (höchstens alle 12 Stunden)
            string baseName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Fmt.SafeFileName(jobName);
            FilePath = Path.Combine(Store.LogDir, baseName + ".log");
            int n = 2;
            while (File.Exists(FilePath)) FilePath = Path.Combine(Store.LogDir, baseName + "_" + (n++) + ".log");
            w = new StreamWriter(FilePath, false, new UTF8Encoding(true));
            w.AutoFlush = true;
        }

        public void Line(string s)
        {
            lock (w) w.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
        }

        public void Dispose()
        {
            lock (w) w.Dispose();
        }
    }

    public class ExecResult
    {
        public int Copied, Deleted, Errors, Skipped, Conflicts, Moved;
        public long Bytes;
        public bool Cancelled;
    }

    public static class Engine
    {
        static bool IsUnder(string rel, HashSet<string> dirs)
        {
            if (dirs.Count == 0) return false;
            if (dirs.Contains(rel) || dirs.Contains("")) return true;
            int i = rel.LastIndexOf('\\');
            while (i > 0)
            {
                rel = rel.Substring(0, i);
                if (dirs.Contains(rel)) return true;
                i = rel.LastIndexOf('\\');
            }
            return false;
        }

        static void AddAncestors(HashSet<string> set, string rel)
        {
            int i = rel.LastIndexOf('\\');
            while (i > 0)
            {
                rel = rel.Substring(0, i);
                if (!set.Add(rel)) return;
                i = rel.LastIndexOf('\\');
            }
        }

        // ---------- Analyse ----------

        /// <summary>
        /// Kopie der Aufgabe mit aufgelösten Pfaden (%VARIABLE%, %DRIVELETTER:Name%) und Anmeldung an
        /// Netzwerkfreigaben mit hinterlegtem Benutzer.
        /// </summary>
        public static SyncJob Prepare(SyncJob job)
        {
            SyncJob r = job.Clone();
            r.FolderA = Paths.Resolve(job.FolderA);
            r.FolderB = Paths.Resolve(job.FolderB);
            NetShare.Connect(r.FolderA, job.UserA, job.PassA);
            NetShare.Connect(r.FolderB, job.UserB, job.PassB);
            return r;
        }

        public static Analysis Analyze(SyncJob job, Func<bool> cancel, Action<string> status)
        {
            Stopwatch sw = Stopwatch.StartNew();
            status("Verbinde \u2026");
            LockFiles.CheckFree(job);
            ignoreDst = job.IgnoreDstOffset;
            job = Prepare(job);
            using (IFileSide sa = Sides.Open(job, true))
            using (IFileSide sb = Sides.Open(job, false))
            {
                status("Verbinde mit " + job.SideTitle(true) + " \u2026");
                sa.CheckRoot();
                status("Verbinde mit " + job.SideTitle(false) + " \u2026");
                sb.CheckRoot();
                Matcher m = new Matcher(job);
                Analysis an = new Analysis();
                an.Job = job;
                HashSet<string> ua = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> ub = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                status("Lese Ordner A \u2026");
                Dictionary<string, FileEntry> a = sa.Scan(job, m, cancel,
                    delegate (int n) { status("Lese Ordner A \u2026 " + n.ToString("N0") + " Eintr\u00e4ge"); }, ua, an.ScanErrors);
                status("Lese Ordner B \u2026");
                Dictionary<string, FileEntry> b = sb.Scan(job, m, cancel,
                    delegate (int n) { status("Lese Ordner B \u2026 " + n.ToString("N0") + " Eintr\u00e4ge"); }, ub, an.ScanErrors);
                // Positivliste: nur passende Dateien und Ordner abgleichen (auf beiden Seiten gleich angewendet)
                a = m.ApplyIncludes(a);
                b = m.ApplyIncludes(b);
                SizeFilter(job, a, b);
                StateLoad stLoad;
                HashSet<string> askSet;
                Dictionary<string, FileEntry[]> last = StateStore.Load(job.Id, out stLoad, out askSet);
                // Stand verloren (beschädigt oder fehlt, obwohl schon synchronisiert): nicht einfach kopieren, sondern fragen –
                // sonst kämen auf einer Seite gelöschte Dateien zurück
                bool twoWay = job.Mode == SyncMode.Abgleichen && job.PropagateDeletes;
                if (twoWay && (stLoad == StateLoad.Lost || stLoad == StateLoad.Missing) && StateStore.SyncedBefore(job.Id))
                {
                    an.StateLost = true;
                    an.StateNote = stLoad == StateLoad.Lost ? "Der gespeicherte Stand der letzten Synchronisation ist beschädigt."
                                                            : "Der gespeicherte Stand der letzten Synchronisation fehlt.";
                }
                else if (stLoad == StateLoad.Backup)
                    an.StateNote = "Der gespeicherte Stand war beschädigt – der Stand der vorletzten Synchronisation wurde verwendet.";
                if (!twoWay) askSet.Clear();

                foreach (FileEntry e in a.Values) if (!e.IsDir) { an.FilesA++; an.BytesA += e.Size; }
                foreach (FileEntry e in b.Values) if (!e.IsDir) { an.FilesB++; an.BytesB += e.Size; }

                HashSet<string> keySet = new HashSet<string>(a.Keys, StringComparer.OrdinalIgnoreCase);
                keySet.UnionWith(b.Keys);
                List<string> keys = keySet.ToList();
                keys.Sort(StringComparer.OrdinalIgnoreCase);

                int i = 0;
                foreach (string k in keys)
                {
                    if ((++i & 255) == 0)
                    {
                        if (cancel()) throw new OperationCanceledException();
                        status("Vergleiche \u2026 " + i.ToString("N0") + " / " + keys.Count.ToString("N0"));
                    }
                    FileEntry ea, eb;
                    FileEntry[] el;
                    a.TryGetValue(k, out ea);
                    b.TryGetValue(k, out eb);
                    last.TryGetValue(k, out el);
                    PlanItem it = new PlanItem();
                    it.Rel = k;
                    it.A = ea;
                    it.B = eb;
                    if (el != null) { it.LastA = el[0]; it.LastB = el[1]; }
                    it.IsDir = (ea ?? eb).IsDir;

                    if (ea != null && eb != null && ea.IsDir != eb.IsDir)
                        Set(it, ActionKind.Konflikt, "Datei und Ordner mit gleichem Namen");
                    else if (IsUnder(k, ua) || IsUnder(k, ub) || DotHidden(k, ea, eb, sa, sb))
                    {
                        it.Unreadable = true;
                        Set(it, ActionKind.Keine, "Auf einer Seite nicht lesbar oder versteckt");
                    }
                    else
                    {
                        bool ask = twoWay && el == null && (an.StateLost || askSet.Contains(k));
                        if (it.IsDir) DecideDir(job, it, ask);
                        else DecideFile(job, it, sa, sb, ask);
                    }

                    an.All.Add(it);
                }

                ApplyConflictRule(job, an.All);
                ApplyNeverDelete(job, an.All);
                FixDirDeletes(an.All);
                DetectRenames(job, an.All, sa, sb);
                DetectCaseRenames(job, an.All);
                foreach (PlanItem p in an.All) p.Chosen = p.Proposed;
                an.Actions = an.All.Where(x => x.Proposed != ActionKind.Keine).ToList();
                an.Seconds = sw.Elapsed.TotalSeconds;
                return an;
            }
        }

        /// <summary>
        /// Dateigröße (wie bei Easy2Sync): zu kleine bzw. zu große Dateien nicht abgleichen. Passt eine Datei auf einer Seite
        /// nicht, fällt sie auf beiden Seiten weg – sonst sähe es so aus, als wäre sie auf einer Seite gelöscht worden.
        /// </summary>
        static void SizeFilter(SyncJob job, Dictionary<string, FileEntry> a, Dictionary<string, FileEntry> b)
        {
            if (job.MinSize <= 0 && job.MaxSize <= 0) return;
            List<string> drop = new List<string>();
            foreach (Dictionary<string, FileEntry> d in new Dictionary<string, FileEntry>[] { a, b })
                foreach (FileEntry e in d.Values)
                    if (!e.IsDir && ((job.MinSize > 0 && e.Size < job.MinSize) || (job.MaxSize > 0 && e.Size > job.MaxSize)))
                        drop.Add(e.Rel);
            foreach (string k in drop)
            {
                a.Remove(k);
                b.Remove(k);
            }
        }

        // ---------- Nur Groß-/Kleinschreibung geändert ----------

        /// <summary>
        /// Windows (und damit der Abgleich) unterscheidet Groß-/Kleinschreibung nicht: "fotos" und "Fotos" sind derselbe Eintrag.
        /// Unterscheidet sich nur die Schreibweise des Namens, wird sie auf der anderen Seite angeglichen. Welche Seite geändert
        /// wurde, zeigt der gespeicherte Stand; sonst (bzw. bei Sichern/Spiegeln immer) gilt die Schreibweise von A.
        /// </summary>
        static void DetectCaseRenames(SyncJob job, List<PlanItem> all)
        {
            foreach (PlanItem it in all)
            {
                if (it.A == null || it.B == null || it.Proposed != ActionKind.Keine || it.Unreadable || it.A.IsDir != it.B.IsDir) continue;
                string na = NameOf(it.A.Rel), nb = NameOf(it.B.Rel);
                if (string.Equals(na, nb, StringComparison.Ordinal) || !string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) continue;
                bool changedA = it.LastA != null && !it.LastA.CaseUnknown && !string.Equals(NameOf(it.LastA.Rel), na, StringComparison.Ordinal);
                bool changedB = it.LastB != null && !it.LastB.CaseUnknown && !string.Equals(NameOf(it.LastB.Rel), nb, StringComparison.Ordinal);
                bool toB;
                string reason;
                if (job.Mode != SyncMode.Abgleichen) { toB = true; reason = "Schreibweise wie in A"; }
                else if (changedA && !changedB) { toB = true; reason = "Schreibweise in A ge\u00e4ndert"; }
                else if (changedB && !changedA) { toB = false; reason = "Schreibweise in B ge\u00e4ndert"; }
                else { toB = true; reason = "Schreibweise unterschiedlich \u2013 wie in A"; }
                it.CaseOnly = true;
                it.From = toB ? it.B.Rel : it.A.Rel;
                it.Rel = ParentOf(it.From).Length > 0 ? ParentOf(it.From) + "\\" + (toB ? na : nb) : (toB ? na : nb);
                Set(it, toB ? ActionKind.UmbenennenB : ActionKind.UmbenennenA,
                    reason + " (" + (toB ? nb : na) + " \u2192 " + (toB ? na : nb) + ")");
            }
        }

        // ---------- Umbenennen und Verschieben erkennen ----------

        /// <summary>
        /// Wurde auf einer Seite ein Ordner bzw. eine Datei umbenannt oder verschoben, sieht der Abgleich zunächst
        /// "alter Pfad gelöscht" und "neuer Pfad neu". Passt der Inhalt zusammen, wird daraus ein Umbenennen auf der
        /// anderen Seite – ohne Dateien neu zu übertragen. Nur eindeutige Fälle; sonst bleibt es bei Löschen und Kopieren.
        /// Mit "Inhalt vergleichen" wird jede Zuordnung zusätzlich am Inhalt geprüft.
        /// </summary>
        static void DetectRenames(SyncJob job, List<PlanItem> all, IFileSide sa, IFileSide sb)
        {
            // auf A umbenannt -> in B nachziehen; auf B umbenannt -> in A nachziehen
            Detect(job, all, ActionKind.LoeschenB, ActionKind.NachB, ActionKind.UmbenennenB, false, sa, sb);
            Detect(job, all, ActionKind.LoeschenA, ActionKind.NachA, ActionKind.UmbenennenA, true, sa, sb);
        }

        static FileEntry Side(PlanItem x, bool a) { return a ? x.A : x.B; }

        static string NameOf(string rel)
        {
            int i = rel.LastIndexOf('\\');
            return i < 0 ? rel : rel.Substring(i + 1);
        }

        static string ParentOf(string rel)
        {
            int i = rel.LastIndexOf('\\');
            return i < 0 ? "" : rel.Substring(0, i);
        }

        static bool SameFile(FileEntry t, FileEntry s)
        {
            if (t == null || s == null || t.Size != s.Size) return false;
            int tol = Math.Max(2, Math.Max(t.Precision, s.Precision));
            return Math.Abs((t.Mtime - s.Mtime).TotalSeconds) <= tol;
        }

        static bool Imprecise(FileEntry t, FileEntry s) { return Math.Max(t.Precision, s.Precision) > 60; }

        /// <summary>Inhalt eines Ordners für die Zuordnung: Einträge nach Unterpfad, gültig nur ohne fremde Einträge darin.</summary>
        class Tree
        {
            public PlanItem Dir;
            public readonly Dictionary<string, PlanItem> Items = new Dictionary<string, PlanItem>(StringComparer.OrdinalIgnoreCase);
            public bool Invalid;
            public string Key;
        }

        /// <summary>
        /// Für alle Kandidaten-Ordner in einem Durchgang: alle Einträge darunter, die auf der Seite vorhanden sind.
        /// Ungültig, wenn darunter etwas liegt, das nicht zur Umbenennung passt.
        /// </summary>
        static Dictionary<string, Tree> Trees(List<PlanItem> all, IEnumerable<PlanItem> dirs, bool side, HashSet<PlanItem> allowed)
        {
            Dictionary<string, Tree> trees = new Dictionary<string, Tree>(StringComparer.OrdinalIgnoreCase);
            foreach (PlanItem d in dirs) trees[d.Rel] = new Tree { Dir = d };
            if (trees.Count == 0) return trees;
            foreach (PlanItem x in all)
            {
                if (Side(x, side) == null) continue;
                bool ok = allowed.Contains(x) && !x.Unreadable;
                for (string p = ParentOf(x.Rel); p.Length > 0; p = ParentOf(p))
                {
                    Tree tr;
                    if (!trees.TryGetValue(p, out tr)) continue;
                    if (!ok) tr.Invalid = true;
                    else tr.Items[x.Rel.Substring(p.Length + 1)] = x;
                }
            }
            foreach (Tree tr in trees.Values)
            {
                if (tr.Invalid) continue;
                // Schlüssel aus Unterpfaden und Größen: nur Ordner mit gleichem Schlüssel kommen als Paar in Frage
                List<string> parts = tr.Items.Select(kv => kv.Key.ToLowerInvariant() + "|" + (kv.Value.IsDir ? "D" : Side(kv.Value, side).Size.ToString())).ToList();
                parts.Sort(StringComparer.Ordinal);
                tr.Key = string.Join("\n", parts);
            }
            return trees;
        }

        static bool SameTree(Tree g, Tree n, bool tSide, Func<PlanItem, PlanItem, bool> sameContent)
        {
            if (g.Items.Count != n.Items.Count) return false;
            foreach (KeyValuePair<string, PlanItem> kv in g.Items)
            {
                PlanItem o;
                if (!n.Items.TryGetValue(kv.Key, out o) || o.IsDir != kv.Value.IsDir) return false;
                if (kv.Value.Mover != null || o.Mover != null) return false;
                if (!o.IsDir && !SameFile(Side(kv.Value, tSide), Side(o, !tSide))) return false;
            }
            if (sameContent != null)
                foreach (KeyValuePair<string, PlanItem> kv in g.Items)
                    if (!kv.Value.IsDir && !sameContent(kv.Value, n.Items[kv.Key])) return false;
            return true;
        }

        static string RenameReason(string from, string to, bool isDir, bool changedOnB)
        {
            string side = changedOnB ? "In B" : "In A";
            if (ParentOf(from).Equals(ParentOf(to), StringComparison.OrdinalIgnoreCase))
                return side + " umbenannt, vorher „" + NameOf(from) + "“";
            return side + " verschoben, vorher " + from + (isDir ? "\\" : "");
        }

        static void Absorb(PlanItem x, PlanItem mover, string reason)
        {
            x.Mover = mover;
            x.Before = x.Proposed;
            Set(x, ActionKind.Keine, reason);
        }

        /// <param name="del">Vorschlag am alten Pfad (Löschen auf der Zielseite)</param>
        /// <param name="add">Vorschlag am neuen Pfad (Kopieren auf die Zielseite)</param>
        /// <param name="targetA">Zielseite ist A (dort wird umbenannt)</param>
        static void Detect(SyncJob job, List<PlanItem> all, ActionKind del, ActionKind add, ActionKind ren, bool targetA,
            IFileSide sa, IFileSide sb)
        {
            bool t = targetA, s = !targetA;
            HashSet<PlanItem> gone = new HashSet<PlanItem>(all.Where(x => x.Proposed == del && !x.Unreadable && x.Mover == null &&
                Side(x, t) != null && Side(x, s) == null));
            HashSet<PlanItem> added = new HashSet<PlanItem>(all.Where(x => x.Proposed == add && !x.Unreadable && x.Mover == null &&
                Side(x, s) != null && Side(x, t) == null));
            if (gone.Count == 0 || added.Count == 0) return;

            // "Inhalt vergleichen": Zuordnung zusätzlich am Inhalt bestätigen (alter Pfad auf der Zielseite, neuer auf der Quellseite)
            Func<PlanItem, PlanItem, bool> sameContent = null;
            if (job.CompareContent)
            {
                IFileSide target = targetA ? sa : sb, source = targetA ? sb : sa;
                sameContent = delegate (PlanItem oldItem, PlanItem newItem)
                {
                    try { return ContentEqual(target, oldItem.Rel, source, newItem.Rel); }
                    catch { return false; }
                };
            }

            // 1. Ganze Ordner: gleicher Inhalt (Unterpfade, Größen, Zeiten), eindeutig in beide Richtungen
            List<PlanItem> goneDirs = gone.Where(x => x.IsDir).OrderBy(x => x.Rel.Length).ToList();
            List<PlanItem> addedDirs = added.Where(x => x.IsDir).ToList();
            if (goneDirs.Count > 0 && addedDirs.Count > 0)
            {
                Dictionary<string, Tree> gTrees = Trees(all, goneDirs, t, gone);
                Dictionary<string, Tree> nTrees = Trees(all, addedDirs, s, added);
                Dictionary<string, List<Tree>> gByKey = gTrees.Values.Where(x => !x.Invalid).GroupBy(x => x.Key)
                    .ToDictionary(x => x.Key, x => x.ToList());
                Dictionary<string, List<Tree>> nByKey = nTrees.Values.Where(x => !x.Invalid).GroupBy(x => x.Key)
                    .ToDictionary(x => x.Key, x => x.ToList());
                foreach (PlanItem gd in goneDirs)
                {
                    Tree g = gTrees[gd.Rel];
                    if (gd.Mover != null || g.Invalid || !g.Items.Values.Any(x => !x.IsDir)) continue;
                    List<Tree> ns, gs;
                    if (!nByKey.TryGetValue(g.Key, out ns) || !gByKey.TryGetValue(g.Key, out gs)) continue;
                    // eindeutig: genau ein passender neuer Ordner, und kein anderer verschwundener Ordner passt zu ihm
                    List<Tree> hits = ns.Where(n => n.Dir.Mover == null && n.Dir.Proposed == add &&
                        !n.Dir.Rel.StartsWith(gd.Rel + "\\", StringComparison.OrdinalIgnoreCase) &&
                        !gd.Rel.StartsWith(n.Dir.Rel + "\\", StringComparison.OrdinalIgnoreCase) &&
                        SameTree(g, n, t, null)).ToList();
                    if (hits.Count != 1) continue;
                    Tree match = hits[0];
                    if (gs.Any(g2 => g2 != g && g2.Dir.Mover == null && SameTree(g2, match, t, null))) continue;
                    if (sameContent != null && !SameTree(g, match, t, sameContent)) continue;

                    PlanItem md = match.Dir;
                    md.Before = md.Proposed;
                    md.From = gd.Rel;
                    md.Counterpart = gd;
                    Set(md, ren, RenameReason(gd.Rel, md.Rel, true, targetA));
                    Absorb(gd, md, "Wird umbenannt nach " + md.Rel + "\\");
                    foreach (KeyValuePair<string, PlanItem> kv in g.Items) Absorb(kv.Value, md, "Im umbenannten Ordner");
                    foreach (KeyValuePair<string, PlanItem> kv in match.Items)
                    {
                        Absorb(kv.Value, md, "Im umbenannten Ordner");
                        kv.Value.Counterpart = g.Items[kv.Key];
                    }
                }
            }

            // 2. Einzelne Dateien: gleiche Größe und Zeit, eindeutig (bei mehreren Treffern entscheidet der gleiche Name)
            List<PlanItem> gf = gone.Where(x => !x.IsDir && x.Mover == null && Side(x, t).Size > 0).ToList();
            List<PlanItem> af = added.Where(x => !x.IsDir && x.Mover == null && x.Proposed == add && Side(x, s).Size > 0).ToList();
            if (gf.Count == 0 || af.Count == 0) return;
            Dictionary<long, List<PlanItem>> goneBySize = gf.GroupBy(x => Side(x, t).Size).ToDictionary(x => x.Key, x => x.ToList());
            Dictionary<long, List<PlanItem>> addBySize = af.GroupBy(x => Side(x, s).Size).ToDictionary(x => x.Key, x => x.ToList());
            foreach (PlanItem n in af)
            {
                List<PlanItem> cands;
                if (!goneBySize.TryGetValue(Side(n, s).Size, out cands)) continue;
                PlanItem pick = Unique(cands.Where(o => o.Mover == null && SameFile(Side(o, t), Side(n, s)) &&
                    (!Imprecise(Side(o, t), Side(n, s)) || NameOf(o.Rel).Equals(NameOf(n.Rel), StringComparison.OrdinalIgnoreCase))).ToList(), n.Rel);
                if (pick == null) continue;
                PlanItem back = Unique(addBySize[Side(n, s).Size].Where(o => o.Mover == null && o.Proposed == add &&
                    SameFile(Side(pick, t), Side(o, s))).ToList(), pick.Rel);
                if (back != n) continue;
                if (sameContent != null && !sameContent(pick, n)) continue;
                n.Before = n.Proposed;
                n.From = pick.Rel;
                n.Counterpart = pick;
                Set(n, ren, RenameReason(pick.Rel, n.Rel, false, targetA));
                Absorb(pick, n, "Wird verschoben nach " + n.Rel);
            }
        }

        /// <summary>Genau ein Kandidat – oder genau einer mit gleichem Dateinamen.</summary>
        static PlanItem Unique(List<PlanItem> cands, string rel)
        {
            if (cands.Count == 1) return cands[0];
            if (cands.Count == 0) return null;
            List<PlanItem> named = cands.Where(x => NameOf(x.Rel).Equals(NameOf(rel), StringComparison.OrdinalIgnoreCase)).ToList();
            return named.Count == 1 ? named[0] : null;
        }

        /// <summary>Datei mit Punkt am Anfang fehlt auf einer FTP-Seite, die solche Dateien evtl. nicht anzeigt.</summary>
        static bool DotHidden(string rel, FileEntry ea, FileEntry eb, IFileSide sa, IFileSide sb)
        {
            int i = rel.LastIndexOf('\\');
            string name = i < 0 ? rel : rel.Substring(i + 1);
            if (!name.StartsWith(".")) return false;
            return (ea == null && sa.HidesDotFiles) || (eb == null && sb.HidesDotFiles);
        }

        static void Set(PlanItem it, ActionKind k, string reason)
        {
            it.Proposed = k;
            it.Reason = reason;
        }

        /// <summary>Sommerzeit-Versatz für die Analyse in diesem Thread (siehe SyncJob.IgnoreDstOffset).</summary>
        [ThreadStatic] static bool ignoreDst;

        /// <summary>Unterschied von genau einer Stunde (± Toleranz): typisch für FAT-Laufwerke und manche NAS nach der Zeitumstellung.</summary>
        static bool DstOffset(double seconds, int tol)
        {
            return ignoreDst && Math.Abs(Math.Abs(seconds) - 3600) <= tol;
        }

        static bool Changed(FileEntry cur, FileEntry last)
        {
            if (last == null) return true;
            if (cur.Size != last.Size) return true;
            double d = (cur.Mtime - last.Mtime).TotalSeconds;
            int tol = Math.Max(2, cur.Precision);
            return Math.Abs(d) > tol && !DstOffset(d, tol);
        }

        /// <summary>Beide Seiten seit dem letzten Abgleich unverändert (dann gelten sie als gleich).</summary>
        static bool BothUnchanged(PlanItem it)
        {
            return it.LastA != null && it.LastB != null && !it.LastA.IsDir &&
                   !Changed(it.A, it.LastA) && !Changed(it.B, it.LastB);
        }

        static bool Same(SyncJob job, PlanItem it, IFileSide sa, IFileSide sb)
        {
            FileEntry a = it.A, b = it.B;
            if (a.Size != b.Size) return false;
            if (BothUnchanged(it)) return true;
            int tol = Math.Max(2, Math.Max(a.Precision, b.Precision));
            double d = (a.Mtime - b.Mtime).TotalSeconds;
            if (Math.Abs(d) <= tol || DstOffset(d, tol)) return true;
            if (job.CompareContent)
            {
                try { return ContentEqual(sa, sb, it.Rel); }
                catch { return false; }
            }
            // Gespeicherter Stand zeigt eine Änderung auf genau einer Seite: nicht gleich
            if (it.LastA != null && it.LastB != null && Changed(a, it.LastA) != Changed(b, it.LastB)) return false;
            // FTP-Server ohne genaue Zeitangabe (kein MLSD): gleiche Größe gilt als gleich
            return a.Precision > 0 || b.Precision > 0;
        }

        static bool ContentEqual(IFileSide sa, IFileSide sb, string rel)
        {
            return ContentEqual(sa, rel, sb, rel);
        }

        static bool ContentEqual(IFileSide s1, string rel1, IFileSide s2, string rel2)
        {
            const int Buf = 1 << 20;
            using (Stream f1 = s1.OpenRead(rel1))
            using (Stream f2 = s2.OpenRead(rel2))
            {
                byte[] b1 = new byte[Buf], b2 = new byte[Buf];
                while (true)
                {
                    int n1 = ReadFull(f1, b1);
                    int n2 = ReadFull(f2, b2);
                    if (n1 != n2) return false;
                    if (n1 == 0) { Sides.Finish(f1); Sides.Finish(f2); return true; }
                    for (int i = 0; i < n1; i++) if (b1[i] != b2[i]) return false;
                }
            }
        }

        static int ReadFull(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        const string UnknownA = "Stand fehlt: neu in A oder in B gelöscht?";
        const string UnknownB = "Stand fehlt: neu in B oder in A gelöscht?";

        // ask: gespeicherter Stand verloren – nur auf einer Seite vorhandene Einträge als Frage statt kopieren
        static void DecideFile(SyncJob job, PlanItem it, IFileSide sa, IFileSide sb, bool ask)
        {
            FileEntry a = it.A, b = it.B, la = it.LastA, lb = it.LastB;
            bool hadBoth = la != null && lb != null;
            switch (job.Mode)
            {
                case SyncMode.Spiegeln:
                    if (a != null && b == null) Set(it, ActionKind.NachB, "Neu in A");
                    else if (a == null) Set(it, ActionKind.LoeschenB, "Nicht in A vorhanden");
                    else if (!Same(job, it, sa, sb)) Set(it, ActionKind.NachB, a.Mtime >= b.Mtime ? "Ge\u00e4ndert in A" : "In B abweichend");
                    break;

                case SyncMode.Sichern:
                    if (a != null && b == null) Set(it, ActionKind.NachB, "Neu in A");
                    else if (a == null)
                    {
                        if (job.PropagateDeletes && hadBoth && !Changed(b, lb)) Set(it, ActionKind.LoeschenB, "In A gel\u00f6scht");
                    }
                    else if (!Same(job, it, sa, sb))
                    {
                        if (hadBoth)
                        {
                            // mit gespeichertem Stand: Zeiten der beiden Seiten nicht direkt vergleichen (FTP-Server-Zeit)
                            if (!Changed(b, lb)) Set(it, ActionKind.NachB, "Ge\u00e4ndert in A");
                            else if (!Changed(a, la)) Set(it, ActionKind.Konflikt, "In B ge\u00e4ndert");
                            else Set(it, ActionKind.Konflikt, "In A und B ge\u00e4ndert");
                        }
                        else if (a.Precision > 0 || b.Precision > 0)
                            Set(it, ActionKind.Konflikt, "Unterschiedlich, FTP-Zeit ungenau");
                        else if ((a.Mtime - b.Mtime).TotalSeconds >= -2)
                            Set(it, ActionKind.NachB, "Ge\u00e4ndert in A");
                        else Set(it, ActionKind.Konflikt, "In B neuer als in A");
                    }
                    break;

                default:
                    if (a != null && b == null)
                    {
                        if (hadBoth && job.PropagateDeletes)
                        {
                            if (!Changed(a, la)) Set(it, ActionKind.LoeschenA, "In B gel\u00f6scht");
                            else Set(it, ActionKind.Konflikt, "In B gel\u00f6scht, in A ge\u00e4ndert");
                        }
                        else if (ask) { Set(it, ActionKind.Konflikt, UnknownA); it.Unknown = true; }
                        else Set(it, ActionKind.NachB, "Neu in A");
                    }
                    else if (a == null)
                    {
                        if (hadBoth && job.PropagateDeletes)
                        {
                            if (!Changed(b, lb)) Set(it, ActionKind.LoeschenB, "In A gel\u00f6scht");
                            else Set(it, ActionKind.Konflikt, "In A gel\u00f6scht, in B ge\u00e4ndert");
                        }
                        else if (ask) { Set(it, ActionKind.Konflikt, UnknownB); it.Unknown = true; }
                        else Set(it, ActionKind.NachA, "Neu in B");
                    }
                    else if (!Same(job, it, sa, sb))
                    {
                        if (!hadBoth)
                        {
                            double d = (a.Mtime - b.Mtime).TotalSeconds;
                            if (a.Precision > 0 || b.Precision > 0) Set(it, ActionKind.Konflikt, "Unterschiedlich, FTP-Zeit ungenau");
                            else if (Math.Abs(d) <= 2) Set(it, ActionKind.Konflikt, "Unterschiedlich, gleiches Datum");
                            else if (d > 0) Set(it, ActionKind.NachB, "In A neuer");
                            else Set(it, ActionKind.NachA, "In B neuer");
                        }
                        else
                        {
                            bool ca = Changed(a, la), cb = Changed(b, lb);
                            if (ca && !cb) Set(it, ActionKind.NachB, "Ge\u00e4ndert in A");
                            else if (cb && !ca) Set(it, ActionKind.NachA, "Ge\u00e4ndert in B");
                            else Set(it, ActionKind.Konflikt, "In A und B ge\u00e4ndert");
                        }
                    }
                    break;
            }
        }

        static void DecideDir(SyncJob job, PlanItem it, bool ask)
        {
            FileEntry a = it.A, b = it.B, l = it.LastA != null && it.LastB != null ? it.LastA : null;
            if (a != null && b != null) return;
            switch (job.Mode)
            {
                case SyncMode.Spiegeln:
                    if (a != null) Set(it, ActionKind.NachB, "Neuer Ordner in A");
                    else Set(it, ActionKind.LoeschenB, "Nicht in A vorhanden");
                    break;
                case SyncMode.Sichern:
                    if (a != null) Set(it, ActionKind.NachB, "Neuer Ordner in A");
                    else if (job.PropagateDeletes && l != null) Set(it, ActionKind.LoeschenB, "In A gelöscht");
                    break;
                default:
                    if (a != null)
                    {
                        if (l != null && job.PropagateDeletes) Set(it, ActionKind.LoeschenA, "In B gelöscht");
                        else if (ask) { Set(it, ActionKind.Konflikt, UnknownA); it.Unknown = true; }
                        else Set(it, ActionKind.NachB, "Neuer Ordner in A");
                    }
                    else
                    {
                        if (l != null && job.PropagateDeletes) Set(it, ActionKind.LoeschenB, "In A gelöscht");
                        else if (ask) { Set(it, ActionKind.Konflikt, UnknownB); it.Unknown = true; }
                        else Set(it, ActionKind.NachA, "Neuer Ordner in B");
                    }
                    break;
            }
        }

        /// <summary>
        /// Konfliktregel der Aufgabe: automatisch entscheiden statt fragen. Wurde eine Datei auf einer Seite gelöscht und auf der
        /// anderen geändert, bleibt immer die geänderte Datei erhalten. Nicht angewendet bei unklarem Stand (verlorener Stand),
        /// bei Datei und Ordner mit gleichem Namen und bei Ordnern.
        /// </summary>
        static void ApplyConflictRule(SyncJob job, List<PlanItem> all)
        {
            if (job.Conflicts == ConflictRule.Fragen) return;
            string rule = job.Conflicts == ConflictRule.Neuer ? "neuere Datei gewinnt"
                        : job.Conflicts == ConflictRule.AGewinnt ? "A gewinnt" : "B gewinnt";
            bool backup = job.Mode == SyncMode.Sichern;
            foreach (PlanItem it in all)
            {
                if (it.Proposed != ActionKind.Konflikt || it.Unknown || it.Unreadable || it.IsDir) continue;
                if (it.A != null && it.B != null && it.A.IsDir != it.B.IsDir) continue;
                string why = it.Reason + " – Regel: " + rule;
                if (it.A == null || it.B == null)
                {
                    // gelöscht gegen geändert: die geänderte Datei auf die andere Seite bringen
                    if (it.A != null) Set(it, ActionKind.NachB, why);
                    else if (!backup) Set(it, ActionKind.NachA, why);
                    continue;
                }
                double d = (it.A.Mtime - it.B.Mtime).TotalSeconds;
                ActionKind k;
                if (job.Conflicts == ConflictRule.AGewinnt) k = ActionKind.NachB;
                else if (job.Conflicts == ConflictRule.BGewinnt) k = backup ? ActionKind.Keine : ActionKind.NachA;
                else if (d > 2) k = ActionKind.NachB;
                else if (d < -2) k = backup ? ActionKind.Keine : ActionKind.NachA;
                else continue; // gleich alt: weiter fragen
                Set(it, k, why);
            }
        }

        /// <summary>"Niemals löschen": vorgeschlagenes Löschen auf der gesperrten Seite überspringen.</summary>
        static void ApplyNeverDelete(SyncJob job, List<PlanItem> all)
        {
            foreach (PlanItem it in all)
            {
                it.NoDeleteA = job.NeverDeleteA;
                it.NoDeleteB = job.NeverDeleteB;
                if (it.Proposed == ActionKind.LoeschenA && job.NeverDeleteA) Set(it, ActionKind.Keine, "Nicht gelöscht – in A wird nie gelöscht");
                else if (it.Proposed == ActionKind.LoeschenB && job.NeverDeleteB) Set(it, ActionKind.Keine, "Nicht gelöscht – in B wird nie gelöscht");
            }
        }

        // Ein Ordner, der noch Dateien enthaelt, die bleiben, wird nicht geloescht, sondern angelegt.
        static void FixDirDeletes(List<PlanItem> all)
        {
            HashSet<string> keepA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> keepB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlanItem it in all)
            {
                if (it.A != null && it.Proposed != ActionKind.LoeschenA) AddAncestors(keepA, it.Rel);
                if (it.B != null && it.Proposed != ActionKind.LoeschenB) AddAncestors(keepB, it.Rel);
            }
            foreach (PlanItem it in all)
            {
                if (!it.IsDir) continue;
                if (it.Proposed == ActionKind.LoeschenA && keepA.Contains(it.Rel))
                    Set(it, it.B == null ? ActionKind.NachB : ActionKind.Keine, "Ordner enthält neue Dateien");
                else if (it.Proposed == ActionKind.LoeschenB && keepB.Contains(it.Rel))
                    Set(it, it.A == null ? ActionKind.NachA : ActionKind.Keine, "Ordner enthält neue Dateien");
            }
        }

        public static string SafetyCheck(Analysis an)
        {
            int files = Math.Max(an.FilesA, an.FilesB);
            int del = an.CountDeletes;
            if (del > 0 && (an.FilesA == 0 || an.FilesB == 0))
                return "Eine Seite ist leer, trotzdem sollen " + del.ToString("N0") + " Dateien gelöscht werden (falsches Laufwerk?)";
            if (del >= 20 && del > files * 0.3)
                return del.ToString("N0") + " Dateien sollen gelöscht werden – das sind mehr als 30 % des Bestands";
            return null;
        }

        public static string ActionText(ActionKind k, bool isDir)
        {
            switch (k)
            {
                case ActionKind.NachB: return isDir ? "→ Ordner in B" : "→ Nach B";
                case ActionKind.NachA: return isDir ? "← Ordner in A" : "← Nach A";
                case ActionKind.LoeschenA: return "Löschen in A";
                case ActionKind.LoeschenB: return "Löschen in B";
                case ActionKind.Konflikt: return "Konflikt";
                case ActionKind.UmbenennenA: return isDir ? "Ordner umbenennen in A" : "Umbenennen in A";
                case ActionKind.UmbenennenB: return isDir ? "Ordner umbenennen in B" : "Umbenennen in B";
                default: return "Überspringen";
            }
        }

        // ---------- Ausfuehrung ----------

        public static ExecResult Execute(Analysis an, Func<bool> cancel, Action<int, int, string> progress, Logger log)
        {
            SyncJob job = an.Job;
            ExecResult r = new ExecResult();
            NetShare.Connect(job.FolderA, job.UserA, job.PassA);
            NetShare.Connect(job.FolderB, job.UserB, job.PassB);
            using (IFileSide sa = Sides.Open(job, true))
            using (IFileSide sb = Sides.Open(job, false))
            {
                sa.CheckRoot();
                sb.CheckRoot();

                // Einträge, die eine Umbenennung miterledigt: wird statt Umbenennen kopiert, gilt wieder ihr eigener Vorschlag
                List<PlanItem> fallback = new List<PlanItem>();
                foreach (PlanItem x in an.All)
                {
                    if (x.Mover == null) continue;
                    bool copyInstead = x.Mover.Chosen == ActionKind.NachA || x.Mover.Chosen == ActionKind.NachB;
                    x.Chosen = copyInstead ? x.Before : ActionKind.Keine;
                    x.Done = x.Failed = false;
                    if (x.Chosen != ActionKind.Keine && x.Chosen != ActionKind.Konflikt) fallback.Add(x);
                }
                List<PlanItem> work = an.Actions.Concat(fallback).ToList();

                foreach (PlanItem x in an.Actions)
                {
                    x.Done = false;
                    x.Failed = false;
                    x.ResultA = null;
                    x.ResultB = null;
                    if (x.Chosen == ActionKind.Keine)
                    {
                        r.Skipped++;
                        log.Line("\u00dcbersprungen: " + x.Rel);
                    }
                    else if (x.Chosen == ActionKind.Konflikt)
                    {
                        r.Conflicts++;
                        log.Line("Konflikt, \u00fcbersprungen (" + job.Sides(x.Reason) + "): " + x.Rel);
                    }
                }

                List<PlanItem> mkdirs = work.Where(x => x.IsDir && x.IsCopy).OrderBy(x => x.Rel.Length).ToList();
                // nur Schreibweise: zuerst und von innen nach außen (so stimmen die bisherigen Pfade noch, auch bei FTP/SFTP)
                List<PlanItem> caseRenames = work.Where(x => x.IsRename && x.CaseOnly).OrderByDescending(x => x.From.Length).ToList();
                List<PlanItem> renames = work.Where(x => x.IsRename && !x.CaseOnly).OrderBy(x => x.IsDir ? 0 : 1).ThenBy(x => x.Rel.Length).ToList();
                List<PlanItem> copies = work.Where(x => !x.IsDir && x.IsCopy).ToList();
                List<PlanItem> delFiles = work.Where(x => !x.IsDir && x.IsDelete).ToList();
                List<PlanItem> delDirs = work.Where(x => x.IsDir && x.IsDelete).OrderByDescending(x => x.Rel.Length).ToList();
                List<PlanItem> order = new List<PlanItem>();
                order.AddRange(caseRenames);
                order.AddRange(mkdirs);
                order.AddRange(renames); // vor dem Löschen: die alten Ordner werden dadurch leer
                order.AddRange(copies);
                order.AddRange(delFiles);
                order.AddRange(delDirs);

                int n = 0;
                foreach (PlanItem it in order)
                {
                    if (cancel())
                    {
                        r.Cancelled = true;
                        log.Line("Vom Benutzer abgebrochen.");
                        break;
                    }
                    progress(n++, order.Count, it.Rel);
                    try
                    {
                        DoItem(job, it, sa, sb, r, log);
                        it.Done = true;
                    }
                    catch (Exception ex)
                    {
                        it.Failed = true;
                        it.Error = ex.Message;
                        r.Errors++;
                        log.Line("FEHLER bei " + it.Rel + ": " + ex.Message);
                    }
                }
                progress(order.Count, order.Count, "");
            }
            return r;
        }

        static void DoItem(SyncJob job, PlanItem it, IFileSide sa, IFileSide sb, ExecResult r, Logger log)
        {
            switch (it.Chosen)
            {
                case ActionKind.NachB:
                case ActionKind.NachA:
                {
                    bool toB = it.Chosen == ActionKind.NachB;
                    IFileSide src = toB ? sa : sb, dst = toB ? sb : sa;
                    string dir = job.Sides(toB ? "A \u2192 B" : "B \u2192 A");
                    if (it.IsDir)
                    {
                        dst.CreateDir(it.Rel);
                        log.Line("Ordner angelegt (" + dir + "): " + it.Rel);
                        if (job.CopyRights) Rights.Copy(src, dst, it.Rel, true, log);
                        return;
                    }
                    // Datei wird überschrieben: bisherige Fassung vorher aufbewahren
                    if (job.KeepVersions && (toB ? it.B : it.A) != null)
                    {
                        string ver = VersionPath(it.Rel);
                        dst.Move(it.Rel, ver, false);
                        log.Line("Bisherige Fassung aufbewahrt (" + job.SideName(!toB) + "): " + ver);
                    }
                    FileEntry res = dst.WriteFile(it.Rel, src, toB ? it.A : it.B);
                    if (job.CopyRights) Rights.Copy(src, dst, it.Rel, false, log);
                    if (toB) it.ResultB = res; else it.ResultA = res;
                    r.Copied++;
                    r.Bytes += it.TransferSize;
                    log.Line("Kopiert (" + dir + "): " + it.Rel);
                    return;
                }
                case ActionKind.UmbenennenA:
                case ActionKind.UmbenennenB:
                {
                    IFileSide side = it.Chosen == ActionKind.UmbenennenA ? sa : sb;
                    string label = job.SideName(it.Chosen == ActionKind.UmbenennenA);
                    if (it.CaseOnly)
                    {
                        // über einen Zwischennamen – Windows übernimmt "fotos" -> "Fotos" sonst nicht
                        string tmp = (ParentOf(it.From).Length > 0 ? ParentOf(it.From) + "\\" : "") + "~sp" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        side.Move(it.From, tmp, it.IsDir);
                        side.Move(tmp, it.Rel, it.IsDir);
                        r.Moved++;
                        log.Line("Schreibweise angeglichen (" + label + "): " + it.From + " \u2192 " + NameOf(it.Rel));
                        return;
                    }
                    side.Move(it.From, it.Rel, it.IsDir);
                    r.Moved++;
                    log.Line((it.IsDir ? "Ordner umbenannt/verschoben (" : "Umbenannt/verschoben (") + label + "): " +
                             it.From + " \u2192 " + it.Rel);
                    return;
                }
                case ActionKind.LoeschenA:
                case ActionKind.LoeschenB:
                {
                    IFileSide side = it.Chosen == ActionKind.LoeschenA ? sa : sb;
                    string label = job.SideName(it.Chosen == ActionKind.LoeschenA);
                    if (it.IsDir)
                    {
                        string how = side.DeleteDir(it.Rel, job.UseRecycleBin);
                        if (how == null) log.Line("Ordner nicht leer oder nicht vorhanden, bleibt (" + label + "): " + it.Rel);
                        else log.Line("Ordner gel\u00f6scht (" + label + ", " + how + "): " + it.Rel);
                        return;
                    }
                    string h;
                    if (job.KeepVersions)
                    {
                        // statt Löschen in den Versionen-Ordner dieser Seite verschieben
                        string ver = VersionPath(it.Rel);
                        try { side.Move(it.Rel, ver, false); h = "aufbewahrt unter " + ver; }
                        catch (FileNotFoundException) { h = null; }
                        catch (DirectoryNotFoundException) { h = null; }
                    }
                    else h = side.DeleteFile(it.Rel, job.UseRecycleBin);
                    if (h == null)
                    {
                        log.Line("Schon nicht mehr vorhanden (" + label + "): " + it.Rel);
                        return;
                    }
                    r.Deleted++;
                    log.Line("Gel\u00f6scht (" + label + ", " + h + "): " + it.Rel);
                    return;
                }
            }
        }

        /// <summary>Ordner für ältere Versionen im Startordner jeder Seite (wird nie abgeglichen).</summary>
        public const string VersionsDir = "_Sync Pilot Versionen";

        /// <summary>Pfad der aufbewahrten Fassung: _Sync Pilot Versionen\Unterordner\Name (2026-11-06 22-10-05).ext</summary>
        static string VersionPath(string rel)
        {
            string dir = ParentOf(rel), name = NameOf(rel);
            string ext = Path.GetExtension(name), stem = ext.Length > 0 && ext.Length < name.Length ? name.Substring(0, name.Length - ext.Length) : name;
            if (stem.Length == name.Length) ext = "";
            string file = stem + " (" + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ")" + ext;
            return dir.Length == 0 ? VersionsDir + "\\" + file : VersionsDir + "\\" + dir + "\\" + file;
        }

        // Neuen Stand merken, damit beim naechsten Lauf Loeschungen und Aenderungen erkannt werden.
        public static void SaveState(Analysis an)
        {
            List<FileEntry[]> list = new List<FileEntry[]>();
            // noch nicht entschieden: beim nächsten Mal wieder fragen (nicht als "neu" kopieren)
            List<string> ask = an.All.Where(x => x.Unknown && !x.Done).Select(x => x.Rel).ToList();
            foreach (PlanItem it in an.All)
            {
                // Umbenennung erledigt: neuer Pfad mit dem Stand der umbenannten Datei, alter Pfad entfällt
                if (it.Mover != null && it.Mover.Done && it.Mover.IsRename)
                {
                    FileEntry[] mp = MovedPair(it, it.Mover.Chosen);
                    if (mp != null) list.Add(mp);
                    continue;
                }
                if (it.CaseOnly)
                {
                    // angeglichen: beide Seiten mit der neuen Schreibweise; übersprungen: so, wie sie jetzt sind
                    if (it.Done && it.Chosen == ActionKind.UmbenennenB) list.Add(new FileEntry[] { it.A, Repath(it.B, it.Rel) });
                    else if (it.Done && it.Chosen == ActionKind.UmbenennenA) list.Add(new FileEntry[] { Repath(it.A, it.Rel), it.B });
                    else list.Add(new FileEntry[] { it.A, it.B });
                    continue;
                }
                if (it.IsRename && it.Done)
                {
                    FileEntry[] mp = MovedPair(it, it.Chosen);
                    if (mp != null) list.Add(mp);
                    continue;
                }
                FileEntry na = null, nb = null;
                bool keep = false;
                if (it.Unreadable) keep = true;
                else if (it.Proposed == ActionKind.Keine && it.Chosen == ActionKind.Keine)
                {
                    if (it.A != null && it.B != null) { na = it.A; nb = it.B; }
                    else keep = true;
                }
                else if (it.Done)
                {
                    if (it.Chosen == ActionKind.NachB) { na = it.A; nb = it.ResultB ?? it.A; }
                    else if (it.Chosen == ActionKind.NachA) { na = it.ResultA ?? it.B; nb = it.B; }
                }
                else keep = true;
                if (keep) { na = it.LastA; nb = it.LastB; }
                if (na != null && nb != null) list.Add(new FileEntry[] { na, nb });
            }
            StateStore.Save(an.Job.Id, list, ask);
        }

        /// <summary>Stand für den neuen Pfad nach dem Umbenennen: Quellseite wie gelesen, Zielseite = umbenannter alter Eintrag.</summary>
        static FileEntry[] MovedPair(PlanItem it, ActionKind kind)
        {
            PlanItem old = it.Counterpart;
            if (old == null) return null; // alter Pfad: kein Stand mehr
            if (kind == ActionKind.UmbenennenB)
                return it.A == null || old.B == null ? null : new FileEntry[] { it.A, Repath(old.B, it.Rel) };
            return it.B == null || old.A == null ? null : new FileEntry[] { Repath(old.A, it.Rel), it.B };
        }

        static FileEntry Repath(FileEntry e, string rel)
        {
            return new FileEntry { Rel = rel, IsDir = e.IsDir, Size = e.Size, Mtime = e.Mtime, Precision = e.Precision };
        }
    }

    public static class Runner
    {
        public static string Version
        {
            get { return AppInfo.Version; }
        }

        public static HistoryEntry ExecuteAnalysis(Analysis an, bool scheduled, Func<bool> cancel, Action<int, int, string> progress)
        {
            SyncJob job = an.Job;
            HistoryEntry h = NewEntry(job, scheduled);
            Stopwatch sw = Stopwatch.StartNew();
            bool scriptsRan = false;
            string scriptError = null;
            using (Logger log = new Logger(job.Name))
            {
                h.LogFile = log.FilePath;
                WriteHeader(log, an);
                Mutex mtx = null;
                bool own = false;
                try
                {
                    mtx = new Mutex(false, "Local\\SyncPilot_Job_" + job.Id);
                    try { own = mtx.WaitOne(0); }
                    catch (AbandonedMutexException) { own = true; }
                    if (!own) throw new SyncException("Diese Aufgabe läuft bereits.");

                    // Sperrdatei (gegen gleichzeitigen Abgleich von einem anderen PC); bei Run() schon vor der Analyse geholt
                    using (LockFiles lk = LockFiles.Acquire(job, false, 0, cancel, null, log))
                    {
                        bool scripts = !job.ScriptsOnlyWithChanges || an.CountActive > 0;
                        bool before = scripts && !string.IsNullOrWhiteSpace(job.ScriptBefore);
                        bool after = scripts && !string.IsNullOrWhiteSpace(job.ScriptAfter);
                        if (before)
                        {
                            scriptsRan = true;
                            int rc = Scripts.Run(job.ScriptBefore, job, "vor dem Kopieren", "", cancel, log,
                                delegate (string l) { if (progress != null) progress(0, 0, "Skript vor dem Kopieren: " + l); });
                            if (cancel()) throw new OperationCanceledException();
                            if (rc != 0 && job.ScriptAbortOnError)
                                throw new SyncException("Das Skript vor dem Kopieren meldet einen Fehler (Rückgabewert " + rc + ") – nicht synchronisiert.");
                            if (rc != 0) scriptError = "Skript vor dem Kopieren: Rückgabewert " + rc;
                        }
                        string afterResult = "Fehler";
                        try
                        {
                            // Das Skript davor kann Dateien ändern (z. B. Zertifikate löschen, Datenbank sichern):
                            // dann neu einlesen, damit nicht nach einem veralteten Plan kopiert wird
                            if (before) Reanalyze(an, cancel, progress, log);
                            ExecResult r = Engine.Execute(an, cancel, progress, log);
                            Engine.SaveState(an);
                            h.Copied = r.Copied;
                            h.Moved = r.Moved;
                            h.Deleted = r.Deleted;
                            h.Errors = r.Errors;
                            h.Skipped = r.Skipped;
                            h.Conflicts = r.Conflicts;
                            h.Bytes = r.Bytes;
                            h.Cancelled = r.Cancelled;
                            h.Result = ResultText(h);
                            int unknown = an.CountUnknown;
                            if (unknown > 0 && h.Errors == 0 && !h.Cancelled)
                                h.Result = "Stand fehlte – " + unknown.ToString("N0") + (unknown == 1 ? " Eintrag wartet" : " Einträge warten") + " auf Entscheidung";
                            log.Line("Ergebnis: " + h.Copied + " kopiert, " + (h.Moved > 0 ? h.Moved + " umbenannt/verschoben, " : "") +
                                     h.Deleted + " gelöscht, " + h.Conflicts +
                                     " Konflikte, " + h.Skipped + " übersprungen, " + h.Errors + " Fehler");
                            afterResult = h.Result;
                        }
                        finally
                        {
                            // auch nach Fehler oder Abbruch (z. B. um einen vorher beendeten Dienst wieder zu starten)
                            if (after)
                            {
                                scriptsRan = true;
                                // läuft auch nach einem Abbruch; "Abbrechen" während des Skripts beendet es aber
                                bool cancelledBefore = cancel();
                                if (progress != null) progress(0, 0, "Skript nach dem Kopieren läuft …");
                                int rc2 = Scripts.Run(job.ScriptAfter, job, "nach dem Kopieren", afterResult,
                                    delegate { return !cancelledBefore && cancel(); }, log,
                                    delegate (string l) { if (progress != null) progress(0, 0, "Skript nach dem Kopieren: " + l); });
                                if (rc2 != 0) scriptError = (scriptError != null ? scriptError + " · " : "") + "Skript nach dem Kopieren: Rückgabewert " + rc2;
                            }
                            // Ein Skriptfehler ist ein Fehler des Laufs: roter Status, Hinweis im Infobereich, Protokoll bleibt
                            if (scriptError != null && !h.Cancelled)
                            {
                                h.Errors++;
                                h.Result = "Fehler: " + scriptError + (h.Copied + h.Moved + h.Deleted > 0 ? " (" + ResultText(h) + ")" : "");
                                log.Line("FEHLER: " + scriptError);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    h.Cancelled = true;
                    h.Result = "Abgebrochen";
                    log.Line("Vom Benutzer abgebrochen.");
                }
                catch (Exception ex)
                {
                    h.Errors++;
                    h.Result = "Fehler: " + ex.Message;
                    log.Line("FEHLER: " + ex.Message);
                }
                finally
                {
                    if (mtx != null)
                    {
                        if (own) mtx.ReleaseMutex();
                        mtx.Dispose();
                    }
                }
            }
            h.Seconds = sw.Elapsed.TotalSeconds + an.Seconds;
            // automatischer Lauf ohne Änderungen, Fehler und Warnungen: kein Protokoll (der Verlauf genügt)
            if (scheduled && !h.Cancelled && h.Errors == 0 && h.Copied == 0 && h.Moved == 0 && h.Deleted == 0 &&
                h.Skipped == 0 && h.Conflicts == 0 && an.ScanErrors.Count == 0 && an.StateNote == null && !scriptsRan)
            {
                try { File.Delete(h.LogFile); h.LogFile = null; } catch { }
            }
            Store.AddHistory(h);
            return h;
        }

        /// <summary>Läuft diese Aufgabe gerade (in diesem oder einem anderen Sync-Pilot-Prozess)?</summary>
        public static bool IsRunning(string jobId)
        {
            try
            {
                using (Mutex m = new Mutex(false, "Local\\SyncPilot_Job_" + jobId))
                {
                    bool own;
                    try { own = m.WaitOne(0); }
                    catch (AbandonedMutexException) { own = true; }
                    if (own) m.ReleaseMutex();
                    return !own;
                }
            }
            catch { return false; }
        }

        public static HistoryEntry Run(SyncJob job, bool scheduled, Func<bool> cancel, Action<string> status)
        {
            // Sperrdatei schon vor der Analyse holen (sonst könnte sich zwischen Analyse und Kopieren etwas ändern);
            // automatische Läufe warten bis zu 30 Minuten, manuelle brechen sofort ab
            LockFiles lk;
            try { lk = LockFiles.Acquire(job, scheduled, 30, cancel, status, null); }
            catch (Exception ex) { return Fail(job, scheduled, ex is OperationCanceledException ? null : ex.Message); }
            try
            {
                Analysis an;
                try
                {
                    an = Engine.Analyze(job, cancel, status);
                }
                catch (Exception ex)
                {
                    return Fail(job, scheduled, ex is OperationCanceledException ? null : ex.Message);
                }
                string warn = Engine.SafetyCheck(an);
                if (warn != null) return Fail(job, scheduled, "Sicherheitsstopp: " + warn);
                return ExecuteAnalysis(an, scheduled, cancel,
                    delegate (int i, int n, string t) { status(n == 0 && !string.IsNullOrEmpty(t) ? job.Name + ": " + t : "Synchronisiere " + job.Name + " … " + i + " / " + n); });
            }
            finally { if (lk != null) lk.Dispose(); }
        }

        // Fuer unerwartete Ausnahmen (z. B. Protokoll nicht schreibbar): nie den Prozess beenden.
        public static HistoryEntry Crash(SyncJob job, bool scheduled, Exception ex)
        {
            try { return Fail(job, scheduled, "Fehler: " + ex.Message); }
            catch
            {
                HistoryEntry h = NewEntry(job, scheduled);
                h.Errors = 1;
                h.Result = "Fehler: " + ex.Message;
                return h;
            }
        }

        static HistoryEntry Fail(SyncJob job, bool scheduled, string message)
        {
            HistoryEntry h = NewEntry(job, scheduled);
            if (message == null)
            {
                h.Cancelled = true;
                h.Result = "Abgebrochen";
            }
            else
            {
                h.Errors = 1;
                h.Result = message;
            }
            try
            {
                using (Logger log = new Logger(job.Name))
                {
                    h.LogFile = log.FilePath;
                    log.Line("Sync Pilot " + Version + " – Aufgabe: " + job.Name);
                    log.Line("A: " + Paths.TryResolve(job.FolderA));
                    log.Line("B: " + Paths.TryResolve(job.FolderB));
                    log.Line(h.Result);
                }
            }
            catch { }
            Store.AddHistory(h);
            return h;
        }

        /// <summary>
        /// Nach dem Skript vor dem Kopieren beide Seiten neu einlesen und den Plan ersetzen. Von Hand geänderte
        /// Aktionen (Rechtsklick) bleiben erhalten, wenn sich am Eintrag nichts geändert hat. Löst die neue Analyse
        /// den Sicherheitsstopp aus (z. B. viele Löschungen), wird nicht synchronisiert.
        /// </summary>
        static void Reanalyze(Analysis an, Func<bool> cancel, Action<int, int, string> progress, Logger log)
        {
            if (progress != null) progress(0, 0, "Lese nach dem Skript neu ein \u2026");
            Analysis fresh = Engine.Analyze(an.Job, cancel, delegate (string st) { if (progress != null) progress(0, 0, st); });
            Dictionary<string, PlanItem> manual = new Dictionary<string, PlanItem>(StringComparer.OrdinalIgnoreCase);
            foreach (PlanItem o in an.All) if (o.Chosen != o.Proposed) manual[o.Rel] = o;
            foreach (PlanItem it in fresh.All)
            {
                PlanItem o;
                if (manual.TryGetValue(it.Rel, out o) && o.Proposed == it.Proposed && it.CanChoose(o.Chosen)) it.Chosen = o.Chosen;
            }
            int before = an.Actions.Count(x => x.Chosen != ActionKind.Keine && x.Chosen != ActionKind.Konflikt);
            int after = fresh.Actions.Count(x => x.Chosen != ActionKind.Keine && x.Chosen != ActionKind.Konflikt);
            bool wasSafe = Engine.SafetyCheck(an) == null;
            an.All = fresh.All;
            an.Actions = fresh.Actions;
            an.ScanErrors = fresh.ScanErrors;
            an.FilesA = fresh.FilesA; an.FilesB = fresh.FilesB;
            an.BytesA = fresh.BytesA; an.BytesB = fresh.BytesB;
            an.StateLost = fresh.StateLost;
            an.StateNote = fresh.StateNote;
            an.Seconds += fresh.Seconds;
            log.Line("Nach dem Skript neu eingelesen: " + after + (after == 1 ? " Aktion" : " Aktionen") +
                     (after != before ? " (vorher " + before + ")" : ""));
            string warn = Engine.SafetyCheck(an);
            if (warn != null && wasSafe) throw new SyncException("Sicherheitsstopp nach dem Skript: " + warn);
        }

        /// <summary>
        /// Analyse ohne jeden Unterschied: zählt wie ein Lauf ohne Änderungen. Der Stand wird gespeichert (sonst gälte
        /// später eine auf einer Seite gelöschte Datei als "neu auf der anderen" und käme zurück) und der Verlauf
        /// zeigt "Alles aktuell" statt "Noch nie ausgeführt". Ohne Protokoll und ohne Skripte.
        /// </summary>
        public static void RecordUpToDate(Analysis an)
        {
            try
            {
                if (an == null || an.Actions.Count > 0 || an.ScanErrors.Count > 0 || an.StateNote != null) return;
                SyncJob job = an.Job;
                if (IsRunning(job.Id)) return;
                Engine.SaveState(an);
                HistoryEntry h = NewEntry(job, false);
                h.Result = "Alles aktuell";
                h.Seconds = an.Seconds;
                Store.AddHistory(h);
            }
            catch { } // nur Komfort – die Analyse selbst ist gültig
        }

        static HistoryEntry NewEntry(SyncJob job, bool scheduled)
        {
            HistoryEntry h = new HistoryEntry();
            h.Start = DateTime.Now;
            h.JobId = job.Id;
            h.JobName = job.Name;
            h.Scheduled = scheduled;
            return h;
        }

        static void WriteHeader(Logger log, Analysis an)
        {
            SyncJob job = an.Job;
            log.Line("Sync Pilot " + Version + " – Aufgabe: " + job.Name);
            log.Line(job.SideName(true) + ": " + job.FolderA + "  (" + an.FilesA.ToString("N0") + " Dateien, " + Fmt.Size(an.BytesA) + ")");
            log.Line(job.SideName(false) + ": " + job.FolderB + "  (" + an.FilesB.ToString("N0") + " Dateien, " + Fmt.Size(an.BytesB) + ")");
            log.Line("Modus: " + job.ModeText + (job.IncludeSubdirs ? "" : ", ohne Unterordner") +
                     (job.UseRecycleBin ? ", Löschen in den Papierkorb" : ", endgültig löschen"));
            foreach (string e in an.ScanErrors) log.Line("Warnung: " + e);
            if (an.StateNote != null) log.Line("Hinweis: " + an.StateNote + (an.StateLost ? " Nur auf einer Seite vorhandene Einträge werden nicht kopiert, sondern als Frage gezeigt." : ""));
        }

        static string ResultText(HistoryEntry h)
        {
            if (h.Cancelled) return "Abgebrochen";
            if (h.Errors > 0) return h.Errors + (h.Errors == 1 ? " Fehler" : " Fehler");
            if (h.Conflicts > 0) return "OK, " + h.Conflicts + (h.Conflicts == 1 ? " Konflikt offen" : " Konflikte offen");
            return "OK";
        }
    }
}
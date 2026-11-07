// Sync Pilot – Aufgaben aus Easy2Sync übernehmen (Datei easy2sync.sdb)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SyncPilot
{
    /// <summary>Eine aus Easy2Sync gelesene Aufgabe mit Hinweisen, was von Hand nachzutragen ist.</summary>
    public class ImportedJob
    {
        public SyncJob Job;
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Liest die Aufgabenliste von Easy2Sync für Dateien (getestet mit Version 8.06). Das Format ist nicht offen gelegt;
    /// gelesen werden nur gut erkennbare Teile: Name, Ordner, Richtung (Abgleich bzw. Replikation A → B), Unterordner,
    /// Filterlisten, Bezeichnungen der Seiten, Sperrdatei und die Befehle vor/nach dem Sync. Kennwörter sind dort
    /// verschlüsselt gespeichert und werden nicht übernommen, ebenso Zeitplan, Ordnerüberwachung und die gespeicherten
    /// Dateistände (Sync Pilot legt beim ersten Lauf einen eigenen Stand an).
    /// </summary>
    public static class Easy2SyncImport
    {
        static readonly string[] SystemDirs = { "recycler", "recycled", "system volume information", "$recycle.bin" };

        // Typische Easy2Sync-Namen: "Sync PC <-> NAS (Dokumente)", "Backup Laptop -> NAS (Fotos)"
        static readonly Regex NamePattern = new Regex(
            @"^\s*(?:Backup|Sicherung|Sync|Synchronisation|Replikation|Spiegel\w*)\s+(.+?)\s*(?:<->|<=>|->|=>|\u2192|\u21C4|\u2194)\s*(.+?)\s*\((.+)\)\s*$",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// Schlägt aus dem Easy2Sync-Namen eine Gruppe und einen kurzen Namen vor.
        /// Gruppe ist das "andere" Gerät: Ist das Ziel dieser Rechner, die Quelle, sonst das Ziel.
        /// </summary>
        public static bool SuggestGroup(string name, out string group, out string shortName)
        {
            group = shortName = null;
            Match m = NamePattern.Match(name ?? "");
            if (!m.Success) return false;
            string x = m.Groups[1].Value.Trim(), y = m.Groups[2].Value.Trim(), n = m.Groups[3].Value.Trim();
            if (x.Length == 0 || y.Length == 0 || n.Length == 0) return false;
            string me = Environment.MachineName;
            if (string.Equals(y, me, StringComparison.OrdinalIgnoreCase)) group = x;
            else group = y;
            shortName = n;
            return true;
        }

        public static List<ImportedJob> Read(string file)
        {
            byte[] d;
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                d = new byte[fs.Length];
                int n = 0;
                while (n < d.Length)
                {
                    int r = fs.Read(d, n, d.Length - n);
                    if (r <= 0) break;
                    n += r;
                }
            }
            Reader rd = new Reader(d);
            List<int[]> recs = FindRecords(rd);
            List<ImportedJob> list = new List<ImportedJob>();
            for (int i = 0; i < recs.Count; i++)
            {
                int end = i + 1 < recs.Count ? recs[i + 1][0] : d.Length;
                try
                {
                    ImportedJob ij = ReadRecord(rd, recs[i][0], end);
                    if (ij != null) list.Add(ij);
                }
                catch { /* unbekannter Aufbau: Aufgabe auslassen */ }
            }
            return list;
        }

        /// <summary>Beginn jeder Aufgabe: Versionsnummer (1–16), Name, Ordner A, Ordner B.</summary>
        static List<int[]> FindRecords(Reader rd)
        {
            List<int[]> res = new List<int[]>();
            byte[] d = rd.Data;
            for (int o = 0; o + 16 < d.Length; o++)
            {
                if (d[o] < 1 || d[o] > 16 || d[o + 1] != 0 || d[o + 2] != 0 || d[o + 3] != 0) continue;
                int p;
                string name = rd.Str(o + 4, out p);
                if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 300 || IsPath(name)) continue;
                string a = rd.Str(p, out p);
                if (!IsPath(a)) continue;
                string b = rd.Str(p, out p);
                if (!IsPath(b)) continue;
                res.Add(new int[] { o });
                o = p - 1;
            }
            return res;
        }

        static bool IsPath(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
            return s.StartsWith("\\\\") || (char.IsLetter(s[0]) && s[1] == ':' && s[2] == '\\') || s.Contains("://") || s.StartsWith("%");
        }

        static ImportedJob ReadRecord(Reader rd, int start, int end)
        {
            int p;
            string name = rd.Str(start + 4, out p);
            string a = rd.Str(p, out p);
            string b = rd.Str(p, out p);
            int[] flags = new int[7];
            for (int k = 0; k < 7; k++) { flags[k] = rd.U32(p); p += 4; }
            // vier Filterlisten: Dateien ignorieren, Ordner ignorieren, nur diese Dateien, nur diese Ordner –
            // jeweils erst der eingegebene Text, dann die wirksame Liste (klein geschrieben, mit den globalen Vorgaben)
            List<string>[] lists = new List<string>[4];
            for (int k = 0; k < 4; k++)
            {
                int n = rd.U32(p); p += 4;
                if (n == 0) p += 4;
                else for (int i = 0; i < n; i++) rd.Str(p, out p);
                int m = rd.U32(p); p += 4;
                if (m < 0 || m > 5000) throw new InvalidDataException();
                List<string> items = new List<string>();
                for (int i = 0; i < m; i++)
                {
                    string s = rd.Str(p, out p);
                    if (s == null) throw new InvalidDataException();
                    if (s.Trim().Length > 0) items.Add(s.Trim());
                }
                lists[k] = items;
            }
            // Replikation (nur A → B): Feld direkt vor dem Zeitstempel des letzten Laufs
            bool replication = false;
            for (int k = 1; k < 40; k++)
            {
                long v = (uint)rd.U32(p + 4 * k);
                if (v > 1400000000L && v < 2100000000L) { replication = rd.U32(p + 4 * (k - 1)) == 1; break; }
            }

            SyncJob j = new SyncJob();
            ImportedJob ij = new ImportedJob { Job = j };
            j.Name = name.Trim();
            j.FolderA = TrimSlash(a);
            j.FolderB = TrimSlash(b);
            j.Mode = replication ? SyncMode.Spiegeln : SyncMode.Abgleichen;
            j.PropagateDeletes = true;
            j.UseRecycleBin = true;
            j.Schedule = ScheduleKind.Manuell;
            j.ExcludeFiles = lists[0].Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            j.ExcludeDirs = lists[1].Where(x => !SystemDirs.Contains(x.ToLowerInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            j.IncludeFiles = lists[2].Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // "\ordner" und "\ordner\*": bei Sync Pilot gehört der Inhalt eines Ordners der Positivliste ohnehin dazu
            j.IncludeDirs = lists[3].Where(x => !x.EndsWith("\\*")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (flags[0] == 0)
            {
                j.IncludeSubdirs = false;
                ij.Notes.Add("Ohne Unterordner übernommen (Option „Unterordner einbeziehen“ aus).");
            }
            if (replication) ij.Notes.Add("Replikation: als „Spiegeln A → B“ übernommen.");

            // Bezeichnungen der Seiten: nach der Liste der Dateistände folgen beide Ordner zweimal, dann die zwei Namen
            int afterLabels = FindLabels(rd, p, end, j.FolderA, j.FolderB, ij);
            if (afterLabels > 0)
            {
                // Anmeldung: 4 Felder (Benutzer A, Benutzer B, Kennwort A, Kennwort B), Inhalt verschlüsselt
                int ua = rd.U32(afterLabels), ub = ua == 0 ? rd.U32(afterLabels + 4) : 0;
                if (ua != 0) ij.Notes.Add("Anmeldung für " + j.SideName(true) + " war in Easy2Sync hinterlegt (verschlüsselt) – bitte unter „Anmeldung“ neu eingeben.");
                else if (ub != 0) ij.Notes.Add("Anmeldung für " + j.SideName(false) + " war in Easy2Sync hinterlegt (verschlüsselt) – bitte unter „Anmeldung“ neu eingeben.");

                // Sperrdatei: [aktiv][Pfad] kurz nach den Bezeichnungen
                for (int k = afterLabels; k < Math.Min(end - 8, afterLabels + 0x400); k++)
                {
                    int q;
                    int on = rd.U32(k);
                    if (on != 0 && on != 1) continue;
                    string s = rd.Str(k + 4, out q);
                    // eine Datei (mit Endung), kein Ordner wie die überwachten Ordner, die danach folgen können
                    if (!IsPath(s) || !HasExtension(s)) continue;
                    if (on == 1) j.LockFile = s;
                    break;
                }
                ReadScripts(rd, afterLabels, end, j);
            }
            if (!string.IsNullOrEmpty(j.ScriptBefore) || !string.IsNullOrEmpty(j.ScriptAfter))
            {
                j.ScriptsOnlyWithChanges = false; // wie bei Easy2Sync: bei jedem Lauf
                ij.Notes.Add("Befehle vor/nach dem Kopieren übernommen – bitte unter „Für Profis“ prüfen.");
            }
            ij.Notes.Add("Zeitplan und Ordnerüberwachung bitte in Sync Pilot neu einstellen.");
            return ij;
        }

        static bool HasExtension(string s)
        {
            int slash = s.LastIndexOf('\\'), dot = s.LastIndexOf('.');
            return dot > slash + 1 && dot < s.Length - 1;
        }

        static string TrimSlash(string p)
        {
            string t = (p ?? "").Trim();
            while (t.Length > 3 && t.EndsWith("\\") && !(t.Length == 3 && t[1] == ':')) t = t.Substring(0, t.Length - 1);
            return t;
        }

        static int FindLabels(Reader rd, int from, int end, string a, string b, ImportedJob ij)
        {
            byte[] d = rd.Data;
            string ta = a.TrimEnd('\\'), tb = b.TrimEnd('\\');
            byte[] first = Encoding.Unicode.GetBytes(ta);
            for (int o = from; o + 8 + first.Length < end; o++)
            {
                // Name von A (mit oder ohne "\" am Ende) als Zeichenkette mit Längenangabe
                int n = rd.U32(o);
                if (n != ta.Length && n != ta.Length + 1) continue;
                if (d[o + 4] != first[0] || d[o + 5] != first[1]) continue;
                int p;
                string s1 = rd.Str(o, out p);
                if (s1 == null || !Same(s1, ta)) continue;
                string s2 = rd.Str(p, out p), s3 = rd.Str(p, out p), s4 = rd.Str(p, out p);
                if (!Same(s2, tb) || !Same(s3, ta) || !Same(s4, tb)) continue;
                string la = rd.Str(p, out p), lb = rd.Str(p, out p);
                if (la == null || lb == null) return -1;
                if (la.Trim().Length > 0) ij.Job.NameA = la.Trim();
                if (lb.Trim().Length > 0) ij.Job.NameB = lb.Trim();
                return p;
            }
            return -1;
        }

        static bool Same(string x, string y)
        {
            return x != null && string.Equals(x.TrimEnd('\\'), y, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Befehle vor und nach dem Sync: [aktiv][Anzahl][Befehle] [Feld][Byte] [aktiv][Anzahl][Befehle] [0] [Zeitstempel].
        /// Gesucht wird der letzte passende Block der Aufgabe.
        /// </summary>
        static void ReadScripts(Reader rd, int from, int end, SyncJob j)
        {
            byte[] d = rd.Data;
            List<string> before = null, after = null;
            bool on = false;
            for (int k = from; k + 20 < end; k++)
            {
                if (d[k] > 1) continue;
                int n = rd.U32(k + 1);
                if (n < 0 || n > 10) continue;
                int q = k + 5;
                List<string> bl = new List<string>();
                bool ok = true;
                for (int i = 0; i < n && ok; i++)
                {
                    string s = rd.Str(q, out q);
                    if (string.IsNullOrEmpty(s)) ok = false; else bl.Add(s);
                }
                if (!ok || q + 10 > end) continue;
                q += 5;
                if (d[q] > 1) continue;
                int m = rd.U32(q + 1);
                if (m < 0 || m > 10) continue;
                q += 5;
                List<string> al = new List<string>();
                for (int i = 0; i < m && ok; i++)
                {
                    string s = rd.Str(q, out q);
                    if (string.IsNullOrEmpty(s)) ok = false; else al.Add(s);
                }
                if (!ok || q + 5 > end || d[q] != 0) continue;
                long ts = (uint)rd.U32(q + 1);
                if (ts < 1400000000L || ts > 2100000000L) continue;
                before = bl;
                after = al;
                on = d[k] == 1;
            }
            if (!on || before == null) return;
            if (before.Count > 0) j.ScriptBefore = Join(before);
            if (after.Count > 0) j.ScriptAfter = Join(after);
        }

        /// <summary>Mehrere Befehle nacheinander (cmd: "&"); Pfade mit Leerzeichen in Anführungszeichen.</summary>
        static string Join(List<string> cmds)
        {
            return string.Join(" & ", cmds.Select(c =>
            {
                string t = c.Trim();
                if (!t.StartsWith("\"") && t.Contains(" ") && (t.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                    t.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    t = "\"" + t + "\"";
                return t;
            }));
        }

        sealed class Reader
        {
            public readonly byte[] Data;
            public Reader(byte[] d) { Data = d; }

            public int U32(int o)
            {
                if (o < 0 || o + 4 > Data.Length) return -1;
                return BitConverter.ToInt32(Data, o);
            }

            /// <summary>Zeichenkette: Länge (Zeichen, 4 Byte), UTF-16, abschließende 0; Länge 0 ohne Endzeichen. null = keine.</summary>
            public string Str(int o, out int next)
            {
                next = o;
                int n = U32(o);
                if (n == 0) { next = o + 4; return ""; }
                if (n < 0 || n > 5000 || o + 6 + n * 2 > Data.Length) return null;
                if (Data[o + 4 + n * 2] != 0 || Data[o + 5 + n * 2] != 0) return null;
                string s;
                try { s = Encoding.Unicode.GetString(Data, o + 4, n * 2); }
                catch { return null; }
                foreach (char c in s) if (c < 32 && c != '\r' && c != '\n' && c != '\t') return null;
                next = o + 6 + n * 2;
                return s;
            }
        }
    }
}

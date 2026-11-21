// Sync Pilot – Prüfwert für Aufgaben, die mit Administratorrechten laufen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace SyncPilot
{
    /// <summary>
    /// Die Aufgaben liegen in %LOCALAPPDATA% und lassen sich von jedem Programm unter dem eigenen Konto ändern – auch ohne
    /// Administratorrechte. Läuft eine Aufgabe mit Administratorrechten (Zeitplan „Als Administrator ausführen“ oder Sync Pilot
    /// selbst als Administrator), könnte so ein fremdes Programm über die Vor-/Nachbefehle oder die Ordner zu
    /// Administratorrechten kommen. Deshalb: Beim Speichern in Sync Pilot wird ein SHA-256-Prüfwert der wichtigen
    /// Einstellungen unter HKLM abgelegt (schreiben nur mit Administratorrechten). Mit Administratorrechten läuft eine
    /// Aufgabe nur, wenn der Prüfwert passt.
    /// </summary>
    public static class JobSeal
    {
        const string KeyPath = @"SOFTWARE\Sync Pilot\Pruefwerte";

        /// <summary>
        /// Felder, die nicht in den Prüfwert gehen: Anzeige (Name, Gruppe, Bezeichnungen), Ein/Aus und alles zum Zeitpunkt
        /// (Zeitplan, Ereignisse). Sie bestimmen nicht, was gelesen, geschrieben, gelöscht oder ausgeführt wird, und werden
        /// teils ohne Speichern geändert (Deaktivieren per Rechtsklick, Umstellung alter Zeitpläne).
        /// Alles andere – auch künftige Felder – ist automatisch enthalten: Ordner, Gerät, Anmeldung, Kennwort,
        /// Zertifikat/Hostschlüssel der Gegenstelle, Richtung, Löschen, Filter, Vergleich, Sperrdatei, Skripte.
        /// </summary>
        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "Group", "NameA", "NameB", "Disabled",
            "Schedule", "ScheduleTime", "Times", "IntervalMinutes", "CatchUp", "SkipOffline", "ScheduledTask",
            "WatchA", "WatchB", "WatchDelay", "TriggerFile", "AvailFolder", "AvailHost", "AvailDrive", "AvailInterval",
            "AvailDelay", "EventMinGap", "EventOnlyChanges"
        };

        /// <summary>Prüfwert über die Einstellungen der Aufgabe und den Inhalt der Skriptdateien, die sie aufruft.</summary>
        public static string Hash(SyncJob j)
        {
            StringBuilder b = new StringBuilder("v2\n");
            Action<string, string> add = delegate (string k, string v)
            {
                v = v ?? "";
                b.Append(k).Append('=').Append(v.Length).Append(':').Append(v).Append('\n');
            };
            List<FieldInfo> fields = new List<FieldInfo>(typeof(SyncJob).GetFields(BindingFlags.Public | BindingFlags.Instance));
            fields.Sort(delegate (FieldInfo x, FieldInfo y) { return string.CompareOrdinal(x.Name, y.Name); });
            foreach (FieldInfo f in fields)
            {
                if (Skip.Contains(f.Name) || f.IsDefined(typeof(XmlIgnoreAttribute), false)) continue;
                object v = f.GetValue(j);
                IEnumerable<string> list = v as IEnumerable<string>;
                if (list != null && !(v is string))
                {
                    StringBuilder l = new StringBuilder();
                    foreach (string e in list) l.Append((e ?? "").Length).Append(':').Append(e).Append('|');
                    add(f.Name, l.ToString());
                }
                else add(f.Name, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            }
            // Skriptdateien: Ein bestätigter Aufruf nützt nichts, wenn die Datei dahinter ausgetauscht werden kann
            foreach (string file in ScriptFiles(j.ScriptBefore, j).Concat(ScriptFiles(j.ScriptAfter, j)))
                add("Datei " + file, file.StartsWith("?") ? "nicht gefunden" : SystemFile(file) ? "Windows/Programme" : FileHash(file));
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(b.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        static readonly string[] ScriptExt = { ".bat", ".cmd", ".ps1", ".exe", ".com", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".py", ".msi" };

        /// <summary>
        /// Dateien, die der Befehl ausführt: absolute Pfade mit bekannter Endung (in Anführungszeichen oder ohne
        /// Leerzeichen) und der aufgerufene Befehl selbst (erstes Wort, nach „call“/„start“ das nächste), so aufgelöst
        /// wie cmd.exe es tut: Arbeitsordner, dann PATH mit den Endungen aus PATHEXT.
        /// </summary>
        public static List<string> ScriptFiles(string cmd, SyncJob job)
        {
            List<string> r = new List<string>();
            if (string.IsNullOrEmpty(cmd)) return r;
            string expanded = Environment.ExpandEnvironmentVariables(cmd.Trim());
            foreach (Match m in Regex.Matches(expanded, "\"([^\"]+)\"|((?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s\"]+)"))
            {
                string p = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                string ext;
                try { ext = Path.GetExtension(p).ToLowerInvariant(); } catch { continue; }
                if (Array.IndexOf(ScriptExt, ext) < 0) continue;
                if (!(p.Length > 2 && p[1] == ':') && !p.StartsWith("\\\\")) continue;
                if (!r.Contains(p)) r.Add(p);
            }
            // aufgerufene Befehle (auch relativ, z. B. „sichern.bat“ oder „net“): das erste Wort jedes Teilbefehls –
            // cmd.exe führt die ganze Zeile aus, also auch alles nach &, &&, ||, | und in Klammern
            List<string> words = Words(expanded);
            string workDir = null;
            bool workDirKnown = false;
            bool commandNext = true;
            foreach (string raw in words)
            {
                string word = raw;
                if (word == "&" || word == "|" || word == "(" || word == ")") { commandNext = true; continue; }
                if (word == "<" || word == ">") { commandNext = false; continue; }
                if (!commandNext) continue;
                if (word.StartsWith("@")) word = word.Substring(1);
                if (word.Length == 0) continue;
                if (word.Equals("call", StringComparison.OrdinalIgnoreCase) || word.Equals("start", StringComparison.OrdinalIgnoreCase) ||
                    word == "\"\"" || word.StartsWith("/")) continue; // Befehl folgt erst danach
                commandNext = false;
                if (!workDirKnown)
                {
                    try { workDir = Scripts.WorkDir(Scripts.QuotePaths(cmd.Trim()), job); } catch { workDir = null; }
                    workDirKnown = true;
                }
                string found = Resolve(word.Trim('"'), workDir);
                if (found != null) { if (!r.Contains(found)) r.Add(found); }
                else if (!r.Contains("?" + word)) r.Add("?" + word); // nicht gefunden bzw. eingebaut: der Name zählt mit
            }
            return r;
        }

        static List<string> Words(string s)
        {
            List<string> w = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                int start = i;
                if (s[i] == '"') { i = s.IndexOf('"', i + 1); i = i < 0 ? s.Length : i + 1; }
                else while (i < s.Length && !char.IsWhiteSpace(s[i]) && "&|<>".IndexOf(s[i]) < 0) i++;
                if (i == start) i++; // Sonderzeichen
                w.Add(s.Substring(start, i - start));
                if (w.Count > 200) break;
            }
            return w;
        }

        /// <summary>Befehl wie cmd.exe auflösen (Arbeitsordner, dann PATH; ohne Endung mit PATHEXT). null = nicht gefunden bzw. eingebaut.</summary>
        static string Resolve(string name, string workDir)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            List<string> exts = new List<string>();
            if (Path.HasExtension(name)) exts.Add("");
            foreach (string e in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';'))
                if (e.Length > 0) exts.Add(e);
            List<string> dirs = new List<string>();
            if (Path.IsPathRooted(name)) dirs.Add("");
            else
            {
                if (!string.IsNullOrEmpty(workDir)) dirs.Add(workDir);
                foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                    if (d.Trim().Length > 0) dirs.Add(Environment.ExpandEnvironmentVariables(d.Trim().Trim('"')));
            }
            foreach (string d in dirs)
                foreach (string e in exts)
                {
                    try
                    {
                        string p = d.Length == 0 ? name + e : Path.Combine(d, name + e);
                        if (File.Exists(p)) return Path.GetFullPath(p);
                    }
                    catch { }
                }
            return null;
        }

        /// <summary>
        /// Datei unter Windows bzw. „Programme“ (nur Administratoren schreiben dort): nur der Pfad zählt, nicht der Inhalt –
        /// sonst passte der Prüfwert nach jedem Windows-Update von z. B. net.exe nicht mehr.
        /// </summary>
        static bool SystemFile(string path)
        {
            foreach (Environment.SpecialFolder f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string d = Environment.GetFolderPath(f);
                if (d.Length > 3 && path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string FileHash(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return "fehlt";
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            }
            catch (Exception ex) { return "unlesbar:" + ex.GetType().Name; }
        }

        static RegistryKey Base() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

        /// <summary>Gespeicherter Prüfwert oder null (noch nie bestätigt).</summary>
        public static string Stored(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                using (RegistryKey root = Base())
                using (RegistryKey k = root.OpenSubKey(KeyPath, false))
                    return k == null ? null : k.GetValue(id) as string;
            }
            catch { return null; }
        }

        public static bool Valid(SyncJob j)
        {
            string s = Stored(j.Id);
            return s != null && string.Equals(s, Hash(j), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Braucht diese Aufgabe einen Prüfwert? Ja, wenn sie mit Administratorrechten laufen kann.</summary>
        public static bool Needed(SyncJob j) { return j.RunAsAdmin || Elevation.IsAdmin; }

        /// <summary>
        /// Vor jedem Lauf mit Administratorrechten (Zeitplan, von Hand, vom Handy, bei Ereignissen): Prüfwert vergleichen.
        /// Ohne Administratorrechte gibt es nichts zu schützen.
        /// </summary>
        public static void Check(SyncJob j)
        {
            if (j.Prepared || Demo.Active || !Elevation.IsAdmin) return;
            // Skripte nur aus geschützten Ordnern: Sonst könnte ein Programm ohne Administratorrechte die Datei genau
            // zwischen Prüfung und Start austauschen – der Prüfwert allein schließt dieses Zeitfenster nicht
            string unsafeFile = UnprotectedScript(j);
            if (unsafeFile != null) throw new SyncException(UnprotectedMessage(unsafeFile));
            if (Valid(j)) return;
            throw new SyncException(Stored(j.Id) == null
                ? "Diese Aufgabe ist für Administratorrechte noch nicht bestätigt. Bitte in Sync Pilot öffnen, prüfen und speichern."
                : "Die Aufgabe oder eine ihrer Skriptdateien wurde seit der Bestätigung geändert und läuft deshalb nicht mit " +
                  "Administratorrechten. Bitte in Sync Pilot öffnen, Einstellungen und Skripte prüfen und speichern.");
        }

        // ------------------------------------------------------------------ Geschützte Ordner

        static readonly HashSet<string> TrustedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S-1-5-32-544",  // Administratoren
            "S-1-5-18",      // SYSTEM
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
            "S-1-3-0"        // ERSTELLER-BESITZER (gilt nur für Neues, der Besitzer wird separat geprüft)
        };

        const FileSystemRights FileWrite = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
                                           FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const FileSystemRights DirWrite = FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
                                          FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const FileSystemRights RootWrite = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        /// <summary>
        /// Erstes Skript bzw. Programm der Aufgabe, das in einem ungeschützten Ordner liegt (oder selbst ohne
        /// Administratorrechte änderbar ist), sonst null. Geschützt heißt: Besitzer und Schreibrechte nur bei
        /// Administratoren, SYSTEM bzw. TrustedInstaller – für die Datei und alle Ordner darüber.
        /// Dazu die EXE von Sync Pilot bei Aufgaben „Als Administrator ausführen“ (startet nach Zeitplan erhöht).
        /// </summary>
        public static string UnprotectedScript(SyncJob j)
        {
            foreach (string f in ScriptFiles(j.ScriptBefore, j).Concat(ScriptFiles(j.ScriptAfter, j)))
            {
                if (f.StartsWith("?")) continue; // eingebauter Befehl (del, copy …) bzw. nicht vorhanden: nichts auszutauschen
                if (!Protected(f)) return f;
            }
            if (j.RunAsAdmin)
            {
                string exe = System.Windows.Forms.Application.ExecutablePath;
                // Projektordner (Erstellen.bat neben der EXE) beim Entwickeln nicht sperren
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(exe), "Erstellen.bat")) && !Protected(exe)) return exe;
            }
            return null;
        }

        public static string UnprotectedMessage(string file)
        {
            string dir = Path.GetDirectoryName(file) ?? file;
            return "„" + file + "“ liegt in einem Ordner, in den auch Programme ohne Administratorrechte schreiben dürfen. " +
                   "Mit Administratorrechten startet Sync Pilot nur Skripte und Programme aus geschützten Ordnern " +
                   "(z. B. unter „Programme“). Ordner schützen (Eingabeaufforderung als Administrator): " +
                   "icacls \"" + dir + "\" /setowner *S-1-5-32-544 /T  und  icacls \"" + dir +
                   "\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-11:(OI)(CI)RX";
        }

        /// <summary>Datei und alle Ordner darüber nur von Administratoren änderbar? Im Zweifel (nicht lesbar) nein.</summary>
        static bool Protected(string file)
        {
            try
            {
                if (file.StartsWith("\\\\")) return false; // Netzwerkfreigabe: Rechte auf dem Server nicht prüfbar
                if (!Safe(File.GetAccessControl(file), FileWrite)) return false;
                DirectoryInfo d = new FileInfo(file).Directory;
                while (d != null)
                {
                    bool root = d.Parent == null;
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return false; // über Verknüpfung: Ziel kann wechseln
                    if (!Safe(Directory.GetAccessControl(d.FullName), root ? RootWrite : DirWrite)) return false;
                    d = d.Parent;
                }
                return true;
            }
            catch { return false; }
        }

        static bool Safe(FileSystemSecurity sec, FileSystemRights dangerous)
        {
            // Der Besitzer darf die Rechte immer ändern: muss also selbst vertrauenswürdig sein
            SecurityIdentifier owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !TrustedSids.Contains(owner.Value)) return false;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue; // gilt nur für Unterobjekte
                if ((rule.FileSystemRights & dangerous) == 0) continue;
                if (!TrustedSids.Contains(rule.IdentityReference.Value)) return false;
            }
            return true;
        }

        /// <summary>PowerShell-Zeilen, die die Prüfwerte schreiben (laufen mit Administratorrechten).</summary>
        public static string Script(IEnumerable<SyncJob> jobs)
        {
            StringBuilder s = new StringBuilder();
            s.Append("$sealRoot = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine','Registry64')\r\n");
            s.Append("$sealKey = $sealRoot.CreateSubKey(" + Ps(KeyPath) + ")\r\n");
            foreach (SyncJob j in jobs)
                s.Append("$sealKey.SetValue(" + Ps(j.Id) + ", " + Ps(Hash(j)) + ")\r\n");
            s.Append("$sealKey.Close(); $sealRoot.Close()\r\n");
            return s.ToString();
        }

        /// <summary>
        /// Prüfwerte speichern: mit eigenen Administratorrechten direkt, sonst über ein kurzes Skript mit Administratorrechten
        /// (Windows fragt einmal nach). Liefert null bei Erfolg, sonst eine Meldung.
        /// </summary>
        public static string Save(ICollection<SyncJob> jobs)
        {
            if (jobs.Count == 0 || Demo.Active) return null;
            if (Elevation.IsAdmin)
            {
                try
                {
                    using (RegistryKey root = Base())
                    using (RegistryKey k = root.CreateSubKey(KeyPath))
                        foreach (SyncJob j in jobs) k.SetValue(j.Id, Hash(j));
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }
            int rc = RunElevated("$ErrorActionPreference = 'Stop'\r\ntry {\r\n" + Script(jobs) + "exit 0\r\n} catch { exit 1 }");
            if (rc == 0) return null;
            return rc == -1 ? "Die Bestätigung von Windows (Administratorrechte) fehlt." : "Der Prüfwert konnte nicht gespeichert werden (Fehler " + rc + ").";
        }

        /// <summary>Nach dem Speichern einer Aufgabe: Prüfwert nachziehen, wenn nötig.</summary>
        public static string Ensure(SyncJob j)
        {
            if (!Needed(j) || Valid(j)) return null;
            return Save(new[] { j });
        }

        /// <summary>Text als PowerShell-Zeichenkette in einfachen Anführungszeichen (auch typografische einfache Anführungszeichen verdoppeln).</summary>
        public static string Ps(string text)
        {
            StringBuilder b = new StringBuilder("'");
            foreach (char c in text ?? "")
            {
                if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛') b.Append(c);
                b.Append(c);
            }
            return b.Append('\'').ToString();
        }

        /// <summary>PowerShell über den festen Pfad (nie über den Suchpfad).</summary>
        public static string PowerShellExe
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"); }
        }

        /// <summary>
        /// Skript mit Administratorrechten ausführen, direkt in der Befehlszeile (-EncodedCommand) statt als Datei – zwischen
        /// Schreiben und Start gibt es so nichts, was sich austauschen ließe. Rückgabewert oder -1 (abgelehnt/Fehler).
        /// </summary>
        public static int RunElevated(string script)
        {
            if (!File.Exists(PowerShellExe)) return -2;
            return Elevation.RunAndWait(PowerShellExe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
    }
}

// Sync Pilot – NTFS-Rechte, Sperrdatei und Skripte vor/nach dem Kopieren
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace SyncPilot
{
    /// <summary>NTFS-Zugriffsrechte übertragen (nur zwischen lokalen Laufwerken und Windows-Freigaben).</summary>
    public static class Rights
    {
        /// <summary>
        /// Übernimmt die ausdrücklich gesetzten Rechte und den Vererbungsschutz der Quelle. Vererbte Rechte kommen auf der
        /// Zielseite vom eigenen Oberordner. Fehler (z. B. fehlende Berechtigung, FAT-Laufwerk, NAS ohne NTFS-Rechte)
        /// stehen nur als Warnung im Protokoll – die Datei selbst ist ja kopiert.
        /// </summary>
        public static void Copy(IFileSide from, IFileSide to, string rel, bool isDir, Logger log)
        {
            LocalSide src = from as LocalSide, dst = to as LocalSide;
            if (src == null || dst == null) return;
            try
            {
                string s = src.Full(rel), d = dst.Full(rel);
                if (isDir)
                {
                    DirectorySecurity ss = Directory.GetAccessControl(s, AccessControlSections.Access);
                    DirectorySecurity ns = new DirectorySecurity();
                    ns.SetAccessRuleProtection(ss.AreAccessRulesProtected, false);
                    foreach (FileSystemAccessRule r in ss.GetAccessRules(true, false, typeof(SecurityIdentifier))) ns.AddAccessRule(r);
                    Directory.SetAccessControl(d, ns);
                }
                else
                {
                    FileSecurity ss = File.GetAccessControl(s, AccessControlSections.Access);
                    FileSecurity ns = new FileSecurity();
                    ns.SetAccessRuleProtection(ss.AreAccessRulesProtected, false);
                    foreach (FileSystemAccessRule r in ss.GetAccessRules(true, false, typeof(SecurityIdentifier))) ns.AddAccessRule(r);
                    File.SetAccessControl(d, ns);
                }
            }
            catch (Exception ex)
            {
                log.Line("Warnung: Rechte nicht übernommen (" + rel + "): " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Sperrdatei gegen gleichzeitigen Abgleich derselben Ordner von mehreren PCs (wie bei Easy2Sync: solange die Datei
    /// existiert, gilt die Aufgabe als belegt). Eine Sperre, die älter als StaleHours ist, gilt als verwaist
    /// (z. B. PC abgestürzt) und wird übernommen.
    /// </summary>
    public sealed class LockFiles : IDisposable
    {
        public const int StaleHours = 6;
        static readonly Dictionary<string, int> held = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly string path;

        LockFiles(string path) { this.path = path; }

        static string PathOf(SyncJob job)
        {
            if (job == null || string.IsNullOrEmpty(job.LockFile)) return null;
            try { return Paths.Resolve(job.LockFile.Trim().Trim('"')); }
            catch { return job.LockFile.Trim().Trim('"'); }
        }

        static bool HeldHere(string p)
        {
            lock (held) return held.ContainsKey(p);
        }

        /// <summary>Wer sperrt gerade (null = frei, verwaist oder von diesem Programm selbst gesperrt)?</summary>
        static string Owner(string p)
        {
            if (HeldHere(p)) return null;
            try
            {
                FileInfo fi = new FileInfo(p);
                if (!fi.Exists) return null;
                if (DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromHours(StaleHours)) return null;
                string who = null, since = fi.LastWriteTime.ToString("dd.MM.yyyy HH:mm");
                try
                {
                    foreach (string line in File.ReadAllLines(p))
                        if (line.StartsWith("Rechner=", StringComparison.OrdinalIgnoreCase)) who = line.Substring(8).Trim();
                }
                catch { }
                return (string.IsNullOrEmpty(who) ? "ein anderes Programm oder ein anderer PC" : who) + " (seit " + since + ")";
            }
            catch { return null; }
        }

        /// <summary>Vor der Analyse: wirft SyncException, wenn gerade ein anderer PC diese Aufgabe abgleicht.</summary>
        public static void CheckFree(SyncJob job)
        {
            string p = PathOf(job);
            if (p == null) return;
            string who = Owner(p);
            if (who != null)
                throw new SyncException("Gesperrt: Diese Ordner werden gerade von " + who + " abgeglichen (Sperrdatei " + p + "). Bitte später erneut versuchen.");
        }

        /// <summary>
        /// Sperre holen. wait: bei belegter Sperre bis zu waitMinutes warten (automatische Läufe), sonst sofort SyncException.
        /// Liefert null, wenn die Aufgabe keine Sperrdatei hat. Mehrfach im selben Programm geholt = nur gezählt.
        /// </summary>
        public static LockFiles Acquire(SyncJob job, bool wait, int waitMinutes, Func<bool> cancel, Action<string> status, Logger log)
        {
            string p = PathOf(job);
            if (p == null) return null;
            lock (held)
            {
                int n;
                if (held.TryGetValue(p, out n)) { held[p] = n + 1; return new LockFiles(p); }
            }
            DateTime until = DateTime.UtcNow.AddMinutes(waitMinutes);
            bool waited = false;
            while (true)
            {
                string who = Owner(p);
                if (who == null)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(p);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        // verwaiste Sperre ersetzen
                        if (File.Exists(p)) { File.Delete(p); if (log != null) log.Line("Verwaiste Sperrdatei ersetzt: " + p); }
                        using (FileStream fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        using (StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)))
                        {
                            w.WriteLine("[Sync Pilot]");
                            w.WriteLine("Rechner=" + Environment.MachineName);
                            w.WriteLine("Benutzer=" + Environment.UserName);
                            w.WriteLine("Aufgabe=" + job.Name);
                            w.WriteLine("Start=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        }
                        lock (held) held[p] = 1;
                        if (log != null) log.Line("Sperrdatei angelegt: " + p);
                        return new LockFiles(p);
                    }
                    catch (IOException)
                    {
                        // gleichzeitig von einem anderen PC angelegt: wie belegt behandeln
                        if (!File.Exists(p)) throw;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        throw new SyncException("Sperrdatei kann nicht angelegt werden (" + p + "): " + ex.Message);
                    }
                    who = Owner(p) ?? "einem anderen PC";
                }
                if (!wait || DateTime.UtcNow > until)
                    throw new SyncException("Gesperrt: Diese Ordner werden gerade von " + who + " abgeglichen (Sperrdatei " + p + ")." +
                        (waited ? " Wartezeit abgelaufen." : ""));
                if (!waited && log != null) log.Line("Sperrdatei belegt von " + who + " – warte …");
                waited = true;
                if (status != null) status("Warte auf Sperrdatei (" + who + ") …");
                for (int i = 0; i < 15; i++)
                {
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    Thread.Sleep(1000);
                }
            }
        }

        public void Dispose()
        {
            lock (held)
            {
                int n;
                if (!held.TryGetValue(path, out n)) return;
                if (n > 1) { held[path] = n - 1; return; }
                held.Remove(path);
            }
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Befehl bzw. Skript vor oder nach dem Kopieren (cmd.exe, Ausgabe ins Protokoll).</summary>
    public static class Scripts
    {
        /// <summary>Führt den Befehl aus und liefert den Rückgabewert (-1, wenn er nicht gestartet werden konnte).</summary>
        public static int Run(string command, SyncJob job, string when, string result, Func<bool> cancel, Logger log)
        {
            return Run(command, job, when, result, cancel, log, null);
        }

        /// <summary>onLine: jede Ausgabezeile (für die Fortschrittsanzeige).</summary>
        public static int Run(string command, SyncJob job, string when, string result, Func<bool> cancel, Logger log, Action<string> onLine)
        {
            string cmd = (command ?? "").Trim();
            if (cmd.Length == 0) return 0;
            log.Line("Skript " + when + ": " + cmd);
            cmd = QuotePaths(cmd);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + cmd + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Encoding oem;
                try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { oem = Encoding.Default; }
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
                psi.WorkingDirectory = WorkDir(cmd, job);
                psi.EnvironmentVariables["SYNCPILOT_AUFGABE"] = job.Name ?? "";
                psi.EnvironmentVariables["SYNCPILOT_A"] = Paths.TryResolve(job.FolderA);
                psi.EnvironmentVariables["SYNCPILOT_B"] = Paths.TryResolve(job.FolderB);
                psi.EnvironmentVariables["SYNCPILOT_ERGEBNIS"] = result ?? "";
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    DataReceivedEventHandler line = delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        log.Line("Skript:   " + e.Data);
                        if (onLine != null && e.Data.Trim().Length > 0) { try { onLine(e.Data.Trim()); } catch { } }
                    };
                    p.OutputDataReceived += line;
                    p.ErrorDataReceived += line;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    while (!p.WaitForExit(500))
                    {
                        if (cancel != null && cancel())
                        {
                            KillTree(p.Id);
                            try { p.Kill(); } catch { }
                            log.Line("Skript abgebrochen.");
                            return -1;
                        }
                    }
                    p.WaitForExit(); // restliche Ausgabe abholen
                    log.Line("Skript beendet, Rückgabewert " + p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                log.Line("Skript konnte nicht gestartet werden: " + ex.Message);
                return -1;
            }
        }

        /// <summary>Abbrechen: auch die vom Skript gestarteten Programme beenden (z. B. powershell, robocopy).</summary>
        static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo k = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + pid + " /T /F");
                k.UseShellExecute = false;
                k.CreateNoWindow = true;
                using (Process t = Process.Start(k)) t.WaitForExit(5000);
            }
            catch { }
        }

        /// <summary>
        /// Programm- bzw. Skriptpfade mit Leerzeichen ohne Anführungszeichen (z. B. C:\Skripte\Backup umbenennen.bat)
        /// in Anführungszeichen setzen – je Befehl bei mehreren mit "&amp;". Gesucht wird der längste Anfang, der eine
        /// vorhandene Datei ist; der Rest bleibt als Parameter stehen.
        /// </summary>
        public static string QuotePaths(string cmd)
        {
            List<string> parts = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool q = false;
            foreach (char c in cmd)
            {
                if (c == '"') q = !q;
                if (c == '&' && !q) { parts.Add(cur.ToString()); cur.Clear(); continue; }
                cur.Append(c);
            }
            parts.Add(cur.ToString());
            for (int i = 0; i < parts.Count; i++)
            {
                string p = parts[i];
                string t = p.Trim();
                if (t.Length == 0 || t.StartsWith("\"") || t.IndexOf(' ') < 0) continue;
                int best = -1;
                for (int k = t.Length; k > 0; k--)
                {
                    if (k < t.Length && t[k] != ' ') continue;
                    string cand = t.Substring(0, k).TrimEnd();
                    if (cand.IndexOf(' ') < 0) break; // ohne Leerzeichen braucht es keine Anführungszeichen
                    try
                    {
                        string f = Environment.ExpandEnvironmentVariables(cand);
                        if (Path.IsPathRooted(f) && File.Exists(f)) { best = cand.Length; break; }
                    }
                    catch { }
                }
                if (best < 0) continue;
                string lead = p.Substring(0, p.Length - p.TrimStart().Length), trail = p.Substring(p.TrimEnd().Length);
                parts[i] = lead + "\"" + t.Substring(0, best) + "\"" + t.Substring(best) + trail;
            }
            return string.Join("&", parts);
        }

        /// <summary>Arbeitsordner: Ordner des Skripts, sonst Ordner A (falls lokal), sonst der Programmordner.</summary>
        static string WorkDir(string cmd, SyncJob job)
        {
            try
            {
                string file = cmd.StartsWith("\"") ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1)) : cmd.Split(' ')[0];
                file = Environment.ExpandEnvironmentVariables(file);
                if (file.Length > 2 && Path.IsPathRooted(file) && File.Exists(file)) return Path.GetDirectoryName(file);
            }
            catch { }
            try
            {
                string a = Paths.TryResolve(job.FolderA);
                if (!FtpUrl.IsRemote(job.FolderA) && Directory.Exists(a) && !a.StartsWith("\\\\")) return a;
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    /// <summary>Nach „Alle synchronisieren“: herunterfahren bzw. schlafen legen, vorher 60 Sekunden Countdown.</summary>
    public static class PowerAfter
    {
        public static void Offer(System.Windows.Forms.IWin32Window owner, string action)
        {
            if (string.IsNullOrEmpty(action) || action == "Nichts") return;
            string what = action == "Herunterfahren" ? "Der PC wird heruntergefahren"
                        : action == "Ruhezustand" ? "Der PC geht in den Ruhezustand" : "Der PC geht in den Energiesparmodus";
            using (CountdownForm f = new CountdownForm(what, 60))
            {
                if (f.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
            }
            try
            {
                if (action == "Herunterfahren")
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /t 0");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                }
                else
                    System.Windows.Forms.Application.SetSuspendState(action == "Ruhezustand" ? System.Windows.Forms.PowerState.Hibernate
                        : System.Windows.Forms.PowerState.Suspend, false, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, action + " hat nicht geklappt:\n" + ex.Message, "Sync Pilot",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }
    }

    /// <summary>Kleines Fenster „… in 60 Sekunden“ mit „Jetzt“ und „Abbrechen“ (OK = ausführen).</summary>
    public sealed class CountdownForm : FixedDialog
    {
        int left;
        readonly System.Windows.Forms.Label text;
        readonly System.Windows.Forms.Timer timer;
        readonly string what;

        // gleicher Fensterrahmen und Hintergrund wie alle Dialoge (FixedDialog: normaler Rahmen, feste Größe)
        public CountdownForm(string what, int seconds) : base("Sync Pilot")
        {
            this.what = what;
            left = seconds;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            int p = Theme.P(24);
            ClientSize = new System.Drawing.Size(Theme.P(440), Theme.P(150));
            text = Ui.WrapLabel("", Theme.Body, Theme.Text, Theme.Bg);
            text.SetBounds(p, p, ClientSize.Width - 2 * p, Theme.P(48));
            FlatButton now = new FlatButton("Jetzt", null, BtnKind.Secondary);
            FlatButton stop = new FlatButton("Abbrechen", null, BtnKind.Primary);
            stop.Location = new System.Drawing.Point(ClientSize.Width - p - stop.Width, ClientSize.Height - p - stop.Height);
            now.Location = new System.Drawing.Point(stop.Left - Theme.P(8) - now.Width, stop.Top);
            now.Click += delegate { DialogResult = System.Windows.Forms.DialogResult.OK; };
            stop.Click += delegate { DialogResult = System.Windows.Forms.DialogResult.Cancel; };
            Controls.AddRange(new System.Windows.Forms.Control[] { text, now, stop });
            CancelButton = null;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                left--;
                if (left <= 0) { timer.Stop(); DialogResult = System.Windows.Forms.DialogResult.OK; return; }
                Show_();
            };
            Show_();
            timer.Start();
            KeyPreview = true;
            KeyDown += delegate (object s, System.Windows.Forms.KeyEventArgs e)
            {
                if (e.KeyCode == System.Windows.Forms.Keys.Escape) DialogResult = System.Windows.Forms.DialogResult.Cancel;
            };
        }

        void Show_()
        {
            text.Text = "Alle Aufgaben sind fertig. " + what + " in " + left + (left == 1 ? " Sekunde." : " Sekunden.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
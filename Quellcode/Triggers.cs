// Sync Pilot – Starten bei Ereignissen: Ordnerüberwachung, Trigger-Datei, Verfügbarkeit
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace SyncPilot
{
    /// <summary>Prüft, ob Laufwerk, Freigabe bzw. Server der Ordner einer Aufgabe gerade erreichbar sind.</summary>
    public static class Reach
    {
        /// <summary>null = beide erreichbar, sonst der Grund ("Ordner B: \\NAS\Daten nicht erreichbar").</summary>
        public static string Offline(SyncJob j)
        {
            string r = Side(j.FolderA, j.UserA, j.PassA);
            if (r != null) return "Ordner A: " + r;
            r = Side(j.FolderB, j.UserB, j.PassB);
            return r == null ? null : "Ordner B: " + r;
        }

        // Nur das "Dahinter" prüfen (Laufwerk, Freigabe, Server). Ein fehlender Ordner selbst ist kein Offline-Fall
        // und wird beim Abgleich als Fehler gemeldet.
        static string Side(string path, string user, string pass)
        {
            try
            {
                if (FtpUrl.IsRemote(path))
                {
                    FtpUrl u = FtpUrl.Parse(path);
                    return Port(u.Host, u.Port, 5000) ? null : u.Host + " nicht erreichbar";
                }
                string r;
                try { r = Paths.Resolve(path); }
                catch (Exception ex) { return ex.Message; } // z. B. USB-Laufwerk nicht angeschlossen
                string share = Paths.ShareRoot(r);
                if (share != null)
                {
                    try { NetShare.Connect(r, user, pass); } catch { }
                    return Directory.Exists(share) ? null : share + " nicht erreichbar";
                }
                string root = Path.GetPathRoot(r);
                if (string.IsNullOrEmpty(root)) return null;
                return Directory.Exists(root) ? null : "Laufwerk " + root + " nicht verfügbar";
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static bool Port(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static bool Ping_(string host)
        {
            try
            {
                using (Ping p = new Ping())
                    if (p.Send(host, 1500).Status == IPStatus.Success) return true;
            }
            catch { }
            // manche Geräte antworten nicht auf Ping: Windows-Freigabe (SMB) versuchen
            return Port(host, 445, 1500);
        }
    }

    /// <summary>
    /// Läuft im Programm (auch im Infobereich) und startet Aufgaben bei Ereignissen. Konflikte werden dabei
    /// wie beim Zeitplan übersprungen, das Ergebnis steht im Verlauf.
    /// </summary>
    public static class Triggers
    {
        static readonly object Lock = new object();
        static readonly Dictionary<string, Watcher> Active = new Dictionary<string, Watcher>();
        /// <summary>Letzter bekannter Zustand je Aufgabe und Ressource (bleibt beim Neueinrichten erhalten).</summary>
        internal static readonly Dictionary<string, bool> LastAvail = new Dictionary<string, bool>();

        /// <summary>Nach einem automatisch gestarteten Lauf (im Hintergrund-Thread).</summary>
        public static event Action<SyncJob, HistoryEntry, string> Finished;

        internal static void RaiseFinished(SyncJob j, HistoryEntry h, string reason)
        {
            Action<SyncJob, HistoryEntry, string> f = Finished;
            if (f != null) try { f(j, h, reason); } catch { }
        }

        public static bool SomeBusy
        {
            get { lock (Lock) return Active.Values.Any(w => w.Running); }
        }

        /// <summary>Namen der Aufgaben, deren automatisch gestarteter Abgleich gerade läuft.</summary>
        public static string[] BusyNames
        {
            get { lock (Lock) return Active.Values.Where(w => w.Running).Select(w => w.Job.Name).ToArray(); }
        }

        static string Signature(SyncJob j)
        {
            return string.Join("|", new string[] { j.FolderA, j.FolderB, j.WatchA.ToString(), j.WatchB.ToString(),
                j.TriggerFile ?? "", j.AvailFolder ?? "", j.AvailHost ?? "", j.AvailDrive ?? "", j.AvailInterval.ToString() });
        }

        /// <summary>Überwachung an die aktuellen Aufgaben anpassen (nach jedem Speichern).</summary>
        public static void Apply(IEnumerable<SyncJob> jobs)
        {
            if (Demo.Active) return;
            lock (Lock)
            {
                Dictionary<string, SyncJob> wanted = new Dictionary<string, SyncJob>();
                foreach (SyncJob j in jobs) if (!j.Disabled && j.HasEvents) wanted[j.Id] = j;
                foreach (string id in Active.Keys.ToList())
                {
                    Watcher w = Active[id];
                    SyncJob nj;
                    if (wanted.TryGetValue(id, out nj) && w.Signature == Signature(nj)) { w.Job = nj.Clone(); continue; }
                    w.Stop(false);
                    Active.Remove(id);
                }
                foreach (SyncJob j in wanted.Values)
                {
                    if (Active.ContainsKey(j.Id)) continue;
                    Watcher w = new Watcher(j.Clone(), Signature(j));
                    Active[j.Id] = w;
                    w.Start();
                }
            }
        }

        /// <summary>Beim Beenden: laufende Abgleiche abbrechen und kurz warten.</summary>
        public static void Stop()
        {
            List<Watcher> all;
            lock (Lock)
            {
                all = Active.Values.ToList();
                Active.Clear();
            }
            foreach (Watcher w in all) w.Stop(true);
        }
    }

    sealed class Watcher
    {
        public volatile SyncJob Job;
        public readonly string Signature;
        public volatile bool Running;
        volatile bool stop;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;

        // Ordnerüberwachung
        FileSystemWatcher[] fsw = new FileSystemWatcher[2];
        DateTime[] retryAt = new DateTime[2];
        readonly object changeLock = new object();
        bool pending;
        string pendingWhere;
        DateTime lastChange, ignoreUntil;

        // Trigger-Datei
        DateTime nextFile;
        DateTime blockedStamp; // Datei ließ sich nicht löschen: erst wieder starten, wenn sie sich ändert

        // Verfügbarkeit
        DateTime nextAvail;

        // "Höchstens alle …": zu früher Anlass wird bis dahin zurückgestellt
        string deferredReason;
        DateTime deferUntil, lastRunUtc;

        public Watcher(SyncJob job, string signature)
        {
            Job = job;
            Signature = signature;
        }

        public void Start()
        {
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "Sync Pilot Ereignisse " + Job.Name;
            thread.Start();
        }

        public void Stop(bool wait)
        {
            stop = true;
            wake.Set();
            for (int i = 0; i < 2; i++) DisposeWatcher(i);
            if (wait && thread != null) thread.Join(5000);
        }

        void Loop()
        {
            while (!stop)
            {
                try { Tick(); }
                catch { }
                wake.WaitOne(1000);
            }
            for (int i = 0; i < 2; i++) DisposeWatcher(i);
        }

        void Tick()
        {
            SyncJob job = Job;
            DateTime now = DateTime.UtcNow;

            // 0. Zurückgestellter Anlass ("Höchstens alle …") ist fällig
            string late = null;
            lock (changeLock)
                if (deferredReason != null && (now >= deferUntil || job.EventMinGap <= 0)) { late = deferredReason; deferredReason = null; }
            if (late != null) Run(late);
            if (stop) return;

            // 1. Änderungen in Ordner A/B: erst starten, wenn WatchDelay Sekunden lang Ruhe war
            EnsureWatcher(0, job.WatchA ? job.FolderA : null, now);
            EnsureWatcher(1, job.WatchB ? job.FolderB : null, now);
            string where = null;
            lock (changeLock)
                if (pending && now - lastChange >= TimeSpan.FromSeconds(Math.Max(0, job.WatchDelay)))
                {
                    pending = false;
                    where = pendingWhere;
                }
            if (where != null) Run("Änderung in " + where);
            if (stop) return;

            // 2. Trigger-Datei: existiert sie, löschen und starten
            string tf = (job.TriggerFile ?? "").Trim();
            if (tf.Length > 0 && now >= nextFile)
            {
                nextFile = now.AddSeconds(Math.Max(5, job.AvailInterval));
                string f = Paths.TryResolve(tf);
                if (File.Exists(f))
                {
                    DateTime stamp = File.GetLastWriteTimeUtc(f);
                    if (stamp != blockedStamp)
                    {
                        try { File.Delete(f); }
                        catch { blockedStamp = stamp; }
                        Run("Trigger-Datei " + Path.GetFileName(f));
                    }
                }
            }
            if (stop) return;

            // 3. Verfügbarkeit: starten, sobald Ordner, Computer oder Laufwerk (wieder) da ist
            if (job.HasAvail && now >= nextAvail)
            {
                nextAvail = now.AddSeconds(Math.Max(1, job.AvailInterval));
                string became = CheckAvailability(job);
                if (became != null)
                {
                    // Wartezeit vor dem Abgleich (z. B. bis das Laufwerk fertig eingebunden ist)
                    if (job.AvailDelay > 0 && wake.WaitOne(job.AvailDelay * 1000) && stop) return;
                    if (!stop) Run(became + " ist verfügbar");
                }
            }
        }

        /// <summary>Liefert die Ressource, die gerade verfügbar geworden ist, sonst null.</summary>
        string CheckAvailability(SyncJob job)
        {
            string became = null;
            string[] keys = { "Ordner", "PC", "Laufwerk" };
            string[] values = { job.AvailFolder, job.AvailHost, job.AvailDrive };
            for (int i = 0; i < 3; i++)
            {
                string v = (values[i] ?? "").Trim();
                if (v.Length == 0) continue;
                bool now;
                if (i == 0) now = Directory.Exists(Paths.TryResolve(v));
                else if (i == 1) now = Reach.Ping_(v);
                else now = Paths.FindDrive(v) != null;
                string key = job.Id + "|" + keys[i] + "|" + v.ToLowerInvariant();
                bool before;
                lock (Triggers.LastAvail)
                {
                    // noch unbekannt (Programmstart): zählt als "war nicht da" – ein schon angeschlossenes Laufwerk startet also einmal
                    if (!Triggers.LastAvail.TryGetValue(key, out before)) before = false;
                    Triggers.LastAvail[key] = now;
                }
                if (now && !before && became == null)
                    became = i == 0 ? "Ordner " + v : i == 1 ? "Computer " + v : "Laufwerk " + v;
            }
            return became;
        }

        void EnsureWatcher(int i, string folder, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(folder) || FtpUrl.IsRemote(folder)) { DisposeWatcher(i); return; }
            if (fsw[i] != null || now < retryAt[i]) return;
            retryAt[i] = now.AddSeconds(30);
            string path;
            try { path = Paths.Resolve(folder); }
            catch { return; } // Laufwerk nicht angeschlossen: später erneut versuchen
            if (!Directory.Exists(path)) return;
            FileSystemWatcher w = new FileSystemWatcher(path);
            w.IncludeSubdirectories = true;
            w.InternalBufferSize = 64 * 1024;
            w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            string where = i == 0 ? "Ordner A" : "Ordner B";
            FileSystemEventHandler h = delegate { Changed(where); };
            w.Changed += h;
            w.Created += h;
            w.Deleted += h;
            w.Renamed += delegate { Changed(where); };
            int idx = i;
            w.Error += delegate
            {
                // Puffer voll oder Netzwerk weg: neu einrichten und vorsichtshalber abgleichen
                Changed(where);
                DisposeWatcher(idx);
            };
            w.EnableRaisingEvents = true;
            bool recreated = fswEverCreated[i];
            fsw[i] = w;
            fswEverCreated[i] = true;
            if (recreated) Changed(where); // während der Unterbrechung verpasste Änderungen
        }

        readonly bool[] fswEverCreated = new bool[2];

        void DisposeWatcher(int i)
        {
            FileSystemWatcher w = fsw[i];
            fsw[i] = null;
            retryAt[i] = DateTime.UtcNow.AddSeconds(5);
            if (w == null) return;
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }

        void Changed(string where)
        {
            lock (changeLock)
            {
                // eigene Änderungen beim Abgleich nicht als neuen Anlass werten
                if (Running || DateTime.UtcNow < ignoreUntil) return;
                if (!pending) pendingWhere = where;
                else if (pendingWhere != where) pendingWhere = "Ordner A und B";
                pending = true;
                lastChange = DateTime.UtcNow;
            }
        }

        void Run(string reason)
        {
            SyncJob job = Job;
            if (job.Disabled || stop) return;
            if (Runner.IsRunning(job.Id))
            {
                // läuft gerade (Oberfläche, Handy, Zeitplan): danach erneut versuchen
                lock (changeLock)
                {
                    if (!pending) pendingWhere = reason.StartsWith("Änderung in ") ? reason.Substring(12) : "Ordner A";
                    pending = true;
                    lastChange = DateTime.UtcNow;
                }
                return;
            }
            // Höchstens alle x Minuten: zu früh -> zurückstellen und dann nachholen (Änderungen gehen nicht verloren)
            if (job.EventMinGap > 0)
            {
                DateTime last = lastRunUtc;
                try
                {
                    HistoryEntry lr = Store.LastRun(Store.LoadHistory(), job.Id);
                    if (lr != null && lr.Start.ToUniversalTime() > last) last = lr.Start.ToUniversalTime();
                }
                catch { }
                DateTime next = last.AddMinutes(job.EventMinGap);
                if (DateTime.UtcNow < next)
                {
                    lock (changeLock)
                    {
                        if (deferredReason == null) deferredReason = reason.EndsWith(" (zurückgestellt)") ? reason : reason + " (zurückgestellt)";
                        deferUntil = next;
                    }
                    return;
                }
            }
            // Laufwerk, Freigabe oder Server fehlt: still auslassen
            if (job.SkipOffline && Reach.Offline(job) != null) return;
            Running = true;
            HistoryEntry h;
            try
            {
                h = Runner.Run(job, true, delegate { return stop; }, delegate (string s) { }, job.EventOnlyChanges);
            }
            catch (Exception ex) { h = Runner.Crash(job, true, ex); }
            finally
            {
                lock (changeLock)
                {
                    Running = false;
                    pending = false;
                    ignoreUntil = DateTime.UtcNow.AddSeconds(5);
                }
            }
            if (h == null) return;   // nichts geändert: still ausgelassen
            lastRunUtc = DateTime.UtcNow;
            Triggers.RaiseFinished(job, h, reason);
        }
    }
}

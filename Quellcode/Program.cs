// Sync Pilot – Einstiegspunkt und Kommandozeile
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Sync Pilot")]
[assembly: AssemblyDescription("Ordner abgleichen, sichern und spiegeln")]
[assembly: AssemblyProduct("Sync Pilot")]
[assembly: AssemblyCopyright("© 2026 Günther Schoder")]
// Versionsnummer: erzeugt die Erstellen.bat in Quellcode\VersionInfo.cs (AppInfo.Version)

namespace SyncPilot
{
    static class Program
    {
        // Aufruf:
        //   "Sync Pilot.exe"                  Oberflaeche
        //   "Sync Pilot.exe" /run <ID|Name|Gruppe\Name>   Aufgabe ohne Fenster ausfuehren (Zeitplan)
        //   "Sync Pilot.exe" /runall          alle Aufgaben ohne Fenster ausfuehren
        //   Rueckgabewert: 0 OK, 1 Fehler/Abbruch, 2 nicht gefunden, 3 Aufgaben nicht lesbar, 4 deaktiviert, 5 offline ausgelassen
        //   "Sync Pilot.exe" --nach-update    vom Update-Hilfsskript nach dem Ersetzen der EXE
        //   "Sync Pilot.exe" /sync <ID|Name>  Oberflaeche oeffnen, Aufgabe analysieren und synchronisieren (Verknuepfung)
        //   "Sync Pilot.exe" --screenshots [Zielordner]   Screenshots fuer GitHub aus Beispieldaten
        //   "Sync Pilot.exe" /tray            unsichtbar im Infobereich starten (Autostart)
        //   ... --neustart                    Neustart mit Administratorrechten: auf das Ende der alten Oberfläche warten
        //   "Sync Pilot.exe" /ordner-a <Pfad> Explorer-Kontextmenü: Ordner A merken
        //   "Sync Pilot.exe" /ordner-b <Pfad> Explorer-Kontextmenü: Ordner B, dann Richtung wählen
        //   "Sync Pilot.exe" /ordner-x        Explorer-Kontextmenü: Auswahl aufheben
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Laufzeitdaten einmalig von %APPDATA% nach %LOCALAPPDATA% übernehmen (gilt auch für Läufe ohne Fenster)
            if (!args.Any(x => x.Equals("--screenshots", StringComparison.OrdinalIgnoreCase))) Store.Migrate();
            bool explorer = false, startSync = false;
            if (args.Length > 0)
            {
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "/run" || cmd == "-run")
                    return RunHeadless(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null);
                if (cmd == "/runall" || cmd == "-runall")
                    return RunHeadless("*");
                if ((cmd == "/sync" || cmd == "-sync") && args.Length > 1)
                {
                    Shortcuts.SetPending(string.Join(" ", args, 1, args.Length - 1).Trim('"'));
                    startSync = true;
                }
                if (cmd == "--screenshots")
                {
                    string dir = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1).Trim('"')
                        : System.IO.Path.Combine(Application.StartupPath, "Screenshots");
                    return Demo.RunScreenshots(dir) ? 0 : 1;
                }
                if (cmd == "/ordner-a" || cmd == "/ordner-b" || cmd == "/ordner-x")
                {
                    Theme.Init();
                    string path = ExplorerMenu.ArgPath(args);
                    if (cmd == "/ordner-x") { ExplorerMenu.Clear(); return 0; }
                    if (cmd == "/ordner-a") { ExplorerMenu.PickA(path); return 0; }
                    if (!ExplorerMenu.PickB(path)) return 0;
                    explorer = true; // weiter wie ein normaler Start, die Oberfläche holt das Ordnerpaar ab
                }
            }

            bool tray = args.Any(x => x.Equals("/tray", StringComparison.OrdinalIgnoreCase));
            bool restarted = args.Any(x => x.Equals("--neustart", StringComparison.OrdinalIgnoreCase));
            // "Immer als Administrator starten": Oberfläche mit Administratorrechten neu starten (einmal, nicht im Kreis)
            // Läuft die Oberfläche schon, holt der Start sie nur nach vorn – dafür keine Abfrage von Windows.
            // Explorer-Menü: Ordnerpaar ist schon gewählt, ein zweiter Start würde erneut fragen.
            if (!restarted && !explorer && !Elevation.IsAdmin && AppSettings.Current.AlwaysAdmin && !GuiRunning() &&
                Elevation.Restart(Elevation.JoinArgs(args)))
                return 0;
            // Neustart durch das Update-Hilfsskript: Ergebnis zeigen
            if (args.Any(x => x.Equals("--nach-update", StringComparison.OrdinalIgnoreCase))) MainForm.AfterUpdate = true;

            // Nur eine Oberfläche: ein zweiter Start holt das laufende Fenster nach vorn
            bool first;
            using (Mutex single = Ipc.Mutex(true, "Local\\SyncPilot_GUI", out first))
            {
                // Neustart nach dem Update bzw. mit Administratorrechten: die alte Oberfläche gibt den Mutex gerade erst frei – darauf warten
                if (!first && (MainForm.AfterUpdate || restarted))
                {
                    try { first = single.WaitOne(30000); }
                    catch (AbandonedMutexException) { first = true; }
                }
                if (!first)
                {
                    if (startSync)
                    {
                        if (!Signal(Shortcuts.EventName)) Signal("Local\\SyncPilot_Show");
                    }
                    else if (!tray)
                    {
                        if (!Signal(explorer ? ExplorerMenu.EventName : "Local\\SyncPilot_Show") && explorer)
                            Signal("Local\\SyncPilot_Show"); // Oberfläche startet gerade erst: sie holt das Paar beim Anzeigen ab
                    }
                    return 0;
                }

                if (!explorer) Theme.Init();
                AppSettings.Current.ApplyAutostart(); // Pfad aktuell halten (falls das Programm verschoben wurde)
                ExplorerMenu.Apply();
                WebServer.Apply();
                Application.Run(new MainForm(tray));
                Triggers.Stop();
                WebServer.Stop();
            }
            return 0;
        }

        static bool GuiRunning()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting("Local\\SyncPilot_GUI", System.Security.AccessControl.MutexRights.Synchronize)) return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch { return true; } // vorhanden, aber ohne Zugriff
        }

        static bool Signal(string name)
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(name)) ev.Set();
                return true;
            }
            catch { return false; }
        }

        static int RunHeadless(string which)
        {
            if (string.IsNullOrEmpty(which)) return 2;
            List<SyncJob> jobs;
            try { jobs = Store.LoadJobs(); }
            catch { return 3; }
            // Rückgabewert: 0 = OK, 1 = Fehler/Abbruch, 2 = nicht gefunden, 3 = Aufgaben nicht lesbar,
            // 4 = Aufgabe deaktiviert (nicht gelaufen), 5 = Ziel offline, ohne Fehler ausgelassen
            bool all = which == "*";
            int errors = 0, found = 0, ran = 0, disabled = 0, offline = 0;
            foreach (SyncJob j in jobs)
            {
                if (!all && !Matches(j, which)) continue;
                found++;
                if (j.Disabled) { disabled++; continue; } // deaktivierte Aufgaben laufen nicht nach Zeitplan
                // Laufwerk, Freigabe oder Server fehlt: ohne Fehlermeldung auslassen (wenn so eingestellt)
                if (j.SkipOffline && Reach.Offline(j) != null) { offline++; continue; }
                ran++;
                HistoryEntry h;
                try { h = Runner.Run(j, true, delegate { return false; }, delegate (string s) { }); }
                catch (Exception ex) { h = Runner.Crash(j, true, ex); }
                if (!h.IsOk) errors++;
            }
            if (found == 0) return 2;
            if (errors > 0) return 1;
            if (offline > 0) return 5;                  // auch bei /runall: mindestens eine Aufgabe fehlte
            if (ran == 0 && disabled > 0 && !all) return 4; // bei /runall sind deaktivierte Aufgaben normal
            return 0;
        }

        /// <summary>/run-Ziel: ID, Name oder "Gruppe\Name" (auch "Gruppe/Name"); Groß-/Kleinschreibung egal.</summary>
        static bool Matches(SyncJob j, string which)
        {
            string w = which.Trim();
            if (string.Equals(j.Id, w, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(j.Name.Trim(), w, StringComparison.OrdinalIgnoreCase)) return true;
            int cut = w.LastIndexOfAny(new[] { '\\', '/' });
            if (cut <= 0 || cut >= w.Length - 1) return false;
            return string.Equals((j.Group ?? "").Trim(), w.Substring(0, cut).Trim(), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(j.Name.Trim(), w.Substring(cut + 1).Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
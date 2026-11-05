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
        //   "Sync Pilot.exe" /run <ID|Name>   Aufgabe ohne Fenster ausfuehren (Zeitplan)
        //   "Sync Pilot.exe" /runall          alle Aufgaben ohne Fenster ausfuehren
        //   "Sync Pilot.exe" --nach-update    vom Update-Hilfsskript nach dem Ersetzen der EXE
        //   "Sync Pilot.exe" /sync <ID|Name>  Oberflaeche oeffnen, Aufgabe analysieren und synchronisieren (Verknuepfung)
        //   "Sync Pilot.exe" --screenshots [Zielordner]   Screenshots fuer GitHub aus Beispieldaten
        //   "Sync Pilot.exe" /tray            unsichtbar im Infobereich starten (Autostart)
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
            // Neustart durch das Update-Hilfsskript: Ergebnis zeigen
            if (args.Any(x => x.Equals("--nach-update", StringComparison.OrdinalIgnoreCase))) MainForm.AfterUpdate = true;

            // Nur eine Oberfläche: ein zweiter Start holt das laufende Fenster nach vorn
            bool first;
            using (Mutex single = new Mutex(true, "Local\\SyncPilot_GUI", out first))
            {
                // Neustart nach dem Update: die alte Oberfläche gibt den Mutex gerade erst frei – darauf warten
                if (!first && MainForm.AfterUpdate)
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
            int errors = 0, found = 0;
            foreach (SyncJob j in jobs)
            {
                bool match = which == "*" || j.Id == which ||
                             string.Equals(j.Name, which, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                found++;
                if (j.Disabled) continue; // deaktivierte Aufgaben laufen nicht nach Zeitplan
                // Laufwerk, Freigabe oder Server fehlt: ohne Fehlermeldung auslassen (wenn so eingestellt)
                if (j.SkipOffline && Reach.Offline(j) != null) continue;
                HistoryEntry h;
                try { h = Runner.Run(j, true, delegate { return false; }, delegate (string s) { }); }
                catch (Exception ex) { h = Runner.Crash(j, true, ex); }
                if (!h.IsOk) errors++;
            }
            if (found == 0) return 2;
            return errors > 0 ? 1 : 0;
        }
    }
}
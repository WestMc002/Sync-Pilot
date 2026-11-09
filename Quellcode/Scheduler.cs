// Sync Pilot – Zeitplan über Windows-Aufgabenplanung bzw. Autostart
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SyncPilot
{
    public static class Scheduler
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string RunValue(SyncJob job) { return "Sync Pilot " + job.Id; }

        static string TaskName(SyncJob job)
        {
            string name = Fmt.SafeFileName(job.Name).Replace("\\", "_");
            return "Sync Pilot\\" + name + " (" + job.Id.Substring(0, 6) + ")";
        }

        /// <summary>Aufruf ohne Fenster über die ID (eindeutig, übersteht Umbenennen).</summary>
        public static string Command(SyncJob job)
        {
            return "\"" + Application.ExecutablePath + "\" /run " + job.Id;
        }

        // Liefert null bei Erfolg, sonst eine Fehlermeldung.
        public static string Apply(SyncJob job)
        {
            Remove(job);
            if (job.Disabled) return null; // deaktiviert: kein Zeitplan
            job.Upgrade(); // ältere Zeitpläne (Täglich, Stündlich) umstellen
            job.NeedsReapply = false;
            string err = null;
            switch (job.Schedule)
            {
                case ScheduleKind.Uhrzeiten:
                case ScheduleKind.Intervall:
                {
                    if (job.Schedule == ScheduleKind.Uhrzeiten && (job.Times == null || job.Times.Count == 0)) break;
                    string tn = TaskName(job);
                    string xml = TaskXml(job);
                    // nicht in %TEMP%: bei Remotedesktop zeigt das auf einen Sitzungsordner (…\Temp\2), den Windows
                    // nach der Abmeldung löscht – dann fehlt der Ordner. Eigener Datenordner, notfalls neu angelegt.
                    string file = Path.Combine(Store.Dir, "SyncPilot_" + job.Id + ".xml");
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        // schtasks erwartet die Aufgabenbeschreibung als UTF-16
                        File.WriteAllText(file, xml, Encoding.Unicode);
                        err = Schtasks("/Create /F /TN \"" + tn + "\" /XML \"" + file + "\"");
                    }
                    catch (Exception ex) { err = ex.Message; }
                    finally { try { File.Delete(file); } catch { } }
                    if (err == null) job.ScheduledTask = tn;
                    break;
                }
                case ScheduleKind.BeiAnmeldung:
                    try
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                            k.SetValue(RunValue(job), Command(job));
                    }
                    catch (Exception ex) { err = ex.Message; }
                    break;
            }
            return err;
        }

        static readonly string[] DayNames = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        static string X(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

        /// <summary>
        /// Aufgabe für die Windows-Aufgabenplanung: mehrere Uhrzeiten mit Wochentagen bzw. Intervall,
        /// "verpassten Start nachholen", ohne Zeitlimit, nicht doppelt starten, auch im Akkubetrieb.
        /// </summary>
        static string TaskXml(SyncJob job)
        {
            StringBuilder t = new StringBuilder();
            DateTime today = DateTime.Today;
            if (job.Schedule == ScheduleKind.Intervall)
            {
                int m = Math.Max(1, job.IntervalMinutes);
                t.Append("<TimeTrigger><Repetition><Interval>PT" + m + "M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>");
                t.Append("<StartBoundary>" + today.ToString("yyyy-MM-dd") + "T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger>");
            }
            else
            {
                foreach (TimeSlot slot in job.Times)
                {
                    string at;
                    if (!TimeSlot.TryParseTime(slot.At, out at) || (slot.Days & 127) == 0) continue;
                    t.Append("<CalendarTrigger><StartBoundary>" + today.ToString("yyyy-MM-dd") + "T" + at + ":00</StartBoundary><Enabled>true</Enabled>");
                    t.Append("<ScheduleByWeek><DaysOfWeek>");
                    for (int d = 0; d < 7; d++) if (slot.Has(d)) t.Append("<" + DayNames[d] + " />");
                    t.Append("</DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger>");
                }
            }
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string exe = Application.ExecutablePath;
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Author>Sync Pilot</Author><Description>" + X("Sync Pilot: " + job.Name) + "</Description></RegistrationInfo>" +
                "<Triggers>" + t + "</Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + X(user) + "</UserId><LogonType>InteractiveToken</LogonType>" +
                "<RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<AllowHardTerminate>true</AllowHardTerminate>" +
                "<StartWhenAvailable>" + (job.CatchUp ? "true" : "false") + "</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
                "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>" +
                "<RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + X(exe) + "</Command><Arguments>" + X("/run " + job.Id) + "</Arguments>" +
                "<WorkingDirectory>" + X(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
        }

        /// <summary>
        /// Wurde die EXE verschoben, zeigen Zeitpläne (Aufgabenplanung, Autostart) noch auf den alten Ort.
        /// Dann alle Zeitpläne neu einrichten. Liefert true, wenn sich Aufgaben geändert haben (speichern).
        /// </summary>
        public static bool RepairAfterMove(List<SyncJob> jobs)
        {
            if (Demo.Active) return false;
            string marker = Path.Combine(Store.Dir, "programmpfad.txt");
            string exe = Application.ExecutablePath;
            string old = null;
            try { if (File.Exists(marker)) old = File.ReadAllText(marker).Trim(); } catch { }
            if (string.Equals(old, exe, StringComparison.OrdinalIgnoreCase)) return false;
            bool changed = false;
            foreach (SyncJob j in jobs)
            {
                if (j.Schedule == ScheduleKind.Manuell) continue;
                Apply(j);
                changed = true;
            }
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.WriteAllText(marker, exe);
            }
            catch { }
            return changed;
        }

        public static void Remove(SyncJob job)
        {
            if (!string.IsNullOrEmpty(job.ScheduledTask))
            {
                Schtasks("/Delete /F /TN \"" + job.ScheduledTask + "\"");
                job.ScheduledTask = null;
            }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null) k.DeleteValue(RunValue(job), false);
            }
            catch { }
        }

        static string Schtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Encoding oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0) return null;
                    string msg = (err + " " + outp).Trim();
                    return msg.Length == 0 ? "schtasks.exe meldet Fehler " + p.ExitCode : msg;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
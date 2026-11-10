// Sync Pilot – Administratorrechte und prozessübergreifende Sperren
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Läuft Sync Pilot mit Administratorrechten? Neustart bzw. Hilfsprogramm mit Administratorrechten.</summary>
    public static class Elevation
    {
        static bool? admin;

        public static bool IsAdmin
        {
            get
            {
                if (admin == null)
                {
                    try { admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                    catch { admin = false; }
                }
                return admin.Value;
            }
        }

        /// <summary>Sync Pilot mit Administratorrechten neu starten (Windows fragt nach). false = abgelehnt oder Fehler.</summary>
        public static bool Restart(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, ((args ?? "") + " --neustart").Trim());
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Application.StartupPath;
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception) { return false; } // 1223: Abfrage abgelehnt
            catch { return false; }
        }

        /// <summary>Programm mit Administratorrechten ausführen und auf das Ende warten. Rückgabewert oder -1 (abgelehnt/Fehler).</summary>
        public static int RunAndWait(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        /// <summary>Argumente für einen Neustart wieder zusammensetzen.</summary>
        public static string JoinArgs(string[] args)
        {
            return string.Join(" ", Array.ConvertAll(args, a => a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 && a.Length > 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""));
        }
    }

    /// <summary>
    /// Benannte Sperren und Signale, die eine Sync-Pilot-Instanz mit und eine ohne Administratorrechte gemeinsam nutzen
    /// können. Ohne eigene Rechte darf die normale Instanz ein Objekt der Admin-Instanz nicht öffnen (Zugriff verweigert).
    /// </summary>
    public static class Ipc
    {
        static SecurityIdentifier User
        {
            get
            {
                try { return WindowsIdentity.GetCurrent().User; }
                catch { return null; }
            }
        }

        public static Mutex Mutex(bool initiallyOwned, string name)
        {
            bool created;
            return Mutex(initiallyOwned, name, out created);
        }

        public static Mutex Mutex(bool initiallyOwned, string name, out bool createdNew)
        {
            SecurityIdentifier u = User;
            if (u == null) return new Mutex(initiallyOwned, name, out createdNew);
            MutexSecurity sec = new MutexSecurity();
            sec.AddAccessRule(new MutexAccessRule(u, MutexRights.FullControl, AccessControlType.Allow));
            try { return new Mutex(initiallyOwned, name, out createdNew, sec); }
            catch (UnauthorizedAccessException)
            {
                // von einer älteren Version (ohne Freigabe) mit Administratorrechten angelegt: nur zum Warten öffnen
                createdNew = false;
                return System.Threading.Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify);
            }
        }

        public static EventWaitHandle Event(string name)
        {
            SecurityIdentifier u = User;
            bool created;
            if (u == null) return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            EventWaitHandleSecurity sec = new EventWaitHandleSecurity();
            sec.AddAccessRule(new EventWaitHandleAccessRule(u, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            return new EventWaitHandle(false, EventResetMode.AutoReset, name, out created, sec);
        }
    }
}

// Sync Pilot – Windows-Firewall-Freigabe für den Handy-Zugriff
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Windows-Firewall: Freigabe "Sync Pilot (Handy)" für den Port prüfen und (mit Administratorrechten) anlegen.</summary>
    internal static class Firewall
    {
        public const string RuleName = "Sync Pilot (Handy)";

        /// <summary>null = alles in Ordnung (oder nicht prüfbar), sonst eine kurze Beschreibung des Problems.</summary>
        public static string Problem(int port)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return null;
                dynamic policy = Activator.CreateInstance(t);
                int current = policy.CurrentProfileTypes;
                bool active = false;
                foreach (int profile in new[] { 1, 2, 4 })
                {
                    if ((current & profile) == 0) continue;
                    object on = t.InvokeMember("FirewallEnabled", System.Reflection.BindingFlags.GetProperty, null, (object)policy, new object[] { profile });
                    if (on is bool && (bool)on) active = true;
                }
                if (!active) return null; // Windows-Firewall aus (z. B. anderes Firewall-Programm)
                dynamic rule;
                try { rule = policy.Rules.Item(RuleName); }
                catch { return "Die Windows-Firewall-Freigabe fehlt"; }
                bool enabled = rule.Enabled;
                int action = rule.Action, direction = rule.Direction, profiles = rule.Profiles;
                string ports = rule.LocalPorts;
                string app = rule.ApplicationName;
                if (!enabled || action != 1 || direction != 1) return "Die Firewall-Freigabe ist ausgeschaltet";
                if (ports != port.ToString()) return "Die Firewall-Freigabe gilt für einen anderen Port";
                if (!string.IsNullOrEmpty(app) && !string.Equals(app, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    return "Die Firewall-Freigabe gilt für einen anderen Programmordner";
                if ((current & 4) != 0 && (profiles & 4) == 0)
                    return "Das Netzwerk ist in Windows als „Öffentlich“ eingestuft – bitte auf „Privat“ umstellen";
                return null;
            }
            catch
            {
                return "Die Windows-Firewall konnte nicht geprüft werden";
            }
        }

        /// <summary>Freigabe (neu) anlegen – mit Administratorrechten (Windows fragt nach). Rückgabe: Fehlertext oder null.</summary>
        public static string Allow(int port)
        {
            string exe = Application.ExecutablePath;
            if (exe.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%' }) >= 0)
                return "Der Programmpfad enthält Sonderzeichen – bitte die Freigabe von Hand anlegen (TCP-Port " + port + ").";
            string inner = "netsh advfirewall firewall delete rule name=\"" + RuleName + "\" >nul 2>&1 & " +
                "netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow protocol=TCP localport=" + port +
                " profile=private,domain program=\"" + exe + "\" enable=yes";
            var psi = new ProcessStartInfo("cmd.exe", "/s /c \"" + inner + "\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "Die Freigabe konnte nicht angelegt werden.";
                    p.WaitForExit(30000);
                    if (!p.HasExited || p.ExitCode != 0) return "Die Freigabe konnte nicht angelegt werden (netsh).";
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return "Freigabe abgebrochen (keine Administratorrechte erteilt).";
                return "Die Freigabe konnte nicht angelegt werden: " + ex.Message;
            }
            return null;
        }
    }
}

// Sync Pilot – Windows-Firewall-Freigabe für den Handy-Zugriff
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>
    /// Windows-Firewall: Freigabe "Sync Pilot (Handy)" für den Port prüfen und (mit Administratorrechten) anlegen.
    /// Die Freigabe gilt nur für TCP, genau diese EXE, die Profile „Privat“ und „Domäne“ und – ohne die Option
    /// „Auch aus dem Internet“ – nur für Heimnetz- und VPN-Adressen (dieselben Bereiche wie WebAuth.IsLocal).
    /// Der Webserver prüft die Adresse zusätzlich selbst.
    /// </summary>
    internal static class Firewall
    {
        public const string RuleName = "Sync Pilot (Handy)";

        /// <summary>Heimnetz und VPN – dieselben Bereiche, die der Webserver ohne Internet-Option annimmt (WebAuth.IsLocal).</summary>
        static readonly string[] LocalRanges =
        {
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "fc00::/7", "fe80::/10"
        };

        const int TcpProtocol = 6;
        const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;

        /// <summary>Soll die Freigabe auch Adressen aus dem Internet zulassen? (wie der Webserver: nur mit HTTPS)</summary>
        static bool Internet { get { return AppSettings.Current.WebInternet && AppSettings.Current.WebHttps; } }

        /// <summary>null = alles in Ordnung (oder nicht prüfbar), sonst eine kurze Beschreibung des Problems.</summary>
        public static string Problem(int port)
        {
            bool active;
            return Problem(port, out active);
        }

        /// <summary>
        /// Wie Problem(port); windowsActive = die Windows-Firewall ist für mindestens ein aktuelles Netzwerk eingeschaltet
        /// (bei einer anderen Firewall wie ESET meist aus).
        /// </summary>
        public static string Problem(int port, out bool windowsActive)
        {
            windowsActive = false;
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return null;
                dynamic policy = Activator.CreateInstance(t);
                int current = policy.CurrentProfileTypes;
                bool active = false;
                foreach (int profile in new[] { ProfileDomain, ProfilePrivate, ProfilePublic })
                {
                    if ((current & profile) == 0) continue;
                    object on = t.InvokeMember("FirewallEnabled", System.Reflection.BindingFlags.GetProperty, null, (object)policy, new object[] { profile });
                    if (on is bool && (bool)on) active = true;
                }
                if (!active) return null; // Windows-Firewall aus (z. B. anderes Firewall-Programm)
                windowsActive = true;
                // alle Regeln dieses Namens prüfen (von Hand angelegte Doppel eingeschlossen): jede muss passen
                List<object> rules = new List<object>();
                foreach (dynamic r in policy.Rules)
                {
                    string name = r.Name;
                    if (string.Equals(name, RuleName, StringComparison.OrdinalIgnoreCase)) rules.Add(r);
                }
                if (rules.Count == 0) return "Die Windows-Firewall-Freigabe fehlt";
                string problem = null;
                bool usable = false;
                foreach (dynamic rule in rules)
                {
                    string p = RuleProblem(rule, port, current);
                    if (p == null) usable = true;
                    else if (problem == null || p.StartsWith("!")) problem = p;
                }
                // zu weit gefasste Regeln (mit „!“ markiert) zählen immer, sonst genügt eine passende
                if (problem != null && (problem.StartsWith("!") || !usable)) return problem.TrimStart('!');
                return null;
            }
            catch
            {
                return "Die Windows-Firewall konnte nicht geprüft werden";
            }
        }

        /// <summary>Problem einer Regel; „!“ am Anfang = die Regel reicht weiter als vorgesehen.</summary>
        static string RuleProblem(dynamic rule, int port, int current)
        {
            bool enabled = rule.Enabled;
            int action = rule.Action, direction = rule.Direction, profiles = rule.Profiles, protocol = rule.Protocol;
            string ports = rule.LocalPorts;
            string app = rule.ApplicationName;
            string remote = rule.RemoteAddresses;
            bool edge = false;
            try { edge = rule.EdgeTraversal; } catch { }
            if (!enabled || action != 1 || direction != 1) return "Die Firewall-Freigabe ist ausgeschaltet";
            // zu weit: ohne Programmbindung, für alle Protokolle, auch öffentliche Netze, Internet-Adressen, Edge Traversal
            if (string.IsNullOrEmpty(app) || !string.Equals(app, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(app) ? "!Die Firewall-Freigabe gilt für alle Programme – bitte neu anlegen"
                                                 : "Die Firewall-Freigabe gilt für einen anderen Programmordner";
            if (protocol != TcpProtocol)
                return protocol == 256 ? "!Die Firewall-Freigabe gilt für alle Protokolle – bitte neu anlegen" : "Die Firewall-Freigabe gilt nicht für TCP";
            if (ports != port.ToString()) return ports == "*" ? "!Die Firewall-Freigabe gilt für alle Ports – bitte neu anlegen" : "Die Firewall-Freigabe gilt für einen anderen Port";
            if ((profiles & ProfilePublic) != 0) return "!Die Firewall-Freigabe gilt auch in öffentlichen Netzen – bitte neu anlegen";
            if (!Internet)
            {
                if (edge) return "!Die Firewall-Freigabe lässt Verbindungen über das Internet zu (Edge Traversal) – bitte neu anlegen";
                if (!OnlyLocal(remote)) return "!Die Firewall-Freigabe gilt auch für Adressen aus dem Internet – bitte neu anlegen";
            }
            else if (remote != "*") return "Die Firewall-Freigabe gilt nur für das Heimnetz – für den Zugriff aus dem Internet bitte neu anlegen";
            // CurrentProfileTypes enthält die Profile aller Verbindungen: Ein öffentlich eingestufter Zusatzadapter
            // (VPN, Hyper-V, nicht erkanntes Netz) setzt „Öffentlich“ mit. Nur warnen, wenn kein Netz privat bzw. Domäne ist.
            if ((current & (ProfilePrivate | ProfileDomain)) == 0)
                return "Das Netzwerk ist in Windows als „Öffentlich“ eingestuft – bitte auf „Privat“ umstellen";
            return null;
        }

        /// <summary>
        /// Name einer bei Windows angemeldeten Firewall eines anderen Herstellers (z. B. ESET), sonst null. Sie verwaltet
        /// eingehende Verbindungen dann selbst; die Windows-Regel genügt nicht unbedingt.
        /// </summary>
        public static string ThirdParty()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwProducts");
                if (t == null) return null;
                dynamic products = Activator.CreateInstance(t);
                int n = products.Count;
                List<string> names = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    string name = products.Item(i).DisplayName;
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name.Trim())) names.Add(name.Trim());
                }
                return names.Count == 0 ? null : string.Join(", ", names);
            }
            catch { return null; }
        }

        /// <summary>Liegt jeder Eintrag der Remote-Adressen ganz in den Heimnetz-/VPN-Bereichen? Unbekanntes zählt als nein.</summary>
        static bool OnlyLocal(string remote)
        {
            if (string.IsNullOrEmpty(remote)) return false;
            foreach (string raw in remote.Split(','))
            {
                string e = raw.Trim();
                if (e.Length == 0) continue;
                if (e.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase)) continue; // nur das eigene Teilnetz
                byte[] lo, hi;
                if (!Range(e, out lo, out hi)) return false; // "*", Schlüsselwörter wie "Internet", Unlesbares
                bool inside = false;
                foreach (string block in LocalRanges)
                {
                    byte[] blo, bhi;
                    if (Range(block, out blo, out bhi) && blo.Length == lo.Length && Compare(lo, blo) >= 0 && Compare(hi, bhi) <= 0) { inside = true; break; }
                }
                if (!inside) return false;
            }
            return true;
        }

        /// <summary>Eintrag „Adresse“, „Adresse/Maske“, „Adresse/Präfix“ oder „Von-Bis“ als Bereich (Bytes).</summary>
        static bool Range(string e, out byte[] lo, out byte[] hi)
        {
            lo = hi = null;
            IPAddress a, b;
            int dash = e.IndexOf('-');
            if (dash > 0)
            {
                if (!IPAddress.TryParse(e.Substring(0, dash), out a) || !IPAddress.TryParse(e.Substring(dash + 1), out b)) return false;
                lo = a.GetAddressBytes(); hi = b.GetAddressBytes();
                return lo.Length == hi.Length && Compare(lo, hi) <= 0;
            }
            int slash = e.IndexOf('/');
            if (slash < 0)
            {
                if (!IPAddress.TryParse(e, out a)) return false;
                lo = a.GetAddressBytes(); hi = (byte[])lo.Clone();
                return true;
            }
            if (!IPAddress.TryParse(e.Substring(0, slash), out a)) return false;
            byte[] addr = a.GetAddressBytes();
            string m = e.Substring(slash + 1);
            int prefix;
            IPAddress mask;
            if (!int.TryParse(m, out prefix))
            {
                if (a.AddressFamily != AddressFamily.InterNetwork || !IPAddress.TryParse(m, out mask)) return false;
                prefix = 0;
                foreach (byte x in mask.GetAddressBytes()) for (int bit = 7; bit >= 0; bit--) if ((x & (1 << bit)) != 0) prefix++;
            }
            if (prefix < 0 || prefix > addr.Length * 8) return false;
            lo = new byte[addr.Length]; hi = new byte[addr.Length];
            for (int i = 0; i < addr.Length; i++)
            {
                int bits = Math.Max(0, Math.Min(8, prefix - i * 8));
                byte keep = (byte)(bits == 0 ? 0 : 0xFF << (8 - bits));
                lo[i] = (byte)(addr[i] & keep);
                hi[i] = (byte)(addr[i] | ~keep);
            }
            return true;
        }

        static int Compare(byte[] x, byte[] y)
        {
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            return 0;
        }

        /// <summary>Freigabe (neu) anlegen – mit Administratorrechten (Windows fragt nach). Rückgabe: Fehlertext oder null.</summary>
        public static string Allow(int port)
        {
            string exe = Application.ExecutablePath;
            if (exe.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%' }) >= 0)
                return "Der Programmpfad enthält Sonderzeichen – bitte die Freigabe von Hand anlegen (TCP-Port " + port + ").";
            // ohne Internet-Option begrenzt schon die Firewall auf Heimnetz und VPN (der Webserver prüft zusätzlich)
            string remote = Internet ? "any" : string.Join(",", LocalRanges);
            // cmd und netsh über feste Pfade, nie über den Suchpfad
            string netsh = "\"" + Path.Combine(Environment.SystemDirectory, "netsh.exe") + "\"";
            string inner = netsh + " advfirewall firewall delete rule name=\"" + RuleName + "\" >nul 2>&1 & " +
                netsh + " advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow protocol=TCP localport=" + port +
                " remoteip=" + remote + " profile=private,domain edge=no program=\"" + exe + "\" enable=yes";
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + inner + "\"")
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

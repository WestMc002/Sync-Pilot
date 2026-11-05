// Sync Pilot – Verknüpfung, die eine Aufgabe startet
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Windows.Forms;

namespace SyncPilot
{
    public static class Shortcuts
    {
        /// <summary>
        /// Verknüpfung "Name synchronisieren.lnk" anlegen (folder = null: Desktop). Ein Doppelklick öffnet Sync Pilot,
        /// analysiert und synchronisiert gleich (bei Konflikten wartet die Vorschau auf eine Entscheidung).
        /// </summary>
        public static void Create(IWin32Window owner, SyncJob job, string folder)
        {
            try
            {
                if (folder == null) folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string file = Path.Combine(folder, Fmt.SafeFileName(job.Name).Replace("\\", "_") + " synchronisieren.lnk");
                if (File.Exists(file) && MessageBox.Show(owner, "Die Verknüpfung gibt es dort schon:\n" + file + "\n\nErsetzen?", "Sync Pilot",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                string exe = Application.ExecutablePath;
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                try
                {
                    dynamic lnk = shell.CreateShortcut(file);
                    lnk.TargetPath = exe;
                    lnk.Arguments = "/sync " + job.Id;
                    lnk.WorkingDirectory = Path.GetDirectoryName(exe);
                    lnk.IconLocation = exe + ",0";
                    lnk.Description = "Sync Pilot: " + job.Name + " synchronisieren";
                    lnk.Save();
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                MessageBox.Show(owner, "Verknüpfung angelegt:\n" + file + "\n\nEin Doppelklick öffnet Sync Pilot und synchronisiert „" + job.Name + "“.",
                    "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Die Verknüpfung konnte nicht angelegt werden:\n" + ex.Message, "Sync Pilot",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------- Übergabe "/sync <ID>" an die laufende Oberfläche ----------

        public const string EventName = "Local\\SyncPilot_Sync";

        static string PendingFile { get { return Path.Combine(Store.Dir, "starten.txt"); } }

        public static void SetPending(string id)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.WriteAllText(PendingFile, id ?? "");
            }
            catch { }
        }

        /// <summary>Wartende Aufgaben-ID abholen (nur einmal).</summary>
        public static string TakePending()
        {
            try
            {
                if (!File.Exists(PendingFile)) return null;
                bool stale = DateTime.UtcNow - File.GetLastWriteTimeUtc(PendingFile) > TimeSpan.FromMinutes(2);
                string id = File.ReadAllText(PendingFile).Trim();
                File.Delete(PendingFile);
                return id.Length == 0 || stale ? null : id;
            }
            catch { return null; }
        }
    }
}

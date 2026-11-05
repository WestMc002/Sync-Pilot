// Sync Pilot – Programmeinstellungen (Hintergrundbetrieb, Autostart, Handy-Zugriff)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace SyncPilot
{
    [XmlRoot("Einstellungen")]
    public class AppSettings
    {
        /// <summary>Beim Schließen des Fensters im Infobereich weiterlaufen.</summary>
        public bool Background = true;
        /// <summary>Mit Windows starten (im Hintergrund).</summary>
        public bool Autostart;
        /// <summary>Eintrag „Sync Pilot“ im Rechtsklickmenü des Explorers.</summary>
        public bool ExplorerEntry;
        /// <summary>Navigation links angepinnt (immer ausgeklappt); sonst nur Symbole, Ausklappen über ☰.</summary>
        public bool NavPinned = true;
        /// <summary>Protokolle älter als so viele Tage automatisch löschen (0 = nie).</summary>
        public int LogDays = 30;
        /// <summary>Beim Start auf GitHub nach einer neuen Version sehen (höchstens einmal täglich).</summary>
        public bool UpdateCheck = true;
        public DateTime LastUpdateCheck;
        /// <summary>Webseite für Handy und Tablet.</summary>
        public bool WebEnabled;
        public int WebPort = 8090;
        /// <summary>Kennwort der Webseite (mit Secret.Protect verschlüsselt).</summary>
        public string WebPass;
        /// <summary>Auch Verbindungen aus dem Internet annehmen (sonst nur Heimnetz und VPN).</summary>
        public bool WebInternet;
        /// <summary>Verschlüsselt (HTTPS) mit eigenem Zertifikat, z. B. von Let's Encrypt.</summary>
        public bool WebHttps;
        /// <summary>"Speicher" = Windows-Zertifikatspeicher (nach Domain), "Datei" = PFX-Datei.</summary>
        public string CertMode = "Speicher";
        public string CertName;
        public string CertFile;
        /// <summary>Privater Schlüssel zu einer .crt/.pem-Datei (leer bei .pfx/.p12).</summary>
        public string CertKeyFile;
        /// <summary>Kennwort der PFX-Datei (verschlüsselt).</summary>
        public string CertPass;
        /// <summary>Name, unter dem das Handy den PC erreicht (bei HTTPS der Name im Zertifikat).</summary>
        public string WebHost;
        /// <summary>Angemeldete Geräte: "SHA256(Token)|Ablauf-Ticks".</summary>
        public List<string> WebSessions = new List<string>();

        static readonly object Sync = new object();
        static AppSettings _current;

        static string FilePath { get { return Path.Combine(Store.Dir, "einstellungen.xml"); } }

        public static AppSettings Current
        {
            get
            {
                lock (Sync)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        AppSettings s = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(fs);
                        if (s.WebSessions == null) s.WebSessions = new List<string>();
                        if (s.WebPort <= 0 || s.WebPort > 65535) s.WebPort = 8090;
                        return s;
                    }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            if (Demo.Active) return;
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Store.Dir);
                    string tmp = FilePath + ".tmp";
                    using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                    lock (WebSessions)
                        new XmlSerializer(typeof(AppSettings)).Serialize(w, this);
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                }
                catch { }
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Autostart-Eintrag setzen bzw. entfernen (immer mit dem aktuellen Programmpfad).</summary>
        public void ApplyAutostart()
        {
            if (Demo.Active) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (Autostart) k.SetValue("Sync Pilot", "\"" + Application.ExecutablePath + "\" /tray");
                    else k.DeleteValue("Sync Pilot", false);
                }
            }
            catch { }
        }

        public bool HasWebPassword { get { return !string.IsNullOrEmpty(Secret.Unprotect(WebPass)); } }
    }
}
// Sync Pilot – Update-Prüfung über die GitHub-Releases und Selbst-Update
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Neueste Veröffentlichung auf GitHub.</summary>
    public class ReleaseInfo
    {
        /// <summary>ZipUrl ist nur gesetzt, wenn das Release gültig signiert ist (sonst nur die Release-Seite).</summary>
        public string Version, PageUrl, ZipUrl;
        /// <summary>SHA-256 des ZIP aus dem signierten Manifest (Großbuchstaben).</summary>
        public string Sha256;
        /// <summary>Kurzmeldung, warum das Update nicht installiert wird (z. B. „Nicht signiert.“), sonst null.</summary>
        public string Problem;
        /// <summary>Ausführlicher Text zu Problem (für „Details“).</summary>
        public string ProblemDetail;
        /// <summary>Das geprüfte Manifest (Bytes wie auf GitHub); das Update-Skript prüft es selbst noch einmal.</summary>
        public byte[] Manifest;
    }

    /// <summary>
    /// Update-Prüfung und Selbst-Update mit signierten Paketen (Globale Richtlinien › Updates, Vorlage: Dock Pilot).
    /// Abgefragt wird nur die öffentliche GitHub-Schnittstelle ohne Anmeldung; übertragen wird nur die Programmversion im
    /// User-Agent. Installiert wird nur ein Paket, dessen Manifest mit dem Schlüssel des Herausgebers signiert ist.
    /// </summary>
    public static class Updater
    {
        public const string Repository = "WestMc002/Sync-Pilot";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        /// <summary>Arbeitsordner (Temp des Benutzers) für Download und Ergebnis.</summary>
        public static string Folder { get { return Path.Combine(Path.GetTempPath(), "Sync Pilot-Update"); } }
        public static string LogFile { get { return Path.Combine(Folder, "Update.log"); } }

        /// <summary>Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts.</summary>
        const string ExeName = "Sync Pilot.exe", ManualName = "Liesmich.txt";

        static void PrepareTls()
        {
            // .NET 4: TLS 1.2 (3072) für github.com einschalten
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        static string UserAgent { get { return "Sync-Pilot/" + AppInfo.Version; } }

        /// <summary>Kein Fehler: /releases/latest liefert 404 – es gibt nur Vorabversionen (oder noch gar nichts).</summary>
        public const string NoRelease = "Nur Vorabversionen";

        /// <summary>Kein passendes ZIP im Release: dann nur die Release-Seite.</summary>
        public const string NoZip = "Im Release ist keine passende ZIP-Datei.";

        /// <summary>Kurzmeldung für das Info-Fenster, technischer Originaltext in Data["Detail"] (für „Details“).</summary>
        static Exception Brief(string brief, string detail)
        {
            Exception e = new Exception(brief);
            if (!string.IsNullOrEmpty(detail)) e.Data["Detail"] = detail;
            return e;
        }

        public static string AssetName(string version) { return "Sync-Pilot-" + version + ".zip"; }
        public static string ManifestName(string version) { return "Sync-Pilot-" + version + ".manifest"; }

        public static ReleaseInfo GetLatest()
        {
            PrepareTls();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            string json;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    json = reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                HttpWebResponse http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.NotFound) throw new Exception(NoRelease);
                string brief = "Nicht erreichbar.";
                if (http != null && (int)http.StatusCode == 403) brief = "Zu viele Anfragen.";
                else if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ConnectFailure) brief = "Keine Verbindung.";
                else if (ex.Status == WebExceptionStatus.Timeout) brief = "Keine Antwort.";
                throw Brief(brief, ex.Message);
            }
            // als JSON lesen (nicht per Suchmuster: der Release-Text könnte sonst Adressen vortäuschen)
            Dictionary<string, object> data;
            try { data = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception ex) { throw Brief("Antwort unlesbar.", ex.Message); }
            if (data == null) throw new Exception("Antwort unlesbar.");
            ReleaseInfo info = new ReleaseInfo();
            info.PageUrl = ReleasesPage;
            object value;
            string tag = data.TryGetValue("tag_name", out value) && value != null ? Convert.ToString(value).Trim() : "";
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(1);
            Version parsed;
            if (tag.Length == 0 || !System.Version.TryParse(tag, out parsed)) throw new Exception("Antwort unlesbar.");
            info.Version = tag;
            if (data.TryGetValue("html_url", out value) && value != null)
            {
                string page = Convert.ToString(value);
                if (page.StartsWith("https://github.com/" + Repository + "/releases/", StringComparison.OrdinalIgnoreCase)) info.PageUrl = page;
            }
            // ZIP und signiertes Manifest: nur mit genau diesen Namen und nur von der Download-Adresse dieses Repositorys
            string wanted = AssetName(info.Version), wantedManifest = ManifestName(info.Version);
            string prefix = "https://github.com/" + Repository + "/releases/download/";
            string zipUrl = null, manifestUrl = null;
            // Listen kommen je nach Serializer als ArrayList oder object[] an
            if (data.TryGetValue("assets", out value) && value is System.Collections.IEnumerable && !(value is string))
            {
                foreach (object item in (System.Collections.IEnumerable)value)
                {
                    Dictionary<string, object> asset = item as Dictionary<string, object>;
                    if (asset == null || !asset.ContainsKey("name") || !asset.ContainsKey("browser_download_url")) continue;
                    string name = Convert.ToString(asset["name"]);
                    string url = Convert.ToString(asset["browser_download_url"]);
                    if (url == null || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, wanted, StringComparison.Ordinal)) zipUrl = url;
                    else if (string.Equals(name, wantedManifest, StringComparison.Ordinal)) manifestUrl = url;
                }
            }
            if (zipUrl == null)
            {
                info.Problem = NoZip;
                return info; // nur die Release-Seite
            }
            // Die Prüfsumme kommt nur aus dem signierten Manifest, nicht von GitHub: Wer das Release austauscht, kann ohne
            // den privaten Schlüssel keine passende Signatur erzeugen.
            if (manifestUrl == null)
            {
                info.Problem = "Nicht signiert.";
                info.ProblemDetail = "Im Release fehlt die Signatur (" + wantedManifest + "). Sync Pilot installiert nur signierte Updates.";
                return info;
            }
            byte[] manifest;
            try { manifest = DownloadSmall(manifestUrl, 8192); }
            catch (Exception ex)
            {
                info.Problem = "Signatur nicht lesbar.";
                info.ProblemDetail = "Die Signatur konnte nicht geladen werden: " + ex.Message;
                return info;
            }
            string sha, detail;
            string problem = CheckManifest(manifest, info.Version, out sha, out detail);
            if (problem != null)
            {
                info.Problem = problem;
                info.ProblemDetail = detail;
                return info;
            }
            info.ZipUrl = zipUrl;
            info.Sha256 = sha;
            info.Manifest = manifest;
            return info;
        }

        /// <summary>true, wenn remote neuer ist als die laufende Version.</summary>
        public static bool IsNewer(string remote)
        {
            Version a, b;
            if (!System.Version.TryParse(remote, out a) || !System.Version.TryParse(AppInfo.Version, out b)) return false;
            return a > b;
        }

        // ------------------------------------------------------------------ Signatur

        // Öffentliche Schlüssel (RSA 3072) des Herausgebers: Haupt- und Reserveschlüssel (für alle eigenen Programme gleich).
        // Signiert wird mit dem Hauptschlüssel; der Reserveschlüssel ersetzt ihn, falls er verloren geht.
        const string MainKey = "<RSAKeyValue><Modulus>tLtylaHt5SgGCrTrEaM0H3Z/UqIQWSL2c3rCpuhZEYnCyJVDIYFCNuLbZ2OeapsJgCttOnwv5djvmpHTcGV6DeFM059O699xepTE0QWKYYM69sCFG4gunH2xh39bM20q9qWbui1Fyy+nN4cqzy26eR7tzXyClAJSS1zCKBuoF1uAtt2c1f1fcCHXdHsjWXZA6Z8OZeiIOizYSfiVHNIJFr8kSFQfnGRaygwp2ta27Hv0z0oKM6Zrvld/vXkgL2um7X1+GV24IzIVT7J8nxmar//yPoc6ugYhgcLjUMBgjwe/5m4REJQFWwntMF1uj5Jf+ehBFZS87kGtSvTykh7mea71JMdLpvAuhicMaP4T8Fr1uTYCPKR4cuiRI+6uogKVOZN23BuZS5iBj1zZkcmopNOXbQupsOkYp2QwejMHoEV7cEc71ZlRqqJed0vBqO8B5H21mrBGJm3kKHxa2QFzY5BZY7ZcC4EG2TZIkC8eZRNUXXmZOdAWGR+aPKeJg7DZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        const string ReserveKey = "<RSAKeyValue><Modulus>zY/NkGbkd4IZHMMuGty2WLmQ0hm6lEZBql2v84BPf3NT+rpenfLK0kk7aKfRXgFS1G8tz+tHHd58GhioFFhnXN2syFTUus7u5csblitORGoNjTf9Rlicxw6Ut3hX7tkn2u+2PRKE5xOOL+epZV1YvBgr/W67WTt5RZEyfA0Vgn8llhrXaSD32iVNEzkeY5yy1bvQAmoxAk3KjNcD8Uuia9XP9qs/orDO0giIj/xsfY2D7qp/rkhSaIOnt/+gRWHbjhC/PGPga3dmcIMC1C3X0Z/meg6XgI+zWeXOOxFe8jCVRlaeGDoRJlJJGvg6uzIJlLCQeNxnSEufZCD10e+ex2xx75Gp3bqCvBvr49VXEC+pLxQteQMj2V5tuhq23MtIeSV0huaKZ1SPm1kxFjG5vj/11bo2VCkcx9dhfEBDdsucJqdUecbLJp58Y6smM2Ah4Qh7nILSNHHcpWey100qMN3WdwXsfzPVuRtD1c16KgC3TQIov2/2fGQGUxn1Awox</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        static readonly string[] ManifestKeys = { "Programm", "Version", "Datei", "SHA256", "Schluessel", "Erstellt" };

        /// <summary>
        /// Manifest prüfen (UTF-8, LF, feste Reihenfolge, letzte Zeile „Signatur=“ mit RSA-SHA256/PKCS#1 v1.5 über alle
        /// Bytes davor). Rückgabe: null = gültig (dann sha gesetzt), sonst Kurzmeldung; detail erklärt sie.
        /// </summary>
        public static string CheckManifest(byte[] data, string version, out string sha, out string detail)
        {
            sha = null;
            detail = null;
            const string Invalid = "Signatur ungültig.";
            byte[] marker = Encoding.ASCII.GetBytes("\nSignatur=");
            int at = IndexOf(data, marker);
            if (at < 0) { detail = "Im Manifest fehlt die Zeile „Signatur=“."; return Invalid; }
            byte[] signed = new byte[at + 1];
            Array.Copy(data, signed, signed.Length);
            byte[] signature;
            try
            {
                string text = Encoding.ASCII.GetString(data, at + marker.Length, data.Length - at - marker.Length).Trim();
                signature = Convert.FromBase64String(text);
            }
            catch { detail = "Die Signatur im Manifest ist nicht lesbar."; return Invalid; }
            string[] lines;
            try { lines = new UTF8Encoding(false, true).GetString(signed).Split('\n'); }
            catch { detail = "Das Manifest ist kein gültiger UTF-8-Text."; return Invalid; }
            if (lines.Length != ManifestKeys.Length + 1 || lines[ManifestKeys.Length].Length != 0)
            {
                detail = "Das Manifest hat nicht den erwarteten Aufbau.";
                return Invalid;
            }
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < ManifestKeys.Length; i++)
            {
                if (!lines[i].StartsWith(ManifestKeys[i] + "=", StringComparison.Ordinal))
                {
                    detail = "Im Manifest fehlt oder steht falsch: " + ManifestKeys[i] + ".";
                    return Invalid;
                }
                values[ManifestKeys[i]] = lines[i].Substring(ManifestKeys[i].Length + 1);
            }
            string keyName = values["Schluessel"];
            string keyXml = keyName == "Haupt" ? MainKey : keyName == "Reserve" ? ReserveKey : null;
            if (keyXml == null) { detail = "Unbekannter Schlüssel im Manifest: " + keyName; return Invalid; }
            bool ok;
            try
            {
                // PROV_RSA_AES (24): RSA mit SHA-256 auch unter .NET 4
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(new CspParameters(24)))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(keyXml);
                    ok = rsa.VerifyData(signed, "SHA256", signature);
                }
            }
            catch (Exception ex) { detail = "Die Signatur konnte nicht geprüft werden: " + ex.Message; return Invalid; }
            if (!ok)
            {
                detail = "Die Signatur passt nicht zum " + (keyName == "Haupt" ? "Haupt" : "Reserve") +
                         "schlüssel. Das Release wurde nicht vom Herausgeber signiert oder nachträglich verändert.";
                return Invalid;
            }
            // gültig signiert: Inhalt muss genau zu diesem Programm und dieser Version passen
            if (values["Programm"] != "Sync Pilot") { detail = "Das Manifest gehört zu „" + values["Programm"] + "“, nicht zu Sync Pilot."; return Invalid; }
            if (values["Version"] != version) { detail = "Das Manifest nennt Version " + values["Version"] + ", das Release " + version + "."; return Invalid; }
            if (values["Datei"] != AssetName(version)) { detail = "Das Manifest nennt die Datei „" + values["Datei"] + "“."; return Invalid; }
            if (!Regex.IsMatch(values["SHA256"], "^[0-9a-f]{64}$")) { detail = "Die Prüfsumme im Manifest ist ungültig."; return Invalid; }
            sha = values["SHA256"].ToUpperInvariant();
            return null;
        }

        static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        /// <summary>Kleine Datei laden (höchstens max Bytes).</summary>
        static byte[] DownloadSmall(string url, int max)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > max) throw new Exception("Die Datei ist zu groß.");
                }
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------ Installation

        /// <summary>Im Projektordner (mit Erstellen.bat) wird nicht automatisch ersetzt: dort entsteht die EXE beim Bauen.</summary>
        public static string InstallBlocker()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "Erstellen.bat")))
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            return null;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "~update-test.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        static int busy;
        public const string Running = "Ein Update läuft bereits.";

        /// <summary>Läuft gerade ein Update (in diesem Programm oder als Skript nach einem früheren Start)?</summary>
        public static bool Busy { get { return busy != 0 || ScriptRunning(); } }

        static string ExeDirectory { get { return Path.GetDirectoryName(Application.ExecutablePath); } }

        /// <summary>Kennung des Installationsziels (24 Zeichen der SHA-256 des Programmordners) für Sperre und Arbeitsordner.</summary>
        static string TargetId
        {
            get
            {
                string dir = Path.GetFullPath(ExeDirectory).TrimEnd('\\').ToUpperInvariant();
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(dir))).Replace("-", "").Substring(0, 24);
            }
        }

        /// <summary>Sperre des Update-Skripts: sitzungsübergreifend und je Installationsziel.</summary>
        static string LockName { get { return @"Global\SyncPilot-Update-" + TargetId; } }

        static bool ScriptRunning()
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(LockName, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; } // vom Skript mit Administratorrechten oder aus einem anderen Konto angelegt
            catch { return false; }
        }

        /// <summary>
        /// ZIP herunterladen, Prüfsumme aus dem signierten Manifest kontrollieren und das Update-Skript starten. Das Skript
        /// wartet, bis Sync Pilot beendet ist (auch ein gerade laufender Abgleich nach Zeitplan), prüft Signatur und
        /// Prüfsumme selbst noch einmal, ersetzt EXE und Liesmich und startet Sync Pilot neu. Ohne Schreibrechte im
        /// Programmordner läuft es mit Administratorrechten in einem geschützten Arbeitsordner unter ProgramData; den
        /// Neustart übernimmt dann ein zweiter Prozess mit normalen Rechten. Danach muss der Aufrufer das Programm beenden.
        /// </summary>
        public static Task Install(ReleaseInfo release, Action<int> progress) { return Install(release, progress, false); }

        /// <param name="tray">nach dem Update wieder unsichtbar im Infobereich starten (automatisches Update im Hintergrund)</param>
        public static async Task Install(ReleaseInfo release, Action<int> progress, bool tray)
        {
            // nur ein Update zur Zeit: von Hand und automatisch teilen sich diese Sperre; sie bleibt nach dem Start des
            // Skripts gesetzt, weil sich Sync Pilot danach beendet
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new Exception(Running);
            bool started = false;
            try
            {
                await InstallLocked(release, progress, tray);
                started = true;
            }
            finally
            {
                if (!started) Interlocked.Exchange(ref busy, 0);
            }
        }

        static async Task InstallLocked(ReleaseInfo release, Action<int> progress, bool tray)
        {
            if (release.Problem != null) throw Brief(release.Problem, release.ProblemDetail);
            if (string.IsNullOrEmpty(release.ZipUrl) || string.IsNullOrEmpty(release.Sha256) || release.Manifest == null) throw new Exception(NoZip);
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            if (ScriptRunning()) throw new Exception(Running);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            CleanOldAttempts();
            // eigener Arbeitsordner je Versuch: Download, Entpacken und Rückfallkopien kommen sich nie in die Quere
            string attempt = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attempt);
            string zip = Path.Combine(attempt, AssetName(release.Version));
            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                if (progress != null) client.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs e) { progress(e.ProgressPercentage); };
                await client.DownloadFileTaskAsync(new Uri(release.ZipUrl), zip);
            }
            string hash = Sha256Of(zip);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(attempt, true); } catch { }
                throw Brief("Download beschädigt.", "Die Prüfsumme (SHA-256) der heruntergeladenen Datei stimmt nicht mit dem signierten Manifest überein.\r\n" +
                                                    "Erwartet: " + release.Sha256 + "\r\nErhalten: " + hash);
            }
            try { File.Delete(LogFile); } catch { }
            string exe = Application.ExecutablePath;
            // Mit Administratorrechten (Programmordner ohne Schreibrechte oder Sync Pilot läuft selbst als Administrator):
            // eigener, nicht vorhersagbarer Arbeitsordner unter ProgramData, den das Skript nur für Administratoren
            // beschreibbar anlegt. Sonst reicht der Temp-Ordner des Benutzers.
            bool elevated = Elevation.IsAdmin || !CanWrite(Path.GetDirectoryName(exe));
            string work = elevated
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SyncPilot-Update-" + TargetId + "-" + Guid.NewGuid().ToString("N"))
                : attempt;
            // Manifest neben das ZIP legen: Das Skript prüft die Signatur selbst und nimmt die Prüfsumme nur daraus
            string manifestFile = Path.Combine(attempt, ManifestName(release.Version));
            File.WriteAllBytes(manifestFile, release.Manifest);
            int pid = Process.GetCurrentProcess().Id;
            StringBuilder vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            Var(vars, "Zip", zip);
            Var(vars, "Manifest", manifestFile);
            Var(vars, "Exe", exe);
            Var(vars, "Version", release.Version);
            Var(vars, "Sha256", release.Sha256.ToUpperInvariant());
            Var(vars, "AssetName", AssetName(release.Version));
            Var(vars, "MainKey", MainKey);
            Var(vars, "ReserveKey", ReserveKey);
            Var(vars, "LockName", LockName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "Log", LogFile);
            Var(vars, "Work", work);
            Var(vars, "ExeName", ExeName);
            Var(vars, "ManualName", ManualName);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            ProcessStartInfo start = PowerShell(vars + InstallScript);
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Skript mit Administratorrechten starten
            if (elevated && !Elevation.IsAdmin) start.Verb = "runas";
            Process installer;
            try { installer = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
            if (!elevated) return;
            // Neustart: Ein eigener Prozess wartet auf das Ende des Updates, übernimmt das Ergebnis und startet Sync Pilot.
            // Ohne eigene Administratorrechte läuft er mit normalen Rechten – Sync Pilot läuft danach also nicht als
            // Administrator (außer „Immer als Administrator starten“ ist an). Läuft Sync Pilot selbst als Administrator,
            // erbt er die Rechte: dann schreibt und löscht er nichts im Temp-Ordner des Benutzers.
            int installerId = 0;
            try { if (installer != null) installerId = installer.Id; } catch { }
            StringBuilder r = new StringBuilder();
            r.Append("$ProcessId = ").Append(pid).Append("\n");
            r.Append("$Installer = ").Append(installerId).Append("\n");
            Var(r, "Attempt", attempt);
            Var(r, "Exe", exe);
            Var(r, "Log", LogFile);
            Var(r, "Work", work);
            r.Append("$UserFiles = $").Append(Elevation.IsAdmin ? "false" : "true").Append("\n");
            r.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            Process.Start(PowerShell(r + RestartScript));
        }

        /// <summary>Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen.</summary>
        static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    DirectoryInfo di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (di.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { di.Delete(true); } catch { }
                }
            }
            catch { }
        }

        static string Sha256Of(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        /// <summary>PowerShell über den festen Systempfad mit dem Skript als Kommando (-EncodedCommand): Es liegt nirgends als Datei.</summary>
        static ProcessStartInfo PowerShell(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) throw new Exception("Windows PowerShell wurde nicht gefunden.");
            // Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein
            StringBuilder compact = new StringBuilder();
            foreach (string raw in script.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                compact.Append(line).Append('\n');
            }
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(compact.ToString()));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            ProcessStartInfo start = new ProcessStartInfo(ps, arguments);
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.WorkingDirectory = system;
            return start;
        }

        /// <summary>Variable für das Skript als PowerShell-Text in einfachen Anführungszeichen.</summary>
        static void Var(StringBuilder sb, string name, string value)
        {
            sb.Append('$').Append(name).Append(" = '");
            foreach (char c in value ?? "")
            {
                if (c == '\n' || c == '\r') throw new Exception("Ungültiger Pfad für das Update.");
                sb.Append(c);
                // PowerShell nimmt auch typografische einfache Anführungszeichen als Begrenzer: verdoppeln
                if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛') sb.Append(c);
            }
            sb.Append("'\n");
        }

        /// <summary>
        /// Ergebnis des letzten Updates (nach dem Neustart mit --nach-update). Lief Sync Pilot als Administrator, liegt es
        /// nur im geschützten Arbeitsordner unter ProgramData (neuester für dieses Programmverzeichnis).
        /// </summary>
        public static string LastResult()
        {
            try { if (File.Exists(LogFile)) return File.ReadAllText(LogFile, Encoding.UTF8).Trim(); } catch { }
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                DirectoryInfo newest = null;
                foreach (DirectoryInfo d in new DirectoryInfo(root).GetDirectories("SyncPilot-Update-" + TargetId + "-*"))
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && (newest == null || d.LastWriteTimeUtc > newest.LastWriteTimeUtc)) newest = d;
                if (newest != null && newest.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-1))
                {
                    string log = Path.Combine(newest.FullName, "Update.log");
                    if (File.Exists(log)) return File.ReadAllText(log, Encoding.UTF8).Trim();
                }
            }
            catch { }
            return null;
        }

        // Update-Skript. Davor setzt das Programm: $ProcessId, $Zip, $Manifest, $Exe, $Version, $Sha256, $AssetName,
        // $MainKey, $ReserveKey, $LockName, $TargetId, $Log, $Work, $ExeName, $ManualName, $Elevated, $Tray.
        // Nur ASCII-Anführungszeichen verwenden.
        const string InstallScript = @"
$ErrorActionPreference = 'Stop'
$target = Split-Path $Exe -Parent
$workReady = -not $Elevated
$result = $null
$lock = $null
$haveLock = $false
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  # auch andere Sync-Pilot-Prozesse mit derselben EXE abwarten (z. B. ein Abgleich nach Zeitplan, hoechstens 30 Minuten)
  $until = (Get-Date).AddMinutes(30)
  while ((Get-Date) -lt $until) {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $Exe } catch { $false } }
    if (-not $running) { break }
    Start-Sleep -Seconds 2
  }
  Start-Sleep -Milliseconds 500
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('SyncPilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    # alte Arbeitsordner frueherer Updates entfernen: nur eigene (Besitzer Administratoren), keine Verknuepfungen,
    # nur nachweislich inaktive: gleiches Ziel (diese Sperre halten wir) oder anderes Ziel ohne gehaltene Sperre
    # und mindestens eine Stunde unveraendert
    Get-ChildItem -LiteralPath $root -Directory -Filter 'SyncPilot-Update-*' -Force -ErrorAction SilentlyContinue | ForEach-Object {
      try {
        if (-not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
          $o = (Get-Acl -LiteralPath $_.FullName).GetOwner([System.Security.Principal.SecurityIdentifier])
          if ($o -eq $admins -or $o -eq $system) {
            $inactive = $false
            if ($_.Name -match '^SyncPilot-Update-([0-9A-F]{24})-[0-9a-f]{32}$') {
              if ($Matches[1] -ceq $TargetId) { $inactive = $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddMinutes(-10) }
              else {
                $held = $true
                try { $other = $null; $held = [System.Threading.Mutex]::TryOpenExisting('Global\SyncPilot-Update-' + $Matches[1], [ref]$other); if ($other) { $other.Dispose() } } catch { $held = $true }
                $inactive = (-not $held) -and ($_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1))
              }
            } else {
              $inactive = $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1)
            }
            if ($inactive) { [System.IO.Directory]::Delete($_.FullName, $true) }
          }
        }
      } catch {}
    }
    # Arbeitsordner gleich mit Rechten anlegen: schreiben nur SYSTEM und Administratoren, lesen alle angemeldeten Benutzer
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    $sec = New-Object System.Security.AccessControl.DirectorySecurity
    $sec.SetAccessRuleProtection($true, $false)
    $sec.SetOwner($admins)
    foreach ($sid in @($admins, $system)) {
      $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, $none, 'Allow')))
    }
    $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $none, 'Allow')))
    if (Test-Path -LiteralPath $Work) { throw 'Der Arbeitsordner fuer das Update existiert bereits.' }
    [void][System.IO.Directory]::CreateDirectory($Work, $sec)
    $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner fuer das Update ist nicht geschuetzt.' }
    $workReady = $true
    $zipWork = Join-Path $Work 'Update.zip'
    $manifestWork = Join-Path $Work 'Update.manifest'
    Copy-Item -LiteralPath $Zip -Destination $zipWork -Force
    Copy-Item -LiteralPath $Manifest -Destination $manifestWork -Force
  } else {
    $zipWork = $Zip
    $manifestWork = $Manifest
  }
  # Signatur des Manifests selbst pruefen (an der Kopie, die verwendet wird)
  $bytes = [System.IO.File]::ReadAllBytes($manifestWork)
  if ($bytes.Length -gt 8192) { throw 'Das Manifest ist zu gross.' }
  $marker = [System.Text.Encoding]::ASCII.GetBytes([string][char]10 + 'Signatur=')
  $at = -1
  for ($i = 0; $i -le $bytes.Length - $marker.Length -and $at -lt 0; $i++) {
    $j = 0
    while ($j -lt $marker.Length -and $bytes[$i + $j] -eq $marker[$j]) { $j++ }
    if ($j -eq $marker.Length) { $at = $i }
  }
  if ($at -lt 0) { throw 'Im Manifest fehlt die Signatur.' }
  $signed = New-Object byte[] ($at + 1)
  [System.Array]::Copy($bytes, $signed, $at + 1)
  $sig = [System.Convert]::FromBase64String([System.Text.Encoding]::ASCII.GetString($bytes, $at + $marker.Length, $bytes.Length - $at - $marker.Length).Trim())
  $lines = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($signed).Split([char]10)
  $names = @('Programm', 'Version', 'Datei', 'SHA256', 'Schluessel', 'Erstellt')
  if ($lines.Count -ne 7 -or $lines[6] -ne '') { throw 'Das Manifest hat nicht den erwarteten Aufbau.' }
  $mv = @{}
  for ($i = 0; $i -lt 6; $i++) {
    $p = $names[$i] + '='
    if (-not $lines[$i].StartsWith($p, [System.StringComparison]::Ordinal)) { throw ('Im Manifest fehlt ' + $names[$i] + '.') }
    $mv[$names[$i]] = $lines[$i].Substring($p.Length)
  }
  if ($mv['Schluessel'] -ceq 'Haupt') { $keyXml = $MainKey }
  elseif ($mv['Schluessel'] -ceq 'Reserve') { $keyXml = $ReserveKey }
  else { throw 'Unbekannter Schluessel im Manifest.' }
  $csp = New-Object System.Security.Cryptography.CspParameters -ArgumentList 24
  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList $csp
  $rsa.PersistKeyInCsp = $false
  $rsa.FromXmlString($keyXml)
  $valid = $rsa.VerifyData($signed, 'SHA256', $sig)
  $rsa.Clear()
  if (-not $valid) { throw 'Die Signatur des Updates ist ungueltig.' }
  if ($mv['Programm'] -cne 'Sync Pilot' -or $mv['Version'] -cne $Version -or $mv['Datei'] -cne $AssetName) { throw 'Das Manifest passt nicht zu diesem Update.' }
  if ($mv['SHA256'] -cnotmatch '^[0-9a-f]{64}$') { throw 'Die Pruefsumme im Manifest ist ungueltig.' }
  $expected = $mv['SHA256'].ToUpperInvariant()
  if ($expected -ne $Sha256) { throw 'Die Pruefsumme im Manifest weicht von der Pruefung im Programm ab.' }
  # Pruefsumme aus dem geprueften Manifest an der Datei pruefen, die tatsaechlich entpackt wird
  $hash = (Get-FileHash -LiteralPath $zipWork -Algorithm SHA256).Hash
  if ($hash -ne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $dir = Join-Path $Work 'Neu'
  if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
  Expand-Archive -LiteralPath $zipWork -DestinationPath $dir -Force
  # nur bekannte Dateien uebernehmen
  $new = Join-Path $dir $ExeName
  if (-not (Test-Path -LiteralPath $new -PathType Leaf)) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($new, $Exe))
  $manual = Join-Path $dir $ManualName
  if (Test-Path -LiteralPath $manual -PathType Leaf) { $pairs += ,@($manual, (Join-Path $target $ManualName)) }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $had = Test-Path -LiteralPath $p[1]
      if ($had) { Copy-Item -LiteralPath $p[1] -Destination $bak -Force }
      $done += ,@($p[1], $bak, $had)
      Copy-Item -LiteralPath $p[0] -Destination $p[1] -Force
    }
  } catch {
    foreach ($d in $done) {
      if ($d[2]) { Copy-Item -LiteralPath $d[1] -Destination $d[0] -Force }
      else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction SilentlyContinue }
    }
    throw
  }
  Remove-Item -LiteralPath $alt -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $zipWork -Force -ErrorAction SilentlyContinue
  if ($Elevated) { Remove-Item -LiteralPath $manifestWork -Force -ErrorAction SilentlyContinue }
  $result = 'OK ' + $Version
} catch {
  $result = 'FEHLER ' + $_.Exception.Message
}
if ($Elevated) {
  # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner schreiben; Neustart macht der zweite Prozess
  if ($workReady) { try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {} }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated) {
  if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
  else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
}
";

        // Neustart nach einem Update mit Administratorrechten. Davor setzt das Programm: $ProcessId, $Installer,
        // $Attempt, $Exe, $Log, $Work, $UserFiles, $Tray. Mit $UserFiles = $false (Prozess hat selbst Administratorrechte)
        // nichts im Temp-Ordner des Benutzers schreiben oder löschen: Das Ergebnis liest Sync Pilot dann aus $Work.
        const string RestartScript = @"
$ErrorActionPreference = 'SilentlyContinue'
try { Wait-Process -Id $ProcessId -Timeout 60 } catch {}
$end = (Get-Date).AddMinutes(45)
$workLog = Join-Path $Work 'Update.log'
while ((Get-Date) -lt $end) {
  if ($Installer -gt 0) { if (-not (Get-Process -Id $Installer -ErrorAction SilentlyContinue)) { break } }
  elseif (Test-Path -LiteralPath $workLog) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Milliseconds 300
if ($UserFiles) {
  $text = $null
  if (Test-Path -LiteralPath $workLog) { $text = Get-Content -LiteralPath $workLog -Raw -Encoding UTF8 }
  if ($text) { $text = $text.Trim() }
  if (-not $text) { $text = 'FEHLER Das Update wurde nicht ausgefuehrt.' }
  $text | Out-File -LiteralPath $Log -Encoding utf8
  Remove-Item -LiteralPath $Attempt -Recurse -Force
}
if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
";
    }

    public enum UpdateKind { Unknown, Current, Available, Failed, Checking }

    /// <summary>Stand der Update-Prüfung für das Info-Fenster.</summary>
    public sealed class UpdateStatus
    {
        public readonly UpdateKind Kind;
        public readonly string Title, Detail;
        /// <summary>Technischer Originaltext zur zweiten Zeile (hinter „Details“), null = keiner.</summary>
        public string More;
        public UpdateStatus(UpdateKind kind, string title, string detail) { Kind = kind; Title = title; Detail = detail ?? ""; }
    }

    /// <summary>Verbindung des Info-Fensters zur Update-Prüfung im Hauptfenster (Globale Richtlinien, Updates).</summary>
    public sealed class UpdateHooks
    {
        public Func<UpdateStatus> Status;
        public Func<bool> Auto;
        public Action<bool> SetAuto;
        public Func<Task<UpdateStatus>> CheckNow;
        public Func<bool> CanInstall;
        public Func<bool> AutoInstall;
        public Action<bool> SetAutoInstall;
        public Action Install;
        public Action OpenPage;
    }

    /// <summary>Symbol des Update-Stands (Segoe Fluent Icons 11 pt, nach Pixeln zentriert).</summary>
    public sealed class StatusGlyph : Control
    {
        char glyph;
        Color color;

        public StatusGlyph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        public void Set(char g, Color c)
        {
            glyph = g;
            color = c;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            if (glyph == '\0') return;
            int gs = (int)Math.Round(11f * 96f / 72f * Theme.Scale);
            Image img = MenuHelper.Glyph(glyph, color, gs);
            if (img == null) return;
            e.Graphics.DrawImage(img, (Width - gs) / 2, (Height - gs) / 2, gs, gs);
            img.Dispose();
        }
    }

    /// <summary>Hinweisleiste oben im Inhalt: neue Version mit "Jetzt aktualisieren", "Release-Seite" und "Später".</summary>
    public class UpdateBar : Control
    {
        public readonly FlatButton Install = new FlatButton("Jetzt aktualisieren", "", BtnKind.Primary);
        public readonly FlatButton Page = new FlatButton("Release-Seite", "", BtnKind.Secondary);
        public readonly FlatButton Later = new FlatButton("Später", null, BtnKind.Subtle);
        readonly Font iconFont = Theme.IconFont(14f);

        public UpdateBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.P(56);
            foreach (FlatButton b in new FlatButton[] { Install, Page, Later }) Controls.Add(b);
        }

        /// <summary>
        /// Farbe der Leiste (nicht der Fensterhintergrund): Die Knöpfe malen ihren Rand in der Farbe des Elternelements –
        /// sonst haben „Jetzt aktualisieren“ helle eckige Ecken und „Später“ ein weißes Rechteck auf der getönten Leiste.
        /// </summary>
        static Color Tint { get { return Theme.Blend(Theme.Bg, Theme.Accent, 0.14f); } }
        public override Color BackColor { get { return Tint; } set { } }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Width - Theme.P(10);
            foreach (FlatButton b in new FlatButton[] { Later, Page, Install })
            {
                x -= b.Width;
                b.Location = new Point(x, (Height - b.Height) / 2);
                x -= Theme.P(8);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Shapes.Rounded(r, Theme.P(8)))
            using (SolidBrush brush = new SolidBrush(Tint))
            using (Pen pen = new Pen(Theme.Blend(Theme.Bg, Theme.Accent, 0.45f)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            int x = Theme.P(16);
            TextRenderer.DrawText(g, "", iconFont, new Rectangle(x, 0, Theme.P(20), Height), Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            x += Theme.P(34);
            int right = Install.Left - Theme.P(12);
            TextRenderer.DrawText(g, Text, Theme.Bold, new Rectangle(x, 0, Math.Max(10, right - x), Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) iconFont.Dispose();
            base.Dispose(disposing);
        }
    }
}

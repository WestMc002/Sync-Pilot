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
        /// <summary>Größe des ZIP laut GitHub („size“), 0 = unbekannt. Der Download darf nicht größer sein.</summary>
        public long ZipSize;
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
                // Antwort auf 1 MB begrenzen (übliche Antworten haben wenige KB)
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                    json = Encoding.UTF8.GetString(ReadLimited(stream, 1024 * 1024));
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
            long zipSize = 0;
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
                    if (string.Equals(name, wanted, StringComparison.Ordinal))
                    {
                        zipUrl = url;
                        object size;
                        if (asset.TryGetValue("size", out size) && size != null)
                            try { zipSize = Convert.ToInt64(size, System.Globalization.CultureInfo.InvariantCulture); } catch { zipSize = 0; }
                    }
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
            info.ZipSize = zipSize;
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

        /// <summary>Höchstgröße des ZIP (auch wenn GitHub mehr angibt).</summary>
        const long MaxZip = 64L * 1024 * 1024;

        /// <summary>Stream lesen, höchstens max Bytes – sonst Abbruch.</summary>
        static byte[] ReadLimited(Stream stream, int max)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > max) throw new Exception("Antwort zu groß.");
                }
                return ms.ToArray();
            }
        }

        /// <summary>Datei laden und beim Lesen auf max Bytes begrenzen (danach Abbruch, Datei weg).</summary>
        static void DownloadLimited(string url, string file, long max, Action<int> progress)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            bool ok = false;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.ContentLength > max) throw new Exception("Das Update ist größer als erwartet.");
                    long expected = response.ContentLength > 0 ? response.ContentLength : max;
                    using (Stream stream = response.GetResponseStream())
                    using (FileStream fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                    {
                        byte[] buffer = new byte[81920];
                        long total = 0;
                        int n, last = -1;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += n;
                            if (total > max) throw new Exception("Das Update ist größer als erwartet.");
                            fs.Write(buffer, 0, n);
                            int percent = (int)Math.Min(100, total * 100 / Math.Max(1, expected));
                            if (progress != null && percent != last) { last = percent; progress(percent); }
                        }
                    }
                }
                ok = true;
            }
            finally
            {
                if (!ok) try { File.Delete(file); } catch { }
            }
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

        /// <summary>
        /// Im Projektordner (mit Erstellen.bat) wird nicht automatisch ersetzt: dort entsteht die EXE beim Bauen. Ist ein
        /// früheres Update nicht abgeschlossen, erst wiederherstellen (PendingText, siehe OfferRepair).
        /// Abweichung Sync Pilot (Globale Richtlinien › Selbst-Update, Schritt 1): Läuft Sync Pilot mit
        /// Administratorrechten, wird trotzdem aktualisiert – dann immer über den geschützten Arbeitsordner unter ProgramData.
        /// </summary>
        public static string InstallBlocker()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "Erstellen.bat")))
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            if (PendingRestore() != null) return PendingText;
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
        /// Prüfsumme selbst noch einmal, ersetzt EXE und Liesmich (atomar über Hilfsdateien, mit Journal) und startet
        /// Sync Pilot neu. Ohne Schreibrechte im Programmordner oder wenn Sync Pilot selbst als Administrator läuft, arbeitet
        /// es mit Administratorrechten in einem geschützten Arbeitsordner unter ProgramData; den Neustart übernimmt dann ein
        /// zweiter Prozess. Danach muss der Aufrufer das Programm beenden.
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
            // eigener Arbeitsordner je Versuch: Download und Rückfallkopien kommen sich nie in die Quere. Der Name beginnt
            // mit der Zielkennung: Die Prüfung auf offene Wiederherstellungen betrachtet nur Ordner dieses Ziels.
            string attempt = Path.Combine(Folder, TargetId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attempt);
            string zip = Path.Combine(attempt, AssetName(release.Version));
            // Download begrenzen: höchstens die von GitHub genannte Größe, in jedem Fall höchstens 64 MB
            long limit = MaxZip;
            if (release.ZipSize > 0)
            {
                if (release.ZipSize > MaxZip) throw new Exception("Das Update ist ungewöhnlich groß und wird nicht geladen.");
                limit = release.ZipSize;
            }
            await Task.Run(() => DownloadLimited(release.ZipUrl, zip, limit, progress));
            long zipLength = new FileInfo(zip).Length;
            if (release.ZipSize > 0 && zipLength != release.ZipSize)
            {
                try { File.Delete(zip); } catch { }
                throw new Exception("Download unvollständig.");
            }
            string hash = Sha256Of(zip);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zip); } catch { }
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
            vars.Append("$ZipSize = ").Append(zipLength).Append("\n");
            Var(vars, "Manifest", manifestFile);
            Var(vars, "Exe", exe);
            Var(vars, "Version", release.Version);
            Var(vars, "Sha256", release.Sha256.ToUpperInvariant());
            Var(vars, "AssetName", AssetName(release.Version));
            Var(vars, "MainKey", MainKey);
            Var(vars, "ReserveKey", ReserveKey);
            Var(vars, "LockName", LockName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "TempRoot", Folder);
            Var(vars, "Log", LogFile);
            Var(vars, "Work", work);
            Var(vars, "ExeName", ExeName);
            Var(vars, "ManualName", ManualName);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            // Skript als Datei im Arbeitsordner, auf der Befehlszeile nur Lader, Werte und SHA-256 des Skripts: Der Lader
            // liest die Datei einmal in den Speicher, prüft die Prüfsumme und führt genau diese Bytes aus. So bleibt die
            // Befehlszeile weit unter 32767 Zeichen, auch bei langen Pfaden.
            byte[] body = new UTF8Encoding(false).GetBytes(Compact(InstallScript));
            string scriptFile = Path.Combine(attempt, "Update-Skript.txt");
            File.WriteAllBytes(scriptFile, body);
            string scriptHash;
            using (SHA256 sha = SHA256.Create()) scriptHash = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");
            Var(vars, "ScriptFile", scriptFile);
            Var(vars, "ScriptHash", scriptHash);
            ProcessStartInfo start = PowerShell(vars + InstallLoader);
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
            int installerId = 0;
            try { if (installer != null) installerId = installer.Id; } catch { }
            StartRestartHelper(pid, installerId, attempt, exe, work, tray);
        }

        /// <summary>
        /// Neustart nach einem Update bzw. einer Wiederherstellung mit Administratorrechten: Ein eigener Prozess wartet auf
        /// das Ende, übernimmt das Ergebnis und startet Sync Pilot. Ohne eigene Administratorrechte läuft er mit normalen
        /// Rechten – Sync Pilot läuft danach also nicht als Administrator. Läuft Sync Pilot selbst als Administrator
        /// („Immer als Administrator starten“), erbt er die Rechte (gewollt): dann schreibt und löscht er nichts im
        /// Temp-Ordner des Benutzers, das Ergebnis liest Sync Pilot aus dem geschützten Arbeitsordner.
        /// </summary>
        static void StartRestartHelper(int pid, int installerId, string attempt, string exe, string work, bool tray)
        {
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

        // ------------------------------------------------------------------ Offene Wiederherstellung

        /// <summary>Merker im Arbeitsordner: Austausch begonnen, aber nicht bestätigt abgeschlossen bzw. zurückgesetzt.</summary>
        const string PendingMarker = "Wiederherstellung-offen";

        /// <summary>Kurzmeldung für Anwender: Ein Update wurde nicht abgeschlossen (Technisches steht im Fehlerprotokoll).</summary>
        public const string PendingText = "Das letzte Update wurde nicht abgeschlossen.";

        /// <summary>
        /// Arbeitsordner eines nicht abgeschlossenen Updates für dieses Installationsziel (null = keiner). Ordner im
        /// Temp-Ordner, bei denen laut Journal nichts zurückzusetzen ist, räumt Sync Pilot dabei still auf.
        /// </summary>
        static string PendingRestore()
        {
            try
            {
                List<string> dirs = new List<string>();
                if (Directory.Exists(Folder)) dirs.AddRange(Directory.GetDirectories(Folder, TargetId + "-*"));
                string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                dirs.AddRange(Directory.GetDirectories(data, "SyncPilot-Update-" + TargetId + "-*"));
                foreach (string dir in dirs)
                {
                    if (!File.Exists(Path.Combine(dir, PendingMarker))) continue;
                    bool temp = dir.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    // still aufräumen: im Temp-Ordner immer, unter ProgramData nur mit Administratorrechten und nur in
                    // einem nachweislich geschützten, eigenen Arbeitsordner (Prüfung wie im Update-Skript)
                    bool mayClean = temp || (Elevation.IsAdmin && IsProtectedWork(dir));
                    if (mayClean && NothingToRestore(dir))
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                            ErrorLog("Nicht abgeschlossenes Update aufgeräumt, es war nichts zurückzusetzen: " + dir);
                            continue;
                        }
                        catch { }
                    }
                    return dir;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Geschützter Arbeitsordner eines Updates für dieses Ziel: direkt unter ProgramData, Name mit eigener TargetId,
        /// keine Verknüpfung, Besitzer Administratoren oder SYSTEM.
        /// </summary>
        static bool IsProtectedWork(string dir)
        {
            try
            {
                string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.Equals(Path.GetDirectoryName(dir), data, StringComparison.OrdinalIgnoreCase)) return false;
                if (!Regex.IsMatch(Path.GetFileName(dir), "^SyncPilot-Update-" + TargetId + "-[0-9a-f]{32}$")) return false;
                DirectoryInfo di = new DirectoryInfo(dir);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                System.Security.Principal.SecurityIdentifier owner = (System.Security.Principal.SecurityIdentifier)
                    di.GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner).GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
                return owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) ||
                       owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid);
            }
            catch { return false; }
        }

        /// <summary>
        /// Prüft das Wiederherstellungsjournal: true, wenn jede eingetragene Datei noch der Sicherung entspricht bzw. bei
        /// „Neu“ nicht vorhanden ist (das Update hat dann nichts verändert). Übrig gebliebene Hilfsdateien werden entfernt.
        /// </summary>
        static bool NothingToRestore(string dir)
        {
            try
            {
                string exe = Application.ExecutablePath;
                string target = Path.GetDirectoryName(exe);
                string journal = Path.Combine(dir, "Wiederherstellung.txt");
                // Journal fehlt: Der Merker wird vor dem Journal geschrieben, verändert wurde also noch nichts
                if (!File.Exists(journal)) return true;
                foreach (string line in File.ReadAllLines(journal, Encoding.UTF8))
                {
                    if (!line.StartsWith("Vorher ", StringComparison.Ordinal)) continue;
                    Match m = Regex.Match(line, @"^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$");
                    if (!m.Success) return false;
                    string dst = m.Groups[3].Value, tmp = m.Groups[4].Value;
                    if (!string.Equals(dst, exe, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(dst, Path.Combine(target, ManualName), StringComparison.OrdinalIgnoreCase)) return false;
                    if (m.Groups[2].Value == "Sicherung")
                    {
                        string bak = Path.Combine(Path.Combine(dir, "Alt"), m.Groups[1].Value);
                        if (!File.Exists(bak) || !File.Exists(dst) || Sha256Of(bak) != Sha256Of(dst)) return false;
                    }
                    else if (File.Exists(dst)) return false;
                    // Hilfsdatei nur im Programmordner und mit dem erwarteten Namen entfernen
                    if (string.Equals(Path.GetDirectoryName(tmp), target, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(tmp).StartsWith("." + Path.GetFileName(dst) + ".update-", StringComparison.OrdinalIgnoreCase))
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Nicht abgeschlossenes Update: nachfragen und die vorherige Version wiederherstellen (Sync Pilot wird dazu
        /// beendet und danach neu gestartet). true = Wiederherstellung läuft, Sync Pilot soll sich beenden.
        /// </summary>
        public static bool OfferRepair(IWin32Window owner, bool tray)
        {
            string dir = PendingRestore();
            if (dir == null) return false;
            ErrorLog("Nicht abgeschlossenes Update gefunden: " + dir);
            if (MessageBox.Show(owner,
                PendingText + "\n\nSync Pilot stellt jetzt die vorherige Version wieder her und startet danach neu.\n\nJetzt wiederherstellen?",
                "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return false;
            try { Repair(dir, tray); return true; }
            catch (Exception ex)
            {
                ErrorLog("Wiederherstellung nicht gestartet: " + ex.Message);
                MessageBox.Show(owner, ex.Message, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        static void Repair(string dir, bool tray)
        {
            // im Projektordner (Erstellen.bat neben der EXE) wird nichts zurückgesetzt – das Skript prüft es ebenso
            if (File.Exists(Path.Combine(ExeDirectory, "Erstellen.bat")))
                throw new Exception("Im Projektordner wird nichts wiederhergestellt – dort entsteht die EXE beim Erstellen.");
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            bool protectedDir = string.Equals(Path.GetDirectoryName(dir), data, StringComparison.OrdinalIgnoreCase);
            string exe = Application.ExecutablePath;
            // Sicherungen aus dem Temp-Ordner nie mit Administratorrechten zurückkopieren (jedes Programm des Benutzers
            // könnte sie verändert haben); dort hat das Update ohne Adminrechte gearbeitet, also reichen sie auch jetzt.
            if (!protectedDir && (Elevation.IsAdmin || !CanWrite(Path.GetDirectoryName(exe))))
                throw new Exception(Elevation.IsAdmin
                    ? "Die vorherige Version lässt sich mit Administratorrechten nicht wiederherstellen. Bitte Sync Pilot einmal normal starten und dann aktualisieren."
                    : "Die vorherige Version lässt sich nicht automatisch wiederherstellen. Bitte Sync Pilot neu herunterladen.");
            bool elevated = protectedDir;
            try { File.Delete(LogFile); } catch { }
            int pid = Process.GetCurrentProcess().Id;
            StringBuilder vars = new StringBuilder();
            vars.Append("$ProcessId = ").Append(pid).Append("\n");
            Var(vars, "Work", dir);
            Var(vars, "Exe", exe);
            Var(vars, "ManualName", ManualName);
            Var(vars, "TargetId", TargetId);
            Var(vars, "Log", LogFile);
            vars.Append("$Elevated = $").Append(elevated ? "true" : "false").Append("\n");
            vars.Append("$Tray = $").Append(tray ? "true" : "false").Append("\n");
            ProcessStartInfo start = PowerShell(vars + RepairScript);
            if (elevated && !Elevation.IsAdmin) start.Verb = "runas";
            Process repairer;
            try { repairer = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Wiederherstellung abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
            if (!elevated) return;
            int repairerId = 0;
            try { if (repairer != null) repairerId = repairer.Id; } catch { }
            StartRestartHelper(pid, repairerId, "", exe, dir, tray);
        }

        /// <summary>Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen – nie mit offener Wiederherstellung.</summary>
        static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    DirectoryInfo di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.Exists(Path.Combine(dir, PendingMarker))) continue;
                    if (di.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { di.Delete(true); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Fehlerprotokoll für Updates (Technisches, das nicht in Meldungen an Anwender gehört). Liegt im Protokollordner
        /// (Info-Fenster › Protokolle, Verlauf › Protokollordner); .txt, damit es die Bereinigung der .log-Dateien nicht erfasst.
        /// </summary>
        public static string ErrorLogFile { get { return Path.Combine(Store.LogDir, "Update-Fehler.txt"); } }

        public static void ErrorLog(string text)
        {
            if (Demo.Active) return;
            try
            {
                Directory.CreateDirectory(Store.LogDir);
                FileInfo fi = new FileInfo(ErrorLogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(ErrorLogFile, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + "  Update: " + text + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        static string Sha256Of(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        /// <summary>PowerShell über den festen Systempfad mit dem Skript als Kommando (-EncodedCommand).</summary>
        static ProcessStartInfo PowerShell(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) throw new Exception("Windows PowerShell wurde nicht gefunden.");
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(Compact(script)));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            ProcessStartInfo start = new ProcessStartInfo(ps, arguments);
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.WorkingDirectory = system;
            return start;
        }

        /// <summary>Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein.</summary>
        static string Compact(string script)
        {
            StringBuilder compact = new StringBuilder();
            foreach (string raw in script.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                compact.Append(line).Append('\n');
            }
            return compact.ToString();
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

        // Lader für das Update-Skript (steht mit den Werten auf der Befehlszeile). Liest $ScriptFile einmal in den
        // Speicher, prüft die SHA-256 gegen $ScriptHash und führt genau diese Bytes aus. Weicht sie ab oder fehlt die
        // Datei, wird nichts verändert und Sync Pilot startet wieder (erhöht über den Neustarthelfer, der dazu das
        // Ergebnis „FEHLER …“ im geschützten Arbeitsordner findet). New-WorkFolder nutzt auch das Update-Skript.
        const string InstallLoader = @"
$ErrorActionPreference = 'Stop'
# geschuetzten Arbeitsordner anlegen: Besitzer Administratoren, schreiben nur SYSTEM und Administratoren, lesen angemeldete Benutzer
function New-WorkFolder {
  $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
  $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
  $root = [Environment]::GetFolderPath('CommonApplicationData')
  if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('SyncPilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
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
}
$code = $null
try {
  # hoechstens 256 KB lesen; waehrend des Lesens darf niemand schreiben (FileShare.Read)
  $fs = [System.IO.File]::Open($ScriptFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt 262144) { throw 'Das Update-Skript ist zu gross.' }
    $b = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $b.Length) { $k = $fs.Read($b, $n, $b.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $b.Length) { throw 'Das Update-Skript ist unvollstaendig.' }
  } finally { $fs.Dispose() }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $h = [System.BitConverter]::ToString($sha.ComputeHash($b)).Replace('-', '')
  $sha.Dispose()
  if ($h -cne $ScriptHash) { throw 'Das Update-Skript wurde veraendert.' }
  $code = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($b)
} catch {
  # eindeutiger Status: Skript nicht geladen, nichts veraendert (FEHLER = bisherige Version darf wieder starten)
  $msg = 'FEHLER Update-Skript nicht geladen, nichts veraendert: ' + $_.Exception.Message
  if ($Elevated) {
    # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner; den Neustart macht der Neustarthelfer
    try { New-WorkFolder; $msg | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
  } else {
    try { $msg | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    # nach der Frist erneut pruefen: laeuft die alte Instanz noch, nicht starten
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
      try { ($msg + ' Sync Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    }
    elseif ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
    else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
  }
  exit 1
}
. ([scriptblock]::Create($code))
";

        // Update-Skript (Datei „Update-Skript.txt“, geladen über InstallLoader). Davor setzt das Programm: $ProcessId,
        // $Zip, $ZipSize, $Manifest, $Exe, $Version, $Sha256, $AssetName, $MainKey, $ReserveKey, $LockName, $TargetId,
        // $TempRoot, $Log, $Work, $ExeName, $ManualName, $Elevated, $Tray, $ScriptFile, $ScriptHash.
        // Nur ASCII-Anführungszeichen verwenden.
        const string InstallScript = @"
$ErrorActionPreference = 'Stop'
# Dateien begrenzt lesen bzw. kopieren; waehrenddessen darf niemand schreiben (FileShare.Read). Bei Ueberschreitung Abbruch, Teilkopie weg.
function Read-Limited([string]$path, [long]$max) {
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $path) }
    $buf = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $buf.Length) { $k = $fs.Read($buf, $n, $buf.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $buf.Length) { throw ('Die Datei ist unvollstaendig: ' + $path) }
    return ,$buf
  } finally { $fs.Dispose() }
}
function Copy-Limited([string]$src, [string]$dst, [long]$max) {
  $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  $out = $null
  try {
    if ($in.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
    $out = [System.IO.File]::Open($dst, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $buf = New-Object byte[] 65536
    $total = 0
    while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
      $total += $k
      if ($total -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
      $out.Write($buf, 0, $k)
    }
    $out.Dispose()
    $out = $null
  } catch {
    if ($out) { $out.Dispose(); $out = $null; Remove-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue }
    throw
  } finally {
    if ($out) { $out.Dispose() }
    $in.Dispose()
  }
}
# ZIP hoechstens so gross wie heruntergeladen und nie ueber 64 MB
$zipMax = [Math]::Min([long]$ZipSize, [long]67108864)
$target = Split-Path $Exe -Parent
$workReady = -not $Elevated
$result = $null
$lock = $null
$haveLock = $false
$noRestart = $false
$archive = $null
$zipStream = $null
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  # erst nach bestaetigtem Ende der alten Instanz Dateien aendern
  $deadline = (Get-Date).AddSeconds(60)
  while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { throw 'Sync Pilot wurde nicht beendet.' }
    Start-Sleep -Milliseconds 250
  }
  # auch andere Sync-Pilot-Prozesse mit derselben EXE abwarten (z. B. ein Abgleich nach Zeitplan, hoechstens 30 Minuten)
  $until = (Get-Date).AddMinutes(30)
  while ($true) {
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $Exe } catch { $false } })
    if ($running.Count -eq 0) { break }
    if ((Get-Date) -gt $until) { throw 'Ein anderer Sync-Pilot-Prozess wurde nach 30 Minuten nicht beendet.' }
    Start-Sleep -Seconds 2
  }
  Start-Sleep -Milliseconds 500
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('SyncPilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    # alte Arbeitsordner frueherer Updates entfernen: nur eigene (Besitzer Administratoren), keine Verknuepfungen,
    # nur nachweislich inaktive: gleiches Ziel (diese Sperre halten wir) oder anderes Ziel ohne gehaltene Sperre
    # und mindestens eine Stunde unveraendert; nie mit offener Wiederherstellung
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
            if (Test-Path -LiteralPath (Join-Path $_.FullName 'Wiederherstellung-offen')) { $inactive = $false }
            if ($inactive) { [System.IO.Directory]::Delete($_.FullName, $true) }
          }
        }
      } catch {}
    }
    # Arbeitsordner gleich mit Rechten anlegen (New-WorkFolder steht im Lader)
    New-WorkFolder
    $workReady = $true
    $zipWork = Join-Path $Work 'Update.zip'
    $manifestWork = Join-Path $Work 'Update.manifest'
    Copy-Limited $Zip $zipWork $zipMax
    Copy-Limited $Manifest $manifestWork 8192
  } else {
    $zipWork = $Zip
    $manifestWork = $Manifest
  }
  # Signatur des Manifests selbst pruefen (an der Kopie, die verwendet wird)
  $bytes = Read-Limited $manifestWork 8192
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
  # ZIP einmal oeffnen (waehrenddessen darf niemand schreiben oder loeschen), Pruefsumme ueber genau diesen Stream
  # berechnen und denselben Stream entpacken; er bleibt bis nach dem Austausch offen
  $zipStream = [System.IO.File]::Open($zipWork, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  if ($zipStream.Length -gt $zipMax) { throw 'Das Update ist zu gross.' }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $hash = [System.BitConverter]::ToString($sha.ComputeHash($zipStream)).Replace('-', '')
  $sha.Dispose()
  if ($hash -ne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $zipStream.Position = 0
  Add-Type -AssemblyName System.IO.Compression
  $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
  # nur bekannte Eintraege (feste Namen im Stammordner), je Datei begrenzt; geschrieben wird direkt aus dem Archiv ins Ziel
  function Find-Entry([string]$name, [long]$max) {
    $hits = @($archive.Entries | Where-Object { $_.FullName -eq $name })
    if ($hits.Count -gt 1) { throw ('Im ZIP steht ' + $name + ' mehrfach.') }
    if ($hits.Count -eq 0) { return $null }
    if ($hits[0].Length -gt $max) { throw ('Im ZIP ist ' + $name + ' zu gross.') }
    return $hits[0]
  }
  # erst vollstaendig in eine Hilfsdatei im Zielordner schreiben, auf den Datentraeger bringen und pruefen,
  # dann das Ziel in einem Schritt ersetzen (File.Replace bzw. File.Move): Das Ziel ist nie halb geschrieben
  function Write-Entry($entry, [string]$dst, [string]$tmp, [long]$max) {
    try {
      $in = $entry.Open()
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
          $buf = New-Object byte[] 65536
          $total = 0
          while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
            $total += $k
            if ($total -gt $max) { throw ('Im ZIP ist ' + $entry.FullName + ' zu gross.') }
            $out.Write($buf, 0, $k)
          }
          $out.Flush($true)
        } finally { $out.Dispose() }
      } finally { $in.Dispose() }
      if ($total -ne $entry.Length) { throw ('Im ZIP ist ' + $entry.FullName + ' unvollstaendig.') }
      # [NullString]::Value statt $null: PowerShell macht aus $null sonst einen leeren Text (ungueltiger Pfad)
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  # Ruecksicherung ebenso: Sicherung erst vollstaendig in eine Hilfsdatei im Zielordner, dann das Ziel in einem Schritt ersetzen
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $exeEntry = Find-Entry $ExeName 33554432
  if (-not $exeEntry) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($exeEntry, $Exe, 33554432))
  $manualEntry = Find-Entry $ManualName 1048576
  if ($manualEntry) { $pairs += ,@($manualEntry, (Join-Path $target $ManualName), 1048576) }
  # unter der Sperre erneut nach offenen Wiederherstellungen fuer dieses Ziel suchen; Treffer oder fehlgeschlagene
  # Pruefung = nichts austauschen
  $open = $null
  try {
    $roots = @(@($TempRoot, ($TargetId + '-*')), @([Environment]::GetFolderPath('CommonApplicationData'), ('SyncPilot-Update-' + $TargetId + '-*')))
    foreach ($r in $roots) {
      if (Test-Path -LiteralPath $r[0]) {
        foreach ($d in @(Get-ChildItem -LiteralPath $r[0] -Directory -Filter $r[1] -Force -ErrorAction Stop)) {
          if ($d.FullName -ne $Work -and (Test-Path -LiteralPath (Join-Path $d.FullName 'Wiederherstellung-offen'))) { $open = $d.FullName }
        }
      }
    }
  } catch { throw ('Offene Wiederherstellungen konnten nicht geprueft werden: ' + $_.Exception.Message) }
  if ($open) { throw ('Eine fruehere Wiederherstellung ist noch offen (' + $open + ').') }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  # Merker vor dem ersten Ueberschreiben dauerhaft anlegen: bricht der Austausch ab (auch hart beendet), bleibt der
  # Arbeitsordner erhalten und neue Updates gesperrt. Laesst er sich nicht schreiben, wird nichts ausgetauscht.
  $pending = Join-Path $Work 'Wiederherstellung-offen'
  $alt | Out-File -LiteralPath $pending -Encoding utf8 -ErrorAction Stop
  if (-not (Test-Path -LiteralPath $pending)) { throw 'Statusdatei konnte nicht angelegt werden.' }
  # Wiederherstellungsjournal: vor jedem Austausch Zielpfad, Sicherungsdatei und ob das Ziel vorher existierte
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  @(('Sync-Pilot-Update ' + $Version + ' - Wiederherstellungsjournal'),
    'Jede Zeile ""Vorher"" gilt als moeglicherweise ueberschrieben, auch ohne folgende Zeile ""Ersetzt"" (Abbruch waehrend des Kopierens).',
    'Wiederherstellen fuer jede Zeile ""Vorher"": bei ""Sicherung"" die Sicherungsdatei auf das Ziel zurueckkopieren (sie ist vollstaendig), bei ""Neu"" die Zieldatei loeschen, falls vorhanden.',
    '""Ersetzt n"" bestaetigt nur, dass der Austausch dieser Datei abgeschlossen wurde. Eine uebrig gebliebene ""Hilfsdatei"" kann geloescht werden.',
    '') | Set-Content -LiteralPath $journal -Encoding utf8 -ErrorAction Stop
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $tmp = Join-Path (Split-Path $p[1] -Parent) ('.' + (Split-Path $p[1] -Leaf) + '.update-' + [Guid]::NewGuid().ToString('N'))
      $had = Test-Path -LiteralPath $p[1]
      if ($had) {
        # erst vollstaendig unter Hilfsnamen kopieren, dann umbenennen: vorhandene Sicherungsdatei = vollstaendig
        Copy-Item -LiteralPath $p[1] -Destination ($bak + '.teil') -Force
        Move-Item -LiteralPath ($bak + '.teil') -Destination $bak -Force
        $line = 'Vorher ' + $i + ': Sicherung | Ziel: ' + $p[1] + ' | Sicherungsdatei: ' + $bak + ' | Hilfsdatei: ' + $tmp
      } else {
        $line = 'Vorher ' + $i + ': Neu | Ziel: ' + $p[1] + ' | Hilfsdatei: ' + $tmp
      }
      Add-Content -LiteralPath $journal -Value $line -Encoding utf8 -ErrorAction Stop
      $done += ,@($p[1], $bak, $had)
      Write-Entry $p[0] $p[1] $tmp $p[2]
      Add-Content -LiteralPath $journal -Value ('Ersetzt ' + $i) -Encoding utf8 -ErrorAction SilentlyContinue
    }
  } catch {
    # jede Datei einzeln zuruecksetzen, Fehler sammeln; bei unvollstaendiger Ruecksicherung Sicherung behalten und nicht neu starten
    $err = $_.Exception.Message
    $failed = @()
    foreach ($d in $done) {
      try {
        if ($d[2]) { Restore-File $d[1] $d[0] }
        else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction Stop }
      } catch { $failed += $d[0] }
    }
    if ($failed.Count -gt 0) {
      $noRestart = $true
      throw ('Ruecksicherung unvollstaendig (' + ($failed -join ', ') + '). Die alten Dateien liegen in ' + $alt + ', die Zuordnung steht in ' + $journal + '. Fehler: ' + $err)
    }
    # Ruecksicherung vollstaendig bestaetigt: Merker entfernen; klappt das nicht, sicherheitshalber nicht neu starten
    try { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop } } catch { $noRestart = $true }
    throw
  }
  # Installation vollstaendig: erst Merker entfernen, dann die Rueckfallkopien
  try { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop }
  catch { $noRestart = $true; throw ('Update installiert, aber der Status in ' + $Work + ' konnte nicht entfernt werden.') }
  Remove-Item -LiteralPath $alt -Recurse -Force -ErrorAction SilentlyContinue
  $archive.Dispose()
  $zipStream.Dispose()
  Remove-Item -LiteralPath $zipWork -Force -ErrorAction SilentlyContinue
  if ($Elevated) { Remove-Item -LiteralPath $manifestWork -Force -ErrorAction SilentlyContinue }
  $result = 'OK ' + $Version
} catch {
  if ($archive) { try { $archive.Dispose() } catch {} }
  if ($zipStream) { try { $zipStream.Dispose() } catch {} }
  # Abschlussstatus: FEHLER = nichts veraendert bzw. vollstaendig zurueckgesetzt (Neustart erlaubt),
  # OFFEN = Ruecksicherung unvollstaendig (kein Neustart, Sicherung bleibt)
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($Elevated) {
  # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner schreiben; Neustart macht der Neustarthelfer
  if ($workReady) {
    try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
    if ($noRestart) { try { 'Kein Neustart' | Out-File -LiteralPath (Join-Path $Work 'KeinNeustart') -Encoding utf8 } catch {} }
  }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if (-not $noRestart) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated -and -not $noRestart) {
  # nur starten, wenn die alte Instanz beendet ist (z. B. nicht nach Ablauf der Wartefrist)
  if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
    try { ($result + ' Sync Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  }
  elseif ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
  else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
}
";

        // Wiederherstellung nach einem nicht abgeschlossenen Update. Davor setzt das Programm: $ProcessId, $Work, $Exe,
        // $ManualName, $TargetId, $Log, $Elevated, $Tray. Liest das Journal, nimmt aber Ziele und Sicherungen nie
        // ungeprüft daraus: Ziel nur EXE oder Liesmich, Sicherung immer <Work>\Alt\<Nummer>. Mit Adminrechten nur aus
        // dem eigenen, geschützten Arbeitsordner unter ProgramData. Ergebnis: ZURUECK, FEHLER (nichts verändert) oder
        // OFFEN (Wiederherstellung unvollständig, kein Neustart).
        const string RepairScript = @"
$ErrorActionPreference = 'Stop'
$result = $null
$noRestart = $false
$target = Split-Path $Exe -Parent
try {
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
  if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { throw 'Sync Pilot wurde nicht beendet.' }
  if (Test-Path -LiteralPath (Join-Path $target 'Erstellen.bat')) { throw 'Im Projektordner wird nichts wiederhergestellt.' }
  $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $Exe } catch { $false } })
  if ($running.Count -gt 0) { throw 'Ein anderer Sync-Pilot-Prozess laeuft noch.' }
  $item = Get-Item -LiteralPath $Work -Force
  if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Unerwarteter Arbeitsordner.' }
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('SyncPilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner ist nicht geschuetzt.' }
  }
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $allowed = @($Exe, (Join-Path $target $ManualName))
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  $lines = @()
  if (Test-Path -LiteralPath $journal) { $lines = [System.IO.File]::ReadAllLines($journal) }
  $failed = @()
  foreach ($line in $lines) {
    if (-not $line.StartsWith('Vorher ')) { continue }
    if ($line -notmatch '^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+?)? \| Hilfsdatei: (.+)$') { throw 'Das Journal ist nicht lesbar.' }
    $n = $Matches[1]; $kind = $Matches[2]; $dst = $Matches[3]; $tmp = $Matches[4]
    if ($allowed -notcontains $dst) { throw 'Unerwarteter Eintrag im Journal.' }
    try {
      if ($kind -eq 'Sicherung') {
        $bak = Join-Path (Join-Path $Work 'Alt') $n
        if (-not (Test-Path -LiteralPath $bak -PathType Leaf)) { throw ('Sicherung fehlt: ' + $bak) }
        $same = (Test-Path -LiteralPath $dst) -and ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash)
        if (-not $same) { Restore-File $bak $dst }
      } elseif (Test-Path -LiteralPath $dst) {
        Remove-Item -LiteralPath $dst -Force
      }
      if ((Split-Path $tmp -Parent) -eq $target -and (Split-Path $tmp -Leaf) -like ('.' + (Split-Path $dst -Leaf) + '.update-*')) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      }
    } catch { $failed += ($dst + ': ' + $_.Exception.Message) }
  }
  if ($failed.Count -gt 0) { $noRestart = $true; throw ('Wiederherstellung unvollstaendig (' + ($failed -join '; ') + ').') }
  Remove-Item -LiteralPath (Join-Path $Work 'Wiederherstellung-offen') -Force
  Remove-Item -LiteralPath (Join-Path $Work 'KeinNeustart') -Force -ErrorAction SilentlyContinue
  $result = 'ZURUECK Die vorherige Version wurde wiederhergestellt.'
} catch {
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($Elevated) {
  # Ergebnis in den geschuetzten Arbeitsordner; den Neustart macht der Neustarthelfer
  try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if ($result.StartsWith('ZURUECK')) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
  if (-not $noRestart -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
    if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
    else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
  }
}
";

        // Neustart nach einem Update bzw. einer Wiederherstellung mit Administratorrechten. Davor setzt das Programm:
        // $ProcessId, $Installer, $Attempt, $Exe, $Log, $Work, $UserFiles, $Tray. Mit $UserFiles = $false (Prozess hat
        // selbst Administratorrechten) nichts im Temp-Ordner des Benutzers schreiben oder löschen: Das Ergebnis liest
        // Sync Pilot dann aus $Work.
        const string RestartScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$workLog = Join-Path $Work 'Update.log'
function Write-Result([string]$text) {
  if ($UserFiles) { $text | Out-File -LiteralPath $Log -Encoding utf8 }
  else { $text | Out-File -LiteralPath $workLog -Encoding utf8 }
}
# Neustart erst nach bestaetigtem Ende des Installers; bei Fristablauf Fehler melden und nicht starten
$end = (Get-Date).AddMinutes(45)
$finished = $false
while ((Get-Date) -lt $end) {
  if ($Installer -gt 0) { if (-not (Get-Process -Id $Installer -ErrorAction SilentlyContinue)) { $finished = $true; break } }
  elseif (Test-Path -LiteralPath $workLog) { $finished = $true; break }
  Start-Sleep -Milliseconds 500
}
if (-not $finished) {
  if ($UserFiles) { 'OFFEN Das Update ist nach 45 Minuten nicht fertig geworden. Sync Pilot wurde nicht neu gestartet.' | Out-File -LiteralPath $Log -Encoding utf8 }
  exit
}
Start-Sleep -Milliseconds 300
$text = $null
if (Test-Path -LiteralPath $workLog) { $text = Get-Content -LiteralPath $workLog -Raw -Encoding UTF8 }
if ($text) { $text = $text.Trim() }
# Neustart nur bei eindeutigem Abschluss: installiert (OK), nichts veraendert bzw. vollstaendig zurueckgesetzt
# (FEHLER) oder wiederhergestellt (ZURUECK), und ohne offenen Merker
$clear = $text -and ($text -match '^(OK|FEHLER|ZURUECK) ') -and -not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen')) -and -not (Test-Path -LiteralPath (Join-Path $Work 'KeinNeustart'))
if (-not $clear) {
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.' }
  Write-Result ($text + ' Sync Pilot wurde nicht neu gestartet.')
  exit
}
if ($UserFiles) {
  # laesst sich das Ergebnis nicht schreiben, nicht neu starten (Arbeitsordner bleibt)
  try { $text | Out-File -LiteralPath $Log -Encoding utf8 -ErrorAction Stop } catch { exit }
  if ($Attempt) { Remove-Item -LiteralPath $Attempt -Recurse -Force }
}
# alte Instanz muss beendet sein (Ladefehler kommen sehr frueh)
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
  Write-Result ($text + ' Sync Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.')
  exit
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

        string fullText, line1, line2;

        /// <summary>
        /// Text „Neue Version x verfügbar – installiert ist y.“; bei wenig Platz zweizeilig: oben fett „Neue Version x“,
        /// darunter grau „Installiert ist y“ (Vorlage: Dock Pilot).
        /// </summary>
        public void SetAvailable(string version, string installed)
        {
            fullText = "Neue Version " + version + " verfügbar – installiert ist " + installed + ".";
            line1 = "Neue Version " + version;
            line2 = "Installiert ist " + installed;
            Text = fullText;
        }

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
            const TextFormatFlags one = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int room = Math.Max(10, right - x);
            if (Text == fullText && line1 != null && TextRenderer.MeasureText(g, Text, Theme.Bold, new Size(int.MaxValue, Height), one).Width > room)
            {
                // zweizeilig: Block senkrecht mittig
                int h1 = TextRenderer.MeasureText(g, line1, Theme.Bold, new Size(int.MaxValue, Height), one).Height;
                int h2 = TextRenderer.MeasureText(g, line2, Theme.Small, new Size(int.MaxValue, Height), one).Height;
                int y = (Height - h1 - h2) / 2;
                TextRenderer.DrawText(g, line1, Theme.Bold, new Rectangle(x, y, room, h1), Theme.Text, one);
                TextRenderer.DrawText(g, line2, Theme.Small, new Rectangle(x, y + h1, room, h2), Theme.TextDim, one);
                return;
            }
            TextRenderer.DrawText(g, Text, Theme.Bold, new Rectangle(x, 0, room, Height), Theme.Text, one | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) iconFont.Dispose();
            base.Dispose(disposing);
        }
    }
}

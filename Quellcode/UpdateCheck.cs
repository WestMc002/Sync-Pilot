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
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Neueste Veröffentlichung auf GitHub.</summary>
    public class ReleaseInfo
    {
        public string Version, PageUrl, ZipUrl;
    }

    /// <summary>Fragt nur die öffentliche GitHub-Schnittstelle ab (keine Anmeldung, es werden keine Daten übertragen).</summary>
    public static class Updater
    {
        public const string Repository = "WestMc002/Sync-Pilot";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        /// <summary>Arbeitsordner für Download und Hilfsskript.</summary>
        public static string Folder { get { return Path.Combine(Path.GetTempPath(), "Sync Pilot-Update"); } }
        public static string LogFile { get { return Path.Combine(Folder, "Update.log"); } }

        static void PrepareTls()
        {
            // .NET 4: TLS 1.2 (3072) für github.com einschalten
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        static string UserAgent { get { return "Sync-Pilot/" + AppInfo.Version; } }

        /// <summary>Kein Fehler: Auf GitHub ist noch keine Version veröffentlicht.</summary>
        public const string NoRelease = "Noch keine Version";

        /// <summary>Kurzmeldung für das Info-Fenster, technischer Originaltext in Data["Detail"] (für „Details“).</summary>
        static Exception Brief(string brief, string detail)
        {
            Exception e = new Exception(brief);
            if (!string.IsNullOrEmpty(detail)) e.Data["Detail"] = detail;
            return e;
        }

        public static ReleaseInfo GetLatest()
        {
            PrepareTls();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases?per_page=20");
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
            // Liste statt /releases/latest: „latest“ übergeht Vorabversionen (Pre-release). Genommen wird die
            // höchste Version, die kein Entwurf ist – Vorabversionen eingeschlossen.
            Dictionary<string, object> data = null;
            System.Version best = null;
            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(json); }
            catch (Exception ex) { throw Brief("Antwort unlesbar.", ex.Message); }
            if (parsed is System.Collections.IEnumerable)
            {
                foreach (object o in (System.Collections.IEnumerable)parsed)
                {
                    Dictionary<string, object> r = o as Dictionary<string, object>;
                    if (r == null) continue;
                    object d;
                    if (r.TryGetValue("draft", out d) && d is bool && (bool)d) continue;
                    object t;
                    if (!r.TryGetValue("tag_name", out t) || t == null) continue;
                    System.Version v;
                    if (!System.Version.TryParse(Convert.ToString(t).TrimStart('v', 'V'), out v)) continue;
                    if (best == null || v > best) { best = v; data = r; }
                }
            }
            if (data == null) throw new Exception(NoRelease);
            ReleaseInfo info = new ReleaseInfo();
            info.PageUrl = ReleasesPage;
            object value;
            if (data.TryGetValue("tag_name", out value) && value != null) info.Version = Convert.ToString(value).TrimStart('v', 'V');
            if (data.TryGetValue("html_url", out value) && value != null) info.PageUrl = Convert.ToString(value);
            // Listen kommen je nach Serializer als ArrayList oder object[] an
            if (data.TryGetValue("assets", out value) && value is System.Collections.IEnumerable && !(value is string))
            {
                foreach (object item in (System.Collections.IEnumerable)value)
                {
                    Dictionary<string, object> asset = item as Dictionary<string, object>;
                    if (asset == null || !asset.ContainsKey("name") || !asset.ContainsKey("browser_download_url")) continue;
                    if (Convert.ToString(asset["name"]).EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ZipUrl = Convert.ToString(asset["browser_download_url"]);
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(info.Version)) throw new Exception("Antwort unlesbar.");
            return info;
        }

        /// <summary>true, wenn remote neuer ist als die laufende Version.</summary>
        public static bool IsNewer(string remote)
        {
            Version a, b;
            if (!System.Version.TryParse(remote, out a) || !System.Version.TryParse(AppInfo.Version, out b)) return false;
            return a > b;
        }

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

        /// <summary>
        /// ZIP herunterladen, Hilfsskript schreiben und starten. Das Skript wartet, bis Sync Pilot beendet ist (auch ein
        /// gerade laufender Abgleich nach Zeitplan), ersetzt EXE und Liesmich und startet das Programm neu.
        /// Danach muss der Aufrufer das Programm beenden.
        /// </summary>
        public static async Task Install(ReleaseInfo release, Action<int> progress)
        {
            if (string.IsNullOrEmpty(release.ZipUrl)) throw new Exception("Im Release ist keine ZIP-Datei angehängt.");
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            string zip = Path.Combine(Folder, "Sync-Pilot-" + release.Version + ".zip");
            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                if (progress != null) client.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs e) { progress(e.ProgressPercentage); };
                await client.DownloadFileTaskAsync(new Uri(release.ZipUrl), zip);
            }
            if (new FileInfo(zip).Length < 1024) throw new Exception("Die heruntergeladene Datei ist unvollständig.");
            string script = Path.Combine(Folder, "Update.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            try { File.Delete(LogFile); } catch { }
            string exe = Application.ExecutablePath;
            string target = Path.GetDirectoryName(exe);
            ProcessStartInfo start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -ProcessId " + Process.GetCurrentProcess().Id +
                " -Zip \"" + zip + "\" -Exe \"" + exe + "\" -Version \"" + release.Version + "\"");
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Hilfsskript mit Administratorrechten starten
            if (!CanWrite(target)) start.Verb = "runas";
            try { Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
        }

        /// <summary>Ergebnis des letzten Updates (nach dem Neustart mit --nach-update).</summary>
        public static string LastResult()
        {
            try { return File.Exists(LogFile) ? File.ReadAllText(LogFile, Encoding.UTF8).Trim() : null; }
            catch { return null; }
        }

        // Wartet auf das Ende dieser Oberfläche und aller anderen Sync-Pilot-Prozesse mit derselben EXE
        // (z. B. ein Abgleich nach Zeitplan, höchstens 30 Minuten), damit die EXE nicht gesperrt ist.
        const string Script = @"# Sync Pilot – Update (wird vom Programm erzeugt)
param([int]$ProcessId,[string]$Zip,[string]$Exe,[string]$Version)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'Update.log'
$name = Split-Path $Exe -Leaf
$target = Split-Path $Exe -Parent
try {
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  $until = (Get-Date).AddMinutes(30)
  while ((Get-Date) -lt $until) {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $Exe } catch { $false } }
    if (-not $running) { break }
    Start-Sleep -Seconds 2
  }
  Start-Sleep -Milliseconds 500
  $dir = Join-Path $PSScriptRoot 'Neu'
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  Expand-Archive -Path $Zip -DestinationPath $dir -Force
  $new = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1
  if (-not $new) { throw ('Im ZIP fehlt ' + $name) }
  $old = $Exe + '.alt'
  Copy-Item $Exe $old -Force
  try {
    Copy-Item $new.FullName $Exe -Force
    Get-ChildItem $new.DirectoryName -File | Where-Object { $_.Name -ne $name } | Copy-Item -Destination $target -Force
  } catch {
    Copy-Item $old $Exe -Force
    throw
  }
  Remove-Item $old -Force
  Remove-Item $dir -Recurse -Force
  Remove-Item $Zip -Force
  ('OK ' + $Version) | Out-File $log -Encoding utf8
} catch {
  ('FEHLER ' + $_.Exception.Message) | Out-File $log -Encoding utf8
}
Start-Process -FilePath $Exe -ArgumentList '--nach-update'
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
            BackColor = Theme.Bg;
            Height = Theme.P(56);
            foreach (FlatButton b in new FlatButton[] { Install, Page, Later }) Controls.Add(b);
        }

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
            using (SolidBrush brush = new SolidBrush(Theme.Blend(Theme.Bg, Theme.Accent, 0.14f)))
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

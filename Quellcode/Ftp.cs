// Sync Pilot – FTP-Client für ftp://, ftps:// (explizit) und ftpsim:// (implizit)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace SyncPilot
{
    public enum FtpSecurity { None, Explicit, Implicit, Sftp }

    /// <summary>Adresse eines FTP-Ziels: ftp://[benutzer@]server[:port]/pfad (auch ftps://, ftpsim:// und sftp://).</summary>
    public class FtpUrl
    {
        public FtpSecurity Security;
        public string Host;
        public int Port;
        public string Path;
        public string UserInUrl;

        public static bool IsFtp(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            return t.StartsWith("ftp://") || t.StartsWith("ftps://") || t.StartsWith("ftpsim://");
        }

        public static bool IsSftp(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant().StartsWith("sftp://");
        }

        public static bool IsRemote(string s) { return IsFtp(s) || IsSftp(s); }

        public static FtpUrl Parse(string s)
        {
            string t = (s ?? "").Trim();
            int i = t.IndexOf("://", StringComparison.Ordinal);
            if (i < 0) throw new SyncException("Ungültige FTP-Adresse: " + s);
            string scheme = t.Substring(0, i).ToLowerInvariant();
            string rest = t.Substring(i + 3);
            FtpUrl u = new FtpUrl();
            if (scheme == "ftp") { u.Security = FtpSecurity.None; u.Port = 21; }
            else if (scheme == "ftps") { u.Security = FtpSecurity.Explicit; u.Port = 21; }
            else if (scheme == "ftpsim") { u.Security = FtpSecurity.Implicit; u.Port = 990; }
            else if (scheme == "sftp") { u.Security = FtpSecurity.Sftp; u.Port = 22; }
            else throw new SyncException("Unbekanntes Protokoll: " + scheme + "://");

            int slash = rest.IndexOf('/');
            string hostPart = slash < 0 ? rest : rest.Substring(0, slash);
            string path = slash < 0 ? "" : rest.Substring(slash);
            int at = hostPart.LastIndexOf('@');
            if (at >= 0)
            {
                u.UserInUrl = Uri.UnescapeDataString(hostPart.Substring(0, at));
                hostPart = hostPart.Substring(at + 1);
            }
            int colon = hostPart.LastIndexOf(':');
            if (colon > 0 && hostPart.IndexOf(']') < colon)
            {
                int port;
                if (!int.TryParse(hostPart.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                    throw new SyncException("Ungültiger Port in " + s);
                u.Port = port;
                hostPart = hostPart.Substring(0, colon);
            }
            u.Host = hostPart.Trim('[', ']');
            if (u.Host.Length == 0) throw new SyncException("In der FTP-Adresse fehlt der Server: " + s);
            u.Path = Uri.UnescapeDataString(path).TrimEnd('/');
            return u;
        }
    }

    /// <summary>Fehlerantwort des FTP-Servers (z. B. 550 Datei nicht gefunden).</summary>
    public class FtpException : Exception
    {
        public readonly int Code;
        public FtpException(int code, string msg) : base(msg) { Code = code; }
    }

    /// <summary>Das TLS-Zertifikat des Servers ist nicht vertrauenswürdig (z. B. selbst signiert).</summary>
    public class FtpCertificateException : SyncException
    {
        public readonly string Thumbprint;
        public readonly string Subject;
        public readonly string Issuer;
        public readonly string ValidUntil;

        public FtpCertificateException(X509Certificate cert, SslPolicyErrors errors)
            : base("Das Zertifikat des FTP-Servers ist nicht vertrauenswürdig (" + errors + "). " +
                   "Unter „Anmeldung › Verbindung prüfen“ kann es einmal bestätigt werden.")
        {
            Thumbprint = cert == null ? "" : cert.GetCertHashString();
            Subject = cert == null ? "" : cert.Subject;
            Issuer = cert == null ? "" : cert.Issuer;
            ValidUntil = cert == null ? "" : cert.GetExpirationDateString();
        }
    }

    public class FtpReply
    {
        public int Code;
        public string Text = "";
        public List<string> Lines = new List<string>();
    }

    public class FtpItem
    {
        public string Name;
        public bool IsDir;
        public bool IsLink;
        public long Size;
        public DateTime Mtime;
        /// <summary>Genauigkeit der Zeit in Sekunden (0 = sekundengenau aus MLSD, sonst grob aus LIST).</summary>
        public int Precision;
    }

    /// <summary>Datenverbindung (Liste, Download, Upload). Finish() schließt sie und prüft die Antwort des Servers.</summary>
    public sealed class FtpData : Stream
    {
        readonly FtpClient client;
        readonly TcpClient tcp;
        readonly Stream s;
        bool done;

        internal FtpData(FtpClient client, TcpClient tcp, Stream s)
        {
            this.client = client;
            this.tcp = tcp;
            this.s = s;
        }

        public void Finish()
        {
            if (done) return;
            done = true;
            try { s.Close(); } catch { }
            try { tcp.Close(); } catch { }
            FtpReply r;
            try { r = client.ReadReply(); }
            catch
            {
                client.Close(); // Steuerverbindung nicht mehr im Takt: lieber neu verbinden
                throw;
            }
            if (r.Code >= 400) throw new FtpException(r.Code, "FTP-Server: " + r.Code + " " + r.Text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !done)
            {
                try { Finish(); }
                catch { client.Close(); }
            }
            base.Dispose(disposing);
        }

        public override bool CanRead { get { return s.CanRead; } }
        public override bool CanWrite { get { return s.CanWrite; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { s.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) { return s.Read(buffer, offset, count); }
        public override void Write(byte[] buffer, int offset, int count) { s.Write(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }

    /// <summary>Schlanker FTP-Client: passiver Modus, Binärübertragung, MLSD (sonst LIST), MFMT/MDTM, TLS.</summary>
    public sealed class FtpClient : IDisposable
    {
        const int Timeout = 60000;
        const SslProtocols Protocols = (SslProtocols)(3072 | 768 | 192); // TLS 1.2, 1.1, 1.0

        readonly FtpUrl url;
        readonly string user, pass, trusted;
        readonly Encoding enc = new UTF8Encoding(false);
        TcpClient ctrl;
        Stream cs;
        bool tls, noEpsv;
        FtpCertificateException certError;

        public bool HasMlsd, HasMfmt, HasMdtm;
        public string Home = "/";

        public FtpClient(FtpUrl url, string user, string pass, string trustedThumbprint)
        {
            this.url = url;
            this.user = string.IsNullOrEmpty(user) ? url.UserInUrl : user;
            this.pass = pass ?? "";
            trusted = trustedThumbprint ?? "";
        }

        public bool Connected { get { return ctrl != null && ctrl.Connected; } }

        public void Connect()
        {
            Close();
            HasMlsd = HasMfmt = HasMdtm = noEpsv = false;
            Home = "/";
            try
            {
                ctrl = NewTcp(url.Host, url.Port);
            }
            catch (SocketException ex)
            {
                throw new SyncException("FTP-Server " + url.Host + ":" + url.Port + " nicht erreichbar (" + ex.Message + ")");
            }
            try
            {
                Login();
            }
            catch
            {
                Close();
                throw;
            }
        }

        void Login()
        {
            cs = ctrl.GetStream();
            tls = url.Security != FtpSecurity.None;
            if (url.Security == FtpSecurity.Implicit) cs = Secure(cs);
            FtpReply r = ReadReply();
            if (r.Code != 220) throw Fail("Server meldet", r);
            if (url.Security == FtpSecurity.Explicit)
            {
                r = Cmd("AUTH TLS");
                if (r.Code != 234) throw new SyncException("Der Server unterstützt kein FTPS (AUTH TLS): " + r.Code + " " + r.Text);
                cs = Secure(cs);
            }
            r = Cmd("USER " + (string.IsNullOrEmpty(user) ? "anonymous" : user));
            if (r.Code == 331) r = Cmd("PASS " + pass);
            if (r.Code != 230 && r.Code != 202)
                throw new SyncException("FTP-Anmeldung fehlgeschlagen: " + r.Code + " " + r.Text);
            if (tls)
            {
                Cmd("PBSZ 0");
                r = Cmd("PROT P");
                if (r.Code != 200) throw Fail("Verschlüsselte Datenverbindung abgelehnt (PROT P)", r);
            }
            r = Cmd("FEAT");
            if (r.Code == 211)
            {
                foreach (string l in r.Lines)
                {
                    string f = l.Trim().ToUpperInvariant();
                    if (f.StartsWith("MLSD") || f.StartsWith("MLST")) HasMlsd = true;
                    if (f.StartsWith("MFMT")) HasMfmt = true;
                    if (f.StartsWith("MDTM")) HasMdtm = true;
                    if (f.StartsWith("UTF8")) Cmd("OPTS UTF8 ON");
                }
            }
            r = Cmd("TYPE I");
            if (r.Code != 200) throw Fail("Binärmodus (TYPE I) abgelehnt", r);
            r = Cmd("PWD");
            if (r.Code == 257)
            {
                int a = r.Text.IndexOf('"'), b = a >= 0 ? r.Text.IndexOf('"', a + 1) : -1;
                if (a >= 0 && b > a) Home = r.Text.Substring(a + 1, b - a - 1).Replace("\"\"", "\"");
            }
        }

        static TcpClient NewTcp(string host, int port)
        {
            TcpClient c = new TcpClient();
            c.ReceiveTimeout = Timeout;
            c.SendTimeout = Timeout;
            IAsyncResult ar = c.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(20000))
            {
                c.Close();
                throw new SocketException(10060); // Zeitüberschreitung
            }
            c.EndConnect(ar);
            return c;
        }

        static TcpClient NewTcp(IPAddress addr, int port)
        {
            TcpClient c = new TcpClient(addr.AddressFamily);
            c.ReceiveTimeout = Timeout;
            c.SendTimeout = Timeout;
            IAsyncResult ar = c.BeginConnect(addr, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(20000))
            {
                c.Close();
                throw new SocketException(10060);
            }
            c.EndConnect(ar);
            return c;
        }

        Stream Secure(Stream s)
        {
            SslStream ssl = new SslStream(s, false, Validate);
            certError = null;
            try
            {
                ssl.AuthenticateAsClient(url.Host, null, Protocols, false);
            }
            catch (AuthenticationException ex)
            {
                if (certError != null) throw certError;
                throw new SyncException("TLS-Verbindung fehlgeschlagen: " + ex.Message);
            }
            return ssl;
        }

        bool Validate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (cert != null && trusted.Length > 0 && string.Equals(cert.GetCertHashString(), trusted, StringComparison.OrdinalIgnoreCase))
                return true;
            certError = new FtpCertificateException(cert, errors);
            return false;
        }

        static FtpException Fail(string what, FtpReply r)
        {
            return new FtpException(r.Code, what + ": " + r.Code + " " + r.Text);
        }

        // ---------- Steuerverbindung ----------

        public FtpReply Cmd(string line)
        {
            byte[] b = enc.GetBytes(line + "\r\n");
            cs.Write(b, 0, b.Length);
            cs.Flush();
            return ReadReply();
        }

        string ReadLine()
        {
            List<byte> buf = new List<byte>(128);
            while (true)
            {
                int b = cs.ReadByte();
                if (b < 0) throw new IOException("Die FTP-Verbindung wurde vom Server getrennt.");
                if (b == '\n') break;
                buf.Add((byte)b);
            }
            return enc.GetString(buf.ToArray()).TrimEnd('\r');
        }

        internal FtpReply ReadReply()
        {
            FtpReply r = new FtpReply();
            string first = ReadLine();
            if (first.Length < 3 || !int.TryParse(first.Substring(0, 3), out r.Code))
                throw new IOException("Unerwartete Antwort des FTP-Servers: " + first);
            r.Text = first.Length > 4 ? first.Substring(4) : "";
            if (first.Length > 3 && first[3] == '-')
            {
                string end = first.Substring(0, 3) + " ";
                while (true)
                {
                    string l = ReadLine();
                    if (l.StartsWith(end)) break;
                    r.Lines.Add(l);
                }
            }
            return r;
        }

        // ---------- Datenverbindung ----------

        public FtpData OpenData(string command)
        {
            IPAddress addr = ((IPEndPoint)ctrl.Client.RemoteEndPoint).Address;
            int port = -1;
            FtpReply r;
            if (!noEpsv)
            {
                r = Cmd("EPSV");
                if (r.Code == 229)
                {
                    Match m = Regex.Match(r.Text, @"\(\|\|\|(\d+)\|\)");
                    if (m.Success) port = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                else noEpsv = true;
            }
            if (port < 0)
            {
                r = Cmd("PASV");
                if (r.Code != 227) throw Fail("Passiver Modus abgelehnt", r);
                Match m = Regex.Match(r.Text, @"(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)");
                if (!m.Success) throw Fail("Unerwartete PASV-Antwort", r);
                // Die IP aus der Antwort wird bewusst ignoriert (hinter NAT oft falsch), es zählt der Server der Steuerverbindung.
                port = int.Parse(m.Groups[5].Value) * 256 + int.Parse(m.Groups[6].Value);
            }

            TcpClient data = NewTcp(addr, port);
            bool started = false;
            try
            {
                r = Cmd(command);
                if (r.Code != 125 && r.Code != 150) throw Fail(command.Split(' ')[0], r);
                started = true;
                Stream ds = data.GetStream();
                if (tls) ds = Secure(ds);
                return new FtpData(this, data, ds);
            }
            catch
            {
                data.Close();
                if (started)
                {
                    // Abschlussantwort (425/426/226) abholen, sonst passen die folgenden Antworten nicht mehr
                    try { ReadReply(); } catch { Close(); }
                }
                throw;
            }
        }

        // ---------- Befehle ----------

        /// <summary>
        /// Inhalt eines Ordners. incomplete = Zeilen, die nicht gelesen werden konnten (Einträge fehlen evtl.);
        /// hidesDot = Server listet Dateien mit Punkt am Anfang (.htaccess) womöglich nicht auf.
        /// </summary>
        public List<FtpItem> List(string path, out bool incomplete, out bool hidesDot)
        {
            incomplete = false;
            hidesDot = false;
            List<string> lines;
            bool mlsd = HasMlsd;
            if (mlsd) lines = ReadListing("MLSD " + path);
            else
            {
                FtpReply cwd = Cmd("CWD " + path);
                if (cwd.Code != 250) throw Fail("Ordner nicht gefunden", cwd);
                lines = null;
                try { lines = ReadListing("LIST -a"); }
                catch (FtpException) { }
                if (lines == null || lines.Count == 0)
                {
                    // "LIST -a" unbekannt oder als Pfad verstanden: normales LIST, Punktdateien evtl. unsichtbar
                    lines = ReadListing("LIST");
                    hidesDot = true;
                }
            }
            List<FtpItem> items = new List<FtpItem>();
            foreach (string l in lines)
            {
                if (l.Trim().Length == 0 || l.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;
                FtpItem it = mlsd ? ParseMlsd(l) : ParseList(l);
                if (it == null)
                {
                    if (!(mlsd && (l.IndexOf("type=cdir", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   l.IndexOf("type=pdir", StringComparison.OrdinalIgnoreCase) >= 0)))
                        incomplete = true;
                    continue;
                }
                if (it.Name == "." || it.Name == "..") continue;
                items.Add(it);
            }
            return items;
        }

        public List<FtpItem> List(string path)
        {
            bool incomplete, hidesDot;
            return List(path, out incomplete, out hidesDot);
        }

        List<string> ReadListing(string command)
        {
            List<string> lines = new List<string>();
            using (FtpData d = OpenData(command))
            {
                StreamReader sr = new StreamReader(d, enc);
                string l;
                while ((l = sr.ReadLine()) != null) lines.Add(l);
                d.Finish();
            }
            return lines;
        }

        static FtpItem ParseMlsd(string line)
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) return null;
            FtpItem it = new FtpItem();
            it.Name = line.Substring(sp + 1);
            string type = "";
            foreach (string fact in line.Substring(0, sp).Split(';'))
            {
                int eq = fact.IndexOf('=');
                if (eq <= 0) continue;
                string k = fact.Substring(0, eq).ToLowerInvariant(), v = fact.Substring(eq + 1);
                if (k == "type") type = v.ToLowerInvariant();
                else if (k == "size") long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out it.Size);
                else if (k == "modify")
                {
                    DateTime t;
                    if (TryParseStamp(v, out t)) it.Mtime = t;
                }
            }
            if (type == "cdir" || type == "pdir") return null;
            if (type == "dir") it.IsDir = true;
            else if (type != "file") it.IsLink = true; // Verknüpfungen und Sonderdateien nicht anfassen
            return it;
        }

        static readonly Regex UnixList = new Regex(
            @"^([\-dlbcps])\S*\s+\d+\s+(?:\S+\s+){1,2}(\d+)\s+(\w{3})\s+(\d{1,2})\s+(\d{1,2}:\d{2}|\d{4})\s+(.+)$");
        static readonly Regex DosList = new Regex(
            @"^(\d{2})-(\d{2})-(\d{2,4})\s+(\d{1,2}):(\d{2})\s*(AM|PM)?\s+(<DIR>|\d+)\s+(.+)$", RegexOptions.IgnoreCase);
        static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        static FtpItem ParseList(string line)
        {
            Match m = UnixList.Match(line);
            if (m.Success)
            {
                FtpItem it = new FtpItem();
                char t = m.Groups[1].Value[0];
                it.IsDir = t == 'd';
                it.IsLink = t != 'd' && t != '-';
                it.Size = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                it.Name = m.Groups[6].Value;
                if (t == 'l')
                {
                    int arrow = it.Name.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow > 0) it.Name = it.Name.Substring(0, arrow);
                }
                int month = Array.IndexOf(Months, m.Groups[3].Value.ToLowerInvariant()) + 1;
                int day = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                string ty = m.Groups[5].Value;
                try
                {
                    if (month <= 0) throw new FormatException();
                    if (ty.Contains(":"))
                    {
                        string[] hm = ty.Split(':');
                        DateTime now = DateTime.UtcNow;
                        DateTime d = new DateTime(now.Year, month, day, int.Parse(hm[0]), int.Parse(hm[1]), 0, DateTimeKind.Utc);
                        if (d > now.AddDays(2)) d = d.AddYears(-1);
                        it.Mtime = d;
                        it.Precision = 60;
                    }
                    else
                    {
                        it.Mtime = new DateTime(int.Parse(ty), month, day, 0, 0, 0, DateTimeKind.Utc);
                        it.Precision = 86400;
                    }
                }
                catch { it.Precision = 86400; }
                return it;
            }
            m = DosList.Match(line);
            if (m.Success)
            {
                FtpItem it = new FtpItem();
                it.IsDir = m.Groups[7].Value.Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
                if (!it.IsDir) it.Size = long.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture);
                it.Name = m.Groups[8].Value;
                try
                {
                    int year = int.Parse(m.Groups[3].Value);
                    if (year < 100) year += year < 70 ? 2000 : 1900;
                    int hour = int.Parse(m.Groups[4].Value) % 12;
                    if (m.Groups[6].Value.Equals("PM", StringComparison.OrdinalIgnoreCase)) hour += 12;
                    else if (m.Groups[6].Value.Length == 0) hour = int.Parse(m.Groups[4].Value);
                    it.Mtime = new DateTime(year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), hour,
                        int.Parse(m.Groups[5].Value), 0, DateTimeKind.Utc);
                }
                catch { }
                it.Precision = 60;
                return it;
            }
            return null;
        }

        static bool TryParseStamp(string v, out DateTime t)
        {
            t = DateTime.MinValue;
            if (v == null || v.Length < 14) return false;
            return DateTime.TryParseExact(v.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);
        }

        public bool DirExists(string path)
        {
            FtpReply r = Cmd("CWD " + path);
            return r.Code == 250;
        }

        public void MakeDir(string path)
        {
            FtpReply r = Cmd("MKD " + path);
            if (r.Code == 257) return;
            if (DirExists(path)) return;
            throw Fail("Ordner konnte nicht angelegt werden", r);
        }

        public void DeleteFile(string path)
        {
            FtpReply r = Cmd("DELE " + path);
            if (r.Code != 250 && r.Code != 200) throw Fail("Löschen fehlgeschlagen", r);
        }

        public void RemoveDir(string path)
        {
            Cmd("CWD /"); // manche Server löschen den aktuellen Ordner nicht
            FtpReply r = Cmd("RMD " + path);
            if (r.Code != 250 && r.Code != 200) throw Fail("Ordner löschen fehlgeschlagen", r);
        }

        public void Rename(string from, string to)
        {
            FtpReply r = Cmd("RNFR " + from);
            if (r.Code != 350) throw Fail("Umbenennen fehlgeschlagen", r);
            r = Cmd("RNTO " + to);
            if (r.Code != 250 && r.Code != 200) throw Fail("Umbenennen fehlgeschlagen", r);
        }

        /// <summary>Setzt die Änderungszeit (MFMT). false, wenn der Server das nicht kann.</summary>
        public bool SetTime(string path, DateTime utc)
        {
            if (!HasMfmt) return false;
            FtpReply r = Cmd("MFMT " + utc.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " " + path);
            return r.Code == 213;
        }

        /// <summary>Änderungszeit einer Datei (MDTM), sonst null.</summary>
        public DateTime? GetTime(string path)
        {
            if (!HasMdtm) return null;
            FtpReply r = Cmd("MDTM " + path);
            DateTime t;
            if (r.Code == 213 && TryParseStamp(r.Text.Trim(), out t)) return t;
            return null;
        }

        public void Close()
        {
            if (ctrl == null) return;
            try
            {
                if (ctrl.Connected)
                {
                    byte[] b = enc.GetBytes("QUIT\r\n");
                    cs.Write(b, 0, b.Length);
                }
            }
            catch { }
            try { cs.Close(); } catch { }
            try { ctrl.Close(); } catch { }
            ctrl = null;
            cs = null;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
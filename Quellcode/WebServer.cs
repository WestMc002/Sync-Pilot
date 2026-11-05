// Sync Pilot – Webseite für Handy und Tablet (eigener kleiner HTTP-Server, Anmeldung, JSON-Schnittstelle)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SyncPilot
{
    public class WebRequest_
    {
        public string Method = "";
        public string Path = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string Remote = "";

        public string Cookie(string name)
        {
            string c;
            if (!Headers.TryGetValue("Cookie", out c)) return null;
            foreach (string part in c.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim() == name) return part.Substring(eq + 1).Trim();
            }
            return null;
        }

        public string BodyText { get { return Encoding.UTF8.GetString(Body); } }
    }

    public class WebResponse_
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = new byte[0];
        public List<string> ExtraHeaders = new List<string>();

        public static WebResponse_ Json(object o)
        {
            WebResponse_ r = new WebResponse_();
            r.Body = Encoding.UTF8.GetBytes(WebServer.Json.Serialize(o));
            return r;
        }

        public static WebResponse_ Error(int status, string message)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["error"] = message;
            WebResponse_ r = Json(d);
            r.Status = status;
            return r;
        }

        public static WebResponse_ Bytes(string type, byte[] data)
        {
            WebResponse_ r = new WebResponse_();
            r.ContentType = type;
            r.Body = data ?? new byte[0];
            return r;
        }
    }

    /// <summary>
    /// Kleiner HTTP-Server auf eigenem Port (Heimnetz bzw. VPN; mit eigenem Zertifikat auch HTTPS). Braucht keine Administratorrechte;
    /// Windows fragt beim ersten Start einmal nach der Firewall-Freigabe.
    /// </summary>
    public static class WebServer
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        static TcpListener listener;
        static volatile bool running;
        static bool runningHttps;
        public static string LastError;
        public static int Port;

        public static bool Running { get { return running; } }
        public static bool Https { get { return running && runningHttps; } }

        /// <summary>Nach Änderungen an den Einstellungen aufrufen: startet, stoppt oder wechselt den Port.</summary>
        public static void Apply()
        {
            AppSettings s = AppSettings.Current;
            if (Demo.Active) return;
            if (!s.WebEnabled || !s.HasWebPassword)
            {
                Stop();
                LastError = null;
                return;
            }
            if (s.WebHttps)
            {
                try { CertProvider.Get(); }
                catch (Exception ex)
                {
                    Stop();
                    LastError = "HTTPS: " + ex.Message;
                    return;
                }
            }
            if (running && Port == s.WebPort && runningHttps == s.WebHttps) return;
            Stop();
            try
            {
                listener = Listen(s.WebPort);
                Port = s.WebPort;
                runningHttps = s.WebHttps;
                running = true;
                LastError = null;
                Thread t = new Thread(Loop);
                t.IsBackground = true;
                t.Name = "Sync Pilot Webserver";
                t.Start();
            }
            catch (SocketException ex)
            {
                running = false;
                LastError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied
                    ? "Port " + s.WebPort + " ist schon belegt (anderes Programm oder Dienst). Bitte einen anderen Port wählen."
                    : "Port " + s.WebPort + " kann nicht geöffnet werden: " + ex.Message;
            }
        }

        /// <summary>
        /// Port für IPv4 und IPv6 zugleich öffnen (sonst nur IPv4). Exklusiv: Nutzt ein anderes Programm den Port schon
        /// (auch nur auf einer bestimmten Adresse), schlägt der Start fehl, statt dass das andere Programm antwortet.
        /// </summary>
        static TcpListener Listen(int port)
        {
            try
            {
                TcpListener l6 = new TcpListener(IPAddress.IPv6Any, port);
                l6.ExclusiveAddressUse = true;
                l6.Server.DualMode = true;
                l6.Start(50);
                return l6;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied) throw;
            }
            TcpListener l4 = new TcpListener(IPAddress.Any, port);
            l4.ExclusiveAddressUse = true;
            l4.Start(50);
            return l4;
        }

        public static void Stop()
        {
            running = false;
            runningHttps = false;
            try { if (listener != null) listener.Stop(); } catch { }
            listener = null;
        }

        /// <summary>Adressen, unter denen die Seite im Netz erreichbar ist.</summary>
        public static List<string> Addresses()
        {
            List<string> list = new List<string>();
            if (runningHttps)
            {
                list.Add("https://" + HttpsHost() + ":" + Port);
                return list;
            }
            try
            {
                list.Add("http://" + Dns.GetHostName().ToLowerInvariant() + ":" + Port);
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add("http://" + a.Address + ":" + Port);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Name für HTTPS-Adressen: eingetragener Name, sonst der erste Name im Zertifikat.</summary>
        public static string HttpsHost()
        {
            AppSettings s = AppSettings.Current;
            if (!string.IsNullOrEmpty(s.WebHost)) return s.WebHost.Trim();
            if (s.CertMode != "Datei" && !string.IsNullOrEmpty(s.CertName) && !s.CertName.Trim().StartsWith("*")) return s.CertName.Trim();
            try { return CertProvider.FirstDnsName(CertProvider.Get()).Replace("*.", "sync."); }
            catch { return Dns.GetHostName().ToLowerInvariant(); }
        }

        /// <summary>Adresse für Link und QR-Code: eingetragene Adresse, bei HTTPS sonst der Zertifikatsname, sonst die IP im Heimnetz.</summary>
        public static string PhoneUrl()
        {
            AppSettings s = AppSettings.Current;
            int port = running ? Port : s.WebPort;
            bool https = running ? runningHttps : s.WebHttps;
            string host = (s.WebHost ?? "").Trim();
            if (host.Length > 0 || https)
            {
                if (host.Length == 0) host = HttpsHost();
                if (host.Contains(":") && !host.StartsWith("[")) host = "[" + host + "]"; // IPv6
                return (https ? "https://" : "http://") + host + ((https && port == 443) || (!https && port == 80) ? "" : ":" + port);
            }
            string best = null;
            foreach (string a in Addresses())
            {
                string h = a.Substring(7, a.LastIndexOf(':') - 7);
                string u = "http://" + h + ":" + port;
                if (h.StartsWith("192.168.") || h.StartsWith("10.") || h.StartsWith("172.")) return u;
                if (best == null) best = u;
            }
            return best ?? "http://" + Dns.GetHostName().ToLowerInvariant() + ":" + port;
        }

        static void Loop()
        {
            TcpListener l = listener;
            while (running && l != null && l == listener)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch
                {
                    if (!running || l != listener) return;
                    Thread.Sleep(200);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
            }
        }

        static void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 15000;
                    c.SendTimeout = 30000;
                    IPAddress remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
                    if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
                    if (!(AppSettings.Current.WebInternet && runningHttps) && !WebAuth.IsLocal(remote))
                        return; // ohne Internet-Option (und ohne HTTPS) nur Heimnetz und VPN: Verbindung sofort schließen
                    Stream ns = c.GetStream();
                    if (runningHttps)
                    {
                        // erstes Byte 0x16 = TLS-Handshake; sonst normale HTTP-Anfrage -> auf https umleiten
                        int first = ns.ReadByte();
                        if (first < 0) return;
                        ns = new PrefixStream(ns, (byte)first);
                        if (first != 0x16)
                        {
                            WebRequest_ plain = ReadRequest(ns);
                            if (plain == null) return;
                            string host;
                            plain.Headers.TryGetValue("Host", out host);
                            host = SafeHost(host);
                            WebResponse_ redirect = WebResponse_.Bytes("text/plain; charset=utf-8", new byte[0]);
                            redirect.Status = 301;
                            redirect.ExtraHeaders.Add("Location: https://" + (host.Length > 0 ? host : HttpsHost() + ":" + Port) + "/");
                            Write(ns, redirect);
                            return;
                        }
                        X509Certificate2 cert;
                        try { cert = CertProvider.Get(); }
                        catch (Exception ex)
                        {
                            LastError = "HTTPS: " + ex.Message; // z. B. Zertifikat abgelaufen oder entfernt
                            return;
                        }
                        SslStream ssl = new SslStream(ns, false);
                        ssl.AuthenticateAsServer(cert, false, (SslProtocols)(3072 | 768), false); // TLS 1.2 / 1.1
                        ns = ssl;
                    }
                    WebRequest_ rq = ReadRequest(ns);
                    if (rq == null) return;
                    rq.Remote = remote.ToString();
                    WebResponse_ rs;
                    try { rs = WebApi.Handle(rq); }
                    catch (Exception ex) { rs = WebResponse_.Error(500, ex.Message); }
                    Write(ns, rs);
                    ns.Close();
                }
            }
            catch { }
        }

        static string SafeHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            foreach (char ch in host)
                if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == ':' || ch == '[' || ch == ']')) return "";
            return host;
        }

        static WebRequest_ ReadRequest(Stream s)
        {
            // Kopfzeilen bis zur Leerzeile lesen (höchstens 32 KB)
            MemoryStream head = new MemoryStream();
            int state = 0;
            while (state < 4)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                if (head.Length > 32768) return null;
                if ((state == 0 || state == 2) && b == '\r') state++;
                else if ((state == 1 || state == 3) && b == '\n') state++;
                else state = b == '\r' ? 1 : 0;
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            WebRequest_ rq = new WebRequest_();
            rq.Method = first[0].ToUpperInvariant();
            rq.Path = first[1];
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) rq.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            string cl;
            int len;
            if (rq.Headers.TryGetValue("Content-Length", out cl) && int.TryParse(cl, out len) && len > 0)
            {
                if (len > 1024 * 1024) return null;
                byte[] body = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = s.Read(body, got, len - got);
                    if (n <= 0) return null;
                    got += n;
                }
                rq.Body = body;
            }
            return rq;
        }

        static readonly Dictionary<int, string> Reasons = new Dictionary<int, string>
        {
            { 200, "OK" }, { 301, "Moved Permanently" }, { 400, "Bad Request" }, { 401, "Unauthorized" }, { 403, "Forbidden" }, { 404, "Not Found" },
            { 409, "Conflict" }, { 429, "Too Many Requests" }, { 500, "Internal Server Error" }
        };

        static void Write(Stream s, WebResponse_ rs)
        {
            string reason;
            if (!Reasons.TryGetValue(rs.Status, out reason)) reason = "OK";
            StringBuilder h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(rs.Status).Append(' ').Append(reason).Append("\r\n");
            h.Append("Content-Type: ").Append(rs.ContentType).Append("\r\n");
            h.Append("Content-Length: ").Append(rs.Body.Length).Append("\r\n");
            h.Append("Connection: close\r\n");
            h.Append("Cache-Control: no-store\r\n");
            h.Append("X-Content-Type-Options: nosniff\r\n");
            h.Append("X-Frame-Options: DENY\r\n");
            h.Append("Referrer-Policy: no-referrer\r\n");
            h.Append("Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'\r\n");
            foreach (string x in rs.ExtraHeaders) h.Append(x).Append("\r\n");
            h.Append("\r\n");
            byte[] hb = Encoding.UTF8.GetBytes(h.ToString());
            s.Write(hb, 0, hb.Length);
            s.Write(rs.Body, 0, rs.Body.Length);
            s.Flush();
        }

        // ---------- eingebettete Dateien ----------

        static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();

        public static byte[] Resource(string name)
        {
            lock (Cache)
            {
                byte[] b;
                if (Cache.TryGetValue(name, out b)) return b;
                using (Stream rs = typeof(WebServer).Assembly.GetManifestResourceStream(name))
                {
                    if (rs == null) return null;
                    MemoryStream ms = new MemoryStream();
                    rs.CopyTo(ms);
                    b = ms.ToArray();
                }
                Cache[name] = b;
                return b;
            }
        }

        static byte[] _icon;

        public static byte[] AppIconPng()
        {
            if (_icon != null) return _icon;
            Image img = AppImages.AppIcon(256);
            if (img == null) return null;
            using (img)
            using (Bitmap b = new Bitmap(img, 192, 192))
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                _icon = ms.ToArray();
            }
            return _icon;
        }
    }

    /// <summary>Anmeldung der Webseite: Kennwort, Sitzungen (30 Tage), Schutz gegen Durchprobieren.</summary>
    public static class WebAuth
    {
        const int SessionDays = 30;
        static readonly Dictionary<string, int> Fails = new Dictionary<string, int>();
        static readonly Dictionary<string, DateTime> LockedUntil = new Dictionary<string, DateTime>();
        static readonly object LoginGate = new object();

        static string Hash(string token)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).Replace("-", "");
        }

        static bool SlowEquals(string a, string b)
        {
            byte[] x = Encoding.UTF8.GetBytes(a ?? ""), y = Encoding.UTF8.GetBytes(b ?? "");
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < x.Length && i < y.Length; i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        // ---------- Schutz gegen Durchprobieren ----------
        //   5 Fehlversuche von einer Adresse      -> diese Adresse 10 Minuten gesperrt
        //   15 Fehlversuche von außen in 1 Stunde -> Anmeldung von außen 1 Stunde gesperrt (Heimnetz geht weiter)

        const int FailsPerAddress = 5, AddressLockMinutes = 10, ExternalFailsPerHour = 15, ExternalLockMinutes = 60;
        static readonly List<DateTime> externalFails = new List<DateTime>();
        static DateTime externalLockedUntil;

        /// <summary>Bis wann die Anmeldung von außen gesperrt ist (UTC; in der Vergangenheit = nicht gesperrt).</summary>
        public static DateTime ExternalLockedUntil { get { lock (Fails) return externalLockedUntil; } }

        /// <summary>Heimnetz, VPN oder dieser PC: private IPv4-Bereiche, CGNAT/Tailscale, Link-Local, IPv6 ULA.</summary>
        public static bool IsLocal(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) ||
                       (b[0] == 169 && b[1] == 254) || (b[0] == 100 && (b[1] & 0xC0) == 64);
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
            return false;
        }

        static bool IsLocal(string remote)
        {
            IPAddress a;
            return IPAddress.TryParse(remote, out a) && IsLocal(a);
        }

        /// <summary>Gesperrt? Dann Wartezeit in Sekunden, sonst 0. Aufruf innerhalb von lock (Fails).</summary>
        static int LockSeconds(string remote)
        {
            DateTime now = DateTime.UtcNow, until;
            DateTime end = DateTime.MinValue;
            if (LockedUntil.TryGetValue(remote, out until) && until > now) end = until;
            if (!IsLocal(remote) && externalLockedUntil > now && externalLockedUntil > end) end = externalLockedUntil;
            return end > now ? (int)Math.Ceiling((end - now).TotalSeconds) : 0;
        }

        /// <summary>Fehlversuch zählen. Liefert die Wartezeit, falls jetzt gesperrt wird. Aufruf innerhalb von lock (Fails).</summary>
        static int RegisterFail(string remote)
        {
            DateTime now = DateTime.UtcNow;
            int wait = 0;
            int n;
            Fails.TryGetValue(remote, out n);
            if (++n >= FailsPerAddress)
            {
                LockedUntil[remote] = now.AddMinutes(AddressLockMinutes);
                Fails.Remove(remote);
                wait = AddressLockMinutes * 60;
            }
            else Fails[remote] = n;
            if (!IsLocal(remote))
            {
                externalFails.RemoveAll(t => t < now.AddHours(-1));
                externalFails.Add(now);
                if (externalFails.Count >= ExternalFailsPerHour)
                {
                    externalLockedUntil = now.AddMinutes(ExternalLockMinutes);
                    externalFails.Clear();
                    wait = ExternalLockMinutes * 60;
                }
            }
            // abgelaufene Einträge aufräumen, damit viele verschiedene Adressen den Speicher nicht füllen
            if (LockedUntil.Count + Fails.Count > 2000)
            {
                foreach (string k in new List<string>(LockedUntil.Keys)) if (LockedUntil[k] < now) LockedUntil.Remove(k);
                if (Fails.Count > 1000) Fails.Clear();
            }
            return wait;
        }

        static void RegisterSuccess(string remote)
        {
            Fails.Remove(remote);
            LockedUntil.Remove(remote);
        }

        /// <summary>Liefert einen neuen Sitzungsschlüssel oder null. waitSeconds &gt; 0 bei Sperre.</summary>
        public static string Login(string remote, string password, out int waitSeconds)
        {
            // Anmeldungen nacheinander prüfen, damit parallele Versuche die Sperre nicht umgehen
            lock (LoginGate) return LoginLocked(remote, password, out waitSeconds);
        }

        static string LoginLocked(string remote, string password, out int waitSeconds)
        {
            lock (Fails)
            {
                waitSeconds = LockSeconds(remote);
                if (waitSeconds > 0) return null;
            }
            AppSettings s = AppSettings.Current;
            string stored = Secret.Unprotect(s.WebPass);
            if (stored.Length == 0 || !SlowEquals(stored, password))
            {
                Thread.Sleep(800);
                lock (Fails) waitSeconds = RegisterFail(remote);
                return null;
            }
            lock (Fails) RegisterSuccess(remote);
            return NewSession();
        }

        public static bool Valid(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            AppSettings s = AppSettings.Current;
            string h = Hash(token);
            lock (s.WebSessions)
            {
                foreach (string e in s.WebSessions)
                {
                    int bar = e.IndexOf('|');
                    if (bar <= 0) continue;
                    long ticks;
                    if (!long.TryParse(e.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    if (ticks > DateTime.UtcNow.Ticks && SlowEquals(e.Substring(0, bar), h)) return true;
                }
            }
            return false;
        }

        public static void Logout(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            AppSettings s = AppSettings.Current;
            string h = Hash(token);
            lock (s.WebSessions) s.WebSessions.RemoveAll(e => e.StartsWith(h + "|"));
            s.Save();
        }

        /// <summary>Alle Geräte abmelden (z. B. nach Kennwortänderung).</summary>
        public static void LogoutAll()
        {
            AppSettings s = AppSettings.Current;
            lock (s.WebSessions) s.WebSessions.Clear();
            s.Save();
        }

        static void Cleanup(AppSettings s)
        {
            long now = DateTime.UtcNow.Ticks;
            s.WebSessions.RemoveAll(delegate (string e)
            {
                int bar = e.IndexOf('|');
                long ticks;
                return bar <= 0 || !long.TryParse(e.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) || ticks < now;
            });
            while (s.WebSessions.Count > 20) s.WebSessions.RemoveAt(0);
        }

        public static string CookieHeader(string token, bool clear)
        {
            return "Set-Cookie: sp=" + (clear ? "" : token) + "; Path=/; HttpOnly; SameSite=Strict; Max-Age=" +
                   (clear ? "0" : (SessionDays * 86400).ToString(CultureInfo.InvariantCulture)) +
                   (WebServer.Https ? "; Secure" : "");
        }

        // ---------- Koppeln per QR-Code ----------

        static string pairHash;
        static DateTime pairUntil;

        /// <summary>Neuer Einmal-Code für den QR-Code (10 Minuten gültig, ersetzt einen älteren Code).</summary>
        public static string NewPairCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] raw = new byte[20];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in raw) sb.Append(chars[b % chars.Length]);
            string code = sb.ToString();
            lock (Fails)
            {
                pairHash = Hash(code);
                pairUntil = DateTime.UtcNow.AddMinutes(10);
            }
            return code;
        }

        /// <summary>Angezeigten Einmal-Code ungültig machen (z. B. wenn die Einstellungen geschlossen werden).</summary>
        public static void CancelPairCode()
        {
            lock (Fails) pairHash = null;
        }

        /// <summary>Löst einen Einmal-Code gegen eine Sitzung ein (null bei falschem oder abgelaufenem Code).</summary>
        public static string Pair(string remote, string code, out int waitSeconds)
        {
            lock (LoginGate)
            {
                lock (Fails)
                {
                    waitSeconds = LockSeconds(remote);
                    if (waitSeconds > 0) return null;
                    bool ok = pairHash != null && DateTime.UtcNow < pairUntil && SlowEquals(pairHash, Hash((code ?? "").Trim().ToUpperInvariant()));
                    if (ok)
                    {
                        pairHash = null; // nur einmal gültig
                        RegisterSuccess(remote);
                        return NewSession();
                    }
                    waitSeconds = RegisterFail(remote);
                }
                Thread.Sleep(800); // außerhalb der Sperre warten, damit die Oberfläche nicht hängt
                return null;
            }
        }

        static string NewSession()
        {
            AppSettings s = AppSettings.Current;
            byte[] raw = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            string token = BitConverter.ToString(raw).Replace("-", "").ToLowerInvariant();
            lock (s.WebSessions)
            {
                Cleanup(s);
                s.WebSessions.Add(Hash(token) + "|" + DateTime.UtcNow.AddDays(SessionDays).Ticks.ToString(CultureInfo.InvariantCulture));
            }
            s.Save();
            return token;
        }
    }

    /// <summary>Analyse und Abgleich, die über die Webseite gestartet wurden (je Aufgabe ein Zustand).</summary>
    public class RemoteRun
    {
        public string JobId;
        /// <summary>leer, analyse, bereit, sync, fertig, fehler</summary>
        public string Phase = "leer";
        public string Status = "";
        public volatile int ProgI, ProgN;
        public volatile bool Cancel;
        public Analysis An;
        public string Error;
        public string Result;
        public bool ResultBad;
        public int Version;
        public DateTime AnalyzedAt;

        static readonly Dictionary<string, RemoteRun> Runs = new Dictionary<string, RemoteRun>();

        public static RemoteRun Get(string jobId, bool create)
        {
            lock (Runs)
            {
                RemoteRun r;
                if (!Runs.TryGetValue(jobId, out r) && create)
                {
                    r = new RemoteRun();
                    r.JobId = jobId;
                    Runs[jobId] = r;
                }
                return r;
            }
        }

        public bool Busy { get { return Phase == "analyse" || Phase == "sync"; } }

        public static bool SomeBusy
        {
            get
            {
                lock (Runs) return Runs.Values.Any(r => r.Busy);
            }
        }

        public void StartAnalysis(SyncJob job, bool thenSync)
        {
            StartAnalysis(job, thenSync, false);
        }

        public void StartAnalysis(SyncJob job, bool thenSync, bool keepResult)
        {
            lock (this)
            {
                if (Busy) return;
                if (!keepResult) { Result = null; ResultBad = false; }
                AnalyzedAt = DateTime.UtcNow;
                Phase = "analyse";
                Status = "Analysiere …";
                Error = null;
                Cancel = false;
                ProgI = ProgN = 0;
                Version++;
            }
            Thread t = new Thread(delegate ()
            {
                Analysis an = null;
                try
                {
                    an = Engine.Analyze(job, delegate { return Cancel; }, delegate (string s) { Status = s; });
                }
                catch (Exception ex)
                {
                    lock (this)
                    {
                        An = null;
                        Phase = "fehler";
                        Error = ex is OperationCanceledException ? "Analyse abgebrochen" : ex.Message;
                        Status = "";
                        Version++;
                    }
                    return;
                }
                bool sync;
                lock (this)
                {
                    An = an;
                    Phase = "bereit";
                    Status = "Analyse abgeschlossen · " + Fmt.Duration(an.Seconds);
                    Version++;
                    sync = thenSync && an.CountActive > 0 && an.CountConflicts == 0 && Engine.SafetyCheck(an) == null;
                    if (thenSync && !sync && an.CountActive > 0)
                        Status = an.CountConflicts > 0 ? "Es gibt Konflikte – bitte prüfen und dann synchronisieren."
                                                       : "Ungewöhnlich viele Löschungen – bitte prüfen.";
                }
                if (sync) StartSync(job);
            });
            t.IsBackground = true;
            t.Start();
        }

        public void StartSync(SyncJob job)
        {
            Analysis current;
            lock (this)
            {
                if (Phase == "analyse" || Phase == "sync" || An == null) return;
                current = An;
                Phase = "sync";
                Status = "Synchronisiere …";
                Cancel = false;
                ProgI = 0;
                ProgN = current.CountActive;
                Version++;
            }
            Thread t = new Thread(delegate ()
            {
                HistoryEntry h;
                try
                {
                    h = Runner.ExecuteAnalysis(current, false, delegate { return Cancel; },
                        delegate (int i, int n, string text) { ProgI = i; ProgN = n; Status = "Synchronisiere … " + text; });
                }
                catch (Exception ex) { h = Runner.Crash(job, false, ex); }
                string msg;
                if (h.Cancelled) msg = "Abgebrochen nach " + h.Copied + " Kopien und " + h.Deleted + " Löschungen";
                else if (h.Errors > 0 && h.Copied == 0 && h.Deleted == 0 && !string.IsNullOrEmpty(h.Result) && h.Result.StartsWith("Fehler"))
                    msg = h.Result;
                else msg = "Synchronisiert: " + h.Copied + " kopiert, " + h.Deleted + " gelöscht, " + h.Errors + " Fehler (" + Fmt.Size(h.Bytes) + ")";
                lock (this)
                {
                    Result = msg;
                    ResultBad = !h.IsOk;
                    Phase = "fertig";
                    Version++;
                }
                // danach neu analysieren, damit der aktuelle Stand (Konflikte, Übersprungenes) sichtbar ist
                lock (this) Phase = "leer";
                StartAnalysis(job, false, true);
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    /// <summary>Seiten und JSON-Schnittstelle der Webseite.</summary>
    public static class WebApi
    {
        public static WebResponse_ Handle(WebRequest_ rq)
        {
            string path = rq.Path;
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            // Öffentlich: Seite, Skript, Gestaltung, Bilder, Anmeldung
            if (rq.Method == "GET")
            {
                switch (path)
                {
                    case "/": case "/index.html": return Static("web.index.html", "text/html; charset=utf-8");
                    case "/app.js": return Static("web.app.js", "text/javascript; charset=utf-8");
                    case "/app.css": return Static("web.app.css", "text/css; charset=utf-8");
                    case "/theme.css": return WebResponse_.Bytes("text/css; charset=utf-8", Encoding.UTF8.GetBytes(ThemeCss()));
                    case "/logo.png": return Png(WebServer.Resource("GS-Logo.png"));
                    case "/icon.png": return Png(WebServer.AppIconPng());
                    case "/manifest.json": return WebResponse_.Bytes("application/manifest+json; charset=utf-8", Encoding.UTF8.GetBytes(Manifest()));
                }
            }
            if (rq.Method == "POST" && !rq.Headers.ContainsKey("X-Sync-Pilot"))
                return WebResponse_.Error(403, "Ungültige Anfrage.");
            if (rq.Method == "POST" && path == "/api/login") return Login(rq);
            if (rq.Method == "POST" && path == "/api/pair") return Pair(rq);

            string token = rq.Cookie("sp");
            if (!WebAuth.Valid(token)) return WebResponse_.Error(401, "Bitte anmelden.");

            if (rq.Method == "POST" && path == "/api/logout")
            {
                WebAuth.Logout(token);
                WebResponse_ r = WebResponse_.Json(new Dictionary<string, object>());
                r.ExtraHeaders.Add(WebAuth.CookieHeader("", true));
                return r;
            }
            if (rq.Method == "GET" && path == "/api/state") return State();
            if (rq.Method == "GET" && path == "/api/history") return History();
            if (rq.Method == "GET" && path.StartsWith("/api/log/")) return Log(Uri.UnescapeDataString(path.Substring(9)));

            if (path.StartsWith("/api/jobs/"))
            {
                string[] parts = path.Substring(10).Split('/');
                if (parts.Length != 2) return WebResponse_.Error(404, "Unbekannt.");
                SyncJob job = Store.LoadJobs().FirstOrDefault(j => j.Id == parts[0]);
                if (job == null) return WebResponse_.Error(404, "Aufgabe nicht gefunden.");
                RemoteRun run = RemoteRun.Get(job.Id, true);
                Dictionary<string, object> body = ParseBody(rq);
                switch (rq.Method + " " + parts[1])
                {
                    case "GET analysis": return AnalysisJson(job, run);
                    case "POST analyze":
                        run.StartAnalysis(job, Flag(body, "sync") && !job.Disabled); // deaktiviert: nur analysieren
                        return AnalysisJson(job, run);
                    case "POST sync":
                        lock (run)
                        {
                            if (job.Disabled) return WebResponse_.Error(409, "Die Aufgabe ist deaktiviert.");
                            if (run.An == null) return WebResponse_.Error(409, "Bitte zuerst analysieren.");
                            if ((DateTime.UtcNow - run.AnalyzedAt).TotalMinutes > 15)
                                return WebResponse_.Error(409, "Die Analyse ist \u00e4lter als 15 Minuten \u2013 bitte neu analysieren.");
                            string warn = Engine.SafetyCheck(run.An);
                            if (warn != null && !Flag(body, "force"))
                            {
                                Dictionary<string, object> d = new Dictionary<string, object>();
                                d["confirm"] = "Achtung: " + warn + ". Trotzdem synchronisieren?";
                                WebResponse_ r = WebResponse_.Json(d);
                                r.Status = 409;
                                return r;
                            }
                        }
                        run.StartSync(job);
                        return AnalysisJson(job, run);
                    case "POST cancel":
                        run.Cancel = true;
                        return AnalysisJson(job, run);
                    case "POST choose":
                        return Choose(job, run, body);
                }
            }
            return WebResponse_.Error(404, "Unbekannt.");
        }

        static WebResponse_ Static(string name, string type)
        {
            byte[] b = WebServer.Resource(name);
            return b == null ? WebResponse_.Error(404, "Fehlt: " + name) : WebResponse_.Bytes(type, b);
        }

        static WebResponse_ Png(byte[] b)
        {
            return b == null ? WebResponse_.Error(404, "Bild fehlt.") : WebResponse_.Bytes("image/png", b);
        }

        static Dictionary<string, object> ParseBody(WebRequest_ rq)
        {
            if (rq.Body.Length == 0) return new Dictionary<string, object>();
            try { return WebServer.Json.Deserialize<Dictionary<string, object>>(rq.BodyText) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        static bool Flag(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static WebResponse_ Login(WebRequest_ rq)
        {
            Dictionary<string, object> body = ParseBody(rq);
            object pw;
            body.TryGetValue("password", out pw);
            int wait;
            string token = WebAuth.Login(rq.Remote, pw as string ?? "", out wait);
            if (token == null)
            {
                WebResponse_ e = WebResponse_.Error(wait > 0 ? 429 : 401,
                    wait > 0 ? "Zu viele Fehlversuche. Bitte in " + Math.Max(1, (wait + 59) / 60) + " Minute(n) erneut versuchen." : "Kennwort falsch.");
                return e;
            }
            WebResponse_ r = WebResponse_.Json(new Dictionary<string, object>());
            r.ExtraHeaders.Add(WebAuth.CookieHeader(token, false));
            return r;
        }

        static WebResponse_ Pair(WebRequest_ rq)
        {
            Dictionary<string, object> body = ParseBody(rq);
            object code;
            body.TryGetValue("code", out code);
            int wait;
            string token = WebAuth.Pair(rq.Remote, code as string ?? "", out wait);
            if (token == null)
                return WebResponse_.Error(wait > 0 ? 429 : 401, wait > 0 ? "Zu viele Fehlversuche. Bitte sp\u00e4ter erneut versuchen."
                    : "Der QR-Code ist abgelaufen oder wurde schon benutzt. Bitte in Sync Pilot einen neuen anzeigen.");
            WebResponse_ r = WebResponse_.Json(new Dictionary<string, object>());
            r.ExtraHeaders.Add(WebAuth.CookieHeader(token, false));
            return r;
        }

        // ---------- Daten ----------

        static string Kind(HistoryEntry h)
        {
            switch (TasksView.StatusKind(h))
            {
                case 1: return "ok";
                case 2: return "warn";
                case 3: return "err";
                default: return "none";
            }
        }

        static string Device(string key, string path)
        {
            string k = key == null || key == "Auto" ? Devices.Detect(path) : key;
            return k.ToLowerInvariant();
        }

        static WebResponse_ State()
        {
            List<SyncJob> jobs = Store.LoadJobs();
            List<HistoryEntry> history = Store.LoadHistory();
            List<object> list = new List<object>();
            foreach (SyncJob j in jobs)
            {
                HistoryEntry h = Store.LastRun(history, j.Id);
                string text;
                Color fg, bg;
                TasksView.Status(h, out text, out fg, out bg);
                RemoteRun run = RemoteRun.Get(j.Id, false);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = j.Id;
                d["name"] = j.Name;
                d["a"] = j.FolderA;
                d["b"] = j.FolderB;
                d["devA"] = Device(j.DeviceA, j.FolderA);
                d["devB"] = Device(j.DeviceB, j.FolderB);
                d["mode"] = j.Mode.ToString();
                d["modeText"] = j.ModeText;
                d["schedule"] = j.ScheduleText;
                d["status"] = text;
                d["kind"] = Kind(h);
                d["last"] = h == null ? "Noch nie" : Fmt.When(h.Start);
                d["busy"] = run != null && run.Busy;
                d["phase"] = run == null ? "leer" : run.Phase;
                list.Add(d);
            }
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["version"] = AppInfo.Version;
            r["pc"] = Environment.MachineName; // Info: "Verbunden mit"
            r["year"] = DateTime.Now.Year;
            r["jobs"] = list;
            return WebResponse_.Json(r);
        }

        static string Time(FileEntry e)
        {
            if (e == null) return "—";
            if (e.IsDir) return "";
            return e.Mtime.ToLocalTime().ToString("dd.MM.yy HH:mm");
        }

        static WebResponse_ AnalysisJson(SyncJob job, RemoteRun run)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            lock (run)
            {
                r["phase"] = run.Phase;
                r["status"] = run.Status;
                r["progI"] = run.ProgI;
                r["progN"] = run.ProgN;
                r["error"] = run.Error;
                r["result"] = run.Result;
                r["resultBad"] = run.ResultBad;
                r["version"] = run.Version;
                r["name"] = job.Name;
                r["a"] = job.FolderA;
                r["b"] = job.FolderB;
                r["modeText"] = job.ModeText;
                Analysis an = run.An;
                if (an != null && run.Phase != "analyse")
                {
                    r["filesA"] = an.FilesA;
                    r["filesB"] = an.FilesB;
                    r["sizeA"] = Fmt.Size(an.BytesA);
                    r["sizeB"] = Fmt.Size(an.BytesB);
                    r["active"] = an.CountActive;
                    r["transfer"] = Fmt.Size(an.TransferBytes);
                    r["conflicts"] = an.CountConflicts;
                    r["deletes"] = an.CountDeletes;
                    r["warn"] = Engine.SafetyCheck(an);
                    List<object> items = new List<object>();
                    int max = Math.Min(an.Actions.Count, 5000);
                    for (int i = 0; i < max; i++)
                    {
                        PlanItem it = an.Actions[i];
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["i"] = i;
                        d["rel"] = it.IsDir ? it.Rel + "\\" : it.Rel;
                        d["dir"] = it.IsDir;
                        d["chosen"] = it.Chosen.ToString();
                        d["proposed"] = it.Proposed.ToString();
                        d["label"] = Engine.ActionText(it.Chosen, it.IsDir);
                        d["reason"] = it.Reason;
                        d["size"] = it.IsDir ? "Ordner" : Fmt.Size((it.A ?? it.B).Size);
                        d["ta"] = Time(it.A);
                        d["tb"] = Time(it.B);
                        d["hasA"] = it.A != null;
                        d["hasB"] = it.B != null;
                        items.Add(d);
                    }
                    r["items"] = items;
                    r["more"] = an.Actions.Count - max;
                }
            }
            return WebResponse_.Json(r);
        }

        static WebResponse_ Choose(SyncJob job, RemoteRun run, Dictionary<string, object> body)
        {
            lock (run)
            {
                if (run.An == null || run.Busy) return WebResponse_.Error(409, "Gerade nicht möglich.");
                object action, items, rels;
                body.TryGetValue("action", out action);
                body.TryGetValue("items", out items);
                body.TryGetValue("rels", out rels);
                string a = action as string ?? "";
                System.Collections.IList idx = items as System.Collections.IList;
                System.Collections.IList names = rels as System.Collections.IList;
                if (idx == null || names == null || names.Count != idx.Count) return WebResponse_.Error(400, "Keine Einträge gewählt.");
                for (int n = 0; n < idx.Count; n++)
                {
                    object o = idx[n];
                    if (!(o is int)) continue;
                    int i = (int)o;
                    if (i < 0 || i >= run.An.Actions.Count) continue;
                    PlanItem it = run.An.Actions[i];
                    string rel = it.IsDir ? it.Rel + "\\" : it.Rel;
                    if (rel != names[n] as string) return WebResponse_.Error(409, "Die Liste hat sich geändert – bitte erneut auswählen.");
                    if (a == "Vorschlag") it.Chosen = it.Proposed;
                    else
                    {
                        ActionKind k;
                        if (Enum.TryParse(a, out k) && it.CanChoose(k)) it.Chosen = k;
                    }
                }
                run.Version++;
            }
            return AnalysisJson(job, run);
        }

        static string Key(HistoryEntry h)
        {
            return h.Start.Ticks.ToString(CultureInfo.InvariantCulture) + "_" + (h.JobId ?? "");
        }

        static WebResponse_ History()
        {
            List<HistoryEntry> history = Store.LoadHistory();
            List<object> list = new List<object>();
            for (int i = history.Count - 1; i >= 0 && list.Count < 200; i--)
            {
                HistoryEntry h = history[i];
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["key"] = Key(h);
                d["when"] = Fmt.When(h.Start);
                d["job"] = h.JobName;
                d["result"] = Fmt.Result(h);
                d["kind"] = Kind(h);
                d["copied"] = h.Copied;
                d["deleted"] = h.Deleted;
                d["conflicts"] = h.Conflicts;
                d["errors"] = h.Errors;
                d["duration"] = Fmt.Duration(h.Seconds);
                d["scheduled"] = h.Scheduled;
                d["hasLog"] = !string.IsNullOrEmpty(h.LogFile) && File.Exists(h.LogFile);
                list.Add(d);
            }
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["runs"] = list;
            return WebResponse_.Json(r);
        }

        static WebResponse_ Log(string key)
        {
            HistoryEntry h = Store.LoadHistory().FirstOrDefault(x => Key(x) == key);
            if (h == null || string.IsNullOrEmpty(h.LogFile)) return WebResponse_.Error(404, "Protokoll nicht gefunden.");
            // nur Dateien aus dem Protokollordner ausliefern
            string full = Path.GetFullPath(h.LogFile);
            if (!full.StartsWith(Path.GetFullPath(Store.LogDir) + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return WebResponse_.Error(404, "Protokoll nicht gefunden.");
            string text;
            using (FileStream fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, fs.Length - 300 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                if (start > 0) text = "… (gekürzt, nur das Ende)\n" + text;
            }
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["job"] = h.JobName;
            r["when"] = Fmt.When(h.Start);
            r["text"] = text;
            return WebResponse_.Json(r);
        }

        // ---------- Gestaltung ----------

        static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        /// <summary>Windows-Akzentfarbe für die Webseite (hell: Dunkel1, dunkel: Hell2 – wie im Programm).</summary>
        static string ThemeCss()
        {
            Color[] p = AccentColors.Palette;
            Color light = p != null ? p[4] : Color.FromArgb(0, 103, 192);
            Color lightHover = p != null ? p[5] : Color.FromArgb(25, 117, 197);
            Color dark = p != null ? p[1] : Color.FromArgb(76, 194, 255);
            Color darkHover = p != null ? p[2] : Color.FromArgb(71, 177, 232);
            string onLight = AccentColors.Luminance(light) > 0.5 ? "#141414" : "#FFFFFF";
            string onDark = AccentColors.Luminance(dark) > 0.5 ? "#141414" : "#FFFFFF";
            return ":root{--accent:" + Hex(light) + ";--accent-hover:" + Hex(lightHover) + ";--on-accent:" + onLight + ";" +
                   "--accent-soft:" + Hex(AccentColors.Mix(light, Color.FromArgb(243, 243, 243), 0.10)) + ";--accent-text:" + Hex(lightHover) + ";}" +
                   "@media (prefers-color-scheme: dark){:root{--accent:" + Hex(dark) + ";--accent-hover:" + Hex(darkHover) +
                   ";--on-accent:" + onDark + ";--accent-soft:" + Hex(AccentColors.Mix(p != null ? p[3] : dark, Color.FromArgb(32, 32, 32), 0.20)) +
                   ";--accent-text:" + Hex(dark) + ";}}";
        }

        static string Manifest()
        {
            return "{\"name\":\"Sync Pilot\",\"short_name\":\"Sync Pilot\",\"start_url\":\"/\",\"display\":\"standalone\"," +
                   "\"background_color\":\"#F3F3F3\",\"theme_color\":\"#F3F3F3\",\"icons\":[{\"src\":\"/icon.png\",\"sizes\":\"192x192\",\"type\":\"image/png\"}]}";
        }
    }

    /// <summary>
    /// Zertifikat für HTTPS: aus dem Windows-Zertifikatspeicher (passend zur Domain, das neueste gültige – so werden
    /// Verlängerungen von Let's Encrypt automatisch übernommen) oder aus einer PFX-Datei. Wird alle 30 Minuten neu geladen.
    /// </summary>
    public static class CertProvider
    {
        static readonly object Sync = new object();
        static X509Certificate2 cached;
        static string cachedKey;
        static DateTime loadedAt;

        /// <summary>Zertifikat beim nächsten Zugriff neu laden (nach Änderungen).</summary>
        public static void Reset()
        {
            lock (Sync) cached = null;
        }

        static DateTime certStamp, keyStamp, lastCheck;

        static DateTime Stamp(string file)
        {
            try { return string.IsNullOrEmpty(file) ? DateTime.MinValue : File.GetLastWriteTimeUtc(file); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Aktuelles Zertifikat. Dateien werden neu geladen, sobald sie sich ändern (höchstens alle 30 Sekunden geprüft),
        /// der Zertifikatspeicher alle 30 Minuten (Verlängerungen, z. B. durch win-acme).
        /// </summary>
        public static X509Certificate2 Get()
        {
            AppSettings s = AppSettings.Current;
            bool file = s.CertMode == "Datei";
            string key = s.CertMode + "|" + s.CertName + "|" + s.CertFile + "|" + s.CertKeyFile + "|" + s.CertPass;
            lock (Sync)
            {
                if (cached != null && key == cachedKey && cached.NotAfter > DateTime.Now)
                {
                    if (!file && (DateTime.UtcNow - loadedAt).TotalMinutes < 30) return cached;
                    if (file)
                    {
                        if ((DateTime.UtcNow - lastCheck).TotalSeconds < 30) return cached;
                        lastCheck = DateTime.UtcNow;
                        if (Stamp(s.CertFile) == certStamp && Stamp(s.CertKeyFile) == keyStamp) return cached;
                    }
                }
                X509Certificate2 c;
                try { c = Load(s.CertMode, s.CertName, s.CertFile, s.CertKeyFile, s.CertPass, null); }
                catch
                {
                    // Neu laden fehlgeschlagen (z. B. Datei gerade in Arbeit): gültiges bisheriges Zertifikat weiter verwenden
                    if (cached != null && key == cachedKey && cached.NotAfter > DateTime.Now)
                    {
                        loadedAt = lastCheck = DateTime.UtcNow;
                        return cached;
                    }
                    throw;
                }
                cached = c;
                cachedKey = key;
                loadedAt = lastCheck = DateTime.UtcNow;
                certStamp = Stamp(s.CertFile);
                keyStamp = Stamp(s.CertKeyFile);
                return c;
            }
        }

        /// <summary>
        /// Zertifikat laden: "Datei" = .crt/.pem/.cer mit privatem Schlüssel (.key/.pem) oder .pfx/.p12 mit Kennwort,
        /// sonst aus dem Windows-Zertifikatspeicher nach Domain. testKey: eigener Schlüsselname zum Prüfen (null = Server).
        /// </summary>
        public static X509Certificate2 Load(string mode, string name, string file, string keyFile, string protectedPass, string testKey)
        {
            X509Certificate2 c;
            if (mode == "Datei")
            {
                try
                {
                    c = testKey != null
                        ? CertLoader.Load(file, keyFile, Secret.Unprotect(protectedPass), testKey)
                        : CertLoader.Load(file, keyFile, Secret.Unprotect(protectedPass));
                }
                catch (InvalidOperationException ex) { throw new SyncException(ex.Message); }
                catch (FileNotFoundException ex) { throw new SyncException(ex.Message); }
                catch (CryptographicException ex) { throw new SyncException("Zertifikat nicht lesbar: " + ex.Message); }
                if (c.NotAfter < DateTime.Now) throw new SyncException("Das Zertifikat ist am " + c.NotAfter.ToString("dd.MM.yyyy") + " abgelaufen.");
            }
            else
            {
                if (string.IsNullOrEmpty(name)) throw new SyncException("Bitte die Domain des Zertifikats angeben.");
                c = null;
                // Benutzer und Computer; "WebHosting" des Computers, weil win-acme Zertifikate oft dort ablegt
                X509Store[] stores =
                {
                    new X509Store(StoreName.My, StoreLocation.CurrentUser),
                    new X509Store(StoreName.My, StoreLocation.LocalMachine),
                    new X509Store("WebHosting", StoreLocation.LocalMachine)
                };
                foreach (X509Store store in stores)
                {
                    try
                    {
                        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                        foreach (X509Certificate2 x in store.Certificates)
                        {
                            if (!x.HasPrivateKey || x.NotBefore > DateTime.Now || x.NotAfter < DateTime.Now) continue;
                            if (!Matches(x, name.Trim())) continue;
                            if (c == null || x.NotAfter > c.NotAfter) c = x;
                        }
                    }
                    catch (CryptographicException) { }
                    finally { store.Close(); }
                }
                if (c == null)
                    throw new SyncException("Kein g\u00fcltiges Zertifikat mit privatem Schl\u00fcssel f\u00fcr \u201e" + name + "\u201c im Zertifikatspeicher gefunden.");
            }
            try
            {
                // Zugriff auf den Schlüssel prüfen (RSA oder ECDSA, auch CNG-Schlüssel)
                using (RSA rsa = c.GetRSAPrivateKey())
                    if (rsa == null)
                        using (ECDsa ec = c.GetECDsaPrivateKey())
                            if (ec == null) throw new SyncException("Der Schl\u00fcsseltyp des Zertifikats wird nicht unterst\u00fctzt.");
            }
            catch (CryptographicException)
            {
                throw new SyncException("Kein Zugriff auf den privaten Schl\u00fcssel. Liegt das Zertifikat beim Computer (z. B. von win-acme), " +
                    "in certlm.msc beim Zertifikat \u201eAlle Aufgaben \u203a Private Schl\u00fcssel verwalten \u2026\u201c dem eigenen Benutzer Lesen erlauben " +
                    "\u2013 oder das Zertifikat als Datei angeben.");
            }
            catch (NotSupportedException) { }
            return c;
        }

        /// <summary>Alle DNS-Namen des Zertifikats (Antragsteller und alternative Namen).</summary>
        public static List<string> DnsNames(X509Certificate2 c)
        {
            List<string> names = new List<string>();
            string cn = c.GetNameInfo(X509NameType.DnsName, false);
            if (!string.IsNullOrEmpty(cn)) names.Add(cn);
            foreach (X509Extension ext in c.Extensions)
            {
                if (ext.Oid == null || ext.Oid.Value != "2.5.29.17") continue;
                // Format ist sprachabhängig ("DNS Name=…" bzw. "DNS-Name=…"), daher nur den Teil nach "=" nehmen
                foreach (string part in ext.Format(true).Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    if (eq < 0) continue;
                    string label = part.Substring(0, eq).Trim().ToUpperInvariant();
                    if (!label.StartsWith("DNS")) continue;
                    string v = part.Substring(eq + 1).Trim();
                    if (v.Length > 0 && !names.Contains(v, StringComparer.OrdinalIgnoreCase)) names.Add(v);
                }
            }
            return names;
        }

        public static string FirstDnsName(X509Certificate2 c)
        {
            List<string> n = DnsNames(c);
            return n.Count > 0 ? n[0] : "";
        }

        static bool Matches(X509Certificate2 c, string host)
        {
            foreach (string n in DnsNames(c))
            {
                if (string.Equals(n, host, StringComparison.OrdinalIgnoreCase)) return true;
                if (n.StartsWith("*.") && host.IndexOf('.') > 0 && host.EndsWith(n.Substring(1), StringComparison.OrdinalIgnoreCase) &&
                    host.IndexOf('.') == host.Length - n.Length + 1) return true;
            }
            return false;
        }
    }

    /// <summary>Stream, der ein bereits gelesenes erstes Byte wieder voranstellt (Erkennung HTTP/HTTPS).</summary>
    internal sealed class PrefixStream : Stream
    {
        private readonly Stream _inner;
        private int _prefix;

        public PrefixStream(Stream inner, byte first)
        {
            _inner = inner;
            _prefix = first;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (_prefix >= 0)
            {
                buffer[offset] = (byte)_prefix;
                _prefix = -1;
                return 1;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); }
        public override void Flush() { _inner.Flush(); }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override int ReadTimeout { get { return _inner.ReadTimeout; } set { _inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return _inner.WriteTimeout; } set { _inner.WriteTimeout = value; } }
        public override bool CanTimeout { get { return _inner.CanTimeout; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

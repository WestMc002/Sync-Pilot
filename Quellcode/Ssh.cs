// Sync Pilot – SSH-Verbindung für SFTP (Schlüsseltausch, Verschlüsselung, Anmeldung, Kanal)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Eigene, schlanke Umsetzung von RFC 4253 (Transport), 4252 (Anmeldung) und 4254 (Kanal):
//   Schlüsseltausch  curve25519-sha256, diffie-hellman-group16-sha512, diffie-hellman-group14-sha256
//   Server-Schlüssel ssh-ed25519, ecdsa-sha2-nistp256/384/521, rsa-sha2-512/256, ssh-rsa
//   Verschlüsselung  aes256-ctr, aes192-ctr, aes128-ctr
//   Prüfsumme        hmac-sha2-256/512 (auch -etm@openssh.com)
//   Anmeldung        password, keyboard-interactive
// Der Server-Schlüssel wird beim ersten Mal bestätigt (Fingerabdruck SHA256:…) und danach geprüft.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SyncPilot
{
    /// <summary>Protokollfehler oder Ablehnung durch den SSH-Server.</summary>
    public class SshException : SyncException
    {
        public SshException(string msg) : base(msg) { }
    }

    /// <summary>Der Server-Schlüssel ist noch nicht bestätigt bzw. hat sich geändert.</summary>
    public class SshHostKeyException : SyncException
    {
        public readonly string Fingerprint;
        public readonly string KeyType;
        public readonly bool Changed;

        public SshHostKeyException(string fingerprint, string keyType, bool changed)
            : base(changed
                ? "ACHTUNG: Der Schlüssel des SFTP-Servers hat sich geändert (" + fingerprint + "). Das kann eine Neuinstallation " +
                  "des Servers sein – oder ein Angriff. Nur wenn die Änderung bekannt ist, unter „Anmeldung › Verbindung prüfen“ neu bestätigen."
                : "Der Schlüssel des SFTP-Servers ist noch nicht bestätigt. Bitte einmal unter „Anmeldung › Verbindung prüfen“ bestätigen.")
        {
            Fingerprint = fingerprint;
            KeyType = keyType;
            Changed = changed;
        }
    }

    /// <summary>Baut SSH-Datenfelder zusammen (uint32, string, mpint …).</summary>
    public sealed class SshBuf
    {
        readonly MemoryStream ms = new MemoryStream();

        public SshBuf Byte(int b) { ms.WriteByte((byte)b); return this; }
        public SshBuf Bool(bool b) { ms.WriteByte(b ? (byte)1 : (byte)0); return this; }
        public SshBuf U32(uint v)
        {
            ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
            return this;
        }
        public SshBuf U64(ulong v) { U32((uint)(v >> 32)); return U32((uint)v); }
        public SshBuf Raw(byte[] b) { ms.Write(b, 0, b.Length); return this; }
        public SshBuf Raw(byte[] b, int off, int n) { ms.Write(b, off, n); return this; }
        public SshBuf Str(byte[] b) { U32((uint)b.Length); return Raw(b); }
        public SshBuf Str(byte[] b, int off, int n) { U32((uint)n); return Raw(b, off, n); }
        public SshBuf Str(string s) { return Str(Encoding.UTF8.GetBytes(s)); }
        public SshBuf MpInt(BigInteger x) { return Raw(Ssh.MpInt(x)); }
        public byte[] ToArray() { return ms.ToArray(); }
        public int Length { get { return (int)ms.Length; } }
    }

    /// <summary>Liest SSH-Datenfelder; wirft SshException bei zu kurzen Daten.</summary>
    public sealed class SshReader
    {
        readonly byte[] d;
        int p;
        readonly int end;

        public SshReader(byte[] data, int start) : this(data, start, data.Length) { }
        public SshReader(byte[] data, int start, int end) { d = data; p = start; this.end = end; }

        public bool AtEnd { get { return p >= end; } }

        void Need(int n) { if (n < 0 || n > end - p) throw new SshException("Ungültige Daten vom Server (zu kurz)."); }
        public byte Byte() { Need(1); return d[p++]; }
        public bool Bool() { return Byte() != 0; }
        public uint U32()
        {
            Need(4);
            uint v = (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);
            p += 4;
            return v;
        }
        public ulong U64() { ulong hi = U32(); return hi << 32 | U32(); }
        public byte[] Str()
        {
            uint n = U32();
            if (n > (uint)(end - p)) throw new SshException("Ungültige Daten vom Server (zu kurz).");
            byte[] b = new byte[n];
            Buffer.BlockCopy(d, p, b, 0, (int)n);
            p += (int)n;
            return b;
        }
        public string Text() { return Encoding.UTF8.GetString(Str()); }
        public string[] NameList()
        {
            string s = Encoding.ASCII.GetString(Str());
            return s.Length == 0 ? new string[0] : s.Split(',');
        }
        public BigInteger MpInt()
        {
            byte[] b = Str();
            if (b.Length == 0) return BigInteger.Zero;
            Array.Reverse(b);
            return new BigInteger(b); // Zweierkomplement wie mpint
        }
    }

    /// <summary>Hilfsfunktionen für Zahlen und Schlüssel.</summary>
    public static class Ssh
    {
        static readonly RandomNumberGenerator Rng = new RNGCryptoServiceProvider();

        public static byte[] Random(int n)
        {
            byte[] b = new byte[n];
            lock (Rng) Rng.GetBytes(b);
            return b;
        }

        /// <summary>mpint-Kodierung (mit Längenfeld) einer nicht negativen Zahl.</summary>
        public static byte[] MpInt(BigInteger x)
        {
            if (x.Sign < 0) throw new ArgumentException("negativ");
            if (x.IsZero) return new byte[4];
            byte[] le = x.ToByteArray(); // kleinstmögliches Zweierkomplement, little endian
            Array.Reverse(le);
            return new SshBuf().Str(le).ToArray();
        }

        /// <summary>Vorzeichenlose Bytes (big endian) als positive Zahl.</summary>
        public static BigInteger Unsigned(byte[] be)
        {
            byte[] le = new byte[be.Length + 1];
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        /// <summary>Positive Zahl als vorzeichenlose Bytes (big endian) ohne führende Nullen bzw. auf feste Länge.</summary>
        public static byte[] ToUnsigned(BigInteger x, int length)
        {
            byte[] le = x.ToByteArray();
            int n = le.Length;
            while (n > 1 && le[n - 1] == 0) n--;
            if (length <= 0) length = n;
            if (n > length) throw new SshException("Zahl zu groß.");
            byte[] be = new byte[length];
            for (int i = 0; i < n; i++) be[length - 1 - i] = le[i];
            return be;
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Fingerabdruck wie bei OpenSSH: SHA256:Base64 ohne Füllzeichen.</summary>
        public static string Fingerprint(byte[] hostKeyBlob)
        {
            using (SHA256 h = SHA256.Create())
                return "SHA256:" + Convert.ToBase64String(h.ComputeHash(hostKeyBlob)).TrimEnd('=');
        }
    }

    /// <summary>AES im Zählermodus (aes*-ctr). Verarbeitet immer ganze 16-Byte-Blöcke.</summary>
    sealed class AesCtr : IDisposable
    {
        readonly Aes aes;
        readonly ICryptoTransform enc;
        readonly byte[] counter = new byte[16];
        byte[] blocks = new byte[0], stream = new byte[0];

        public AesCtr(byte[] key, byte[] iv)
        {
            aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            enc = aes.CreateEncryptor(key, new byte[16]);
            Buffer.BlockCopy(iv, 0, counter, 0, 16);
        }

        public void Process(byte[] buf, int off, int len)
        {
            if (len % 16 != 0) throw new SshException("Interner Fehler: Blockgröße.");
            if (blocks.Length < len) { blocks = new byte[len]; stream = new byte[len]; }
            for (int b = 0; b < len; b += 16)
            {
                Buffer.BlockCopy(counter, 0, blocks, b, 16);
                for (int i = 15; i >= 0; i--) if (++counter[i] != 0) break;
            }
            enc.TransformBlock(blocks, 0, len, stream, 0);
            for (int i = 0; i < len; i++) buf[off + i] ^= stream[i];
        }

        public void Dispose()
        {
            enc.Dispose();
            aes.Dispose();
        }
    }

    /// <summary>X25519 (RFC 7748) und Ed25519-Prüfung (RFC 8032), mit BigInteger gerechnet.</summary>
    static class Curve25519
    {
        static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        static readonly BigInteger D = Mod(-121665 * Inv(121666));
        static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);
        static readonly BigInteger[] Base = MakeBase();

        static BigInteger Mod(BigInteger a) { BigInteger r = a % P; return r.Sign < 0 ? r + P : r; }
        static BigInteger Inv(BigInteger a) { return BigInteger.ModPow(Mod(a), P - 2, P); }

        static BigInteger LittleEndian(byte[] b, int off, int len)
        {
            byte[] le = new byte[len + 1];
            Buffer.BlockCopy(b, off, le, 0, len);
            return new BigInteger(le);
        }

        static byte[] ToLittleEndian32(BigInteger x)
        {
            byte[] le = x.ToByteArray();
            byte[] r = new byte[32];
            Buffer.BlockCopy(le, 0, r, 0, Math.Min(32, le.Length));
            return r;
        }

        public static byte[] X25519(byte[] scalar, byte[] u)
        {
            byte[] k = (byte[])scalar.Clone();
            k[0] &= 248; k[31] &= 127; k[31] |= 64;
            BigInteger kk = LittleEndian(k, 0, 32);
            byte[] uu = (byte[])u.Clone();
            uu[31] &= 127;
            BigInteger x1 = Mod(LittleEndian(uu, 0, 32));
            BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1, t;
            int swap = 0;
            for (int i = 254; i >= 0; i--)
            {
                int bit = (int)((kk >> i) & 1);
                if ((swap ^ bit) != 0) { t = x2; x2 = x3; x3 = t; t = z2; z2 = z3; z3 = t; }
                swap = bit;
                BigInteger A = Mod(x2 + z2), AA = Mod(A * A), B = Mod(x2 - z2), BB = Mod(B * B), E = Mod(AA - BB);
                BigInteger C = Mod(x3 + z3), Dd = Mod(x3 - z3), DA = Mod(Dd * A), CB = Mod(C * B);
                x3 = Mod((DA + CB) * (DA + CB));
                z3 = Mod(x1 * Mod((DA - CB) * (DA - CB)));
                x2 = Mod(AA * BB);
                z2 = Mod(E * (AA + 121665 * E));
            }
            if (swap != 0) { x2 = x3; z2 = z3; }
            return ToLittleEndian32(Mod(x2 * Inv(z2)));
        }

        public static byte[] X25519Base(byte[] scalar)
        {
            byte[] nine = new byte[32];
            nine[0] = 9;
            return X25519(scalar, nine);
        }

        static bool RecoverX(BigInteger y, int sign, out BigInteger x)
        {
            x = BigInteger.Zero;
            if (y >= P) return false;
            BigInteger x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
            if (x2.IsZero) return sign == 0;
            x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod(x * x - x2).IsZero) x = Mod(x * I);
            if (!Mod(x * x - x2).IsZero) return false;
            if ((int)(x & 1) != sign) x = P - x;
            return true;
        }

        static BigInteger[] MakeBase()
        {
            BigInteger y = Mod(4 * Inv(5)), x;
            RecoverX(y, 0, out x);
            return new BigInteger[] { x, y, 1, Mod(x * y) };
        }

        static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
        {
            BigInteger A = Mod((p[1] - p[0]) * (q[1] - q[0])), B = Mod((p[1] + p[0]) * (q[1] + q[0]));
            BigInteger C = Mod(p[3] * 2 * D * q[3]), Dd = Mod(p[2] * 2 * q[2]);
            BigInteger E = Mod(B - A), F = Mod(Dd - C), G = Mod(Dd + C), H = Mod(B + A);
            return new BigInteger[] { Mod(E * F), Mod(G * H), Mod(F * G), Mod(E * H) };
        }

        static BigInteger[] Mul(BigInteger s, BigInteger[] p)
        {
            BigInteger[] q = { 0, 1, 1, 0 };
            while (s.Sign > 0)
            {
                if (!s.IsEven) q = Add(q, p);
                p = Add(p, p);
                s >>= 1;
            }
            return q;
        }

        static byte[] Encode(BigInteger[] p)
        {
            BigInteger zi = Inv(p[2]), x = Mod(p[0] * zi), y = Mod(p[1] * zi);
            byte[] b = ToLittleEndian32(y);
            if (!x.IsEven) b[31] |= 0x80;
            return b;
        }

        public static bool Ed25519Verify(byte[] pub, byte[] msg, byte[] sig)
        {
            if (pub == null || sig == null || pub.Length != 32 || sig.Length != 64) return false;
            byte[] yb = (byte[])pub.Clone();
            int sign = yb[31] >> 7;
            yb[31] &= 127;
            BigInteger x;
            if (!RecoverX(LittleEndian(yb, 0, 32), sign, out x)) return false;
            BigInteger y = LittleEndian(yb, 0, 32);
            BigInteger S = LittleEndian(sig, 32, 32);
            if (S >= L) return false;
            byte[] hin = new byte[64 + msg.Length];
            Buffer.BlockCopy(sig, 0, hin, 0, 32);
            Buffer.BlockCopy(pub, 0, hin, 32, 32);
            Buffer.BlockCopy(msg, 0, hin, 64, msg.Length);
            byte[] hash;
            using (SHA512 sha = SHA512.Create()) hash = sha.ComputeHash(hin);
            BigInteger h = LittleEndian(hash, 0, 64) % L;
            BigInteger[] negA = { Mod(-x), y, 1, Mod(-x * y) };
            byte[] r = Encode(Add(Mul(S, Base), Mul(h, negA)));
            for (int i = 0; i < 32; i++) if (r[i] != sig[i]) return false;
            return true;
        }
    }

    /// <summary>SSH-Verbindung mit einem einzigen Kanal für das SFTP-Subsystem.</summary>
    public sealed class SshClient : IDisposable
    {
        public const int Timeout = 60000;
        const int Window = 2 * 1024 * 1024;
        const int MaxIncomingPacket = 32768;

        static readonly string[] KexAlgs = { "curve25519-sha256", "curve25519-sha256@libssh.org", "diffie-hellman-group16-sha512", "diffie-hellman-group14-sha256" };
        static readonly string[] HostKeyAlgs = { "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" };
        static readonly string[] Ciphers = { "aes256-ctr", "aes192-ctr", "aes128-ctr" };
        static readonly string[] Macs = { "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com", "hmac-sha2-256", "hmac-sha2-512" };

        static readonly BigInteger P14 = Ssh.Unsigned(Hex(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AACAA68FFFFFFFFFFFFFFFF"));
        static readonly BigInteger P16 = Ssh.Unsigned(Hex(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A92108011A723C12A787E6D788719A10BDBA5B2699C327186AF4E23C1A946834B6150BDA2583E9CA2AD44CE8DBBBC2DB04DE8EF92E8EFC141FBECAA6287C59474E6BC05D99B2964FA090C3A2233BA186515BE7ED1F612970CEE2D7AFB81BDD762170481CD0069127D5B05AA993B4EA988D8FDDC186FFB7DC90A6C08F4DF435C934063199FFFFFFFFFFFFFFFF"));

        static byte[] Hex(string s)
        {
            byte[] b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16);
            return b;
        }

        TcpClient tcp;
        Stream net;
        uint seqOut, seqIn;
        AesCtr encOut, encIn;
        HMAC macOut, macIn;
        bool etmOut, etmIn;
        int macLenOut, macLenIn;
        byte[] sessionId, clientVersion, serverVersion;
        string trustedFp, firstFp;
        bool strict;

        uint chanRemote;
        long remoteWindow, localWindow;
        int remoteMax;
        bool chanEof, closeSent;
        byte[] inBuf = new byte[1 << 16];
        int inStart, inEnd;

        /// <summary>Fingerabdruck und Typ des Server-Schlüssels (nach dem Verbinden).</summary>
        public string Fingerprint { get; private set; }
        public string HostKeyType { get; private set; }
        public bool Connected { get { return tcp != null && chanRemote != uint.MaxValue && !chanEof; } }

        public SshClient() { chanRemote = uint.MaxValue; }

        // ================= Verbindung =================

        public void Connect(string host, int port, string user, string password, string trustedFingerprint)
        {
            trustedFp = trustedFingerprint;
            tcp = new TcpClient();
            tcp.ReceiveTimeout = Timeout;
            tcp.SendTimeout = Timeout;
            tcp.NoDelay = true;
            IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(20000))
            {
                tcp.Close();
                tcp = null;
                throw new SocketException(10060); // Zeitüberschreitung
            }
            tcp.EndConnect(ar);
            net = new BufferedStream(tcp.GetStream(), 1 << 16);
            try
            {
                clientVersion = Encoding.ASCII.GetBytes("SSH-2.0-SyncPilot_" + AppInfo.Version);
                net.Write(clientVersion, 0, clientVersion.Length);
                net.Write(new byte[] { 13, 10 }, 0, 2);
                net.Flush();
                ReadVersion();
                Kex(null);
                Authenticate(user ?? "", password ?? "");
                OpenSftp();
            }
            catch
            {
                Close();
                throw;
            }
        }

        void ReadVersion()
        {
            for (int lines = 0; lines < 50; lines++)
            {
                MemoryStream line = new MemoryStream();
                while (true)
                {
                    int b = net.ReadByte();
                    if (b < 0) throw new IOException("Verbindung vom Server getrennt.");
                    if (b == '\n') break;
                    if (line.Length < 1024) line.WriteByte((byte)b);
                }
                byte[] l = line.ToArray();
                int n = l.Length;
                if (n > 0 && l[n - 1] == '\r') n--;
                string s = Encoding.ASCII.GetString(l, 0, n);
                if (!s.StartsWith("SSH-")) continue; // Begrüßungstext vor der Kennung
                if (!s.StartsWith("SSH-2.0-") && !s.StartsWith("SSH-1.99-"))
                    throw new SshException("Der Server spricht kein SSH 2 (" + s + ").");
                serverVersion = new byte[n];
                Buffer.BlockCopy(l, 0, serverVersion, 0, n);
                return;
            }
            throw new SshException("Der Server hat sich nicht als SSH-Server gemeldet. Stimmt der Port?");
        }

        // ================= Pakete =================

        void ReadExact(byte[] b, int off, int n)
        {
            while (n > 0)
            {
                int r = net.Read(b, off, n);
                if (r <= 0) throw new IOException("Verbindung vom Server getrennt.");
                off += r;
                n -= r;
            }
        }

        static byte[] U32(uint v) { return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }; }

        byte[] Mac(HMAC h, uint seq, byte[] data, int off, int len)
        {
            h.Initialize();
            byte[] s = U32(seq);
            h.TransformBlock(s, 0, 4, null, 0);
            h.TransformFinalBlock(data, off, len);
            return h.Hash;
        }

        void Send(byte[] payload)
        {
            int bs = encOut != null ? 16 : 8;
            int baseLen = etmOut ? 1 + payload.Length : 5 + payload.Length;
            int pad = bs - baseLen % bs;
            if (pad < 4) pad += bs;
            int bodyLen = 1 + payload.Length + pad;
            byte[] pkt = new byte[4 + bodyLen + macLenOut];
            byte[] len = U32((uint)bodyLen);
            Buffer.BlockCopy(len, 0, pkt, 0, 4);
            pkt[4] = (byte)pad;
            Buffer.BlockCopy(payload, 0, pkt, 5, payload.Length);
            byte[] rnd = Ssh.Random(pad);
            Buffer.BlockCopy(rnd, 0, pkt, 5 + payload.Length, pad);
            if (encOut != null)
            {
                if (etmOut)
                {
                    encOut.Process(pkt, 4, bodyLen);
                    byte[] mac = Mac(macOut, seqOut, pkt, 0, 4 + bodyLen);
                    Buffer.BlockCopy(mac, 0, pkt, 4 + bodyLen, macLenOut);
                }
                else
                {
                    byte[] mac = Mac(macOut, seqOut, pkt, 0, 4 + bodyLen);
                    encOut.Process(pkt, 0, 4 + bodyLen);
                    Buffer.BlockCopy(mac, 0, pkt, 4 + bodyLen, macLenOut);
                }
            }
            net.Write(pkt, 0, pkt.Length);
            net.Flush();
            seqOut++;
        }

        byte[] ReadPacket()
        {
            byte[] body;
            int bodyOff, bodyLen;
            if (encIn == null)
            {
                byte[] lb = new byte[4];
                ReadExact(lb, 0, 4);
                uint len = new SshReader(lb, 0).U32();
                if (len < 5 || len > 256 * 1024) throw new SshException("Ungültige Paketlänge vom Server.");
                body = new byte[len];
                ReadExact(body, 0, (int)len);
                bodyOff = 0;
                bodyLen = (int)len;
            }
            else if (etmIn)
            {
                byte[] lb = new byte[4];
                ReadExact(lb, 0, 4);
                uint len = new SshReader(lb, 0).U32();
                if (len < 16 || len > 256 * 1024 || len % 16 != 0) throw new SshException("Ungültige Paketlänge vom Server.");
                body = new byte[4 + len];
                Buffer.BlockCopy(lb, 0, body, 0, 4);
                ReadExact(body, 4, (int)len);
                byte[] mac = new byte[macLenIn];
                ReadExact(mac, 0, macLenIn);
                if (!Ssh.SameBytes(mac, Mac(macIn, seqIn, body, 0, body.Length)))
                    throw new SshException("Prüfsumme eines Pakets falsch – Verbindung gestört.");
                encIn.Process(body, 4, (int)len);
                bodyOff = 4;
                bodyLen = (int)len;
            }
            else
            {
                byte[] first = new byte[16];
                ReadExact(first, 0, 16);
                encIn.Process(first, 0, 16);
                uint len = new SshReader(first, 0).U32();
                if (len < 12 || len > 256 * 1024 || (len + 4) % 16 != 0) throw new SshException("Ungültige Paketlänge vom Server.");
                body = new byte[4 + len];
                Buffer.BlockCopy(first, 0, body, 0, 16);
                ReadExact(body, 16, (int)len + 4 - 16);
                encIn.Process(body, 16, (int)len + 4 - 16);
                byte[] mac = new byte[macLenIn];
                ReadExact(mac, 0, macLenIn);
                if (!Ssh.SameBytes(mac, Mac(macIn, seqIn, body, 0, body.Length)))
                    throw new SshException("Prüfsumme eines Pakets falsch – Verbindung gestört.");
                bodyOff = 4;
                bodyLen = (int)len;
            }
            seqIn++;
            int pad = body[bodyOff];
            int payloadLen = bodyLen - 1 - pad;
            if (payloadLen < 1) throw new SshException("Ungültiges Paket vom Server.");
            byte[] payload = new byte[payloadLen];
            Buffer.BlockCopy(body, bodyOff + 1, payload, 0, payloadLen);
            return payload;
        }

        /// <summary>
        /// Liest das nächste Paket. Verwaltungsnachrichten (Ignorieren, neuer Schlüsseltausch, Kanaldaten, Fenster)
        /// werden hier erledigt; dann kommt null zurück, damit der Aufrufer seine Bedingung neu prüft.
        /// </summary>
        byte[] Receive(bool inKex)
        {
            while (true)
            {
                byte[] p = ReadPacket();
                int t = p[0];
                if (inKex && strict && t != 20 && t != 21 && t != 30 && t != 31 && t != 1)
                    throw new SshException("Unerwartete Nachricht " + t + " während des Schlüsseltauschs.");
                switch (t)
                {
                    case 1: // DISCONNECT
                    {
                        SshReader r = new SshReader(p, 1);
                        r.U32();
                        string msg = r.Text();
                        throw new SshException("Der SFTP-Server hat die Verbindung beendet" + (msg.Length > 0 ? ": " + msg : "."));
                    }
                    case 2: case 3: case 4: case 7: // IGNORE, UNIMPLEMENTED, DEBUG, EXT_INFO
                        continue;
                    case 20: // KEXINIT: der Server möchte neue Schlüssel
                        if (inKex) return p;
                        Kex(p);
                        continue;
                    case 80: // GLOBAL_REQUEST (z. B. keepalive)
                    {
                        SshReader r = new SshReader(p, 1);
                        r.Str();
                        if (r.Bool()) Send(new byte[] { 82 });
                        continue;
                    }
                    case 93: // WINDOW_ADJUST
                    {
                        SshReader r = new SshReader(p, 1);
                        r.U32();
                        remoteWindow += r.U32();
                        return null;
                    }
                    case 94: // DATA
                    {
                        SshReader r = new SshReader(p, 1);
                        r.U32();
                        uint n = r.U32();
                        if (n > p.Length - 9) throw new SshException("Ungültige Kanaldaten vom Server.");
                        Append(p, 9, (int)n);
                        Consumed((int)n);
                        return null;
                    }
                    case 95: // EXTENDED_DATA (Fehlerausgabe): verwerfen
                    {
                        SshReader r = new SshReader(p, 1);
                        r.U32();
                        r.U32();
                        Consumed(r.Str().Length);
                        return null;
                    }
                    case 96: // EOF
                        chanEof = true;
                        return null;
                    case 97: // CLOSE: einmal bestätigen
                        chanEof = true;
                        if (!closeSent)
                        {
                            closeSent = true;
                            try { Send(new SshBuf().Byte(97).U32(chanRemote).ToArray()); } catch { }
                        }
                        return null;
                    case 98: // CHANNEL_REQUEST vom Server (z. B. keepalive)
                    {
                        SshReader r = new SshReader(p, 1);
                        r.U32();
                        r.Str();
                        if (r.Bool()) Send(new SshBuf().Byte(100).U32(chanRemote).ToArray());
                        continue;
                    }
                }
                return p;
            }
        }

        byte[] Expect(bool inKex, params int[] types)
        {
            byte[] p = null;
            while (p == null) p = Receive(inKex);
            if (Array.IndexOf(types, (int)p[0]) < 0)
                throw new SshException("Unerwartete Antwort des Servers (Nachricht " + p[0] + ").");
            return p;
        }

        void Append(byte[] src, int off, int n)
        {
            if (inEnd + n > inBuf.Length)
            {
                int used = inEnd - inStart;
                if (used + n > inBuf.Length)
                {
                    byte[] nb = new byte[Math.Max(inBuf.Length * 2, used + n)];
                    Buffer.BlockCopy(inBuf, inStart, nb, 0, used);
                    inBuf = nb;
                }
                else Buffer.BlockCopy(inBuf, inStart, inBuf, 0, used);
                inStart = 0;
                inEnd = used;
            }
            Buffer.BlockCopy(src, off, inBuf, inEnd, n);
            inEnd += n;
        }

        void Consumed(int n)
        {
            localWindow -= n;
            if (localWindow < Window / 2)
            {
                long add = Window - localWindow;
                Send(new SshBuf().Byte(93).U32(chanRemote).U32((uint)add).ToArray());
                localWindow += add;
            }
        }

        // ================= Schlüsseltausch =================

        static string Pick(string[] ours, string[] theirs, string what)
        {
            foreach (string a in ours)
                if (Array.IndexOf(theirs, a) >= 0) return a;
            throw new SshException("Kein gemeinsames Verfahren mit dem Server (" + what + "). Der Server bietet: " + string.Join(", ", theirs));
        }

        byte[] KexInit()
        {
            List<string> kex = new List<string>(KexAlgs);
            if (sessionId == null) kex.Add("kex-strict-c-v00@openssh.com");
            SshBuf b = new SshBuf().Byte(20).Raw(Ssh.Random(16));
            b.Str(string.Join(",", kex)).Str(string.Join(",", HostKeyAlgs));
            b.Str(string.Join(",", Ciphers)).Str(string.Join(",", Ciphers));
            b.Str(string.Join(",", Macs)).Str(string.Join(",", Macs));
            b.Str("none").Str("none").Str("").Str("").Bool(false).U32(0);
            return b.ToArray();
        }

        void Kex(byte[] serverKexInit)
        {
            bool first = sessionId == null;
            byte[] my = KexInit();
            Send(my);
            if (serverKexInit == null) serverKexInit = Expect(true, 20);
            SshReader r = new SshReader(serverKexInit, 17);
            string[][] lists = new string[10][];
            for (int i = 0; i < 10; i++) lists[i] = r.NameList();
            bool follows = r.Bool();
            if (first && Array.IndexOf(lists[0], "kex-strict-s-v00@openssh.com") >= 0)
            {
                strict = true;
                if (seqIn != 1) throw new SshException("Schlüsseltausch gestört (KEXINIT war nicht das erste Paket).");
            }
            string kexAlg = Pick(KexAlgs, lists[0], "Schlüsseltausch");
            string hkAlg = Pick(HostKeyAlgs, lists[1], "Server-Schlüssel");
            string cCs = Pick(Ciphers, lists[2], "Verschlüsselung"), cSc = Pick(Ciphers, lists[3], "Verschlüsselung");
            string mCs = Pick(Macs, lists[4], "Prüfsumme"), mSc = Pick(Macs, lists[5], "Prüfsumme");
            if (Array.IndexOf(lists[6], "none") < 0 || Array.IndexOf(lists[7], "none") < 0)
                throw new SshException("Der Server verlangt Kompression – wird nicht unterstützt.");
            if (follows && (lists[0].Length == 0 || lists[0][0] != kexAlg || lists[1].Length == 0 || lists[1][0] != hkAlg))
                ReadPacket(); // falsch geratenes erstes Schlüsseltausch-Paket des Servers verwerfen

            HashAlgorithm hash = kexAlg.EndsWith("sha512") ? (HashAlgorithm)SHA512.Create() : SHA256.Create();
            try
            {
                byte[] kS, sig, K, exchange;
                if (kexAlg.StartsWith("curve25519"))
                {
                    byte[] priv = Ssh.Random(32);
                    byte[] qC = Curve25519.X25519Base(priv);
                    Send(new SshBuf().Byte(30).Str(qC).ToArray());
                    SshReader rr = new SshReader(Expect(true, 31), 1);
                    kS = rr.Str();
                    byte[] qS = rr.Str();
                    sig = rr.Str();
                    if (qS.Length != 32) throw new SshException("Ungültiger Schlüssel vom Server.");
                    byte[] shared = Curve25519.X25519(priv, qS);
                    bool zero = true;
                    foreach (byte x in shared) if (x != 0) zero = false;
                    if (zero) throw new SshException("Ungültiger Schlüssel vom Server.");
                    K = Ssh.MpInt(Ssh.Unsigned(shared));
                    exchange = new SshBuf().Str(qC).Str(qS).ToArray();
                }
                else
                {
                    BigInteger prime = kexAlg.Contains("group16") ? P16 : P14;
                    BigInteger x = Ssh.Unsigned(Ssh.Random(64)) % (prime - 3) + 2;
                    BigInteger e = BigInteger.ModPow(2, x, prime);
                    Send(new SshBuf().Byte(30).MpInt(e).ToArray());
                    SshReader rr = new SshReader(Expect(true, 31), 1);
                    kS = rr.Str();
                    BigInteger f = rr.MpInt();
                    sig = rr.Str();
                    if (f <= 1 || f >= prime - 1) throw new SshException("Ungültiger Wert vom Server beim Schlüsseltausch.");
                    K = Ssh.MpInt(BigInteger.ModPow(f, x, prime));
                    exchange = new SshBuf().MpInt(e).MpInt(f).ToArray();
                }
                byte[] hIn = new SshBuf().Str(clientVersion).Str(serverVersion).Str(my).Str(serverKexInit).Str(kS)
                    .Raw(exchange).Raw(K).ToArray();
                byte[] H = hash.ComputeHash(hIn);

                VerifyHostKey(hkAlg, kS, sig, H);
                string fp = Ssh.Fingerprint(kS);
                string type = new SshReader(kS, 0).Text();
                if (first)
                {
                    Fingerprint = fp;
                    HostKeyType = type;
                    if (!string.Equals(trustedFp, fp, StringComparison.Ordinal))
                        throw new SshHostKeyException(fp, type, !string.IsNullOrEmpty(trustedFp));
                    sessionId = H;
                    firstFp = fp;
                }
                else if (fp != firstFp)
                    throw new SshException("Der Server-Schlüssel hat sich während der Verbindung geändert.");

                Send(new byte[] { 21 });
                if (strict) seqOut = 0;
                if (encOut != null) encOut.Dispose();
                if (macOut != null) macOut.Dispose();
                encOut = new AesCtr(Derive(hash, K, H, 'C', KeySize(cCs)), Derive(hash, K, H, 'A', 16));
                macOut = NewMac(mCs, Derive(hash, K, H, 'E', MacSize(mCs)));
                etmOut = mCs.EndsWith("-etm@openssh.com");
                macLenOut = MacSize(mCs);

                Expect(true, 21);
                if (strict) seqIn = 0;
                if (encIn != null) encIn.Dispose();
                if (macIn != null) macIn.Dispose();
                encIn = new AesCtr(Derive(hash, K, H, 'D', KeySize(cSc)), Derive(hash, K, H, 'B', 16));
                macIn = NewMac(mSc, Derive(hash, K, H, 'F', MacSize(mSc)));
                etmIn = mSc.EndsWith("-etm@openssh.com");
                macLenIn = MacSize(mSc);
            }
            finally { hash.Dispose(); }
        }

        static int KeySize(string cipher) { return int.Parse(cipher.Substring(3, 3)) / 8; }
        static int MacSize(string mac) { return mac.Contains("512") ? 64 : 32; }
        static HMAC NewMac(string mac, byte[] key) { return mac.Contains("512") ? (HMAC)new HMACSHA512(key) : new HMACSHA256(key); }

        byte[] Derive(HashAlgorithm hash, byte[] K, byte[] H, char letter, int n)
        {
            byte[] k = hash.ComputeHash(new SshBuf().Raw(K).Raw(H).Byte(letter).Raw(sessionId).ToArray());
            while (k.Length < n)
            {
                byte[] more = hash.ComputeHash(new SshBuf().Raw(K).Raw(H).Raw(k).ToArray());
                byte[] nk = new byte[k.Length + more.Length];
                Buffer.BlockCopy(k, 0, nk, 0, k.Length);
                Buffer.BlockCopy(more, 0, nk, k.Length, more.Length);
                k = nk;
            }
            byte[] res = new byte[n];
            Buffer.BlockCopy(k, 0, res, 0, n);
            return res;
        }

        static void VerifyHostKey(string alg, byte[] keyBlob, byte[] sigBlob, byte[] H)
        {
            SshReader k = new SshReader(keyBlob, 0);
            string keyType = k.Text();
            SshReader s = new SshReader(sigBlob, 0);
            string sigType = s.Text();
            byte[] sig = s.Str();
            bool ok;
            if (alg == "ssh-ed25519")
            {
                if (keyType != alg || sigType != alg) throw new SshException("Server-Schlüssel und Signatur passen nicht zusammen.");
                ok = Curve25519.Ed25519Verify(k.Str(), H, sig);
            }
            else if (alg.StartsWith("ecdsa-sha2-"))
            {
                if (keyType != alg || sigType != alg) throw new SshException("Server-Schlüssel und Signatur passen nicht zusammen.");
                string curve = k.Text();
                byte[] q = k.Str();
                int size;
                int magic;
                HashAlgorithm h;
                if (curve == "nistp256") { size = 32; magic = 0x31534345; h = SHA256.Create(); }
                else if (curve == "nistp384") { size = 48; magic = 0x33534345; h = SHA384.Create(); }
                else if (curve == "nistp521") { size = 66; magic = 0x35534345; h = SHA512.Create(); }
                else throw new SshException("Unbekannte Kurve: " + curve);
                if (q.Length != 1 + 2 * size || q[0] != 4) throw new SshException("Ungültiger Server-Schlüssel.");
                byte[] blob = new byte[8 + 2 * size];
                Buffer.BlockCopy(BitConverter.GetBytes(magic), 0, blob, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(size), 0, blob, 4, 4);
                Buffer.BlockCopy(q, 1, blob, 8, 2 * size);
                SshReader rs = new SshReader(sig, 0);
                BigInteger rr = rs.MpInt(), ss = rs.MpInt();
                if (rr.Sign <= 0 || ss.Sign <= 0) throw new SshException("Ungültige Signatur des Servers.");
                byte[] raw = new byte[2 * size];
                Buffer.BlockCopy(Ssh.ToUnsigned(rr, size), 0, raw, 0, size);
                Buffer.BlockCopy(Ssh.ToUnsigned(ss, size), 0, raw, size, size);
                byte[] digest;
                using (h) digest = h.ComputeHash(H);
                using (CngKey key = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                using (ECDsaCng ec = new ECDsaCng(key))
                    ok = ec.VerifyHash(digest, raw);
            }
            else
            {
                if (keyType != "ssh-rsa" || sigType != alg) throw new SshException("Server-Schlüssel und Signatur passen nicht zusammen.");
                BigInteger e = k.MpInt(), n = k.MpInt();
                if (e.Sign <= 0 || n.Sign <= 0) throw new SshException("Ungültiger Server-Schlüssel.");
                RSAParameters rp = new RSAParameters();
                rp.Exponent = Ssh.ToUnsigned(e, 0);
                rp.Modulus = Ssh.ToUnsigned(n, 0);
                if (sig.Length > rp.Modulus.Length) throw new SshException("Ungültige Signatur des Servers.");
                byte[] padded = new byte[rp.Modulus.Length];
                Buffer.BlockCopy(sig, 0, padded, padded.Length - sig.Length, sig.Length);
                HashAlgorithmName hn;
                HashAlgorithm h;
                if (alg == "rsa-sha2-512") { hn = HashAlgorithmName.SHA512; h = SHA512.Create(); }
                else if (alg == "rsa-sha2-256") { hn = HashAlgorithmName.SHA256; h = SHA256.Create(); }
                else { hn = HashAlgorithmName.SHA1; h = SHA1.Create(); }
                byte[] digest;
                using (h) digest = h.ComputeHash(H);
                using (RSACng rsa = new RSACng())
                {
                    rsa.ImportParameters(rp);
                    ok = rsa.VerifyHash(digest, padded, hn, RSASignaturePadding.Pkcs1);
                }
            }
            if (!ok) throw new SshException("Die Signatur des Servers ist ungültig – Verbindung abgebrochen.");
        }

        // ================= Anmeldung =================

        void Authenticate(string user, string password)
        {
            if (user.Length == 0) throw new SshException("Für SFTP bitte unter „Anmeldung“ einen Benutzernamen eintragen.");
            Send(new SshBuf().Byte(5).Str("ssh-userauth").ToArray());
            Expect(false, 6);
            Send(Request(user, "none").ToArray());
            byte[] p = AuthAnswer();
            if (p[0] == 52) return;
            if (p[0] != 51) throw new SshException("Unerwartete Antwort bei der Anmeldung.");
            string[] methods = new SshReader(p, 1).NameList();
            bool tried = false;
            if (Array.IndexOf(methods, "password") >= 0)
            {
                tried = true;
                Send(Request(user, "password").Bool(false).Str(password).ToArray());
                p = AuthAnswer();
                if (p[0] == 52) return;
                if (p[0] == 60) throw new SshException("Das Kennwort ist abgelaufen und muss auf dem Server geändert werden.");
                if (p[0] != 51) throw new SshException("Unerwartete Antwort bei der Anmeldung.");
                methods = new SshReader(p, 1).NameList();
            }
            if (Array.IndexOf(methods, "keyboard-interactive") >= 0)
            {
                tried = true;
                Send(Request(user, "keyboard-interactive").Str("").Str("").ToArray());
                for (int round = 0; round < 10; round++)
                {
                    p = AuthAnswer();
                    if (p[0] == 52) return;
                    if (p[0] == 51) break;
                    if (p[0] != 60) throw new SshException("Unerwartete Antwort bei der Anmeldung.");
                    SshReader r = new SshReader(p, 1);
                    r.Str(); r.Str(); r.Str();
                    uint n = r.U32();
                    if (n > 20) throw new SshException("Unerwartete Anfrage bei der Anmeldung.");
                    SshBuf ans = new SshBuf().Byte(61).U32(n);
                    for (int i = 0; i < n; i++)
                    {
                        r.Str();
                        r.Bool();
                        ans.Str(password); // jede Abfrage (meist "Password:") mit dem Kennwort beantworten
                    }
                    Send(ans.ToArray());
                }
            }
            if (!tried)
                throw new SshException("Der Server erlaubt keine Anmeldung mit Kennwort (angeboten: " + string.Join(", ", methods) +
                    "). Anmeldung mit Schlüsseldatei wird noch nicht unterstützt.");
            throw new SshException("Anmeldung am SFTP-Server fehlgeschlagen: Benutzername oder Kennwort falsch.");
        }

        static SshBuf Request(string user, string method)
        {
            return new SshBuf().Byte(50).Str(user).Str("ssh-connection").Str(method);
        }

        byte[] AuthAnswer()
        {
            while (true)
            {
                byte[] p = Receive(false);
                if (p == null || p[0] == 53) continue; // Begrüßungstext (BANNER)
                return p;
            }
        }

        // ================= Kanal =================

        void OpenSftp()
        {
            localWindow = Window;
            Send(new SshBuf().Byte(90).Str("session").U32(0).U32(Window).U32(MaxIncomingPacket).ToArray());
            byte[] p = Expect(false, 91, 92);
            if (p[0] == 92) throw new SshException("Der Server hat die Sitzung abgelehnt.");
            SshReader r = new SshReader(p, 1);
            r.U32();
            chanRemote = r.U32();
            remoteWindow = r.U32();
            remoteMax = (int)Math.Min(r.U32(), 1u << 20);
            if (remoteMax < 1024) remoteMax = 1024;
            Send(new SshBuf().Byte(98).U32(chanRemote).Str("subsystem").Bool(true).Str("sftp").ToArray());
            p = Expect(false, 99, 100);
            if (p[0] == 100) throw new SshException("Der Server bietet kein SFTP an.");
        }

        /// <summary>Daten in den Kanal schreiben (wartet bei Bedarf auf Freigabe durch den Server).</summary>
        public void Write(byte[] data, int off, int count)
        {
            while (count > 0)
            {
                while (remoteWindow <= 0)
                {
                    if (chanEof) throw new IOException("Der SFTP-Kanal wurde geschlossen.");
                    if (Receive(false) != null) throw new SshException("Unerwartete Nachricht vom Server.");
                }
                int n = (int)Math.Min(Math.Min(count, remoteWindow), remoteMax - 64);
                Send(new SshBuf().Byte(94).U32(chanRemote).Str(data, off, n).ToArray());
                remoteWindow -= n;
                off += n;
                count -= n;
            }
        }

        /// <summary>Genau count Bytes aus dem Kanal lesen.</summary>
        public void Read(byte[] dst, int off, int count)
        {
            while (inEnd - inStart < count)
            {
                if (chanEof) throw new IOException("Der SFTP-Kanal wurde geschlossen.");
                if (Receive(false) != null) throw new SshException("Unerwartete Nachricht vom Server.");
            }
            Buffer.BlockCopy(inBuf, inStart, dst, off, count);
            inStart += count;
            if (inStart == inEnd) inStart = inEnd = 0;
        }

        public void Close()
        {
            if (tcp == null) return;
            try
            {
                if (net != null && encOut != null)
                    Send(new SshBuf().Byte(1).U32(11).Str("").Str("").ToArray());
            }
            catch { }
            try { tcp.Close(); } catch { }
            tcp = null;
            net = null;
            if (encOut != null) encOut.Dispose();
            if (encIn != null) encIn.Dispose();
            if (macOut != null) macOut.Dispose();
            if (macIn != null) macIn.Dispose();
            encOut = encIn = null;
            macOut = macIn = null;
            chanRemote = uint.MaxValue;
        }

        public void Dispose() { Close(); }
    }
}

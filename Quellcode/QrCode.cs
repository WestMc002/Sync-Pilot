// Sync Pilot – QR-Code-Erzeuger (Byte-Modus, Fehlerkorrektur M, Versionen 1-9) für die Handy-Einrichtung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace SyncPilot
{
    /// <summary>QR-Code nach ISO/IEC 18004, genug für Adressen bis ca. 180 Zeichen.</summary>
    public sealed class QrCode
    {
        static readonly int[] Ecc = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22 };
        static readonly int[] Blocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5 };
        const int MaxVersion = 9; // ab Version 10 wäre die Längenangabe 16 statt 8 Bit lang

        public readonly int Size;
        readonly bool[,] mod;
        readonly bool[,] fn;

        public bool this[int x, int y] { get { return mod[y, x]; } }

        static int RawModules(int ver)
        {
            int r = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int n = ver / 7 + 2;
                r -= (25 * n - 10) * n - 55;
                if (ver >= 7) r -= 36;
            }
            return r;
        }

        static int DataCodewords(int ver) { return RawModules(ver) / 8 - Ecc[ver] * Blocks[ver]; }

        static int GfMul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = ((z << 1) ^ ((z >> 7) * 0x11D)) & 0xFF;
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        static int[] RsDivisor(int deg)
        {
            int[] res = new int[deg];
            res[deg - 1] = 1;
            int root = 1;
            for (int k = 0; k < deg; k++)
            {
                for (int j = 0; j < deg; j++)
                {
                    res[j] = GfMul(res[j], root);
                    if (j + 1 < deg) res[j] ^= res[j + 1];
                }
                root = GfMul(root, 2);
            }
            return res;
        }

        static int[] RsRemainder(List<int> data, int[] div)
        {
            int[] res = new int[div.Length];
            foreach (int b in data)
            {
                int f = b ^ res[0];
                Array.Copy(res, 1, res, 0, res.Length - 1);
                res[res.Length - 1] = 0;
                for (int i = 0; i < res.Length; i++) res[i] ^= GfMul(div[i], f);
            }
            return res;
        }

        static List<int> AlignPositions(int ver)
        {
            List<int> res = new List<int>();
            if (ver == 1) return res;
            int n = ver / 7 + 2;
            int step = (ver * 8 + n * 3 + 5) / (n * 4 - 4) * 2;
            int pos = ver * 4 + 10;
            for (int i = 0; i < n - 1; i++)
            {
                res.Insert(0, pos);
                pos -= step;
            }
            res.Insert(0, 6);
            return res;
        }

        public QrCode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? "");
            int ver = 1;
            while (ver <= MaxVersion && 4 + 8 + data.Length * 8 > DataCodewords(ver) * 8) ver++;
            if (ver > MaxVersion) throw new ArgumentException("Text zu lang für den QR-Code.");

            // Bitfolge: Modus Byte (0100), Länge, Daten, Abschluss, Füllbytes
            List<bool> bits = new List<bool>();
            Action<int, int> app = delegate (int v, int n)
            {
                for (int i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) != 0);
            };
            int cap = DataCodewords(ver) * 8;
            app(4, 4);
            app(data.Length, 8);
            foreach (byte b in data) app(b, 8);
            app(0, Math.Min(4, cap - bits.Count));
            app(0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < cap; pad ^= 0xEC ^ 0x11) app(pad, 8);
            List<int> cw = new List<int>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int v = 0;
                for (int j = 0; j < 8; j++) v = (v << 1) | (bits[i + j] ? 1 : 0);
                cw.Add(v);
            }

            // Blöcke mit Reed-Solomon-Fehlerkorrektur, verschränkt
            int nb = Blocks[ver], ecl = Ecc[ver], raw = RawModules(ver) / 8;
            int nShort = nb - raw % nb, shortLen = raw / nb;
            int[] div = RsDivisor(ecl);
            List<List<int>> blocks = new List<List<int>>();
            int k = 0;
            for (int i = 0; i < nb; i++)
            {
                int dl = shortLen - ecl + (i < nShort ? 0 : 1);
                List<int> dat = cw.GetRange(k, dl);
                k += dl;
                int[] ec = RsRemainder(dat, div);
                if (i < nShort) dat.Add(0);
                dat.AddRange(ec);
                blocks.Add(dat);
            }
            List<int> output = new List<int>();
            for (int i = 0; i < blocks[0].Count; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortLen - ecl || j >= nShort) output.Add(blocks[j][i]);

            Size = ver * 4 + 17;
            mod = new bool[Size, Size];
            fn = new bool[Size, Size];

            // Taktlinien, Suchmuster, Ausrichtungsmuster
            for (int i = 0; i < Size; i++)
            {
                SetF(6, i, i % 2 == 0);
                SetF(i, 6, i % 2 == 0);
            }
            Finder(3, 3);
            Finder(Size - 4, 3);
            Finder(3, Size - 4);
            List<int> ap = AlignPositions(ver);
            int na = ap.Count;
            for (int i = 0; i < na; i++)
                for (int j = 0; j < na; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == na - 1) || (i == na - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            SetF(ap[i] + dx, ap[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            DrawFormat(0);
            if (ver >= 7)
            {
                int rem = ver;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int vb = ver << 12 | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((vb >> i) & 1) != 0;
                    int a = Size - 11 + i % 3, c = i / 3;
                    SetF(a, c, bit);
                    SetF(c, a, bit);
                }
            }

            // Daten im Zickzack einzeichnen
            int idx = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!fn[y, x] && idx < output.Count * 8)
                        {
                            mod[y, x] = ((output[idx >> 3] >> (7 - (idx & 7))) & 1) != 0;
                            idx++;
                        }
                    }
            }

            // Beste Maske wählen
            int best = 0, bestPen = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(m);
                DrawFormat(m);
                int p = Penalty();
                if (p < bestPen) { bestPen = p; best = m; }
                ApplyMask(m);
            }
            ApplyMask(best);
            DrawFormat(best);
        }

        void SetF(int x, int y, bool v)
        {
            mod[y, x] = v;
            fn[y, x] = true;
        }

        void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy)), x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < Size && y >= 0 && y < Size) SetF(x, y, d != 2 && d != 4);
                }
        }

        void DrawFormat(int mask)
        {
            int data = mask; // Fehlerkorrektur M = 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int b = (data << 10 | rem) ^ 0x5412;
            for (int i = 0; i < 6; i++) SetF(8, i, Bit(b, i));
            SetF(8, 7, Bit(b, 6));
            SetF(8, 8, Bit(b, 7));
            SetF(7, 8, Bit(b, 8));
            for (int i = 9; i < 15; i++) SetF(14 - i, 8, Bit(b, i));
            for (int i = 0; i < 8; i++) SetF(Size - 1 - i, 8, Bit(b, i));
            for (int i = 8; i < 15; i++) SetF(8, Size - 15 + i, Bit(b, i));
            SetF(8, Size - 8, true);
        }

        static bool Bit(int v, int i) { return ((v >> i) & 1) != 0; }

        void ApplyMask(int m)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (fn[y, x]) continue;
                    bool inv;
                    switch (m)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (inv) mod[y, x] = !mod[y, x];
                }
        }

        int Penalty()
        {
            int p = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < Size; a++)
                {
                    StringBuilder sb = new StringBuilder(Size);
                    int run = 1;
                    for (int b = 0; b < Size; b++)
                    {
                        bool v = pass == 0 ? mod[a, b] : mod[b, a];
                        sb.Append(v ? '1' : '0');
                        if (b == 0) continue;
                        bool prev = pass == 0 ? mod[a, b - 1] : mod[b - 1, a];
                        if (v == prev)
                        {
                            run++;
                            if (run == 5) p += 3;
                            else if (run > 5) p++;
                        }
                        else run = 1;
                    }
                    string s = sb.ToString();
                    p += 40 * (Count(s, "10111010000") + Count(s, "00001011101"));
                }
            for (int y = 0; y < Size - 1; y++)
                for (int x = 0; x < Size - 1; x++)
                {
                    bool c = mod[y, x];
                    if (c == mod[y, x + 1] && c == mod[y + 1, x] && c == mod[y + 1, x + 1]) p += 3;
                }
            int dark = 0, total = Size * Size;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (mod[y, x]) dark++;
            int kk = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            p += kk * 10;
            return p;
        }

        static int Count(string s, string pat)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(pat, i, StringComparison.Ordinal)) >= 0) { n++; i++; }
            return n;
        }

        /// <summary>Bild mit 4 Modulen Rand (schwarz auf weiß – auch im dunklen Modus, damit Kameras es sicher lesen).</summary>
        public Bitmap Render(int pixelPerModule)
        {
            int q = 4, s = (Size + 2 * q) * pixelPerModule;
            Bitmap bmp = new Bitmap(s, s);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (mod[y, x])
                            g.FillRectangle(Brushes.Black, (x + q) * pixelPerModule, (y + q) * pixelPerModule, pixelPerModule, pixelPerModule);
            }
            return bmp;
        }
    }
}
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// QR code for a short text (the remote's pairing link): byte mode, error correction level M,
    /// versions 1-10 (up to 213 bytes), mask chosen by the standard penalty rules.
    /// </summary>
    internal sealed class QrCode
    {
        // Level M, versions 1..10: error correction codewords per block, number of blocks.
        private static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] BlockCount = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

        public int Size { get; }
        private readonly bool[,] _modules;
        private readonly bool[,] _function;

        public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

        private QrCode(int version, byte[] dataCodewords)
        {
            Size = version * 4 + 17;
            _modules = new bool[Size, Size];
            _function = new bool[Size, Size];
            DrawFunctionPatterns(version);
            DrawCodewords(AddErrorCorrection(version, dataCodewords));
            int bestMask = 0;
            long bestPenalty = long.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(mask);
                long penalty = Penalty();
                if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
                ApplyMask(mask); // XOR again: undo
            }
            ApplyMask(bestMask);
            DrawFormatBits(bestMask);
        }

        public static QrCode Encode(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            for (int version = 1; version <= 10; version++)
            {
                int capacity = DataCodewords(version);
                int countBits = version <= 9 ? 8 : 16;
                int needed = 4 + countBits + bytes.Length * 8;
                if (needed > capacity * 8) continue;

                var bits = new List<bool>(capacity * 8);
                void Append(int value, int length) { for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
                Append(0b0100, 4);                 // byte mode
                Append(bytes.Length, countBits);
                foreach (byte b in bytes) Append(b, 8);
                Append(0, Math.Min(4, capacity * 8 - bits.Count));
                Append(0, (8 - bits.Count % 8) % 8);
                for (int pad = 0xEC; bits.Count < capacity * 8; pad ^= 0xEC ^ 0x11) Append(pad, 8);

                var data = new byte[capacity];
                for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                return new QrCode(version, data);
            }
            throw new ArgumentException("Text too long for a QR code.");
        }

        /// <summary>Dark modules on a light square with the quiet zone the standard asks for.</summary>
        public void Draw(Graphics g, Rectangle bounds, Color dark, Color light)
        {
            const int quiet = 4;
            int cell = Math.Max(1, Math.Min(bounds.Width, bounds.Height) / (Size + quiet * 2));
            int side = cell * (Size + quiet * 2);
            int left = bounds.Left + (bounds.Width - side) / 2, top = bounds.Top + (bounds.Height - side) / 2;
            var previous = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            using (var back = new SolidBrush(light)) g.FillRectangle(back, left, top, side, side);
            using var brush = new SolidBrush(dark);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (_modules[y, x]) g.FillRectangle(brush, left + (x + quiet) * cell, top + (y + quiet) * cell, cell, cell);
            g.SmoothingMode = previous;
        }

        private static int RawModules(int version)
        {
            int result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                int align = version / 7 + 2;
                result -= (25 * align - 10) * align - 55;
                if (version >= 7) result -= 36;
            }
            return result;
        }

        private static int DataCodewords(int version) => RawModules(version) / 8 - EccPerBlock[version] * BlockCount[version];

        private byte[] AddErrorCorrection(int version, byte[] data)
        {
            int blocks = BlockCount[version], eccLength = EccPerBlock[version], raw = RawModules(version) / 8;
            int shortBlocks = blocks - raw % blocks, shortLength = raw / blocks;
            byte[] divisor = ReedSolomonDivisor(eccLength);
            var all = new byte[blocks][];
            for (int i = 0, k = 0; i < blocks; i++)
            {
                int dataLength = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
                var block = new byte[shortLength + 1];
                Array.Copy(data, k, block, 0, dataLength);
                byte[] ecc = ReedSolomonRemainder(data.AsSpan(k, dataLength), divisor);
                Array.Copy(ecc, 0, block, block.Length - eccLength, eccLength);
                k += dataLength;
                all[i] = block;
            }
            var result = new byte[raw];
            for (int i = 0, k = 0; i < all[0].Length; i++)
                for (int j = 0; j < all.Length; j++)
                    // The short blocks have one data byte less: skip the gap before their error correction.
                    if (i != shortLength - eccLength || j >= shortBlocks) result[k++] = all[j][i];
            return result;
        }

        private static byte[] ReedSolomonDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = Multiply(result[j], (byte)root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Multiply((byte)root, 0x02);
            }
            return result;
        }

        private static byte[] ReedSolomonRemainder(ReadOnlySpan<byte> data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                byte factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[^1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }

        private static byte Multiply(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private void SetFunction(int x, int y, bool dark)
        {
            _modules[y, x] = dark;
            _function[y, x] = true;
        }

        private void DrawFunctionPatterns(int version)
        {
            for (int i = 0; i < Size; i++)
            {
                SetFunction(6, i, i % 2 == 0);
                SetFunction(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);

            int[] positions = AlignmentPositions(version);
            for (int i = 0; i < positions.Length; i++)
                for (int j = 0; j < positions.Length; j++)
                    if (!((i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0)))
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                                SetFunction(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);

            DrawFormatBits(0);
            if (version >= 7)
            {
                int remainder = version;
                for (int i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
                int bits = (version << 12) | remainder;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = Size - 11 + i % 3, b = i / 3;
                    SetFunction(a, b, bit);
                    SetFunction(b, a, bit);
                }
            }
        }

        private void DrawFinder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < Size && y >= 0 && y < Size) SetFunction(x, y, distance != 2 && distance != 4);
                }
        }

        private static int[] AlignmentPositions(int version)
        {
            if (version == 1) return Array.Empty<int>();
            int count = version / 7 + 2;
            int step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var result = new int[count];
            result[0] = 6;
            for (int i = count - 1, position = version * 4 + 10; i >= 1; i--, position -= step) result[i] = position;
            return result;
        }

        private void DrawFormatBits(int mask)
        {
            int data = (0 << 3) | mask;            // level M is 00
            int remainder = data;
            for (int i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            int bits = ((data << 10) | remainder) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;

            for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
            SetFunction(8, 7, Bit(6));
            SetFunction(8, 8, Bit(7));
            SetFunction(7, 8, Bit(8));
            for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));

            for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
            for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
            SetFunction(8, Size - 8, true);
        }

        private void DrawCodewords(byte[] data)
        {
            int index = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vertical = 0; vertical < Size; vertical++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vertical : vertical;
                        if (_function[y, x] || index >= data.Length * 8) continue;
                        _modules[y, x] = ((data[index >> 3] >> (7 - (index & 7))) & 1) != 0;
                        index++;
                    }
            }
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (_function[y, x]) continue;
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                    };
                    if (invert) _modules[y, x] = !_modules[y, x];
                }
        }

        private long Penalty()
        {
            long result = 0;
            int dark = 0;
            for (int y = 0; y < Size; y++)
            {
                int runX = 0, runY = 0;
                bool colourX = false, colourY = false;
                for (int x = 0; x < Size; x++)
                {
                    if (_modules[y, x]) dark++;
                    // Runs of the same colour, by rows (x, y) and by columns (y, x).
                    if (x == 0 || _modules[y, x] != colourX) { colourX = _modules[y, x]; runX = 1; }
                    else if (++runX == 5) result += 3; else if (runX > 5) result++;
                    if (x == 0 || _modules[x, y] != colourY) { colourY = _modules[x, y]; runY = 1; }
                    else if (++runY == 5) result += 3; else if (runY > 5) result++;
                    // Finder-like patterns 1:1:3:1:1 with four light modules on one side.
                    if (x + 10 < Size)
                    {
                        if (FinderLike(i => _modules[y, x + i])) result += 40;
                        if (FinderLike(i => _modules[x + i, y])) result += 40;
                    }
                    if (x + 1 < Size && y + 1 < Size && _modules[y, x] == _modules[y, x + 1] && _modules[y, x] == _modules[y + 1, x] && _modules[y, x] == _modules[y + 1, x + 1])
                        result += 3;
                }
            }
            int total = Size * Size;
            result += (Math.Abs(dark * 20 - total * 10) + total - 1) / total * 10 - 10 < 0 ? 0 : (Math.Abs(dark * 20 - total * 10) + total - 1) / total * 10 - 10;
            return result;
        }

        private static bool FinderLike(Func<int, bool> at)
        {
            bool core = at(0) && !at(1) && at(2) && at(3) && at(4) && !at(5) && at(6);
            bool coreShifted = at(4) && !at(5) && at(6) && at(7) && at(8) && !at(9) && at(10);
            return (core && !at(7) && !at(8) && !at(9) && !at(10)) || (coreShifted && !at(0) && !at(1) && !at(2) && !at(3));
        }
    }
}

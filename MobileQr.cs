using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace KeyParcel
{
    // The pairing URL fits QR version 3-L, byte mode, one Reed–Solomon block.
    internal sealed class MobileQr
    {
        private const int Size = 29;
        private readonly bool[,] modules = new bool[Size, Size];
        private readonly bool[,] reserved = new bool[Size, Size];

        internal static Bitmap Draw(string url)
        {
            var qr = new MobileQr();
            qr.Encode(url);
            const int quiet = 4, scale = 5;
            var bitmap = new Bitmap((Size + quiet * 2) * scale, (Size + quiet * 2) * scale);
            using (var canvas = Graphics.FromImage(bitmap))
            {
                canvas.Clear(Color.White);
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (qr.modules[x, y])
                            canvas.FillRectangle(Brushes.Black, (x + quiet) * scale, (y + quiet) * scale, scale, scale);
            }
            return bitmap;
        }

        private void Encode(string url)
        {
            var data = Encoding.ASCII.GetBytes(url);
            if (data.Length > 53) throw new ArgumentException("QR payload is too long");
            var bits = new List<bool>();
            Append(bits, 4, 4);
            Append(bits, data.Length, 8);
            foreach (byte value in data) Append(bits, value, 8);
            Append(bits, 0, 4);
            for (int pad = 0; bits.Count < 55 * 8; pad++) Append(bits, pad % 2 == 0 ? 236 : 17, 8);
            var payload = new byte[55];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) payload[i / 8] |= (byte)(1 << (7 - i % 8));
            foreach (byte value in ErrorCorrection(payload)) Append(bits, value, 8);

            Finder(3, 3);
            Finder(Size - 4, 3);
            Finder(3, Size - 4);
            for (int i = 8; i < Size - 8; i++)
            {
                Set(i, 6, i % 2 == 0);
                Set(6, i, i % 2 == 0);
            }
            for (int y = -2; y <= 2; y++)
                for (int x = -2; x <= 2; x++)
                    Set(22 + x, 22 + y, Math.Max(Math.Abs(x), Math.Abs(y)) != 1);
            Format();

            int bit = 0;
            bool up = true;
            for (int right = Size - 1; right >= 1; right -= 2, up = !up)
            {
                if (right == 6) right--;
                for (int row = 0; row < Size; row++)
                    for (int column = 0; column < 2; column++)
                    {
                        int x = right - column, y = up ? Size - 1 - row : row;
                        if (reserved[x, y]) continue;
                        bool value = bit < bits.Count && bits[bit];
                        bit++;
                        modules[x, y] = value ^ ((x + y) % 2 == 0);
                    }
            }
        }

        private void Format()
        {
            const int levelLMask0 = 0x77C4;
            for (int i = 0; i < 15; i++)
            {
                bool dark = ((levelLMask0 >> i) & 1) != 0;
                if (i < 6) Set(8, i, dark);
                else if (i < 8) Set(8, i + 1, dark);
                else if (i == 8) Set(7, 8, dark);
                else Set(14 - i, 8, dark);
                if (i < 8) Set(Size - i - 1, 8, dark);
                else Set(8, Size - 15 + i, dark);
            }
            Set(8, Size - 8, true);
        }

        private void Finder(int centerX, int centerY)
        {
            for (int y = -4; y <= 4; y++)
                for (int x = -4; x <= 4; x++)
                {
                    int distance = Math.Max(Math.Abs(x), Math.Abs(y));
                    Set(centerX + x, centerY + y, distance != 2 && distance != 4);
                }
        }

        private void Set(int x, int y, bool value)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size) return;
            modules[x, y] = value;
            reserved[x, y] = true;
        }

        private static void Append(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        private static byte[] ErrorCorrection(byte[] data)
        {
            var generator = new byte[16];
            generator[0] = 1;
            byte root = 1;
            for (int i = 0; i < 15; i++)
            {
                for (int j = i + 1; j > 0; j--) generator[j] ^= Multiply(generator[j - 1], root);
                root = Multiply(root, 2);
            }
            var working = new byte[data.Length + 15];
            data.CopyTo(working, 0);
            for (int i = 0; i < data.Length; i++)
            {
                byte factor = working[i];
                for (int j = 0; j < generator.Length; j++) working[i + j] ^= Multiply(generator[j], factor);
            }
            var remainder = new byte[15];
            Array.Copy(working, data.Length, remainder, 0, remainder.Length);
            return remainder;
        }

        private static byte Multiply(byte left, byte right)
        {
            int result = 0, term = left;
            for (int i = 0; i < 8; i++)
            {
                if (((right >> i) & 1) != 0) result ^= term;
                term = (term << 1) ^ ((term & 128) != 0 ? 0x11D : 0);
            }
            return (byte)result;
        }
    }
}

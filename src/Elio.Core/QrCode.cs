using System.Text;

namespace Elio.Core;

/// <summary>
/// Draws a short text as a QR code: the grid of dark and light squares a phone's camera reads.
/// </summary>
/// <remarks>
/// Only what Elio needs of the standard: text as bytes, the "M" level of error correction (a quarter of the
/// picture can be damaged or covered), and the six smallest sizes, which hold up to 106 bytes. That is plenty
/// for a link, and small enough to stay sharp on a screen across a desk.
/// </remarks>
public static class QrCode
{
    /// <summary>The longest text that fits, in UTF-8 bytes.</summary>
    public const int MaxBytes = 106;

    // For sizes 1 to 6 at level M: how many bytes of data in all, into how many blocks they are split, and how
    // many correction bytes each block gets.
    private static readonly (int Data, int Blocks, int Correction)[] Sizes =
    [
        (16, 1, 10), (28, 1, 16), (44, 1, 26), (64, 2, 18), (86, 2, 24), (108, 4, 16),
    ];

    /// <summary>The squares of the code for a text, dark where true, indexed [row, column]. No margin around it.</summary>
    /// <exception cref="ArgumentException">The text is longer than <see cref="MaxBytes"/> bytes.</exception>
    public static bool[,] Encode(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        // Two bytes of each size go to saying how the data is written and how long it is.
        int version = Array.FindIndex(Sizes, s => bytes.Length <= s.Data - 2) + 1;
        if (version == 0) throw new ArgumentException("The text is too long for a QR code of this kind.", nameof(text));

        (int dataLength, int blocks, int correction) = Sizes[version - 1];
        byte[] codewords = Interleave(Pack(bytes, dataLength), blocks, correction);

        int size = 17 + (4 * version);
        var dark = new bool[size, size];
        var reserved = new bool[size, size];
        DrawFixedPatterns(dark, reserved, version);
        DrawData(dark, reserved, codewords);

        // The data is overlaid with one of eight patterns, so that it never looks like the corner marks or
        // lies in large blank areas. Each is tried, and the one that leaves the calmest picture is kept.
        bool[,]? best = null;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])dark.Clone();
            ApplyMask(candidate, reserved, mask);
            DrawFormat(candidate, mask);
            int penalty = Penalty(candidate);
            if (penalty >= bestPenalty) continue;

            best = candidate;
            bestPenalty = penalty;
        }

        return best!;
    }

    // The header (bytes follow, and how many), the bytes, an end mark, and filler up to the size's capacity.
    private static byte[] Pack(byte[] bytes, int capacity)
    {
        var bits = new List<bool>(capacity * 8);
        void Append(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        Append(0b0100, 4);
        Append(bytes.Length, 8);
        foreach (byte b in bytes) Append(b, 8);
        Append(0, Math.Min(4, (capacity * 8) - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);

        var packed = new byte[capacity];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) packed[i >> 3] |= (byte)(0x80 >> (i & 7));
        for (int i = bits.Count / 8, filler = 0xEC; i < capacity; i++, filler ^= 0xEC ^ 0x11) packed[i] = (byte)filler;
        return packed;
    }

    // Splits the data into blocks, gives each its correction bytes, and deals them out a byte from each in turn,
    // so that a smudge in one place costs every block a little rather than one block everything.
    private static byte[] Interleave(byte[] data, int blocks, int correction)
    {
        int each = data.Length / blocks;
        byte[] divisor = Divisor(correction);
        var parts = new byte[blocks][];
        var checks = new byte[blocks][];
        for (int b = 0; b < blocks; b++)
        {
            parts[b] = data[(b * each)..((b + 1) * each)];
            checks[b] = Remainder(parts[b], divisor);
        }

        var all = new List<byte>((each + correction) * blocks);
        for (int i = 0; i < each; i++)
            for (int b = 0; b < blocks; b++) all.Add(parts[b][i]);
        for (int i = 0; i < correction; i++)
            for (int b = 0; b < blocks; b++) all.Add(checks[b][i]);
        return [.. all];
    }

    // ---- Reed-Solomon correction bytes, over the field of 256 elements the standard uses ----

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

    private static byte[] Divisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }

            root = Multiply(root, 2);
        }

        return result;
    }

    private static byte[] Remainder(byte[] data, byte[] divisor)
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

    // ---- The picture ----

    private static void DrawFixedPatterns(bool[,] dark, bool[,] reserved, int version)
    {
        int size = dark.GetLength(0);

        void Set(int row, int column, bool value)
        {
            dark[row, column] = value;
            reserved[row, column] = true;
        }

        // The dotted lines that give the reader its grid.
        for (int i = 0; i < size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }

        // The three corner marks, each with a light border.
        foreach ((int row, int column) in ((int, int)[])[(3, 3), (3, size - 4), (size - 4, 3)])
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int y = row + dy, x = column + dx;
                    if (y < 0 || y >= size || x < 0 || x >= size) continue;

                    int ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(y, x, ring != 2 && ring != 4);
                }
            }
        }

        // The small mark near the fourth corner, which every size but the smallest has.
        if (version >= 2)
        {
            int center = size - 7;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Set(center + dy, center + dx, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        // Where the format is written later, kept clear of the data until then.
        for (int i = 0; i < 9; i++)
        {
            if (!reserved[8, i]) Set(8, i, false);
            if (!reserved[i, 8]) Set(i, 8, false);
        }

        for (int i = 0; i < 8; i++)
        {
            Set(8, size - 1 - i, false);
            Set(size - 1 - i, 8, false);
        }

        Set(size - 8, 8, true);
    }

    // The data runs in a two-square-wide band from the bottom right corner, up and down in turn, around
    // everything that is already there.
    private static void DrawData(bool[,] dark, bool[,] reserved, byte[] codewords)
    {
        int size = dark.GetLength(0);
        int bit = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int step = 0; step < size; step++)
            {
                int row = upward ? size - 1 - step : step;
                for (int j = 0; j < 2; j++)
                {
                    int column = right - j;
                    if (reserved[row, column] || bit >= codewords.Length * 8) continue;

                    dark[row, column] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                    bit++;
                }
            }
        }
    }

    private static void ApplyMask(bool[,] dark, bool[,] reserved, int mask)
    {
        int size = dark.GetLength(0);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (reserved[y, x]) continue;

                bool flip = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => ((x / 3) + (y / 2)) % 2 == 0,
                    5 => ((x * y) % 2) + ((x * y) % 3) == 0,
                    6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
                    _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
                };
                if (flip) dark[y, x] = !dark[y, x];
            }
        }
    }

    // Which correction level and which pattern, written twice beside the corner marks with a correction code of its own.
    private static void DrawFormat(bool[,] dark, int mask)
    {
        int size = dark.GetLength(0);
        int data = mask; // The two bits for level M are 00.
        int remainder = data;
        for (int i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        int bits = ((data << 10) | remainder) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (int i = 0; i <= 5; i++) dark[i, 8] = Bit(i);
        dark[7, 8] = Bit(6);
        dark[8, 8] = Bit(7);
        dark[8, 7] = Bit(8);
        for (int i = 9; i < 15; i++) dark[8, 14 - i] = Bit(i);

        for (int i = 0; i < 8; i++) dark[8, size - 1 - i] = Bit(i);
        for (int i = 8; i < 15; i++) dark[size - 15 + i, 8] = Bit(i);
        dark[size - 8, 8] = true;
    }

    // How hard a picture is to read, by the standard's four measures: long runs of one color, solid two-by-two
    // blocks, shapes that look like a corner mark, and too much of one color overall.
    private static int Penalty(bool[,] dark)
    {
        int size = dark.GetLength(0);
        int penalty = 0;

        for (int line = 0; line < size; line++)
        {
            for (int direction = 0; direction < 2; direction++)
            {
                bool At(int i) => direction == 0 ? dark[line, i] : dark[i, line];

                int run = 1;
                for (int i = 1; i <= size; i++)
                {
                    if (i < size && At(i) == At(i - 1))
                    {
                        run++;
                        continue;
                    }

                    if (run >= 5) penalty += run - 2;
                    run = 1;
                }

                // Dark, light, three dark, light, dark, with four light on either side: what a corner mark looks like in a line.
                for (int i = 0; i + 10 < size; i++)
                {
                    bool before = !At(i) && !At(i + 1) && !At(i + 2) && !At(i + 3) && At(i + 4) && !At(i + 5) && At(i + 6) && At(i + 7) && At(i + 8) && !At(i + 9) && At(i + 10);
                    bool after = At(i) && !At(i + 1) && At(i + 2) && At(i + 3) && At(i + 4) && !At(i + 5) && At(i + 6) && !At(i + 7) && !At(i + 8) && !At(i + 9) && !At(i + 10);
                    if (before || after) penalty += 40;
                }
            }
        }

        int darkCount = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (dark[y, x]) darkCount++;
                if (y + 1 < size && x + 1 < size && dark[y, x] == dark[y, x + 1] && dark[y, x] == dark[y + 1, x] && dark[y, x] == dark[y + 1, x + 1]) penalty += 3;
            }
        }

        int percent = darkCount * 100 / (size * size);
        penalty += Math.Abs(percent - 50) / 5 * 10;
        return penalty;
    }
}

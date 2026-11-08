using System.Globalization;
using System.Text;

namespace Paduraksa.Dashboard.Components;

/// <summary>
/// A QR code (ISO/IEC 18004) for the TOTP enrolment's <c>otpauth://</c> URI, made here and drawn as inline SVG: the secret it carries never
/// leaves the server for an image service, and no package is needed. Byte mode, error correction level M, the smallest version (1 to 40)
/// the text fits, and the mask with the lowest penalty, as the standard describes.
/// </summary>
internal sealed class QrCode
{
    // Per version (index 0 unused), for error correction level M.
    private static readonly int[] EccCodewordsPerBlock =
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28];

    private static readonly int[] ErrorCorrectionBlocks =
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49];

    /// <summary>Level M's two format bits.</summary>
    private const int EccFormatBits = 0;

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    private QrCode(int version)
    {
        Version = version;
        Size = (version * 4) + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    /// <summary>The version (1 to 40).</summary>
    public int Version { get; }

    /// <summary>Modules per side, without the quiet zone.</summary>
    public int Size { get; }

    /// <summary>The mask that was applied (0 to 7).</summary>
    public int Mask { get; private set; }

    /// <summary>Whether the module at column <paramref name="x"/>, row <paramref name="y"/> is dark.</summary>
    public bool this[int x, int y] => _modules[y, x];

    /// <summary>Encodes <paramref name="text"/> as UTF-8 bytes.</summary>
    /// <exception cref="ArgumentException">The text is too long for a version 40 code.</exception>
    public static QrCode Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        for (; ; version++)
        {
            if (version > 40)
            {
                throw new ArgumentException("The text is too long for a QR code.", nameof(text));
            }

            if (DataBitsNeeded(data.Length, version) <= DataCodewords(version) * 8)
            {
                break;
            }
        }

        QrCode code = new(version);
        code.DrawFunctionPatterns();
        code.DrawCodewords(code.AddErrorCorrection(DataCodewordsFor(data, version)));
        code.ChooseMask();
        return code;
    }

    /// <summary>
    /// The dark modules as one SVG path in a square of <see cref="Size"/> plus a quiet zone of <paramref name="border"/> modules each side
    /// (one unit per module): each run of dark modules in a row is one rectangle.
    /// </summary>
    public string ToSvgPath(int border = 4)
    {
        StringBuilder path = new();
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (!_modules[y, x])
                {
                    continue;
                }

                int start = x;
                while (x + 1 < Size && _modules[y, x + 1])
                {
                    x++;
                }

                path.Append(CultureInfo.InvariantCulture, $"M{start + border},{y + border}h{x - start + 1}v1h-{x - start + 1}z");
            }
        }

        return path.ToString();
    }

    private static int DataBitsNeeded(int bytes, int version) => 4 + (version < 10 ? 8 : 16) + (bytes * 8);

    private static int RawDataModules(int version)
    {
        int result = ((16 * version) + 128) * version + 64;
        if (version >= 2)
        {
            int alignments = (version / 7) + 2;
            result -= ((25 * alignments) - 10) * alignments - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    private static int DataCodewords(int version) =>
        (RawDataModules(version) / 8) - (EccCodewordsPerBlock[version] * ErrorCorrectionBlocks[version]);

    /// <summary>Mode indicator, count, the bytes, terminator and padding, as codewords.</summary>
    private static byte[] DataCodewordsFor(byte[] data, int version)
    {
        List<bool> bits = [];
        void Append(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        Append(0b0100, 4);
        Append(data.Length, version < 10 ? 8 : 16);
        foreach (byte b in data)
        {
            Append(b, 8);
        }

        int capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - (bits.Count % 8)) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
        {
            Append(pad, 8);
        }

        byte[] codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }

        return codewords;
    }

    /// <summary>Splits the data into blocks, adds each block's Reed-Solomon codewords and interleaves them.</summary>
    private byte[] AddErrorCorrection(byte[] data)
    {
        int blockCount = ErrorCorrectionBlocks[Version];
        int eccLength = EccCodewordsPerBlock[Version];
        int rawCodewords = RawDataModules(Version) / 8;
        int shortBlocks = blockCount - (rawCodewords % blockCount);
        int shortBlockLength = rawCodewords / blockCount;
        byte[] divisor = ReedSolomonDivisor(eccLength);

        List<byte[]> blocks = [];
        for (int i = 0, k = 0; i < blockCount; i++)
        {
            int dataLength = shortBlockLength - eccLength + (i < shortBlocks ? 0 : 1);
            byte[] block = new byte[shortBlockLength + 1];
            Array.Copy(data, k, block, 0, dataLength);
            k += dataLength;
            byte[] ecc = ReedSolomonRemainder(data.AsSpan(k - dataLength, dataLength), divisor);
            Array.Copy(ecc, 0, block, shortBlockLength + 1 - eccLength, eccLength);
            blocks.Add(block);
        }

        List<byte> result = new(rawCodewords);
        for (int i = 0; i < shortBlockLength + 1; i++)
        {
            for (int j = 0; j < blocks.Count; j++)
            {
                // A short block has no codeword at the last data position.
                if (i != shortBlockLength - eccLength || j >= shortBlocks)
                {
                    result.Add(blocks[j][i]);
                }
            }
        }

        return [.. result];
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < result.Length)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ReedSolomonRemainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        byte[] result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    /// <summary>Multiplication in GF(2⁸) modulo x⁸ + x⁴ + x³ + x² + 1.</summary>
    private static byte Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        int[] positions = AlignmentPositions();
        for (int i = 0; i < positions.Length; i++)
        {
            for (int j = 0; j < positions.Length; j++)
            {
                bool finderCorner = (i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0);
                if (!finderCorner)
                {
                    DrawAlignment(positions[i], positions[j]);
                }
            }
        }

        DrawFormatBits(0);
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx;
                int y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size)
                {
                    SetFunction(x, y, distance != 2 && distance != 4);
                }
            }
        }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1)
        {
            return [];
        }

        int count = (Version / 7) + 2;
        int step = ((Version * 8) + (count * 3) + 5) / ((count * 4) - 4) * 2;
        int[] result = new int[count];
        result[0] = 6;
        for (int i = count - 1, position = Size - 7; i >= 1; i--, position -= step)
        {
            result[i] = position;
        }

        return result;
    }

    private void DrawFormatBits(int mask)
    {
        int data = (EccFormatBits << 3) | mask;
        int remainder = data;
        for (int i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        int bits = ((data << 10) | remainder) ^ 0x5412;
        for (int i = 0; i <= 5; i++)
        {
            SetFunction(8, i, Bit(bits, i));
        }

        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++)
        {
            SetFunction(14 - i, 8, Bit(bits, i));
        }

        for (int i = 0; i < 8; i++)
        {
            SetFunction(Size - 1 - i, 8, Bit(bits, i));
        }

        for (int i = 8; i < 15; i++)
        {
            SetFunction(8, Size - 15 + i, Bit(bits, i));
        }

        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }

        int remainder = Version;
        for (int i = 0; i < 12; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        }

        int bits = (Version << 12) | remainder;
        for (int i = 0; i < 18; i++)
        {
            bool bit = Bit(bits, i);
            int a = Size - 11 + (i % 3);
            int b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int vertical = 0; vertical < Size; vertical++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vertical : vertical;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    private void ChooseMask()
    {
        int best = 0;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            int penalty = Penalty();
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }

            ApplyMask(mask); // XOR again: undone.
        }

        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
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
                if (invert && !_isFunction[y, x])
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    /// <summary>The standard's four penalty rules: long runs, 2x2 blocks, finder-like patterns and an unbalanced dark share.</summary>
    private int Penalty()
    {
        int penalty = 0;
        for (int line = 0; line < Size; line++)
        {
            penalty += LinePenalty(i => _modules[line, i]) + LinePenalty(i => _modules[i, line]);
        }

        int dark = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                dark += _modules[y, x] ? 1 : 0;
                if (x + 1 < Size && y + 1 < Size)
                {
                    bool colour = _modules[y, x];
                    if (colour == _modules[y, x + 1] && colour == _modules[y + 1, x] && colour == _modules[y + 1, x + 1])
                    {
                        penalty += 3;
                    }
                }
            }
        }

        int total = Size * Size;
        int k = ((Math.Abs((dark * 20) - (total * 10)) + total - 1) / total) - 1;
        return penalty + (k * 10);
    }

    private static readonly bool[] FinderLike = [true, false, true, true, true, false, true];

    private int LinePenalty(Func<int, bool> at)
    {
        int penalty = 0;
        int run = 1;
        for (int i = 1; i <= Size; i++)
        {
            if (i < Size && at(i) == at(i - 1))
            {
                run++;
                continue;
            }

            if (run >= 5)
            {
                penalty += 3 + (run - 5);
            }

            run = 1;
        }

        // 1:1:3:1:1 with four light modules on one side (outside the symbol counts as light).
        for (int i = -4; i + 7 <= Size + 4; i++)
        {
            bool matches = true;
            for (int j = 0; j < 7 && matches; j++)
            {
                matches = Module(at, i + j) == FinderLike[j];
            }

            if (!matches)
            {
                continue;
            }

            bool lightBefore = true;
            bool lightAfter = true;
            for (int j = 1; j <= 4; j++)
            {
                lightBefore &= !Module(at, i - j);
                lightAfter &= !Module(at, i + 6 + j);
            }

            penalty += (lightBefore ? 40 : 0) + (lightAfter ? 40 : 0);
        }

        return penalty;
    }

    private bool Module(Func<int, bool> at, int i) => i >= 0 && i < Size && at(i);

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
}

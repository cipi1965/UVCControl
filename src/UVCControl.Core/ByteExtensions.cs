using System.Globalization;
using System.Text;

namespace UVCControl.Core;

/// <summary>Little-endian field access and hex formatting for UVC payloads.</summary>
public static class ByteExtensions
{
    public static long ReadInt(this ReadOnlySpan<byte> bytes, int offset, int size, bool signed)
    {
        if (offset < 0 || size <= 0 || offset + size > bytes.Length) return 0;
        ulong raw = 0;
        for (var i = 0; i < size; i++) raw |= (ulong)bytes[offset + i] << (8 * i);
        if (signed && size < 8 && (raw & (1UL << (8 * size - 1))) != 0)
            raw |= ~((1UL << (8 * size)) - 1);
        return unchecked((long)raw);
    }

    public static long ReadInt(this byte[] bytes, int offset, int size, bool signed) =>
        ((ReadOnlySpan<byte>)bytes).ReadInt(offset, size, signed);

    /// <summary>Returns a copy of <paramref name="bytes"/> with <paramref name="value"/> written little-endian, growing it if needed.</summary>
    public static byte[] WithInt(this byte[] bytes, long value, int offset, int size)
    {
        var result = new byte[Math.Max(bytes.Length, offset + size)];
        bytes.CopyTo(result, 0);
        WriteInt(result, value, offset, size);
        return result;
    }

    public static void WriteInt(this Span<byte> bytes, long value, int offset, int size)
    {
        var raw = unchecked((ulong)value);
        for (var i = 0; i < size; i++) bytes[offset + i] = (byte)(raw >> (8 * i));
    }

    public static void WriteInt(this byte[] bytes, long value, int offset, int size) =>
        ((Span<byte>)bytes).WriteInt(value, offset, size);

    public static byte[] Padded(this byte[] bytes, int length)
    {
        if (bytes.Length == length) return bytes;
        var result = new byte[length];
        bytes.AsSpan(0, Math.Min(bytes.Length, length)).CopyTo(result);
        return result;
    }

    public static string ToHexString(this ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string ToHexString(this byte[] bytes) => ((ReadOnlySpan<byte>)bytes).ToHexString();

    /// <summary>Parses hex like "0A 1b:ff,00"; whitespace, ':' and ',' are ignored.</summary>
    public static byte[]? ParseHex(string text)
    {
        var digits = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != ',').ToArray());
        if (digits.Length % 2 != 0) return null;
        var bytes = new byte[digits.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(digits.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }

    /// <summary>Shows the payload as ASCII when it contains at least four printable characters.</summary>
    public static string? PrintableAscii(this byte[] bytes)
    {
        static bool IsPrintable(byte b) => b is >= 0x20 and < 0x7F;
        if (bytes.Count(IsPrintable) < 4) return null;
        return new string(bytes.Select(b => IsPrintable(b) ? (char)b : '.').ToArray());
    }
}

using System.Buffers;
using System.Globalization;
using System.Text;

namespace Miningcore.Extensions;

public static class StringExtensions
{
    /// <summary>
    /// Converts a hex string to byte array
    /// </summary>
    /// <param name="str"></param>
    /// <returns></returns>
    public static byte[] HexToByteArray(this string str)
    {
        if(str.StartsWith("0x"))
            str = str[2..];

        var arr = new byte[str.Length >> 1];
        var count = str.Length >> 1;

        for(var i = 0; i < count; ++i)
            arr[i] = (byte) ((GetHexVal(str[i << 1]) << 4) + GetHexVal(str[(i << 1) + 1]));

        return arr;
    }

    /// <summary>
    /// Converts a hex string to byte array
    /// </summary>
    /// <param name="str"></param>
    /// <returns></returns>
    public static byte[] HexToReverseByteArray(this string str)
    {
        if(str.StartsWith("0x"))
            str = str[2..];

        var arr = new byte[str.Length >> 1];
        var count = str.Length >> 1;

        for(var i = 0; i < count; ++i)
            arr[count - 1 - i] = (byte) ((GetHexVal(str[i << 1]) << 4) + GetHexVal(str[(i << 1) + 1]));

        return arr;
    }

    private static int GetHexVal(char hex)
    {
        var val = (int) hex;
        return val - (val < 58 ? 48 : (val < 97 ? 55 : 87));
    }

    public static string ToStringHex8(this uint value)
    {
        return value.ToString("x8", CultureInfo.InvariantCulture);
    }

    public static string ToStringHex8(this int value)
    {
        return value.ToString("x8", CultureInfo.InvariantCulture);
    }

    public static string ToStringHexWithPrefix(this ulong value)
    {
        if(value == 0)
            return "0x0";

        return "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    }

    public static string ToStringHexWithPrefix(this long value)
    {
        if(value == 0)
            return "0x0";

        return "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    }

    public static string ToStringHexWithPrefix(this uint value)
    {
        if(value == 0)
            return "0x0";

        return "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    }

    public static string ToStringHexWithPrefix(this int value)
    {
        if(value == 0)
            return "0x0";

        return "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    }

    public static string StripHexPrefix(this string value)
    {
        if(value?.ToLower().StartsWith("0x") == true)
            return value[2..];

        return value;
    }

    public static T IntegralFromHex<T>(this string value)
    {
        var underlyingType = Nullable.GetUnderlyingType(typeof(T));

        if(value.StartsWith("0x"))
            value = value[2..];

        if(!ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var val))
            throw new FormatException();

        return (T) Convert.ChangeType(val, underlyingType ?? typeof(T));
    }

    public static string ToLowerCamelCase(this string str)
    {
        if(string.IsNullOrEmpty(str))
            return str;

        return char.ToLowerInvariant(str[0]) + str[1..];
    }

    public static string AsString(this ReadOnlySequence<byte> line, Encoding encoding)
    {
        return encoding.GetString(line.ToSpan());
    }

    public static string Capitalize(this string str)
    {
        if(string.IsNullOrEmpty(str))
            return str;

        return str[..1].ToUpper() + str[1..];
    }

    /// <summary>
    /// Masks a wallet/payout address for public-facing REST and WebSocket responses by keeping only
    /// the first 12 and last 6 characters (eg. "web1pm4tcwj3...vmh0et").
    ///
    /// This MUST stay byte-for-byte identical to the frontend's display truncation
    /// (poolmainpage/assets/js/pool.js, fmt.addr(a, 12)) — both the slice points (12 / 6) and the
    /// "leave short strings untouched" guard (length &lt;= 25). The frontend compares its own
    /// locally-truncated address against this masked value to detect "is this my block" in the
    /// myminer tab, so any divergence here silently breaks that comparison.
    ///
    /// Does not mutate the input — returns a new string, safe to call on values that are also used
    /// elsewhere (eg. persistence entities) without affecting those other uses.
    /// </summary>
    public static string MaskAddress(this string address)
    {
        const int prefixLen = 12;
        const int suffixLen = 6;

        if(string.IsNullOrEmpty(address) || address.Length <= prefixLen + suffixLen + 1)
            return address;

        return $"{address[..prefixLen]}...{address[^suffixLen..]}";
    }
}

using System.Globalization;

namespace VpnFleet;

/// <summary>Version numbers of apps ("1.104.7", tag "v1.104.7") and of AXIS OS ("10.12.250").</summary>
public static class AppVersions
{
    /// <summary>The numbers of a version: "v1.104.7-beta" gives [1, 104, 7]; empty when it starts with no number.</summary>
    public static IReadOnlyList<int> Parse(string? version)
    {
        var text = (version ?? "").Trim().TrimStart('v', 'V');
        var numbers = new List<int>();
        foreach (var part in text.Split('.'))
        {
            var digits = new string([.. part.TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                break;
            }

            numbers.Add(n);
            if (digits.Length != part.Length)
            {
                break;
            }
        }

        return numbers;
    }

    public static int Compare(string? a, string? b)
    {
        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            var c = (i < x.Count ? x[i] : 0).CompareTo(i < y.Count ? y[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is a later version than <paramref name="installed"/> (both readable).</summary>
    public static bool IsNewer(string candidate, string installed) =>
        Parse(candidate).Count > 0 && Parse(installed).Count > 0 && Compare(candidate, installed) > 0;

    /// <summary>"v1.104.7" gives "1.104.7".</summary>
    public static string FromTag(string tag) => tag.Trim().TrimStart('v', 'V');
}

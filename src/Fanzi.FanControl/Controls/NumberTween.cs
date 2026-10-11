using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// A formatted metric split into its parts: <c>"72,4 °C"</c> → prefix "", value 72.4, 1 decimal, ',' separator,
/// suffix " °C". Lets <see cref="AnimatedNumber"/> count between two readings and print every frame in the
/// same format (and culture separator) as the final text.
/// </summary>
public readonly record struct NumberText(string Prefix, double Value, int Decimals, char Separator, string Suffix)
{
    /// <summary>Same unit and prefix — only then does counting from one to the other make sense.</summary>
    public bool IsCompatibleWith(NumberText other)
        => string.Equals(Prefix, other.Prefix, StringComparison.Ordinal)
           && string.Equals(Suffix, other.Suffix, StringComparison.Ordinal);

    /// <summary>Prints <paramref name="value"/> with this text's decimals, separator, prefix and suffix.</summary>
    public string Format(double value)
    {
        double rounded = Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0; // no "-0.0"
        string number = rounded.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (Separator != '.') number = number.Replace('.', Separator);
        return Prefix + number + Suffix;
    }
}

/// <summary>Pure parsing and easing for the animated number transitions.</summary>
public static class NumberTween
{
    // One number, optionally signed, with '.' or ',' decimals; no other digits around it
    // ("72.0 °C", "38 %", "4725 MHz", "1,284 V"). "--", "Not detected" or "2 of 5" don't parse.
    private static readonly Regex Pattern = new(
        @"^(?<prefix>[^\d\-]*)(?<number>-?\d+(?:(?<sep>[.,])(?<decimals>\d+))?)(?<suffix>[^\d]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Parses a formatted metric; false for placeholders and text with more than one number.</summary>
    public static bool TryParse(string? text, out NumberText number)
    {
        number = default;
        if (string.IsNullOrEmpty(text)) return false;
        var match = Pattern.Match(text);
        if (!match.Success) return false;

        char separator = match.Groups["sep"].Success ? match.Groups["sep"].Value[0] : '.';
        int decimals = match.Groups["decimals"].Success ? match.Groups["decimals"].Value.Length : 0;
        string invariant = match.Groups["number"].Value.Replace(',', '.');
        if (!double.TryParse(invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.IsNaN(value) || double.IsInfinity(value))
            return false;

        number = new NumberText(match.Groups["prefix"].Value, value, decimals, separator, match.Groups["suffix"].Value);
        return true;
    }

    /// <summary>Cubic ease-out on 0..1 (fast start, gentle landing); clamps outside the range.</summary>
    public static double Ease(double progress)
    {
        double t = double.IsNaN(progress) ? 1 : Math.Clamp(progress, 0, 1);
        double inverse = 1 - t;
        return 1 - inverse * inverse * inverse;
    }

    /// <summary>The value shown at <paramref name="progress"/> (0..1) of a count from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double ValueAt(double from, double to, double progress) => from + (to - from) * Ease(progress);
}

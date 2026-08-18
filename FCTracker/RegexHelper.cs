namespace FCTracker;

using System.Text.RegularExpressions;

internal static partial class RegexHelper
{
    [GeneratedRegex(@"""([^""]+)""|\S+", RegexOptions.CultureInvariant)]
    public static partial Regex ArgumentParserRegex();

    /// <summary>Matches a formatted gil amount, e.g. "3,750,000 gil". Group 1 keeps the separators.</summary>
    [GeneratedRegex(@"([0-9][0-9\s.,\u00A0\u202F]*)\s*gil", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex GilAmountRegex();
}

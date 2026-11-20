namespace SmartSubtitleConverterApi.Binary;

/// <summary>
/// Former ISO 3166-1 alpha-3 codes (ISO 3166-3), accepted for EBU STL countryOfOrigin because archive material carries them
/// (a 1990 programme from Burma is BUR). Codes and withdrawal years: Debian iso-codes data file iso_3166-3.json
/// (https://salsa.debian.org/iso-codes-team/iso-codes, data/iso_3166-3.json, 31 entries). Names and successor codes: the ISO 3166-3
/// list of former names ("New country names and codes"; https://en.wikipedia.org/wiki/ISO_3166-3), which the Debian file lacks.
/// ATF is left out: it is also the current code of the French Southern Territories. Only alpha-3 is accepted: the former
/// alpha-2 codes clash with each other (CS) and with current ones (GE), and the four-letter codes are not used in headers.
/// </summary>
public static class RetiredCountryCodes
{
    public sealed record Retired(string Alpha3, string Name, int WithdrawnYear, string[] Successors);

    private static readonly (string A3, string Name, int Year, string Successors)[] Rows =
    [
        ("AFI", "French Afars and Issas", 1977, "DJI"),
        ("ANT", "Netherlands Antilles", 2010, "BES CUW SXM"),
        ("ATB", "British Antarctic Territory", 1979, "ATA"),
        ("ATN", "Dronning Maud Land", 1983, "ATA"),
        ("BUR", "Burma", 1989, "MMR"),
        ("BYS", "Byelorussian SSR", 1992, "BLR"),
        ("CSK", "Czechoslovakia", 1993, "CZE SVK"),
        ("CTE", "Canton and Enderbury Islands", 1984, "KIR"),
        ("DDR", "German Democratic Republic", 1990, "DEU"),
        ("DHY", "Dahomey", 1977, "BEN"),
        ("FXX", "France, Metropolitan", 1997, "FRA"),
        ("GEL", "Gilbert Islands", 1979, "KIR"),
        ("HVO", "Upper Volta", 1984, "BFA"),
        ("JTN", "Johnston Island", 1986, "UMI"),
        ("MID", "Midway Islands", 1986, "UMI"),
        ("NHB", "New Hebrides", 1980, "VUT"),
        ("NTZ", "Neutral Zone", 1993, "IRQ SAU"),
        ("PCI", "Pacific Islands (Trust Territory)", 1986, "MHL FSM MNP PLW"),
        ("PCZ", "Panama Canal Zone", 1980, "PAN"),
        ("PUS", "United States Miscellaneous Pacific Islands", 1986, "UMI"),
        ("RHO", "Southern Rhodesia", 1980, "ZWE"),
        ("SCG", "Serbia and Montenegro", 2006, "MNE SRB"),
        ("SKM", "Sikkim", 1975, "IND"),
        ("SUN", "USSR", 1992, "ARM AZE EST GEO KAZ KGZ LVA LTU MDA RUS TJK TKM UZB"),
        ("TMP", "East Timor", 2002, "TLS"),
        ("VDR", "Democratic Republic of Viet-Nam", 1977, "VNM"),
        ("WAK", "Wake Island", 1986, "UMI"),
        ("YMD", "Democratic Yemen", 1990, "YEM"),
        ("YUG", "Yugoslavia", 2003, "SCG"),
        ("ZAR", "Zaire", 1997, "COD"),
    ];

    private static readonly Dictionary<string, Retired> ByAlpha3 =
        Rows.ToDictionary(r => r.A3, r => new Retired(r.A3, r.Name, r.Year, r.Successors.Split(' ')), StringComparer.Ordinal);

    public static IReadOnlyCollection<Retired> All => ByAlpha3.Values;

    /// <summary>A retired alpha-3 code in any case with surrounding whitespace; null for anything else, including a code that is also current.</summary>
    public static Retired? Find(string? input)
    {
        if (input == null) return null;
        var up = input.Trim().ToUpperInvariant();
        return up.Length == 3 && CountryCodes.ByCode(up) == null && ByAlpha3.TryGetValue(up, out var r) ? r : null;
    }

    /// <summary>The successors as "MMR (Myanmar)" or "CZE (Czechia) and SVK (Slovakia)"; a successor that is itself retired says so.</summary>
    public static string SuccessorList(Retired r)
    {
        var parts = r.Successors.Select(a => CountryCodes.ByCode(a) is { } c ? $"{a} ({c.DisplayName})" : ByAlpha3.TryGetValue(a, out var old) ? $"{a} ({old.Name}, retired)" : a).ToList();
        return parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }
}

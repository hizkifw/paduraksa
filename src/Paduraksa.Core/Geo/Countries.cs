using System.Collections.Frozen;

namespace Paduraksa.Core.Geo;

/// <summary>One country a rule can name: its code (upper case) and its English name, ASCII only.</summary>
public readonly record struct Country(string Code, string Name);

/// <summary>
/// The countries an access list may name: the ISO 3166-1 alpha-2 assignments, plus <c>XK</c> (Kosovo, which Cloudflare reports and
/// ISO doesn't assign), and the two pseudo-codes of <c>CF-IPCountry</c>, <c>T1</c> (Tor) and <c>XX</c> (unknown). A code
/// outside the list is refused when written, so a typo can't make a rule that never matches.
/// </summary>
public static class Countries
{
    /// <summary>The pseudo-code a rule uses for Tor exit nodes.</summary>
    public const string TorCode = "T1";

    /// <summary>The pseudo-code a rule uses for a request with no known country.</summary>
    public const string UnknownCode = "XX";

    private static readonly Country[] Table =
    [
        new("AD", "Andorra"),
        new("AE", "United Arab Emirates"),
        new("AF", "Afghanistan"),
        new("AG", "Antigua and Barbuda"),
        new("AI", "Anguilla"),
        new("AL", "Albania"),
        new("AM", "Armenia"),
        new("AO", "Angola"),
        new("AQ", "Antarctica"),
        new("AR", "Argentina"),
        new("AS", "American Samoa"),
        new("AT", "Austria"),
        new("AU", "Australia"),
        new("AW", "Aruba"),
        new("AX", "Aland Islands"),
        new("AZ", "Azerbaijan"),
        new("BA", "Bosnia and Herzegovina"),
        new("BB", "Barbados"),
        new("BD", "Bangladesh"),
        new("BE", "Belgium"),
        new("BF", "Burkina Faso"),
        new("BG", "Bulgaria"),
        new("BH", "Bahrain"),
        new("BI", "Burundi"),
        new("BJ", "Benin"),
        new("BL", "Saint Barthelemy"),
        new("BM", "Bermuda"),
        new("BN", "Brunei"),
        new("BO", "Bolivia"),
        new("BQ", "Bonaire, Sint Eustatius and Saba"),
        new("BR", "Brazil"),
        new("BS", "Bahamas"),
        new("BT", "Bhutan"),
        new("BV", "Bouvet Island"),
        new("BW", "Botswana"),
        new("BY", "Belarus"),
        new("BZ", "Belize"),
        new("CA", "Canada"),
        new("CC", "Cocos (Keeling) Islands"),
        new("CD", "Congo (Democratic Republic)"),
        new("CF", "Central African Republic"),
        new("CG", "Congo"),
        new("CH", "Switzerland"),
        new("CI", "Cote d'Ivoire"),
        new("CK", "Cook Islands"),
        new("CL", "Chile"),
        new("CM", "Cameroon"),
        new("CN", "China"),
        new("CO", "Colombia"),
        new("CR", "Costa Rica"),
        new("CU", "Cuba"),
        new("CV", "Cabo Verde"),
        new("CW", "Curacao"),
        new("CX", "Christmas Island"),
        new("CY", "Cyprus"),
        new("CZ", "Czechia"),
        new("DE", "Germany"),
        new("DJ", "Djibouti"),
        new("DK", "Denmark"),
        new("DM", "Dominica"),
        new("DO", "Dominican Republic"),
        new("DZ", "Algeria"),
        new("EC", "Ecuador"),
        new("EE", "Estonia"),
        new("EG", "Egypt"),
        new("EH", "Western Sahara"),
        new("ER", "Eritrea"),
        new("ES", "Spain"),
        new("ET", "Ethiopia"),
        new("FI", "Finland"),
        new("FJ", "Fiji"),
        new("FK", "Falkland Islands"),
        new("FM", "Micronesia"),
        new("FO", "Faroe Islands"),
        new("FR", "France"),
        new("GA", "Gabon"),
        new("GB", "United Kingdom"),
        new("GD", "Grenada"),
        new("GE", "Georgia"),
        new("GF", "French Guiana"),
        new("GG", "Guernsey"),
        new("GH", "Ghana"),
        new("GI", "Gibraltar"),
        new("GL", "Greenland"),
        new("GM", "Gambia"),
        new("GN", "Guinea"),
        new("GP", "Guadeloupe"),
        new("GQ", "Equatorial Guinea"),
        new("GR", "Greece"),
        new("GS", "South Georgia and the South Sandwich Islands"),
        new("GT", "Guatemala"),
        new("GU", "Guam"),
        new("GW", "Guinea-Bissau"),
        new("GY", "Guyana"),
        new("HK", "Hong Kong"),
        new("HM", "Heard Island and McDonald Islands"),
        new("HN", "Honduras"),
        new("HR", "Croatia"),
        new("HT", "Haiti"),
        new("HU", "Hungary"),
        new("ID", "Indonesia"),
        new("IE", "Ireland"),
        new("IL", "Israel"),
        new("IM", "Isle of Man"),
        new("IN", "India"),
        new("IO", "British Indian Ocean Territory"),
        new("IQ", "Iraq"),
        new("IR", "Iran"),
        new("IS", "Iceland"),
        new("IT", "Italy"),
        new("JE", "Jersey"),
        new("JM", "Jamaica"),
        new("JO", "Jordan"),
        new("JP", "Japan"),
        new("KE", "Kenya"),
        new("KG", "Kyrgyzstan"),
        new("KH", "Cambodia"),
        new("KI", "Kiribati"),
        new("KM", "Comoros"),
        new("KN", "Saint Kitts and Nevis"),
        new("KP", "North Korea"),
        new("KR", "South Korea"),
        new("KW", "Kuwait"),
        new("KY", "Cayman Islands"),
        new("KZ", "Kazakhstan"),
        new("LA", "Laos"),
        new("LB", "Lebanon"),
        new("LC", "Saint Lucia"),
        new("LI", "Liechtenstein"),
        new("LK", "Sri Lanka"),
        new("LR", "Liberia"),
        new("LS", "Lesotho"),
        new("LT", "Lithuania"),
        new("LU", "Luxembourg"),
        new("LV", "Latvia"),
        new("LY", "Libya"),
        new("MA", "Morocco"),
        new("MC", "Monaco"),
        new("MD", "Moldova"),
        new("ME", "Montenegro"),
        new("MF", "Saint Martin (French part)"),
        new("MG", "Madagascar"),
        new("MH", "Marshall Islands"),
        new("MK", "North Macedonia"),
        new("ML", "Mali"),
        new("MM", "Myanmar"),
        new("MN", "Mongolia"),
        new("MO", "Macao"),
        new("MP", "Northern Mariana Islands"),
        new("MQ", "Martinique"),
        new("MR", "Mauritania"),
        new("MS", "Montserrat"),
        new("MT", "Malta"),
        new("MU", "Mauritius"),
        new("MV", "Maldives"),
        new("MW", "Malawi"),
        new("MX", "Mexico"),
        new("MY", "Malaysia"),
        new("MZ", "Mozambique"),
        new("NA", "Namibia"),
        new("NC", "New Caledonia"),
        new("NE", "Niger"),
        new("NF", "Norfolk Island"),
        new("NG", "Nigeria"),
        new("NI", "Nicaragua"),
        new("NL", "Netherlands"),
        new("NO", "Norway"),
        new("NP", "Nepal"),
        new("NR", "Nauru"),
        new("NU", "Niue"),
        new("NZ", "New Zealand"),
        new("OM", "Oman"),
        new("PA", "Panama"),
        new("PE", "Peru"),
        new("PF", "French Polynesia"),
        new("PG", "Papua New Guinea"),
        new("PH", "Philippines"),
        new("PK", "Pakistan"),
        new("PL", "Poland"),
        new("PM", "Saint Pierre and Miquelon"),
        new("PN", "Pitcairn Islands"),
        new("PR", "Puerto Rico"),
        new("PS", "Palestine"),
        new("PT", "Portugal"),
        new("PW", "Palau"),
        new("PY", "Paraguay"),
        new("QA", "Qatar"),
        new("RE", "Reunion"),
        new("RO", "Romania"),
        new("RS", "Serbia"),
        new("RU", "Russia"),
        new("RW", "Rwanda"),
        new("SA", "Saudi Arabia"),
        new("SB", "Solomon Islands"),
        new("SC", "Seychelles"),
        new("SD", "Sudan"),
        new("SE", "Sweden"),
        new("SG", "Singapore"),
        new("SH", "Saint Helena, Ascension and Tristan da Cunha"),
        new("SI", "Slovenia"),
        new("SJ", "Svalbard and Jan Mayen"),
        new("SK", "Slovakia"),
        new("SL", "Sierra Leone"),
        new("SM", "San Marino"),
        new("SN", "Senegal"),
        new("SO", "Somalia"),
        new("SR", "Suriname"),
        new("SS", "South Sudan"),
        new("ST", "Sao Tome and Principe"),
        new("SV", "El Salvador"),
        new("SX", "Sint Maarten (Dutch part)"),
        new("SY", "Syria"),
        new("SZ", "Eswatini"),
        new("TC", "Turks and Caicos Islands"),
        new("TD", "Chad"),
        new("TF", "French Southern Territories"),
        new("TG", "Togo"),
        new("TH", "Thailand"),
        new("TJ", "Tajikistan"),
        new("TK", "Tokelau"),
        new("TL", "Timor-Leste"),
        new("TM", "Turkmenistan"),
        new("TN", "Tunisia"),
        new("TO", "Tonga"),
        new("TR", "Turkiye"),
        new("TT", "Trinidad and Tobago"),
        new("TV", "Tuvalu"),
        new("TW", "Taiwan"),
        new("TZ", "Tanzania"),
        new("UA", "Ukraine"),
        new("UG", "Uganda"),
        new("UM", "United States Minor Outlying Islands"),
        new("US", "United States"),
        new("UY", "Uruguay"),
        new("UZ", "Uzbekistan"),
        new("VA", "Vatican City"),
        new("VC", "Saint Vincent and the Grenadines"),
        new("VE", "Venezuela"),
        new("VG", "British Virgin Islands"),
        new("VI", "US Virgin Islands"),
        new("VN", "Vietnam"),
        new("VU", "Vanuatu"),
        new("WF", "Wallis and Futuna"),
        new("WS", "Samoa"),
        new("XK", "Kosovo"),
        new("YE", "Yemen"),
        new("YT", "Mayotte"),
        new("ZA", "South Africa"),
        new("ZM", "Zambia"),
        new("ZW", "Zimbabwe"),
    ];

    /// <summary>The assigned countries, sorted by name.</summary>
    public static IReadOnlyList<Country> All { get; } = [.. Table.OrderBy(static c => c.Name, StringComparer.Ordinal)];

    private static readonly FrozenDictionary<string, Country> ByCode = Table.ToFrozenDictionary(static c => c.Code, StringComparer.Ordinal);

    /// <summary>The slot of an upper- or lower-case rule code (an assigned country, <c>T1</c> or <c>XX</c>); false for any other.</summary>
    public static bool TryParseRuleCode(ReadOnlySpan<char> value, out CountryCode code)
    {
        code = CountryCode.Unknown;
        if (value.Length != 2)
        {
            return false;
        }

        if (value.Equals(UnknownCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals(TorCode, StringComparison.OrdinalIgnoreCase))
        {
            code = CountryCode.Tor;
            return true;
        }

        var parsed = CountryCode.Parse(value);
        if (parsed.IsUnknown || !ByCode.ContainsKey(parsed.ToString()))
        {
            return false;
        }

        code = parsed;
        return true;
    }

    /// <summary>The name of an assigned country, or of the pseudo-codes; the code itself for any other letter pair.</summary>
    public static string NameOf(CountryCode code) =>
        code.IsUnknown ? "Unknown country"
        : code.IsTor ? "Tor exit nodes"
        : ByCode.TryGetValue(code.ToString(), out var country) ? country.Name
        : code.ToString();
}

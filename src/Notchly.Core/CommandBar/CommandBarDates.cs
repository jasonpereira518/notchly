// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarDates.swift: the questions about time that people put into a
// search field. Swift's Calendar/DateFormatter/TimeZone become System.Globalization and TimeZoneInfo; the clock and the zone are
// parameters so answers are deterministic. Platform-free.
using System.Globalization;
using System.Text.RegularExpressions;

namespace Notchly.Core.CommandBar;

/// <summary>
/// The questions about time that people put into a search field: what day is it three weeks from now, how many days until a date,
/// what time it is somewhere else. Answered by the calendar and time zone database the machine already carries, never by the
/// network. Deliberately narrow, like the calculator: every answer needs a full pattern (a number, a unit and a direction, or a time
/// word and a place). Anything short of that is left to the search, because a launcher that guesses at dates turns "3 dias" into an
/// answer nobody asked for.
/// </summary>
public static partial class CommandBarDates
{
    public sealed record Result(
        /// <summary>The answer itself, written the way the culture writes dates.</summary>
        string Formatted,
        /// <summary>One line under it: the weekday, the place, whatever gives it sense.</summary>
        string Detail);

    // MARK: Parser vocabulary
    //
    // Not visible text: people type in their own words whatever the interface language is, so each set carries the languages the app
    // speaks, already folded (no accents, lower case). Every word is also stored folded, since typed text is folded before the lookup.

    private static HashSet<string> Words(params string[] words)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            set.Add(word);
            set.Add(AnswerText.Normalized(word));
        }
        return set;
    }

    private static readonly HashSet<string> TodayWords = Words(
        "today", "hoje", "heute", "hoy", "oggi", "aujourdhui", "aujourd'hui", "segodnya", "сегодня", "bugun", "今日", "오늘", "今天");

    private static readonly HashSet<string> FutureWords = Words(
        "in", "em", "daqui", "dentro", "nach", "tra", "fra", "dans", "через", "sonra", "後", "후", "后", "from");

    private static readonly HashSet<string> PastWords = Words(
        "ago", "atras", "ha", "hace", "vor", "fa", "назад", "once", "前", "전");

    private static readonly HashSet<string> UntilWords = Words(
        "until", "till", "ate", "hasta", "bis", "fino", "jusqu", "до", "kadar", "까지", "까지는");

    private static readonly HashSet<string> TimeWords = Words(
        "time", "hora", "horas", "hour", "uhrzeit", "uhr", "ora", "heure", "время", "saat", "時刻", "時間", "시간", "时间", "现在");

    private enum Component { Day, Week, Month, Year }

    private static readonly Dictionary<string, Component> Units = BuildUnits();

    private static Dictionary<string, Component> BuildUnits()
    {
        var map = new Dictionary<string, Component>(StringComparer.Ordinal);
        void Add(string[] names, Component component)
        {
            foreach (var name in names)
            {
                map[name] = component;
                map[AnswerText.Normalized(name)] = component;
            }
        }
        Add(["day", "days", "dia", "dias", "tag", "tage", "giorno", "giorni", "jour", "jours", "den", "dnya", "dney", "дня", "дней",
             "день", "gun", "gunler", "日", "일", "天"], Component.Day);
        Add(["week", "weeks", "semana", "semanas", "woche", "wochen", "settimana", "settimane", "semaine", "semaines", "nedelya",
             "недели", "недель", "неделя", "hafta", "週", "주", "周", "星期"], Component.Week);
        Add(["month", "months", "mes", "meses", "monat", "monate", "mese", "mesi", "mois", "месяц", "месяца", "месяцев", "ay", "月",
             "월", "个月"], Component.Month);
        Add(["year", "years", "ano", "anos", "jahr", "jahre", "anno", "anni", "an", "ans", "annee", "annees", "god", "года", "лет",
             "год", "yil", "年", "년"], Component.Year);
        return map;
    }

    /// <summary>
    /// Places whose everyday name differs from the one the time zone database uses. The database covers the rest on its own, so
    /// this stays a handful rather than a table to maintain.
    /// </summary>
    private static readonly Dictionary<string, string> PlaceAliases = new()
    {
        ["londres"] = "London", ["lisboa"] = "Lisbon", ["roma"] = "Rome",
        ["moscou"] = "Moscow", ["moscovo"] = "Moscow", ["москва"] = "Moscow",
        ["nova york"] = "New_York", ["nueva york"] = "New_York", ["nova iorque"] = "New_York",
        ["cidade do mexico"] = "Mexico_City", ["ciudad de mexico"] = "Mexico_City",
        ["pequim"] = "Beijing", ["pequin"] = "Beijing", ["toquio"] = "Tokyo", ["tokio"] = "Tokyo",
        ["genebra"] = "Geneva", ["viena"] = "Vienna", ["copenhague"] = "Copenhagen",
        ["praga"] = "Prague", ["atenas"] = "Athens", ["varsovia"] = "Warsaw",
        ["bruxelas"] = "Brussels", ["zurique"] = "Zurich", ["munique"] = "Munich",
        ["colonia"] = "Cologne", ["estocolmo"] = "Stockholm", ["hamburgo"] = "Hamburg",
    };

    /// <summary>
    /// The IANA identifiers the command bar knows by city (upstream asks the system for its whole list; Windows only lists its own
    /// zone names, so the canonical IANA list ships here). The first identifier wins for a name that occurs twice.
    /// </summary>
    private static readonly (string Region, string Cities)[] ZoneList =
    [
        ("Africa", "Abidjan Accra Addis_Ababa Algiers Asmara Bamako Bangui Banjul Bissau Blantyre Brazzaville Bujumbura Cairo Casablanca Ceuta "
                   + "Conakry Dakar Dar_es_Salaam Djibouti Douala El_Aaiun Freetown Gaborone Harare Johannesburg Juba Kampala Khartoum Kigali "
                   + "Kinshasa Lagos Libreville Lome Luanda Lubumbashi Lusaka Malabo Maputo Maseru Mbabane Mogadishu Monrovia Nairobi Ndjamena "
                   + "Niamey Nouakchott Ouagadougou Porto-Novo Sao_Tome Tripoli Tunis Windhoek"),
        ("America", "Adak Anchorage Anguilla Antigua Araguaina Argentina/Buenos_Aires Argentina/Cordoba Argentina/Mendoza Aruba Asuncion "
                    + "Atikokan Bahia Bahia_Banderas Barbados Belem Belize Blanc-Sablon Boa_Vista Bogota Boise Cambridge_Bay Campo_Grande Cancun "
                    + "Caracas Cayenne Cayman Chicago Chihuahua Costa_Rica Creston Cuiaba Curacao Danmarkshavn Dawson Dawson_Creek Denver Detroit "
                    + "Dominica Edmonton Eirunepe El_Salvador Fort_Nelson Fortaleza Glace_Bay Goose_Bay Grand_Turk Grenada Guadeloupe Guatemala "
                    + "Guayaquil Guyana Halifax Havana Hermosillo Indiana/Indianapolis Inuvik Iqaluit Jamaica Juneau Kentucky/Louisville Kralendijk "
                    + "La_Paz Lima Los_Angeles Lower_Princes Maceio Managua Manaus Marigot Martinique Matamoros Mazatlan Menominee Merida "
                    + "Metlakatla Mexico_City Miquelon Moncton Monterrey Montevideo Montserrat Nassau New_York Nome Noronha North_Dakota/Center "
                    + "Nuuk Ojinaga Panama Paramaribo Phoenix Port-au-Prince Port_of_Spain Porto_Velho Puerto_Rico Punta_Arenas Rankin_Inlet "
                    + "Recife Regina Resolute Rio_Branco Santarem Santiago Santo_Domingo Sao_Paulo Scoresbysund Sitka St_Barthelemy St_Johns "
                    + "St_Kitts St_Lucia St_Thomas St_Vincent Swift_Current Tegucigalpa Thule Tijuana Toronto Tortola Vancouver Whitehorse "
                    + "Winnipeg Yakutat"),
        ("Antarctica", "Casey Davis Mawson McMurdo Palmer Rothera Syowa Troll Vostok"),
        ("Arctic", "Longyearbyen"),
        ("Asia", "Aden Almaty Amman Anadyr Aqtau Aqtobe Ashgabat Atyrau Baghdad Bahrain Baku Bangkok Barnaul Beirut Bishkek Brunei Chita "
                 + "Choibalsan Colombo Damascus Dhaka Dili Dubai Dushanbe Famagusta Gaza Hebron Ho_Chi_Minh Hong_Kong Hovd Irkutsk Jakarta "
                 + "Jayapura Jerusalem Kabul Kamchatka Karachi Kathmandu Khandyga Kolkata Krasnoyarsk Kuala_Lumpur Kuching Kuwait Macau Magadan "
                 + "Makassar Manila Muscat Nicosia Novokuznetsk Novosibirsk Omsk Oral Phnom_Penh Pontianak Pyongyang Qatar Qostanay Qyzylorda "
                 + "Riyadh Sakhalin Samarkand Seoul Shanghai Singapore Srednekolymsk Taipei Tashkent Tbilisi Tehran Thimphu Tokyo Tomsk "
                 + "Ulaanbaatar Urumqi Ust-Nera Vientiane Vladivostok Yakutsk Yangon Yekaterinburg Yerevan"),
        ("Atlantic", "Azores Bermuda Canary Cape_Verde Faroe Madeira Reykjavik South_Georgia St_Helena Stanley"),
        ("Australia", "Adelaide Brisbane Broken_Hill Darwin Eucla Hobart Lindeman Lord_Howe Melbourne Perth Sydney"),
        ("Europe", "Amsterdam Andorra Astrakhan Athens Belgrade Berlin Bratislava Brussels Bucharest Budapest Busingen Chisinau Copenhagen "
                   + "Dublin Gibraltar Guernsey Helsinki Isle_of_Man Istanbul Jersey Kaliningrad Kirov Kyiv Lisbon Ljubljana London Luxembourg "
                   + "Madrid Malta Mariehamn Minsk Monaco Moscow Oslo Paris Podgorica Prague Riga Rome Samara San_Marino Sarajevo Saratov "
                   + "Simferopol Skopje Sofia Stockholm Tallinn Tirane Ulyanovsk Vaduz Vatican Vienna Vilnius Volgograd Warsaw Zagreb Zurich"),
        ("Indian", "Antananarivo Chagos Christmas Cocos Comoro Kerguelen Mahe Maldives Mauritius Mayotte Reunion"),
        ("Pacific", "Apia Auckland Bougainville Chatham Chuuk Easter Efate Fakaofo Fiji Funafuti Galapagos Gambier Guadalcanal Guam Honolulu "
                    + "Kanton Kiritimati Kosrae Kwajalein Majuro Marquesas Midway Nauru Niue Norfolk Noumea Pago_Pago Palau Pitcairn Pohnpei "
                    + "Port_Moresby Rarotonga Saipan Tahiti Tarawa Tongatapu Wake Wallis"),
    ];

    /// <summary>Every time zone identifier by the folded name of the city in it, built once. "America/Sao_Paulo" answers to "sao paulo".</summary>
    private static readonly Dictionary<string, string> ZonesByCity = BuildZonesByCity();

    private static Dictionary<string, string> BuildZonesByCity()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (region, cities) in ZoneList)
        {
            foreach (var path in cities.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var city = path[(path.LastIndexOf('/') + 1)..];
                var name = AnswerText.Normalized(city.Replace('_', ' '));
                map.TryAdd(name, region + "/" + path);
            }
        }
        foreach (var (alias, city) in PlaceAliases)
        {
            if (map.TryGetValue(AnswerText.Normalized(city.Replace('_', ' ')), out var id)) map[alias] = id;
        }
        return map;
    }

    // MARK: Answering

    /// <summary>
    /// Answers a date or place question, or null. <paramref name="now"/> is the clock, <paramref name="zone"/> the zone "today" is
    /// counted in, <paramref name="culture"/> the language the answer is written in.
    /// </summary>
    public static Result? Evaluate(string input, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var trimmed = input.Trim(' ', '\t');
        if (new StringInfo(trimmed).LengthInTextElements > 80) return null;
        var tokens = AnswerText.Normalized(trimmed).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;

        return RelativeDate(tokens, now, zone, culture)
               ?? DaysUntil(tokens, now, zone, culture)
               ?? TimeSomewhereElse(tokens, now, culture);
    }

    /// <summary>"in 3 weeks", "daqui 3 semanas", "3 days ago", "hoje + 10 dias".</summary>
    private static Result? RelativeDate(string[] tokens, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        // The number and its unit have to sit together; everything else in the phrase only decides which way to count.
        long? amount = null;
        Component? component = null;
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!long.TryParse(tokens[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                || value is < -10_000 or > 10_000 || !Units.TryGetValue(tokens[index + 1], out var unit)) continue;
            amount = value;
            component = unit;
            break;
        }
        if (amount is not { } count || component is not { } part) return null;

        var hasToday = tokens.Any(TodayWords.Contains);
        var hasPast = tokens.Any(PastWords.Contains);
        var hasFuture = tokens.Any(FutureWords.Contains);
        var hasMinus = tokens.Contains("-");
        // Without a direction "3 days" is not a question, and answering it would push aside whatever the person was really looking for.
        if (!(hasToday || hasPast || hasFuture)) return null;

        var backwards = hasPast || hasMinus;
        var steps = (int)(backwards ? -count : count);
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        DateTime date;
        try
        {
            date = part switch
            {
                Component.Day => local.AddDays(steps),
                Component.Week => local.AddDays(steps * 7),
                Component.Month => local.AddMonths(steps),
                _ => local.AddYears(steps),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        return new Result(LongDate(date, culture), Weekday(date, culture));
    }

    /// <summary>"days until 25/12", "dias ate 25/12": how far away a written date is.</summary>
    private static Result? DaysUntil(string[] tokens, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var untilIndex = Array.FindIndex(tokens, UntilWords.Contains);
        if (untilIndex < 0 || untilIndex + 1 >= tokens.Length) return null;
        var written = string.Join(' ', tokens[(untilIndex + 1)..]);
        var startOfToday = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (ParseDate(written, startOfToday, culture) is not { } target) return null;
        var days = (int)(target.Date - startOfToday).TotalDays;
        return new Result(CountedDays(Math.Abs(days), culture), LongDate(target, culture));
    }

    /// <summary>"time in tokyo", "hora em londres", "tokyo time".</summary>
    private static Result? TimeSomewhereElse(string[] tokens, DateTimeOffset now, CultureInfo culture)
    {
        if (!tokens.Any(TimeWords.Contains)) return null;
        // Whatever is left after the time word and the little joining words is the name of the place, which may well be two words long.
        var rest = tokens.Where(t => !TimeWords.Contains(t) && !FutureWords.Contains(t) && t != "a" && t != "at" && t != "de").ToArray();
        if (rest.Length == 0) return null;
        var name = string.Join(' ', rest);
        if (!ZonesByCity.TryGetValue(name, out var identifier) && !ZonesByCity.TryGetValue(rest[^1], out identifier)) return null;
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(identifier);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }

        var there = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var info = culture.DateTimeFormat;
        var time = there.ToString(info.ShortTimePattern, culture);
        var day = there.ToString(info.LongDatePattern, culture);
        var place = identifier[(identifier.LastIndexOf('/') + 1)..].Replace('_', ' ');
        return new Result(time, place + " · " + day);
    }

    // MARK: Writing and reading dates

    /// <summary>The long date style: the culture's full date without the weekday ("August 18, 2026").</summary>
    private static string LongDate(DateTime date, CultureInfo culture) =>
        date.ToString(WithoutWeekday(culture.DateTimeFormat.LongDatePattern), culture);

    private static string Weekday(DateTime date, CultureInfo culture) => culture.DateTimeFormat.GetDayName(date.DayOfWeek);

    /// <summary>The pattern with its weekday (and the punctuation that tied it to the rest) taken out.</summary>
    internal static string WithoutWeekday(string pattern)
    {
        var start = -1;
        var end = -1;
        char? quote = null;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (quote is { } open)
            {
                if (c == open) quote = null;
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '\\') { i++; continue; }
            if (c != 'd') continue;
            var run = i;
            while (run < pattern.Length && pattern[run] == 'd') run++;
            if (run - i >= 3) { start = i; end = run; break; }
            i = run - 1;
        }
        if (start < 0) return pattern;
        var before = pattern[..start];
        var after = pattern[end..];
        if (before.Trim(' ', ',', '.', ' ', '،').Length == 0)
            after = after.TrimStart(' ', ',', '.', ' ', '،');
        else if (after.Trim(' ', ',', '.', ' ', '،').Length == 0)
            before = before.TrimEnd(' ', ',', '.', ' ', '،');
        else
            after = after.TrimStart(' ', ',', '.', ' ', '،');
        var result = (before + after).Trim();
        return result.Length == 0 ? pattern : result;
    }

    /// <summary>
    /// A date the person wrote, read the way their culture writes dates. A year left out means the next time that day comes around,
    /// which is what someone counting the days to a birthday means.
    /// </summary>
    private static DateTime? ParseDate(string written, DateTime startOfToday, CultureInfo culture)
    {
        var info = culture.DateTimeFormat;
        var shortPattern = info.ShortDatePattern;
        // The user types "2/9/2026" for "dd/MM/yyyy" as readily as "02/09/2026", and a two-digit year as readily as four.
        var patterns = new List<string>();
        foreach (var year in new[] { "yyyy", "yy" })
        {
            var pattern = YearRun().Replace(shortPattern, year);
            pattern = DoubleDay().Replace(pattern, "d");
            pattern = DoubleMonth().Replace(pattern, "M");
            patterns.Add(pattern);
        }
        if (DateTime.TryParseExact(written, patterns.ToArray(), culture, DateTimeStyles.None, out var parsed)) return parsed.Date;

        // Without a year the formatter refuses, so the missing year is filled in: the first year, from this one, where that day exists
        // and has not gone by. A 29 February waits for the next leap year instead of sliding into a neighbouring day.
        var parts = new List<int>();
        foreach (var run in NonDigits().Split(written))
            if (run.Length > 0 && run.All(char.IsAsciiDigit) && int.TryParse(run, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                parts.Add(number);
        if (parts.Count != 2) return null;
        var dayFirst = DayComesFirst(shortPattern);
        var day = dayFirst ? parts[0] : parts[1];
        var month = dayFirst ? parts[1] : parts[0];
        if (day is < 1 or > 31 || month is < 1 or > 12) return null;
        for (var candidateYear = startOfToday.Year; candidateYear <= startOfToday.Year + 8; candidateYear++)
        {
            if (candidateYear > 9999 || day > DateTime.DaysInMonth(candidateYear, month)) continue;
            var candidate = new DateTime(candidateYear, month, day);
            if (candidate >= startOfToday) return candidate;
        }
        return null;
    }

    /// <summary>Whether the culture writes the day before the month.</summary>
    private static bool DayComesFirst(string shortPattern)
    {
        var day = shortPattern.IndexOf('d');
        var month = shortPattern.IndexOf('M');
        return day >= 0 && month >= 0 && day < month;
    }

    /// <summary>"581 days": a whole number of days written out in full, in the culture's language (DateComponentsFormatter's full style).</summary>
    private static string CountedDays(int days, CultureInfo culture)
    {
        var number = days.ToString("N0", culture);
        return culture.TwoLetterISOLanguageName switch
        {
            "pt" => $"{number} {(days == 1 ? "dia" : "dias")}",
            "es" => $"{number} {(days == 1 ? "día" : "días")}",
            "fr" => $"{number} {(days <= 1 ? "jour" : "jours")}",
            "de" => $"{number} {(days == 1 ? "Tag" : "Tage")}",
            "it" => $"{number} {(days == 1 ? "giorno" : "giorni")}",
            "tr" => $"{number} gün",
            "ru" => $"{number} {RussianDays(days)}",
            "ja" => $"{number}日",
            "ko" => $"{number}일",
            "zh" => $"{number}天",
            _ => $"{number} {(days == 1 ? "day" : "days")}",
        };
    }

    private static string RussianDays(int days)
    {
        var lastTwo = days % 100;
        var last = days % 10;
        if (lastTwo is >= 11 and <= 14) return "дней";
        return last switch { 1 => "день", >= 2 and <= 4 => "дня", _ => "дней" };
    }

    [GeneratedRegex("y+")]
    private static partial Regex YearRun();

    [GeneratedRegex("(?<!d)dd(?!d)")]
    private static partial Regex DoubleDay();

    [GeneratedRegex("(?<!M)MM(?!M)")]
    private static partial Regex DoubleMonth();

    [GeneratedRegex("[^0-9]+")]
    private static partial Regex NonDigits();
}

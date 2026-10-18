//! Conversions typed into the search field, answered offline as a row that Enter copies:
//! units ("10 km in mi", "72f to c"), dates ("days until 25 dec", "3 weeks from today") and time zones
//! ("time in tokyo", "3pm london in new york"). Currency needs the web and lives with the other web
//! answers in the shell (`WebAnswers.cs`).
//!
//! Each reading starts with a cheap look at the text, since this runs on every keystroke, and gives up
//! quietly when the words are not exactly what it knows: a half-typed or unrelated query gets no row.

use crate::model::{Action, Hit, Kind};
use windows::Win32::Foundation::SYSTEMTIME;
use windows::Win32::System::SystemInformation::{GetLocalTime, GetSystemTime};
use windows::Win32::System::Time::{
    GetTimeZoneInformationForYear, SystemTimeToTzSpecificLocalTime, TzSpecificLocalTimeToSystemTime,
    DYNAMIC_TIME_ZONE_INFORMATION, TIME_ZONE_INFORMATION,
};

/// The row for a conversion `query` asks for, if it asks for one.
pub fn hit(query: &str) -> Option<Hit> {
    let q = query.trim().to_lowercase();
    if q.len() < 4 || q.len() > 80 {
        return None;
    }
    let (title, detail) = units(&q).or_else(|| dates(&q, today())).or_else(|| zones(&q))?;
    Some(Hit {
        subtitle: format!("{detail}  \u{b7}  Enter to copy"),
        action: Action::Copy(title.clone()),
        title,
        kind: Kind::Calc,
        score: 10_000,
    })
}

/// Six significant digits, no trailing zeros.
fn fmt(v: f64) -> String {
    if v == 0.0 {
        return "0".into();
    }
    let a = v.abs();
    if !(1e-6..1e15).contains(&a) {
        return format!("{v:e}");
    }
    let mag = a.log10().floor() as i32;
    let dec = (5 - mag).clamp(0, 12) as usize;
    let s = format!("{v:.dec$}");
    if s.contains('.') {
        s.trim_end_matches('0').trim_end_matches('.').to_string()
    } else {
        s
    }
}

/// "a to b" split at the last separator word ("5 in in cm" is 5 inches in centimetres).
fn split_last<'a>(s: &'a str, seps: &[&str]) -> Option<(&'a str, &'a str)> {
    seps.iter()
        .filter_map(|sep| s.rfind(sep).map(|i| (i, sep.len())))
        .max_by_key(|(i, _)| *i)
        .map(|(i, l)| (&s[..i], &s[i + l..]))
}

// ---------------------------------------------------------------------------------------------- units

#[derive(Clone, Copy, PartialEq, Debug)]
enum Cat {
    Length,
    Mass,
    Volume,
    Time,
    Speed,
    Area,
    Data,
    Temp,
}

struct Unit {
    cat: Cat,
    sym: &'static str,
    /// How many of the category's base unit (m, kg, l, s, m/s, m2, byte) one of these is.
    factor: f64,
    /// Lower case, without spaces or dots; a plural "s" is tried when the name itself is unknown.
    names: &'static [&'static str],
}

macro_rules! u {
    ($cat:ident, $sym:expr, $f:expr, [$($n:expr),*]) => {
        Unit { cat: Cat::$cat, sym: $sym, factor: $f, names: &[$($n),*] }
    };
}

const UNITS: &[Unit] = &[
    u!(Length, "mm", 0.001, ["mm", "millimeter", "millimetre"]),
    u!(Length, "cm", 0.01, ["cm", "centimeter", "centimetre"]),
    u!(Length, "m", 1.0, ["m", "meter", "metre"]),
    u!(Length, "km", 1000.0, ["km", "kilometer", "kilometre"]),
    u!(Length, "in", 0.0254, ["in", "inch", "inches", "\""]),
    u!(Length, "ft", 0.3048, ["ft", "foot", "feet", "'"]),
    u!(Length, "yd", 0.9144, ["yd", "yard"]),
    u!(Length, "mi", 1609.344, ["mi", "mile"]),
    u!(Length, "nmi", 1852.0, ["nmi", "nauticalmile"]),
    u!(Mass, "mg", 1e-6, ["mg", "milligram"]),
    u!(Mass, "g", 0.001, ["g", "gram", "gramme"]),
    u!(Mass, "kg", 1.0, ["kg", "kilogram", "kilogramme", "kilo"]),
    u!(Mass, "t", 1000.0, ["t", "tonne", "metricton"]),
    u!(Mass, "oz", 0.028349523125, ["oz", "ounce"]),
    u!(Mass, "lb", 0.45359237, ["lb", "lbs", "pound"]),
    u!(Mass, "st", 6.35029318, ["st", "stone"]),
    u!(Volume, "ml", 0.001, ["ml", "milliliter", "millilitre"]),
    u!(Volume, "cl", 0.01, ["cl", "centiliter", "centilitre"]),
    u!(Volume, "l", 1.0, ["l", "liter", "litre"]),
    u!(Volume, "tsp", 0.00492892159375, ["tsp", "teaspoon"]),
    u!(Volume, "tbsp", 0.01478676478125, ["tbsp", "tablespoon"]),
    u!(Volume, "fl oz", 0.0295735295625, ["floz", "fluidounce"]),
    u!(Volume, "cup", 0.2365882365, ["cup"]),
    u!(Volume, "pt", 0.473176473, ["pt", "pint"]),
    u!(Volume, "qt", 0.946352946, ["qt", "quart"]),
    u!(Volume, "gal", 3.785411784, ["gal", "gallon"]),
    u!(Time, "ms", 0.001, ["ms", "millisecond"]),
    u!(Time, "s", 1.0, ["s", "sec", "second"]),
    u!(Time, "min", 60.0, ["min", "minute"]),
    u!(Time, "h", 3600.0, ["h", "hr", "hour"]),
    u!(Time, "d", 86_400.0, ["d", "day"]),
    u!(Time, "wk", 604_800.0, ["wk", "week"]),
    u!(Time, "yr", 31_557_600.0, ["yr", "year"]),
    u!(Speed, "m/s", 1.0, ["m/s", "mps"]),
    u!(Speed, "km/h", 1.0 / 3.6, ["km/h", "kmh", "kph", "kmph"]),
    u!(Speed, "mph", 0.44704, ["mph", "mi/h"]),
    u!(Speed, "kn", 0.514444444, ["kn", "kt", "knot"]),
    u!(Speed, "ft/s", 0.3048, ["ft/s", "fps"]),
    u!(Area, "m\u{b2}", 1.0, ["m2", "sqm", "squaremeter", "squaremetre"]),
    u!(Area, "km\u{b2}", 1e6, ["km2", "sqkm", "squarekilometer", "squarekilometre"]),
    u!(Area, "cm\u{b2}", 1e-4, ["cm2", "sqcm"]),
    u!(Area, "ha", 1e4, ["ha", "hectare"]),
    u!(Area, "ft\u{b2}", 0.09290304, ["ft2", "sqft", "squarefoot", "squarefeet"]),
    u!(Area, "in\u{b2}", 0.00064516, ["in2", "sqin"]),
    u!(Area, "mi\u{b2}", 2_589_988.110336, ["mi2", "sqmi"]),
    u!(Area, "acre", 4046.8564224, ["acre"]),
    u!(Data, "bit", 0.125, ["bit"]),
    u!(Data, "byte", 1.0, ["byte"]),
    u!(Data, "KB", 1e3, ["kb", "kilobyte"]),
    u!(Data, "MB", 1e6, ["mb", "megabyte"]),
    u!(Data, "GB", 1e9, ["gb", "gigabyte"]),
    u!(Data, "TB", 1e12, ["tb", "terabyte"]),
    u!(Data, "KiB", 1024.0, ["kib"]),
    u!(Data, "MiB", 1_048_576.0, ["mib"]),
    u!(Data, "GiB", 1_073_741_824.0, ["gib"]),
    u!(Data, "TiB", 1_099_511_627_776.0, ["tib"]),
    u!(Temp, "\u{b0}C", 1.0, ["c", "celsius", "degc", "centigrade"]),
    u!(Temp, "\u{b0}F", 1.0, ["f", "fahrenheit", "degf"]),
    u!(Temp, "K", 1.0, ["k", "kelvin"]),
];

fn unit(text: &str) -> Option<&'static Unit> {
    let mut n: String = text
        .chars()
        .filter(|c| !matches!(c, ' ' | '\u{b0}' | '.'))
        .map(|c| if c == '\u{b2}' { '2' } else { c })
        .collect();
    n = n.replace("^2", "2");
    let find = |n: &str| UNITS.iter().find(|u| u.names.contains(&n));
    find(&n).or_else(|| n.strip_suffix('s').filter(|s| s.len() >= 2).and_then(find))
}

/// "1,5" is a decimal comma, "1,000" and "1,234.5" are thousands.
fn parse_num(s: &str) -> Option<f64> {
    let s = if s.contains('.') {
        s.replace(',', "")
    } else {
        match s.split_once(',') {
            Some((_, after)) if after.len() != 3 && !after.contains(',') => s.replace(',', "."),
            _ => s.replace(',', ""),
        }
    };
    s.parse::<f64>().ok().filter(|v| v.is_finite())
}

/// "72f" as (72, "f"); "10 km" as (10, "km").
fn split_number(s: &str) -> Option<(f64, &str)> {
    let end = s.find(|c: char| !(c.is_ascii_digit() || matches!(c, '.' | ',' | '-' | '+'))).unwrap_or(s.len());
    let (num, rest) = s.split_at(end);
    let rest = rest.trim();
    if rest.is_empty() || !num.bytes().any(|b| b.is_ascii_digit()) {
        return None;
    }
    Some((parse_num(num)?, rest))
}

fn to_celsius(v: f64, sym: &str) -> f64 {
    match sym {
        "\u{b0}F" => (v - 32.0) * 5.0 / 9.0,
        "K" => v - 273.15,
        _ => v,
    }
}

fn from_celsius(v: f64, sym: &str) -> f64 {
    match sym {
        "\u{b0}F" => v * 9.0 / 5.0 + 32.0,
        "K" => v + 273.15,
        _ => v,
    }
}

fn units(q: &str) -> Option<(String, String)> {
    if !q.bytes().any(|b| b.is_ascii_digit()) {
        return None;
    }
    let (left, right) = split_last(q, &[" to ", " in ", " into ", " as "])?;
    let (n, from) = split_number(left.trim())?;
    let (from, to) = (unit(from)?, unit(right.trim())?);
    if from.cat != to.cat {
        return None;
    }
    let v = if from.cat == Cat::Temp {
        from_celsius(to_celsius(n, from.sym), to.sym)
    } else {
        n * from.factor / to.factor
    };
    if !v.is_finite() {
        return None;
    }
    let out = format!("{} {}", fmt(v), to.sym);
    Some((out.clone(), format!("{} {} = {out}", fmt(n), from.sym)))
}

// ---------------------------------------------------------------------------------------------- dates

type Ymd = (i32, u32, u32);

const MONTHS: [&str; 12] = [
    "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November",
    "December",
];
const WEEKDAYS: [&str; 7] = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

fn today() -> Ymd {
    let t = unsafe { GetLocalTime() };
    (t.wYear as i32, t.wMonth as u32, t.wDay as u32)
}

// Days since 1970-01-01 and back (Howard Hinnant's civil-date algorithms).
fn days_from_civil((y, m, d): Ymd) -> i64 {
    let y = (if m <= 2 { y - 1 } else { y }) as i64;
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = (m as i64 + 9) % 12;
    let doy = (153 * mp + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

fn civil_from_days(z: i64) -> Ymd {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    (y as i32, m as u32, d as u32)
}

fn month_len(y: i32, m: u32) -> u32 {
    match m {
        2 if (y % 4 == 0 && y % 100 != 0) || y % 400 == 0 => 29,
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

fn valid((y, m, d): Ymd) -> bool {
    (1..=12).contains(&m) && d >= 1 && d <= month_len(y, m) && (1..=9999).contains(&y)
}

fn add_days(d: Ymd, n: i64) -> Ymd {
    civil_from_days(days_from_civil(d) + n)
}

/// Calendar months later, the day clamped to the month's length (31 Jan + 1 month is 28 or 29 Feb).
fn add_months((y, m, d): Ymd, n: i64) -> Ymd {
    let total = y as i64 * 12 + (m as i64 - 1) + n;
    let (y2, m2) = (total.div_euclid(12) as i32, total.rem_euclid(12) as u32 + 1);
    (y2, m2, d.min(month_len(y2, m2)))
}

fn weekday(d: Ymd) -> usize {
    (days_from_civil(d) + 3).rem_euclid(7) as usize
}

fn long_date(d: Ymd) -> String {
    format!("{} {} {} {}", WEEKDAYS[weekday(d)], d.2, MONTHS[d.1 as usize - 1], d.0)
}

fn month_of(word: &str) -> Option<u32> {
    if word.len() < 3 {
        return None;
    }
    MONTHS
        .iter()
        .position(|m| {
            let m = m.to_lowercase();
            m == word || (word.len() == 3 && m.starts_with(word)) || (word == "sept" && m == "september")
        })
        .map(|i| i as u32 + 1)
}

/// The next (or, for `future` false, the last) time the day comes round, today included.
fn yearly(today: Ymd, m: u32, d: u32, future: bool) -> Option<Ymd> {
    let mut y = today.0;
    let here = (y, m, d);
    if future && here < today {
        y += 1;
    } else if !future && here > today {
        y -= 1;
    }
    Some((y, m, d)).filter(|c| valid(*c))
}

fn parse_date(s: &str, today: Ymd, future: bool) -> Option<Ymd> {
    let s = s.trim();
    match s {
        "today" | "now" => return Some(today),
        "tomorrow" => return Some(add_days(today, 1)),
        "yesterday" => return Some(add_days(today, -1)),
        "christmas" | "xmas" | "christmas day" => return yearly(today, 12, 25, future),
        "new year" | "new years" | "new year's" | "new years day" | "new year's day" => {
            return yearly(today, 1, 1, future)
        }
        _ => {}
    }
    // 2026-12-25
    let iso: Vec<&str> = s.split('-').collect();
    if iso.len() == 3 && iso[0].len() == 4 {
        let n = |i: usize| iso[i].parse::<u32>().ok();
        let d = (iso[0].parse::<i32>().ok()?, n(1)?, n(2)?);
        return Some(d).filter(|d| valid(*d));
    }
    // 25 dec, 25 december 2026, dec 25th, december 25, 2026
    let (mut month, mut day, mut year) = (None, None, None);
    for tok in s.split(|c: char| c.is_whitespace() || c == ',').filter(|t| !t.is_empty() && *t != "of") {
        let num = tok.trim_end_matches(|c: char| c.is_alphabetic());
        if let Some(m) = month_of(tok) {
            if month.replace(m).is_some() {
                return None;
            }
        } else if !num.is_empty() && num.bytes().all(|b| b.is_ascii_digit()) && matches!(&tok[num.len()..], "" | "st" | "nd" | "rd" | "th") {
            let v: u32 = num.parse().ok()?;
            if num.len() == 4 {
                if year.replace(v as i32).is_some() {
                    return None;
                }
            } else if day.replace(v).is_some() {
                return None;
            }
        } else {
            return None;
        }
    }
    let (month, day) = (month?, day?);
    match year {
        Some(y) => Some((y, month, day)).filter(|d| valid(*d)),
        None => yearly(today, month, day, future),
    }
}

fn plural(n: i64, word: &str) -> String {
    if n.abs() == 1 {
        format!("{n} {word}")
    } else {
        format!("{n} {word}s")
    }
}

fn capitalised(s: &str) -> String {
    let mut c = s.chars();
    c.next().map(|f| f.to_uppercase().chain(c).collect()).unwrap_or_default()
}

fn dates(q: &str, today: Ymd) -> Option<(String, String)> {
    let q = q.strip_prefix("how many ").unwrap_or(q);
    let w: Vec<&str> = q.split_whitespace().collect();
    if w.len() < 3 {
        return None;
    }

    // "days until 25 dec", "weeks since 1 jan", "days to christmas"
    if matches!(w[0], "days" | "weeks") && matches!(w[1], "until" | "till" | "to" | "since") {
        let since = w[1] == "since";
        let target = parse_date(&w[2..].join(" "), today, !since)?;
        let diff = days_from_civil(target) - days_from_civil(today);
        let n = diff;
        let count = if w[0] == "days" {
            plural(n.abs(), "day")
        } else {
            let (wk, rest) = (n.abs() / 7, n.abs() % 7);
            if rest == 0 { plural(wk, "week") } else { format!("{}, {}", plural(wk, "week"), plural(rest, "day")) }
        };
        let title = match n {
            0 => "Today".to_string(),
            n if n < 0 => format!("{count} ago"),
            _ if since => format!("In {count}"),
            _ => count,
        };
        let verb = if since { "Since" } else { "Until" };
        return Some((title, format!("{verb} {}", long_date(target))));
    }

    // "in 3 weeks", "3 weeks ago", "3 weeks from today", "2 days before 25 dec"
    let (sign, n, word, base) = if w[0] == "in" && w.len() == 3 {
        (1, w[1], w[2], today)
    } else if w.len() == 3 && w[2] == "ago" {
        (-1, w[0], w[1], today)
    } else if w.len() >= 4 && matches!(w[2], "from" | "after" | "before") {
        let base = parse_date(&w[3..].join(" "), today, true)?;
        (if w[2] == "before" { -1 } else { 1 }, w[0], w[1], base)
    } else {
        return None;
    };
    let n: i64 = match n {
        "a" | "an" | "one" => 1,
        _ => n.parse().ok().filter(|v| (0..=100_000).contains(v))?,
    };
    let when = match word.strip_suffix('s').unwrap_or(word) {
        "day" => add_days(base, sign * n),
        "week" => add_days(base, sign * n * 7),
        "month" if n <= 12_000 => add_months(base, sign * n),
        "year" if n <= 1000 => add_months(base, sign * n * 12),
        _ => return None,
    };
    Some((long_date(when), capitalised(q)))
}

// ---------------------------------------------------------------------------------------------- time zones

/// City (or name) to Windows time zone id. Windows keeps the daylight-saving rules; this only names them.
const ZONES: &[(&str, &str)] = &[
    ("utc", "UTC"),
    ("gmt", "UTC"),
    ("london", "GMT Standard Time"),
    ("dublin", "GMT Standard Time"),
    ("lisbon", "GMT Standard Time"),
    ("reykjavik", "Greenwich Standard Time"),
    ("paris", "Romance Standard Time"),
    ("madrid", "Romance Standard Time"),
    ("brussels", "Romance Standard Time"),
    ("copenhagen", "Romance Standard Time"),
    ("berlin", "W. Europe Standard Time"),
    ("rome", "W. Europe Standard Time"),
    ("amsterdam", "W. Europe Standard Time"),
    ("vienna", "W. Europe Standard Time"),
    ("zurich", "W. Europe Standard Time"),
    ("stockholm", "W. Europe Standard Time"),
    ("oslo", "W. Europe Standard Time"),
    ("prague", "Central Europe Standard Time"),
    ("budapest", "Central Europe Standard Time"),
    ("warsaw", "Central European Standard Time"),
    ("athens", "GTB Standard Time"),
    ("helsinki", "FLE Standard Time"),
    ("kyiv", "FLE Standard Time"),
    ("kiev", "FLE Standard Time"),
    ("istanbul", "Turkey Standard Time"),
    ("moscow", "Russian Standard Time"),
    ("cairo", "Egypt Standard Time"),
    ("johannesburg", "South Africa Standard Time"),
    ("lagos", "W. Central Africa Standard Time"),
    ("nairobi", "E. Africa Standard Time"),
    ("dubai", "Arabian Standard Time"),
    ("tehran", "Iran Standard Time"),
    ("karachi", "Pakistan Standard Time"),
    ("delhi", "India Standard Time"),
    ("mumbai", "India Standard Time"),
    ("kolkata", "India Standard Time"),
    ("bangkok", "SE Asia Standard Time"),
    ("jakarta", "SE Asia Standard Time"),
    ("singapore", "Singapore Standard Time"),
    ("manila", "Singapore Standard Time"),
    ("hong kong", "China Standard Time"),
    ("beijing", "China Standard Time"),
    ("shanghai", "China Standard Time"),
    ("taipei", "Taipei Standard Time"),
    ("seoul", "Korea Standard Time"),
    ("tokyo", "Tokyo Standard Time"),
    ("sydney", "AUS Eastern Standard Time"),
    ("melbourne", "AUS Eastern Standard Time"),
    ("perth", "W. Australia Standard Time"),
    ("auckland", "New Zealand Standard Time"),
    ("honolulu", "Hawaiian Standard Time"),
    ("anchorage", "Alaskan Standard Time"),
    ("los angeles", "Pacific Standard Time"),
    ("san francisco", "Pacific Standard Time"),
    ("seattle", "Pacific Standard Time"),
    ("vancouver", "Pacific Standard Time"),
    ("phoenix", "US Mountain Standard Time"),
    ("denver", "Mountain Standard Time"),
    ("chicago", "Central Standard Time"),
    ("dallas", "Central Standard Time"),
    ("houston", "Central Standard Time"),
    ("mexico city", "Central Standard Time (Mexico)"),
    ("new york", "Eastern Standard Time"),
    ("nyc", "Eastern Standard Time"),
    ("boston", "Eastern Standard Time"),
    ("miami", "Eastern Standard Time"),
    ("atlanta", "Eastern Standard Time"),
    ("toronto", "Eastern Standard Time"),
    ("sao paulo", "E. South America Standard Time"),
    ("buenos aires", "Argentina Standard Time"),
];

fn zone(name: &str) -> Option<(&'static str, String)> {
    let name = name.split_whitespace().collect::<Vec<_>>().join(" ");
    let id = ZONES.iter().find(|(n, _)| *n == name)?.1;
    let shown = if id == "UTC" { name.to_uppercase() } else { name.split(' ').map(capitalised).collect::<Vec<_>>().join(" ") };
    Some((id, shown))
}

fn tz_info(id: &str, year: u16) -> Option<TIME_ZONE_INFORMATION> {
    let mut dynamic: DYNAMIC_TIME_ZONE_INFORMATION = unsafe { std::mem::zeroed() };
    for (i, c) in id.encode_utf16().enumerate().take(127) {
        dynamic.TimeZoneKeyName[i] = c;
    }
    let mut info: TIME_ZONE_INFORMATION = unsafe { std::mem::zeroed() };
    unsafe { GetTimeZoneInformationForYear(year, Some(&dynamic), &mut info) }.ok()?;
    Some(info)
}

fn minutes(t: &SYSTEMTIME) -> i64 {
    days_from_civil((t.wYear as i32, t.wMonth as u32, t.wDay as u32)) * 1440 + t.wHour as i64 * 60 + t.wMinute as i64
}

fn ymd(t: &SYSTEMTIME) -> Ymd {
    (t.wYear as i32, t.wMonth as u32, t.wDay as u32)
}

/// `utc` as the wall clock in a zone (None: this PC's).
fn wall(zone: Option<&TIME_ZONE_INFORMATION>, utc: &SYSTEMTIME) -> Option<SYSTEMTIME> {
    let mut out: SYSTEMTIME = unsafe { std::mem::zeroed() };
    unsafe { SystemTimeToTzSpecificLocalTime(zone.map(|z| z as *const _), utc, &mut out) }.ok()?;
    Some(out)
}

fn clock(minute_of_day: i64, twelve: bool) -> String {
    let (h, m) = ((minute_of_day / 60) % 24, minute_of_day % 60);
    if twelve {
        format!("{}:{m:02} {}", if h % 12 == 0 { 12 } else { h % 12 }, if h < 12 { "AM" } else { "PM" })
    } else {
        format!("{h:02}:{m:02}")
    }
}

/// "3pm", "3:30 pm", "15:00" at the start of `w`: (minute of day, 12-hour style, words used).
fn parse_clock(w: &[&str]) -> Option<(i64, bool, usize)> {
    let first = *w.first()?;
    let (body, meridiem, used) = if let Some(b) = first.strip_suffix("am") {
        (b, Some(false), 1)
    } else if let Some(b) = first.strip_suffix("pm") {
        (b, Some(true), 1)
    } else if matches!(w.get(1), Some(&"am") | Some(&"pm")) {
        (first, Some(w[1] == "pm"), 2)
    } else {
        (first, None, 1)
    };
    let (h, m) = match body.split_once(':') {
        Some((h, m)) => (h.parse::<i64>().ok()?, m.parse::<i64>().ok()?),
        None if meridiem.is_some() => (body.parse::<i64>().ok()?, 0),
        None => return None,
    };
    if !(0..60).contains(&m) {
        return None;
    }
    match meridiem {
        Some(pm) if (1..=12).contains(&h) => Some(((h % 12 + if pm { 12 } else { 0 }) * 60 + m, true, used)),
        None if (0..24).contains(&h) => Some((h * 60 + m, false, used)),
        _ => None,
    }
}

fn ahead(diff_minutes: i64) -> String {
    let (h, m) = (diff_minutes.abs() / 60, diff_minutes.abs() % 60);
    let span = match (h, m) {
        (h, 0) => format!("{h} h"),
        (0, m) => format!("{m} min"),
        (h, m) => format!("{h} h {m} min"),
    };
    match diff_minutes {
        0 => "same time as here".into(),
        d if d > 0 => format!("{span} ahead"),
        _ => format!("{span} behind"),
    }
}

fn zones(q: &str) -> Option<(String, String)> {
    const ASKS: [&str; 7] =
        ["what time is it in ", "what is the time in ", "what's the time in ", "current time in ", "time in ", "time at ", "time "];
    let city = ASKS
        .iter()
        .find_map(|p| q.strip_prefix(p))
        .or_else(|| q.strip_suffix(" time"))
        .and_then(|c| zone(c.trim().trim_end_matches('?')));
    if let Some((id, shown)) = city {
        return time_now(id, &shown);
    }
    let w: Vec<&str> = q.split_whitespace().collect();
    let (at, twelve, used) = parse_clock(&w)?;
    let rest = w[used..].join(" ");
    let (from, to) = split_last(&rest, &[" to ", " in ", " into "])?;
    let ((from_id, from_name), (to_id, to_name)) = (zone(from)?, zone(to)?);
    time_between(at, twelve, (from_id, &from_name), (to_id, &to_name))
}

fn time_now(id: &str, shown: &str) -> Option<(String, String)> {
    let utc = unsafe { GetSystemTime() };
    let info = tz_info(id, utc.wYear)?;
    let there = wall(Some(&info), &utc)?;
    let here = wall(None, &utc)?;
    let diff = minutes(&there) - minutes(&here);
    let day = if ymd(&there) != ymd(&here) { format!("{} \u{b7} ", WEEKDAYS[weekday(ymd(&there))]) } else { String::new() };
    let title = clock(there.wHour as i64 * 60 + there.wMinute as i64, false);
    Some((title, format!("{shown} \u{b7} {day}{}", ahead(diff))))
}

fn time_between(at: i64, twelve: bool, from: (&str, &str), to: (&str, &str)) -> Option<(String, String)> {
    let utc_now = unsafe { GetSystemTime() };
    let (from_info, to_info) = (tz_info(from.0, utc_now.wYear)?, tz_info(to.0, utc_now.wYear)?);
    // The day in the first city is its today.
    let mut start = wall(Some(&from_info), &utc_now)?;
    start.wHour = (at / 60) as u16;
    start.wMinute = (at % 60) as u16;
    start.wSecond = 0;
    start.wMilliseconds = 0;
    let mut utc: SYSTEMTIME = unsafe { std::mem::zeroed() };
    unsafe { TzSpecificLocalTimeToSystemTime(Some(&from_info), &start, &mut utc) }.ok()?;
    let end = wall(Some(&to_info), &utc)?;
    let title = clock(end.wHour as i64 * 60 + end.wMinute as i64, twelve);
    let shift = days_from_civil(ymd(&end)) - days_from_civil(ymd(&start));
    let day = match shift {
        0 => "same day",
        1 => "next day",
        -1 => "day before",
        _ => "",
    };
    let detail = format!("{} in {} = {title} in {}{}", clock(at, twelve), from.1, to.1, if day.is_empty() { String::new() } else { format!(" \u{b7} {day}") });
    Some((title, detail))
}

#[cfg(test)]
mod tests {
    use super::*;

    const SUNDAY: Ymd = (2026, 10, 18);

    fn answer(q: &str) -> Option<String> {
        units(q).map(|(t, _)| t)
    }

    #[test]
    fn units_convert() {
        assert_eq!(answer("10 km in mi").as_deref(), Some("6.21371 mi"));
        assert_eq!(answer("72f to c").as_deref(), Some("22.2222 \u{b0}C"));
        assert_eq!(answer("100 c to f").as_deref(), Some("212 \u{b0}F"));
        assert_eq!(answer("0 c to k").as_deref(), Some("273.15 K"));
        assert_eq!(answer("5 in to cm").as_deref(), Some("12.7 cm"));
        assert_eq!(answer("5 in in cm").as_deref(), Some("12.7 cm"));
        assert_eq!(answer("2 h to min").as_deref(), Some("120 min"));
        assert_eq!(answer("1 gb to mb").as_deref(), Some("1000 MB"));
        assert_eq!(answer("1 gal to l").as_deref(), Some("3.78541 l"));
        assert_eq!(answer("3 pounds to kg").as_deref(), Some("1.36078 kg"));
        assert_eq!(answer("1,5 km to m").as_deref(), Some("1500 m"));
        assert_eq!(answer("1,000 m to km").as_deref(), Some("1 km"));
        assert_eq!(answer("60 mph to km/h").as_deref(), Some("96.5606 km/h"));
    }

    #[test]
    fn unrelated_words_are_left_alone() {
        assert_eq!(answer("10 km to kg"), None);
        assert_eq!(answer("windows 11 to go"), None);
        assert_eq!(answer("7 to 1"), None);
        assert_eq!(answer("km to mi"), None);
        assert_eq!(answer("10 foo in bar"), None);
    }

    #[test]
    fn civil_dates_round_trip() {
        assert_eq!(days_from_civil((1970, 1, 1)), 0);
        for z in [-800_000_i64, -1, 0, 1, 19_000, 20_000, 800_000] {
            assert_eq!(days_from_civil(civil_from_days(z)), z);
        }
        assert_eq!(WEEKDAYS[weekday((2026, 1, 1))], "Thursday");
        assert_eq!(WEEKDAYS[weekday(SUNDAY)], "Sunday");
        assert_eq!(add_months((2026, 1, 31), 1), (2026, 2, 28));
        assert_eq!(add_months((2024, 1, 31), 1), (2024, 2, 29));
        assert_eq!(add_months((2026, 11, 15), 3), (2027, 2, 15));
        assert_eq!(add_months((2026, 3, 15), -5), (2025, 10, 15));
    }

    #[test]
    fn dates_are_read_in_several_forms() {
        assert_eq!(parse_date("25 dec", SUNDAY, true), Some((2026, 12, 25)));
        assert_eq!(parse_date("december 25th", SUNDAY, true), Some((2026, 12, 25)));
        assert_eq!(parse_date("25 december 2027", SUNDAY, true), Some((2027, 12, 25)));
        assert_eq!(parse_date("2026-12-25", SUNDAY, true), Some((2026, 12, 25)));
        assert_eq!(parse_date("1 jan", SUNDAY, true), Some((2027, 1, 1)));
        assert_eq!(parse_date("1 jan", SUNDAY, false), Some((2026, 1, 1)));
        assert_eq!(parse_date("christmas", SUNDAY, true), Some((2026, 12, 25)));
        assert_eq!(parse_date("30 feb", SUNDAY, true), None);
        assert_eq!(parse_date("hello", SUNDAY, true), None);
        assert_eq!(parse_date("12", SUNDAY, true), None);
    }

    #[test]
    fn date_questions_are_answered() {
        let ask = |q: &str| dates(q, SUNDAY);
        assert_eq!(ask("days until 25 dec").unwrap(), ("68 days".into(), "Until Friday 25 December 2026".into()));
        assert_eq!(ask("how many days until christmas").unwrap().0, "68 days");
        assert_eq!(ask("weeks until 25 dec").unwrap().0, "9 weeks, 5 days");
        assert_eq!(ask("days until 18 oct").unwrap().0, "Today");
        assert_eq!(ask("days since 1 oct").unwrap().0, "17 days ago");
        assert_eq!(ask("days until 1 oct 2026").unwrap().0, "17 days ago");
        assert_eq!(ask("3 weeks from today").unwrap(), ("Sunday 8 November 2026".into(), "3 weeks from today".into()));
        assert_eq!(ask("in 2 days").unwrap().0, "Tuesday 20 October 2026");
        assert!(ask("2 months ago").unwrap().0.ends_with("18 August 2026"));
        assert_eq!(ask("a week from tomorrow").unwrap().0, "Monday 26 October 2026");
        assert!(ask("10 days before 25 dec").unwrap().0.ends_with("15 December 2026"));
        assert_eq!(ask("days until never"), None);
        assert_eq!(ask("3 apples from today"), None);
    }

    #[test]
    fn clocks_are_read_and_shown() {
        assert_eq!(parse_clock(&["3pm", "london"]), Some((15 * 60, true, 1)));
        assert_eq!(parse_clock(&["3:30", "pm", "x"]), Some((15 * 60 + 30, true, 2)));
        assert_eq!(parse_clock(&["15:00"]), Some((900, false, 1)));
        assert_eq!(parse_clock(&["12am"]), Some((0, true, 1)));
        assert_eq!(parse_clock(&["3", "london"]), None);
        assert_eq!(parse_clock(&["25:00"]), None);
        assert_eq!(clock(15 * 60 + 5, true), "3:05 PM");
        assert_eq!(clock(0, true), "12:00 AM");
        assert_eq!(clock(15 * 60 + 5, false), "15:05");
    }

    #[test]
    fn zone_names_are_found() {
        assert_eq!(zone("new  york"), Some(("Eastern Standard Time", "New York".into())));
        assert_eq!(zone("utc").map(|z| z.1), Some("UTC".into()));
        assert!(zone("atlantis").is_none());
        assert_eq!(ahead(540), "9 h ahead");
        assert_eq!(ahead(-330), "5 h 30 min behind");
        assert_eq!(ahead(0), "same time as here");
    }

    #[test]
    fn tokyo_is_nine_hours_from_utc() {
        // Asks Windows for the rules, so this also checks the calls are made right.
        let tokyo = tz_info("Tokyo Standard Time", 2026).expect("zone");
        let utc = SYSTEMTIME { wYear: 2026, wMonth: 6, wDay: 1, wHour: 12, ..Default::default() };
        let local = wall(Some(&tokyo), &utc).expect("wall");
        assert_eq!((local.wDay, local.wHour), (1, 21));
        let (title, detail) = time_between(15 * 60, true, ("GMT Standard Time", "London"), ("Tokyo Standard Time", "Tokyo")).expect("convert");
        assert!(title.ends_with("AM") || title.ends_with("PM"), "{title}");
        assert!(detail.contains("3:00 PM in London"), "{detail}");
    }

    #[test]
    fn rows_need_the_whole_phrase() {
        assert!(hit("10 km in mi").is_some());
        assert!(hit("notepad").is_none());
        assert!(hit("time").is_none());
        assert!(hit("tokyo").is_none());
    }
}

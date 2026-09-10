using System.Globalization;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Translates QWeather's condition codes into the WMO interpretation codes the rest of the
/// product already speaks.
///
/// The panel resolves a localized condition name from a WMO code and the taskbar entry draws
/// a glyph from the token derived from it, so a second condition vocabulary would mean a
/// second copy of both tables in two processes. Adapting here instead keeps the card payload
/// byte-identical between sources: nothing downstream can tell which provider produced it.
///
/// The mapping is lossy in one direction on purpose - QWeather distinguishes rainfall
/// intensities WMO does not - and returns null for codes that describe no sky condition at
/// all (heat and cold advisories, unknown). A null is published as an absent weather code,
/// which the card reads as "no description" rather than inventing a nearby condition.
/// </summary>
public static class QWeatherConditionCodes
{
    public static int? ToWeatherCode(string? conditionCode)
    {
        if (conditionCode is null ||
            !int.TryParse(
                conditionCode,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int code))
        {
            return null;
        }

        return ToWeatherCode(code);
    }

    public static int? ToWeatherCode(int conditionCode) =>
        conditionCode switch
        {
            // 100 晴, 102 少云, 103 晴间多云, 101 多云, 104 阴.
            100 => 0,
            102 => 1,
            101 or 103 => 2,
            104 => 3,

            // 300/301 阵雨. Showers are their own WMO family, which is why they do not
            // collapse into the steady-rain codes below.
            300 => 80,
            301 => 82,

            // 302/303 雷阵雨, 304 雷阵雨伴有冰雹.
            302 or 303 => 95,
            304 => 96,

            // 305-312 小雨 through 特大暴雨, 314-318 the "X to Y" bands, 399 雨.
            // WMO tops out at heavy rain, so everything above 大雨 lands on the same code.
            309 => 51,
            305 or 399 => 61,
            306 or 314 => 63,
            307 or 308 or 310 or 311 or 312 or 315 or 316 or 317 or 318 => 65,

            // 313 冻雨.
            313 => 66,

            // 400-403 小雪 through 暴雪, 408-410 the bands, 499 雪.
            400 or 499 => 71,
            401 or 408 => 73,
            402 or 403 or 409 or 410 => 75,

            // 404 雨夹雪, 405 雨雪天气, 406 阵雨夹雪, 407 阵雪. Mixed precipitation has no
            // WMO interpretation code of its own; snow is the half that changes what the
            // reader has to do about it.
            404 or 405 => 71,
            406 or 407 => 85,

            // 500-515: fog, haze, and blowing dust or sand. WMO's interpretation set covers
            // only fog, and all of these are obscurations that cut visibility, so they share
            // its code - and with it the card's fog treatment, which is the honest picture
            // for every one of them.
            500 or 501 or 502 or 503 or 504 or 507 or 508 or 509 or 510 or 511 or 512
                or 513 or 514 or 515 => 45,

            // 900 热, 901 冷, 999 未知, and anything added after this build.
            _ => null,
        };
}

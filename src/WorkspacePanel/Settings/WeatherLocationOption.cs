using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// One pickable place in the weather settings dialog. The search returns name, region and
/// country as separate fields so same-named places can be told apart; this composes the two
/// strings the list actually shows and the label that gets saved.
/// </summary>
public sealed record WeatherLocationOption
{
    private WeatherLocationOption(
        string label,
        string description,
        double latitude,
        double longitude,
        string timezone)
    {
        Label = label;
        Description = description;
        Latitude = latitude;
        Longitude = longitude;
        Timezone = timezone;
    }

    /// <summary>The name saved as the weather location label.</summary>
    public string Label { get; }

    /// <summary>Region and country, shown under the name to disambiguate.</summary>
    public string Description { get; }

    /// <summary>
    /// False for a place whose name needs no second line. The row then shows one line rather
    /// than reserving an empty one, so a list of mixed entries keeps an even rhythm.
    /// </summary>
    public bool HasDescription => Description.Length > 0;

    public double Latitude { get; }

    public double Longitude { get; }

    public string Timezone { get; }

    /// <summary>Coordinates in a fixed format, so the dialog can show what will be saved.</summary>
    public string CoordinatesText => string.Concat(
        Latitude.ToString("0.####", CultureInfo.InvariantCulture),
        ", ",
        Longitude.ToString("0.####", CultureInfo.InvariantCulture));

    public static WeatherLocationOption FromCandidate(WeatherLocationCandidateDto candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        string label = candidate.Name;
        if (label.Length > WeatherSettingsContract.MaxLabelLength)
        {
            label = label[..WeatherSettingsContract.MaxLabelLength];
        }

        string[] parts = new[] { candidate.Region, candidate.Country }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WeatherLocationOption(
            label,
            string.Join(" · ", parts),
            candidate.Latitude,
            candidate.Longitude,
            candidate.Timezone);
    }
}

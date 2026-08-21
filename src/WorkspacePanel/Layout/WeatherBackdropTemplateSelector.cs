using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Layout;

/// <summary>
/// Chooses the weather card's illustration from the condition token. Only the selected
/// backdrop is ever inflated, so an unused condition costs nothing; that matters because the
/// grid realises and recycles card surfaces while the user drags.
/// </summary>
public sealed class WeatherBackdropTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ClearDayTemplate { get; set; }

    public DataTemplate? ClearNightTemplate { get; set; }

    public DataTemplate? PartlyCloudyDayTemplate { get; set; }

    public DataTemplate? PartlyCloudyNightTemplate { get; set; }

    public DataTemplate? CloudyTemplate { get; set; }

    public DataTemplate? FogTemplate { get; set; }

    public DataTemplate? RainTemplate { get; set; }

    public DataTemplate? SnowTemplate { get; set; }

    public DataTemplate? ThunderstormTemplate { get; set; }

    /// <summary>
    /// Used for <see cref="WeatherConditionContract.Unknown"/> and for anything this build
    /// does not recognise. It draws nothing: a confident illustration of weather nobody
    /// reported is worse than a plain card.
    /// </summary>
    public DataTemplate? EmptyTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is not string conditionIconId)
        {
            return EmptyTemplate;
        }

        if (conditionIconId == WeatherConditionContract.ClearDay)
        {
            return ClearDayTemplate;
        }

        if (conditionIconId == WeatherConditionContract.ClearNight)
        {
            return ClearNightTemplate;
        }

        if (conditionIconId == WeatherConditionContract.PartlyCloudyDay)
        {
            return PartlyCloudyDayTemplate;
        }

        if (conditionIconId == WeatherConditionContract.PartlyCloudyNight)
        {
            return PartlyCloudyNightTemplate;
        }

        if (conditionIconId == WeatherConditionContract.Cloudy)
        {
            return CloudyTemplate;
        }

        if (conditionIconId == WeatherConditionContract.Fog)
        {
            return FogTemplate;
        }

        // Drizzle and rain differ only in how much falls. One illustration reads for both,
        // and a card-sized drawing could not honestly show the difference anyway.
        if (conditionIconId == WeatherConditionContract.Drizzle ||
            conditionIconId == WeatherConditionContract.Rain)
        {
            return RainTemplate;
        }

        if (conditionIconId == WeatherConditionContract.Snow)
        {
            return SnowTemplate;
        }

        if (conditionIconId == WeatherConditionContract.Thunderstorm)
        {
            return ThunderstormTemplate;
        }

        return EmptyTemplate;
    }

    protected override DataTemplate? SelectTemplateCore(
        object item,
        DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}

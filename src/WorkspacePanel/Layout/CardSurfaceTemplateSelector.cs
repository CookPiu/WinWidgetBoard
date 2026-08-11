using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardSurfaceTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NotesTemplate { get; set; }

    public DataTemplate? TimerTemplate { get; set; }

    public DataTemplate? TodoTemplate { get; set; }

    public DataTemplate? CalendarTemplate { get; set; }

    public DataTemplate? DefaultTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is not CardSurfaceItem card)
        {
            return DefaultTemplate;
        }

        return card.CardTypeId switch
        {
            BuiltInCardCatalog.NotesCardTypeId => NotesTemplate,
            BuiltInCardCatalog.TimerCardTypeId => TimerTemplate,
            BuiltInCardCatalog.TodoCardTypeId => TodoTemplate,
            BuiltInCardCatalog.CalendarCardTypeId => CalendarTemplate,
            _ => DefaultTemplate,
        };
    }

    protected override DataTemplate? SelectTemplateCore(
        object item,
        DependencyObject container) => SelectTemplateCore(item);
}

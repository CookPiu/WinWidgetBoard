using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

        return card.InstanceId switch
        {
            "demo.notes" => NotesTemplate,
            "demo.timer" => TimerTemplate,
            "demo.todo" => TodoTemplate,
            "demo.calendar" => CalendarTemplate,
            _ => DefaultTemplate,
        };
    }

    protected override DataTemplate? SelectTemplateCore(
        object item,
        DependencyObject container) => SelectTemplateCore(item);
}

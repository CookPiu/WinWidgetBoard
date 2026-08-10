using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinWidgetBoard.WorkspacePanel.Notes;

public sealed class NoteMarkdownPreviewTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BlankTemplate { get; set; }

    public DataTemplate? ParagraphTemplate { get; set; }

    public DataTemplate? Heading1Template { get; set; }

    public DataTemplate? Heading2Template { get; set; }

    public DataTemplate? Heading3Template { get; set; }

    public DataTemplate? UnorderedListItemTemplate { get; set; }

    public DataTemplate? OrderedListItemTemplate { get; set; }

    public DataTemplate? QuoteTemplate { get; set; }

    public DataTemplate? CodeTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(
        object item,
        DependencyObject container)
    {
        if (item is not NoteMarkdownPreviewBlock block)
        {
            return ParagraphTemplate;
        }

        return block.Kind switch
        {
            NoteMarkdownPreviewBlockKind.Blank => BlankTemplate,
            NoteMarkdownPreviewBlockKind.Heading1 => Heading1Template,
            NoteMarkdownPreviewBlockKind.Heading2 => Heading2Template,
            NoteMarkdownPreviewBlockKind.Heading3 => Heading3Template,
            NoteMarkdownPreviewBlockKind.UnorderedListItem => UnorderedListItemTemplate,
            NoteMarkdownPreviewBlockKind.OrderedListItem => OrderedListItemTemplate,
            NoteMarkdownPreviewBlockKind.Quote => QuoteTemplate,
            NoteMarkdownPreviewBlockKind.Code => CodeTemplate,
            _ => ParagraphTemplate,
        };
    }
}

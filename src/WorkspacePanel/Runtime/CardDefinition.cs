using System.Collections.Frozen;
using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

public sealed class CardDefinition : ICardDefinition
{
    private readonly FrozenSet<CardSize> _supportedSizes;

    public CardDefinition(
        string cardTypeId,
        string titleResourceKey,
        CardSize defaultSize,
        IEnumerable<CardSize> supportedSizes)
    {
        CardTypeId = CardRuntimeContractGuards.RequireIdentifier(
            cardTypeId,
            nameof(cardTypeId));
        TitleResourceKey = CardRuntimeContractGuards.RequireIdentifier(
            titleResourceKey,
            nameof(titleResourceKey));
        if (!Enum.IsDefined(defaultSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultSize),
                defaultSize,
                "Default size must be a defined card size.");
        }

        ArgumentNullException.ThrowIfNull(supportedSizes);
        CardSize[] materializedSizes = supportedSizes.ToArray();
        if (materializedSizes.Length == 0)
        {
            throw new ArgumentException(
                "At least one supported size is required.",
                nameof(supportedSizes));
        }

        foreach (CardSize size in materializedSizes)
        {
            if (!Enum.IsDefined(size))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(supportedSizes),
                    size,
                    "Supported sizes must contain only defined card sizes.");
            }
        }

        _supportedSizes = materializedSizes.ToFrozenSet();
        if (_supportedSizes.Count != materializedSizes.Length)
        {
            throw new ArgumentException(
                "Supported sizes cannot contain duplicates.",
                nameof(supportedSizes));
        }

        if (!_supportedSizes.Contains(defaultSize))
        {
            throw new ArgumentException(
                "The default size must be included in supported sizes.",
                nameof(defaultSize));
        }

        DefaultSize = defaultSize;
    }

    public string CardTypeId { get; }

    public string TitleResourceKey { get; }

    public CardSize DefaultSize { get; }

    public IReadOnlySet<CardSize> SupportedSizes => _supportedSizes;

    public bool SupportsSize(CardSize size) => _supportedSizes.Contains(size);
}

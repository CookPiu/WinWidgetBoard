using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// One vendor's row in the settings dialog. Availability is reported by the broker and is not
/// something the user chooses: a vendor with no session directory on this machine can still be
/// left switched on - installing it later should just start counting - but the row says plainly
/// that there is nothing to read yet, rather than looking like a silent failure.
/// </summary>
public sealed class TokenUsageVendorOption : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _isAvailable;

    public TokenUsageVendorOption(string vendorId, string displayName)
    {
        VendorId = vendorId;
        DisplayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string VendorId { get; }

    public string DisplayName { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                Raise(nameof(IsEnabled));
            }
        }
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable != value)
            {
                _isAvailable = value;
                Raise(nameof(IsAvailable));
                Raise(nameof(IsUnavailableVisible));
            }
        }
    }

    public bool IsUnavailableVisible => !_isAvailable;

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Settings-dialog state for the token-usage card: which vendors are counted.
///
/// Deliberately nothing else. Where the records live is not configurable - each vendor writes
/// to one well-known place under the user profile - and adding a path box would turn a
/// two-checkbox dialog into a validation surface for a case that does not exist yet.
/// </summary>
public sealed class TokenUsageSettingsViewModel : INotifyPropertyChanged
{
    private readonly ITokenUsageSettingsClient? _client;
    private readonly Func<string, string> _text;
    private string _statusText = string.Empty;
    private string _pricingSyncedText = string.Empty;
    private int _revision;
    private bool _isBusy;
    private bool _wasSaved;
    private bool _isLoaded;
    private bool _syncPricing = true;

    public TokenUsageSettingsViewModel(
        ITokenUsageSettingsClient? client,
        Func<string, string>? textResolver = null)
    {
        _client = client;
        _text = textResolver ?? (static key => key);
        Vendors = [];
        foreach (string vendorId in TokenUsageContract.VendorIds)
        {
            Vendors.Add(
                new TokenUsageVendorOption(
                    vendorId,
                    _text(Runtime.TokenUsageResourceKeys.GetPageNameKey(vendorId)))
                {
                    // Enabled until the broker says otherwise, which matches the default the
                    // broker itself applies when nothing has been saved.
                    IsEnabled = true,
                });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TokenUsageVendorOption> Vendors { get; }

    /// <summary>
    /// Whether the broker may fetch the public price list once a day (ADR-0035). Saved with
    /// the vendor selection; on by default because a stale price is the one error in the
    /// cost figure that does not show.
    /// </summary>
    public bool SyncPricing
    {
        get => _syncPricing;
        set => Set(ref _syncPricing, value);
    }

    /// <summary>
    /// How fresh the synced prices are, as a sentence: when they were last fetched, or that
    /// the built-in table is what is pricing today.
    /// </summary>
    public string PricingSyncedText
    {
        get => _pricingSyncedText;
        private set => Set(ref _pricingSyncedText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(CanSave));
                Raise(nameof(CanSyncNow));
            }
        }
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (Set(ref _isLoaded, value))
            {
                Raise(nameof(CanSave));
            }
        }
    }

    /// <summary>True once a save in this dialog session succeeded.</summary>
    public bool WasSaved
    {
        get => _wasSaved;
        private set => Set(ref _wasSaved, value);
    }

    public bool CanSave => _client is not null && IsLoaded && !IsBusy;

    /// <summary>
    /// A fetch on request is an explicit action, so it is offered even while the daily
    /// switch is off; the switch governs what the broker does on its own.
    /// </summary>
    public bool CanSyncNow => _client is not null && !IsBusy;

    public async Task SyncPricingNowAsync(CancellationToken cancellationToken)
    {
        if (!CanSyncNow || _client is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = _text("TokenUsagePricingSyncingStatus");
        try
        {
            TokenUsagePricingSyncResponse response = await _client.SyncTokenPricingAsync(
                TokenUsageContract.DefaultInstanceId,
                cancellationToken).ConfigureAwait(true);
            PricingSyncedText = FormatPricingSynced(response.PricingSyncedAtUtc);
            StatusText = _text(
                response.Updated
                    ? "TokenUsagePricingSyncUpdatedStatus"
                    : "TokenUsagePricingSyncUnchangedStatus");
        }
        catch (CoreBrokerClientException)
        {
            StatusText = _text("TokenUsagePricingSyncFailedStatus");
        }
        catch (IOException)
        {
            StatusText = _text("TokenUsagePricingSyncFailedStatus");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public int Revision => _revision;

    /// <summary>
    /// The selection to send. Contract order rather than list order so the saved value is
    /// stable regardless of how the rows happen to be laid out.
    /// </summary>
    public IReadOnlyList<string> SelectedVendors =>
        TokenUsageContract.VendorIds
            .Where(vendorId => Vendors.Any(option =>
                string.Equals(option.VendorId, vendorId, StringComparison.Ordinal) &&
                option.IsEnabled))
            .ToArray();

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            StatusText = _text("TokenUsageSettingsUnavailableStatus");
            return;
        }

        IsBusy = true;
        try
        {
            TokenUsageSettingsGetResponse response =
                await _client.GetTokenUsageSettingsAsync(
                    TokenUsageContract.DefaultInstanceId,
                    cancellationToken).ConfigureAwait(true);
            Apply(response);
            IsLoaded = true;
            StatusText = _text("TokenUsageSettingsLoadedStatus");
        }
        catch (CoreBrokerClientException)
        {
            StatusText = _text("TokenUsageSettingsLoadFailedStatus");
        }
        catch (IOException)
        {
            StatusText = _text("TokenUsageSettingsLoadFailedStatus");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!CanSave || _client is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            TokenUsageSettingsDto saved = await _client.SaveTokenUsageSettingsAsync(
                new TokenUsageSettingsSaveRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    InstanceId = TokenUsageContract.DefaultInstanceId,
                    EnabledVendors = SelectedVendors,
                    SyncPricing = SyncPricing,
                    ExpectedRevision = _revision,
                },
                cancellationToken).ConfigureAwait(true);
            ApplySettings(saved);
            WasSaved = true;
            StatusText = _text("TokenUsageSettingsSavedStatus");
        }
        catch (CoreBrokerClientException exception)
        {
            // A conflict means another surface saved first. The stored value is authoritative,
            // so the dialog reloads rather than overwriting it with what was on screen.
            StatusText = string.Equals(
                exception.Code,
                "conflict.tokenusage-settings-revision",
                StringComparison.Ordinal)
                ? _text("TokenUsageSettingsConflictStatus")
                : _text("TokenUsageSettingsSaveFailedStatus");
            if (string.Equals(
                    exception.Code,
                    "conflict.tokenusage-settings-revision",
                    StringComparison.Ordinal))
            {
                await LoadAsync(cancellationToken).ConfigureAwait(true);
                StatusText = _text("TokenUsageSettingsConflictStatus");
            }
        }
        catch (IOException)
        {
            StatusText = _text("TokenUsageSettingsSaveFailedStatus");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(TokenUsageSettingsGetResponse response)
    {
        ApplySettings(response.Settings);
        foreach (TokenUsageVendorStatusDto status in response.Vendors)
        {
            TokenUsageVendorOption? option = Vendors.FirstOrDefault(item =>
                string.Equals(item.VendorId, status.VendorId, StringComparison.Ordinal));
            if (option is not null)
            {
                option.IsAvailable = status.IsAvailable;
            }
        }
    }

    private void ApplySettings(TokenUsageSettingsDto settings)
    {
        _revision = settings.Revision;
        foreach (TokenUsageVendorOption option in Vendors)
        {
            option.IsEnabled = settings.EnabledVendors.Contains(
                option.VendorId,
                StringComparer.Ordinal);
        }

        SyncPricing = settings.SyncPricing;
        PricingSyncedText = FormatPricingSynced(settings.PricingSyncedAtUtc);
        Raise(nameof(Revision));
    }

    private string FormatPricingSynced(string syncedAtUtc)
    {
        if (!string.IsNullOrEmpty(syncedAtUtc) &&
            DateTimeOffset.TryParse(
                syncedAtUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset syncedAt))
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _text("TokenUsagePricingSyncedStatus"),
                syncedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        }

        return _text("TokenUsagePricingNotSyncedStatus");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

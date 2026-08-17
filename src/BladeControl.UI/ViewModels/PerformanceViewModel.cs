using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

/// <summary>
/// One selectable policy value. Values the backend models but has not hardware-validated are
/// listed with <see cref="IsAvailable"/> false and can never become the selection — there is
/// deliberately no force/raw/unsafe path in this UI.
/// </summary>
public sealed class PolicyOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public PolicyOptionViewModel(
        string value,
        string label,
        string description,
        bool isAvailable,
        string? blockedReason = null)
    {
        Value = value;
        Label = label;
        Description = description;
        IsAvailable = isAvailable;
        BlockedReason = blockedReason;
    }

    public string Value { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsAvailable { get; }

    public string? BlockedReason { get; }

    public string Tooltip => IsAvailable ? Description : BlockedReason ?? Description;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }
}

public sealed class PerformanceViewModel : PageViewModel
{
    private const string NotValidated =
        "Not hardware validated. Runtime Core rejects this value, so BladeControl does not " +
        "offer it. There is no bypass.";

    private string _selectedMode = "Balanced";
    private string _selectedCpuLevel = "Medium";
    private string _selectedGpuLevel = "Low";
    private string _currentMode = Display.Unavailable;
    private string _currentZone2Mode = Display.Unavailable;
    private string _currentCpuLevel = Display.Unavailable;
    private string _currentGpuLevel = Display.Unavailable;
    private bool _hasCurrentState;
    private bool _pendingInitialized;
    private bool _modeSelectionConfirmed;

    public PerformanceViewModel(RuntimeConnection connection, CancellationToken lifetime)
        : base(
            connection,
            lifetime,
            "Performance",
            "Performance",
            "Power policy applied through Runtime Core",
            Icons.Performance)
    {
        Modes =
        [
            new PolicyOptionViewModel(
                "Balanced",
                "Balanced",
                "Firmware balanced profile with automatic fan control.",
                true),
            new PolicyOptionViewModel(
                "Silent",
                "Silent",
                "Firmware silent profile; lowest acoustics.",
                true),
            new PolicyOptionViewModel(
                "Custom",
                "Custom",
                "Choose CPU and GPU performance levels explicitly.",
                true)
        ];
        CpuLevels =
        [
            new PolicyOptionViewModel("Low", "Low", "Lowest CPU power ceiling.", true),
            new PolicyOptionViewModel("Medium", "Medium", "Balanced CPU power ceiling.", true),
            new PolicyOptionViewModel("High", "High", "Modelled by the protocol.", false, NotValidated),
            new PolicyOptionViewModel("Boost", "Boost", "Modelled by the protocol.", false, NotValidated),
            new PolicyOptionViewModel(
                "Overclock",
                "Overclock",
                "Modelled by the protocol.",
                false,
                NotValidated)
        ];
        GpuLevels =
        [
            new PolicyOptionViewModel("Low", "Low", "Lowest GPU power ceiling.", true),
            new PolicyOptionViewModel("Medium", "Medium", "Modelled by the protocol.", false, NotValidated),
            new PolicyOptionViewModel("High", "High", "Modelled by the protocol.", false, NotValidated)
        ];

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        RestoreCommand = new RelayCommand(RestoreFromCurrent, () => _hasCurrentState);
        RefreshCommand = new AsyncRelayCommand(RefreshFromRuntimeAsync, () => Connection.IsOnline);
        SyncSelectionFlags();
    }

    public IReadOnlyList<PolicyOptionViewModel> Modes { get; }

    public IReadOnlyList<PolicyOptionViewModel> CpuLevels { get; }

    public IReadOnlyList<PolicyOptionViewModel> GpuLevels { get; }

    public AsyncRelayCommand ApplyCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public string SelectedMode
    {
        get => _selectedMode;
        set => TrySelectMode(value);
    }

    public string SelectedCpuLevel
    {
        get => _selectedCpuLevel;
        set => TrySelectCpuLevel(value);
    }

    public string SelectedGpuLevel
    {
        get => _selectedGpuLevel;
        set => TrySelectGpuLevel(value);
    }

    public bool IsCustomSelected => string.Equals(_selectedMode, "Custom", StringComparison.Ordinal);

    public string CurrentMode
    {
        get => _currentMode;
        private set => Set(ref _currentMode, value);
    }

    public string CurrentCpuLevel
    {
        get => _currentCpuLevel;
        private set => Set(ref _currentCpuLevel, value);
    }

    public string CurrentGpuLevel
    {
        get => _currentGpuLevel;
        private set => Set(ref _currentGpuLevel, value);
    }

    public string PendingSummary => IsCustomSelected
        ? $"Custom · CPU {_selectedCpuLevel} · GPU {_selectedGpuLevel}"
        : _selectedMode;

    public string CurrentSummary => _hasCurrentState
        ? !string.Equals(_currentMode, _currentZone2Mode, StringComparison.Ordinal)
            ? $"Zone 1 {_currentMode} · Zone 2 {_currentZone2Mode}"
            : string.Equals(_currentMode, "Custom", StringComparison.Ordinal)
            ? $"Custom · CPU {_currentCpuLevel} · GPU {_currentGpuLevel}"
            : _currentMode
        : Display.Unavailable;

    public bool HasPendingChanges => _hasCurrentState &&
        (!string.Equals(_currentMode, _currentZone2Mode, StringComparison.Ordinal) ||
         !string.Equals(_selectedMode, _currentMode, StringComparison.Ordinal) ||
         string.Equals(_currentMode, "Custom", StringComparison.Ordinal) &&
         (!string.Equals(_selectedCpuLevel, _currentCpuLevel, StringComparison.Ordinal) ||
          !string.Equals(_selectedGpuLevel, _currentGpuLevel, StringComparison.Ordinal)));

    public bool CanApply => Connection.CanApplyStaticProfile && IsPendingSelectionValid;

    public string? ApplyBlockedReason => Connection.StaticProfileBlockedReason ??
        (!_pendingInitialized
            ? "Waiting for Runtime Core to report the current performance policy."
            : !_modeSelectionConfirmed
                ? "Runtime Core reported different or unsupported zone modes. Choose a " +
                    "validated mode before applying."
                : !HasAvailableMode(_selectedMode) ||
                  IsCustomSelected &&
                  (!HasAvailableCpuLevel(_selectedCpuLevel) ||
                   !HasAvailableGpuLevel(_selectedGpuLevel))
                    ? "The reported Custom levels are not hardware validated. Choose " +
                        "validated CPU and GPU levels before applying."
                    : null);

    public bool HasApplyBlockedReason => !string.IsNullOrEmpty(ApplyBlockedReason);

    /// <summary>The levels a Custom apply would send. Used by the dashboard quick action.</summary>
    public (string CpuLevel, string GpuLevel) CustomLevels =>
        (_selectedCpuLevel, _selectedGpuLevel);

    public bool CanApplyCustomSelection =>
        _pendingInitialized &&
        HasAvailableCpuLevel(_selectedCpuLevel) &&
        HasAvailableGpuLevel(_selectedGpuLevel);

    private bool IsPendingSelectionValid =>
        _pendingInitialized &&
        _modeSelectionConfirmed &&
        HasAvailableMode(_selectedMode) &&
        (!IsCustomSelected || CanApplyCustomSelection);

    public bool TrySelectMode(string value)
    {
        PolicyOptionViewModel? option = Modes.FirstOrDefault(item =>
            string.Equals(item.Value, value, StringComparison.Ordinal));
        if (option is null || !option.IsAvailable)
        {
            return false;
        }

        bool changed = Set(ref _selectedMode, value, nameof(SelectedMode));
        bool confirmed = !_modeSelectionConfirmed;
        _modeSelectionConfirmed = true;
        if (changed || confirmed)
        {
            SyncSelectionFlags();
            NotifyPendingSelectionChanged();
        }

        return true;
    }

    public bool TrySelectCpuLevel(string value)
    {
        PolicyOptionViewModel? option = CpuLevels.FirstOrDefault(item =>
            string.Equals(item.Value, value, StringComparison.Ordinal));
        if (option is null || !option.IsAvailable)
        {
            return false;
        }

        if (Set(ref _selectedCpuLevel, value, nameof(SelectedCpuLevel)))
        {
            SyncSelectionFlags();
            NotifyPendingSelectionChanged();
        }

        return true;
    }

    public bool TrySelectGpuLevel(string value)
    {
        PolicyOptionViewModel? option = GpuLevels.FirstOrDefault(item =>
            string.Equals(item.Value, value, StringComparison.Ordinal));
        if (option is null || !option.IsAvailable)
        {
            return false;
        }

        if (Set(ref _selectedGpuLevel, value, nameof(SelectedGpuLevel)))
        {
            SyncSelectionFlags();
            NotifyPendingSelectionChanged();
        }

        return true;
    }

    public override void Refresh()
    {
        if (!Connection.IsOnline)
        {
            _pendingInitialized = false;
            _modeSelectionConfirmed = false;
        }

        PerformanceStateDto? state = Connection.Performance;
        if (state is not null)
        {
            _hasCurrentState = true;
            CurrentMode = state.Mode.Zone1PerformanceMode;
            _currentZone2Mode = state.Mode.Zone2PerformanceMode;
            CurrentCpuLevel = state.CpuLevel;
            CurrentGpuLevel = state.GpuLevel;
            if (Connection.IsOnline && !_pendingInitialized)
            {
                SynchronizePendingFromCurrent(state);
            }
        }

        RaiseAll(
            nameof(CurrentSummary),
            nameof(PendingSummary),
            nameof(HasPendingChanges),
            nameof(CanApply),
            nameof(ApplyBlockedReason),
            nameof(HasApplyBlockedReason),
            nameof(CanApplyCustomSelection));
        ApplyCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    public override void Activate() => Refresh();

    /// <summary>Discards the pending selection and mirrors what the hardware reports today.</summary>
    public void RestoreFromCurrent()
    {
        if (!_hasCurrentState)
        {
            return;
        }

        if (Connection.Performance is { } state)
        {
            SynchronizePendingFromCurrent(state);
        }

        ClearStatus();
    }

    private async Task ApplyAsync()
    {
        if (!CanApply)
        {
            return;
        }

        string mode = _selectedMode;
        string? cpu = IsCustomSelected ? _selectedCpuLevel : null;
        string? gpu = IsCustomSelected ? _selectedGpuLevel : null;
        await RunCommandAsync(async (client, token) =>
        {
            RuntimeCommandResultDto result = await client.ApplyPerformanceProfileAsync(
                new ApplyPerformanceProfileRequest(mode, cpu, gpu),
                token).ConfigureAwait(false);
            string message = string.IsNullOrWhiteSpace(result.Message)
                ? $"{result.Outcome ?? "Applied"}."
                : $"{result.Outcome ?? "Applied"} — {result.Message}";
            return result.Succeeded
                ? RuntimeCommandOutcome.Ok(message)
                : RuntimeCommandOutcome.Fail(message);
        }).ConfigureAwait(true);

        // A rejected apply must leave the panel showing what the firmware actually reports.
        // RunCommandAsync has already refreshed the authoritative profile state; mirror it
        // instead of retrying.
        if (StatusIsError && Connection.Performance is { } authoritative)
        {
            SynchronizePendingFromCurrent(authoritative);
        }
    }

    private async Task RefreshFromRuntimeAsync()
    {
        ClearStatus();
        bool succeeded = await Connection.RefreshProfilesNowAsync(Lifetime).ConfigureAwait(true);
        if (succeeded && Connection.Performance is { } state)
        {
            SynchronizePendingFromCurrent(state);
        }

        Refresh();
    }

    private void SynchronizePendingFromCurrent(PerformanceStateDto state)
    {
        bool zonesAgree = string.Equals(
            state.Mode.Zone1PerformanceMode,
            state.Mode.Zone2PerformanceMode,
            StringComparison.Ordinal);
        _selectedMode = zonesAgree
            ? state.Mode.Zone1PerformanceMode
            : $"{state.Mode.Zone1PerformanceMode} / {state.Mode.Zone2PerformanceMode}";
        _selectedCpuLevel = state.CpuLevel;
        _selectedGpuLevel = state.GpuLevel;
        _pendingInitialized = Connection.IsOnline;
        _modeSelectionConfirmed = zonesAgree && HasAvailableMode(_selectedMode);
        SyncSelectionFlags();
        RaiseAll(nameof(SelectedMode), nameof(SelectedCpuLevel), nameof(SelectedGpuLevel));
        NotifyPendingSelectionChanged();
    }

    private bool HasAvailableMode(string value) => Modes.Any(item =>
        item.IsAvailable && string.Equals(item.Value, value, StringComparison.Ordinal));

    private bool HasAvailableCpuLevel(string value) => CpuLevels.Any(item =>
        item.IsAvailable && string.Equals(item.Value, value, StringComparison.Ordinal));

    private bool HasAvailableGpuLevel(string value) => GpuLevels.Any(item =>
        item.IsAvailable && string.Equals(item.Value, value, StringComparison.Ordinal));

    private void NotifyPendingSelectionChanged()
    {
        RaiseAll(
            nameof(IsCustomSelected),
            nameof(PendingSummary),
            nameof(HasPendingChanges),
            nameof(CanApply),
            nameof(ApplyBlockedReason),
            nameof(HasApplyBlockedReason),
            nameof(CanApplyCustomSelection));
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private void SyncSelectionFlags()
    {
        foreach (PolicyOptionViewModel option in Modes)
        {
            option.IsSelected = string.Equals(option.Value, _selectedMode, StringComparison.Ordinal);
        }

        foreach (PolicyOptionViewModel option in CpuLevels)
        {
            option.IsSelected =
                string.Equals(option.Value, _selectedCpuLevel, StringComparison.Ordinal);
        }

        foreach (PolicyOptionViewModel option in GpuLevels)
        {
            option.IsSelected =
                string.Equals(option.Value, _selectedGpuLevel, StringComparison.Ordinal);
        }
    }
}

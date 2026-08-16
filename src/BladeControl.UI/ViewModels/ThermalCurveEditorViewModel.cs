using System.Collections.ObjectModel;
using System.Text.Json;
using BladeControl.Runtime;

namespace BladeControl.UI.ViewModels;

public enum ThermalCurveTarget
{
    Cpu,
    Gpu
}

public sealed class ThermalCurvePointViewModel : ObservableObject
{
    private double _temperatureCelsius;
    private int _rpm;
    private string? _error;

    public ThermalCurvePointViewModel(double temperatureCelsius, int rpm)
    {
        _temperatureCelsius = temperatureCelsius;
        _rpm = rpm;
    }

    public event Action? Edited;

    public double TemperatureCelsius
    {
        get => _temperatureCelsius;
        set
        {
            if (Set(ref _temperatureCelsius, value))
            {
                Edited?.Invoke();
            }
        }
    }

    public int Rpm
    {
        get => _rpm;
        set
        {
            if (Set(ref _rpm, value))
            {
                Edited?.Invoke();
            }
        }
    }

    public string? Error
    {
        get => _error;
        internal set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public StoredThermalCurvePoint ToPoint() => new(_temperatureCelsius, _rpm);
}

/// <summary>
/// Edits a monotonic thermal curve and validates it against the Runtime Core constraints.
/// It performs no interpolation and no hysteresis: those decisions belong to the runtime.
/// </summary>
/// <remarks>
/// Runtime Core V1 exposes only the immutable built-in "default" curve over IPC
/// (<c>ListBuiltInCurves</c> returns a single entry and <c>StartThermalControl</c> rejects
/// any other name), so <see cref="CanApply"/> is always false in this build. The editor
/// still validates and previews, and the curve can be copied out as runtime-shaped JSON.
/// See docs/gui-backend-needs.md (item 2).
/// </remarks>
public sealed class ThermalCurveEditorViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    private readonly Action<string>? _copyToClipboard;
    private readonly ObservableCollection<string> _errors = [];
    private ThermalCurveTarget _target = ThermalCurveTarget.Cpu;
    private string _curveName = "default";
    private bool _isValid = true;
    private bool _isDirty;
    private bool _isLoadedFromRuntime;
    private string? _loadError;
    private string? _copyStatus;

    public ThermalCurveEditorViewModel(Action<string>? copyToClipboard = null)
    {
        _copyToClipboard = copyToClipboard;
        CpuPoints = [];
        GpuPoints = [];
        Errors = new ReadOnlyObservableCollection<string>(_errors);
        AddPointCommand = new RelayCommand(AddPoint, () => CanAddPoint);
        RemovePointCommand = new RelayCommand(RemoveSelectedPoint, () => ActivePoints.Count > 0);
        CopyJsonCommand = new RelayCommand(CopyJson, () => _copyToClipboard is not null);
        Validate();
    }

    public ObservableCollection<ThermalCurvePointViewModel> CpuPoints { get; }

    public ObservableCollection<ThermalCurvePointViewModel> GpuPoints { get; }

    public ReadOnlyObservableCollection<string> Errors { get; }

    public RelayCommand AddPointCommand { get; }

    public RelayCommand RemovePointCommand { get; }

    public RelayCommand CopyJsonCommand { get; }

    public int MinimumRpm => ThermalCurveValidator.MinimumRpm;

    public int MaximumRpm => ThermalCurveValidator.MaximumRpm;

    public int RpmIncrement => ThermalCurveValidator.RpmIncrement;

    public ThermalCurveTarget Target
    {
        get => _target;
        set
        {
            if (Set(ref _target, value))
            {
                RaiseAll(
                    nameof(ActivePoints),
                    nameof(IsCpuSelected),
                    nameof(IsGpuSelected),
                    nameof(ActiveTargetLabel),
                    nameof(CanAddPoint));
                AddPointCommand.RaiseCanExecuteChanged();
                RemovePointCommand.RaiseCanExecuteChanged();
                Validate();
            }
        }
    }

    public bool IsCpuSelected
    {
        get => _target == ThermalCurveTarget.Cpu;
        set
        {
            if (value)
            {
                Target = ThermalCurveTarget.Cpu;
            }
        }
    }

    public bool IsGpuSelected
    {
        get => _target == ThermalCurveTarget.Gpu;
        set
        {
            if (value)
            {
                Target = ThermalCurveTarget.Gpu;
            }
        }
    }

    public string ActiveTargetLabel => _target == ThermalCurveTarget.Cpu ? "CPU" : "GPU";

    public ObservableCollection<ThermalCurvePointViewModel> ActivePoints =>
        _target == ThermalCurveTarget.Cpu ? CpuPoints : GpuPoints;

    public ThermalCurvePointViewModel? SelectedPoint { get; set; }

    public string CurveName
    {
        get => _curveName;
        private set => Set(ref _curveName, value);
    }

    public bool IsValid
    {
        get => _isValid;
        private set => Set(ref _isValid, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    public bool IsLoadedFromRuntime
    {
        get => _isLoadedFromRuntime;
        private set
        {
            if (Set(ref _isLoadedFromRuntime, value))
            {
                Raise(nameof(SourceLabel));
            }
        }
    }

    public string SourceLabel => IsLoadedFromRuntime
        ? $"Loaded from Runtime Core · {CurveName}"
        : "No runtime curve loaded";

    public string? LoadError
    {
        get => _loadError;
        private set => Set(ref _loadError, value);
    }

    public string? CopyStatus
    {
        get => _copyStatus;
        private set => Set(ref _copyStatus, value);
    }

    public bool CanAddPoint => ActivePoints.Count == 0 ||
        ActivePoints[^1].TemperatureCelsius <
        ThermalCurveValidator.MaximumExclusiveTemperature - 1;

    /// <summary>
    /// Runtime Core V1 accepts only its immutable built-in curve, so the GUI never offers to
    /// push an edited curve. Kept as a property so the view binds to a single explicit gate.
    /// </summary>
    public bool CanApply => false;

    public string ApplyBlockedReason =>
        "Runtime Core V1 accepts only its immutable built-in \"default\" curve over IPC. " +
        "Edits here are validated and previewed locally; they are not sent to the runtime.";

    public IReadOnlyList<StoredThermalCurvePoint> ActiveSnapshot =>
        ActivePoints.Select(point => point.ToPoint()).ToArray();

    public IReadOnlyList<StoredThermalCurvePoint> CpuSnapshot =>
        CpuPoints.Select(point => point.ToPoint()).ToArray();

    public IReadOnlyList<StoredThermalCurvePoint> GpuSnapshot =>
        GpuPoints.Select(point => point.ToPoint()).ToArray();

    public void Load(StoredThermalCurveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CurveName = document.Name;
        Replace(CpuPoints, document.Cpu);
        Replace(GpuPoints, document.Gpu);
        LoadError = null;
        IsLoadedFromRuntime = true;
        Validate();
        IsDirty = false;
        Raise(nameof(SourceLabel));
    }

    public void ReportLoadFailure(string message)
    {
        LoadError = message;
        IsLoadedFromRuntime = false;
    }

    public void AddPoint()
    {
        ObservableCollection<ThermalCurvePointViewModel> points = ActivePoints;
        double temperature = points.Count == 0
            ? 50
            : Math.Min(
                ThermalCurveValidator.MaximumExclusiveTemperature - 1,
                points[^1].TemperatureCelsius + 5);
        int rpm = points.Count == 0
            ? ThermalCurveValidator.MinimumRpm
            : Math.Min(ThermalCurveValidator.MaximumRpm, points[^1].Rpm + 200);
        points.Add(Track(new ThermalCurvePointViewModel(temperature, Align(rpm))));
        AddPointCommand.RaiseCanExecuteChanged();
        RemovePointCommand.RaiseCanExecuteChanged();
        MarkDirty();
    }

    public void RemovePoint(ThermalCurvePointViewModel point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!ActivePoints.Remove(point))
        {
            return;
        }

        point.Edited -= MarkDirty;
        AddPointCommand.RaiseCanExecuteChanged();
        RemovePointCommand.RaiseCanExecuteChanged();
        MarkDirty();
    }

    public void Validate()
    {
        ThermalCurveValidationResult cpu = ThermalCurveValidator.Validate(CpuSnapshot);
        ThermalCurveValidationResult gpu = ThermalCurveValidator.Validate(GpuSnapshot);
        ApplyPointErrors(CpuPoints, cpu);
        ApplyPointErrors(GpuPoints, gpu);

        _errors.Clear();
        foreach (string error in cpu.Errors)
        {
            _errors.Add($"CPU curve — {error}");
        }

        foreach (string error in gpu.Errors)
        {
            _errors.Add($"GPU curve — {error}");
        }

        IsValid = cpu.IsValid && gpu.IsValid;
    }

    public string ToJson() => JsonSerializer.Serialize(
        new StoredThermalCurveDocument(1, CurveName, CpuSnapshot, GpuSnapshot),
        ExportOptions);

    private void CopyJson()
    {
        if (_copyToClipboard is null)
        {
            return;
        }

        try
        {
            _copyToClipboard(ToJson());
            CopyStatus = "Curve JSON copied to the clipboard.";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or
            System.Runtime.InteropServices.ExternalException)
        {
            CopyStatus = $"Clipboard unavailable: {exception.Message}";
        }
    }

    private void MarkDirty()
    {
        IsDirty = true;
        Validate();
        AddPointCommand.RaiseCanExecuteChanged();
        RaiseAll(nameof(ActiveSnapshot), nameof(CpuSnapshot), nameof(GpuSnapshot));
    }

    private void RemoveSelectedPoint()
    {
        ThermalCurvePointViewModel? point = SelectedPoint ??
            (ActivePoints.Count > 0 ? ActivePoints[^1] : null);
        if (point is not null)
        {
            RemovePoint(point);
        }
    }

    private ThermalCurvePointViewModel Track(ThermalCurvePointViewModel point)
    {
        point.Edited += MarkDirty;
        return point;
    }

    private void Replace(
        ObservableCollection<ThermalCurvePointViewModel> target,
        IReadOnlyList<StoredThermalCurvePoint> source)
    {
        foreach (ThermalCurvePointViewModel existing in target)
        {
            existing.Edited -= MarkDirty;
        }

        target.Clear();
        foreach (StoredThermalCurvePoint point in source)
        {
            target.Add(Track(new ThermalCurvePointViewModel(point.TemperatureCelsius, point.Rpm)));
        }
    }

    private static void ApplyPointErrors(
        IReadOnlyList<ThermalCurvePointViewModel> points,
        ThermalCurveValidationResult result)
    {
        for (int index = 0; index < points.Count; index++)
        {
            points[index].Error = result.PointErrors.TryGetValue(index, out string? error)
                ? error
                : null;
        }
    }

    private static int Align(int rpm) =>
        rpm / ThermalCurveValidator.RpmIncrement * ThermalCurveValidator.RpmIncrement;
}

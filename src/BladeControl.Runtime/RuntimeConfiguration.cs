using System.Text.Json;
using BladeControl.Thermal;

namespace BladeControl.Runtime;

public sealed record RuntimePreferencesDocument(
    int Version,
    int ControlPeriodMilliseconds,
    int WatchdogIntervalSeconds)
{
    public static RuntimePreferencesDocument Default { get; } = new(1, 500, 5);
}

public sealed record StoredThermalCurvePoint(double TemperatureCelsius, int Rpm);

public sealed record StoredThermalCurveDocument(
    int Version,
    string Name,
    IReadOnlyList<StoredThermalCurvePoint> Cpu,
    IReadOnlyList<StoredThermalCurvePoint> Gpu);

public sealed record RuntimeUserProfileDocument(
    int Version,
    string Name,
    string PerformanceProfile,
    string FanProfile,
    string? ThermalCurveName);

public sealed record ConfigurationLoadResult<T>(
    bool Succeeded,
    T? Value,
    string Message)
    where T : class;

public sealed class RuntimeConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    private readonly string _directory;

    public RuntimeConfigurationStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public static StoredThermalCurveDocument BuiltInDefault => FromProfile(
        BuiltInThermalProfiles.Default);

    public void SavePreferences(RuntimePreferencesDocument preferences) =>
        Save("runtime-preferences.json", preferences, ValidatePreferences);

    public ConfigurationLoadResult<RuntimePreferencesDocument> LoadPreferences() =>
        Load<RuntimePreferencesDocument>("runtime-preferences.json", ValidatePreferences);

    public void SaveUserCurve(StoredThermalCurveDocument curve)
    {
        ValidateCurve(curve);
        Save($"curve-{SafeName(curve.Name)}.json", curve, ValidateCurve);
    }

    public ConfigurationLoadResult<StoredThermalCurveDocument> LoadUserCurve(string name) =>
        Load<StoredThermalCurveDocument>($"curve-{SafeName(name)}.json", ValidateCurve);

    public void SaveUserProfile(RuntimeUserProfileDocument profile) =>
        Save($"profile-{SafeName(profile.Name)}.json", profile, ValidateUserProfile);

    public ConfigurationLoadResult<RuntimeUserProfileDocument> LoadUserProfile(string name) =>
        Load<RuntimeUserProfileDocument>($"profile-{SafeName(name)}.json", ValidateUserProfile);

    private void Save<T>(string fileName, T value, Action<T> validate)
    {
        ArgumentNullException.ThrowIfNull(value);
        validate(value);
        Directory.CreateDirectory(_directory);
        string destination = Path.Combine(_directory, fileName);
        string temporary = Path.Combine(
            _directory,
            $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            string json = JsonSerializer.Serialize(value, Options);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private ConfigurationLoadResult<T> Load<T>(string fileName, Action<T> validate)
        where T : class
    {
        string path = Path.Combine(_directory, fileName);
        try
        {
            if (!File.Exists(path))
            {
                return new ConfigurationLoadResult<T>(false, null, "Configuration file does not exist.");
            }

            T value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ??
                throw new FormatException("Configuration document is empty.");
            validate(value);
            return new ConfigurationLoadResult<T>(true, value, "Configuration loaded.");
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or FormatException or
            ArgumentException)
        {
            return new ConfigurationLoadResult<T>(
                false,
                null,
                $"Configuration rejected safely: {exception.Message}");
        }
    }

    private static StoredThermalCurveDocument FromProfile(ThermalProfile profile) => new(
        1,
        profile.Name,
        profile.CpuCurve.Points.Select(point =>
            new StoredThermalCurvePoint(point.TemperatureCelsius, point.TargetRpm.Value)).ToArray(),
        profile.GpuCurve.Points.Select(point =>
            new StoredThermalCurvePoint(point.TemperatureCelsius, point.TargetRpm.Value)).ToArray());

    private static void ValidatePreferences(RuntimePreferencesDocument value)
    {
        if (value.Version != 1 ||
            value.ControlPeriodMilliseconds is < 100 or > 5000 ||
            value.WatchdogIntervalSeconds is < 1 or > 60)
        {
            throw new FormatException("Runtime preferences have an unsupported version or unsafe timing value.");
        }
    }

    private static void ValidateCurve(StoredThermalCurveDocument value)
    {
        if (value.Version != 1 || string.IsNullOrWhiteSpace(value.Name))
        {
            throw new FormatException("Thermal curve version/name is invalid.");
        }

        _ = new ThermalCurve(value.Cpu.Select(point =>
            new ThermalCurvePoint(point.TemperatureCelsius, new Razer.FanRpm(point.Rpm))));
        _ = new ThermalCurve(value.Gpu.Select(point =>
            new ThermalCurvePoint(point.TemperatureCelsius, new Razer.FanRpm(point.Rpm))));
    }

    private static void ValidateUserProfile(RuntimeUserProfileDocument value)
    {
        if (value.Version != 1 || string.IsNullOrWhiteSpace(value.Name) ||
            string.IsNullOrWhiteSpace(value.PerformanceProfile) ||
            string.IsNullOrWhiteSpace(value.FanProfile))
        {
            throw new FormatException("User profile version or required fields are invalid.");
        }
    }

    private static string SafeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || name.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException(
                "Configuration names may contain only ASCII letters, digits, '-' and '_'.",
                nameof(name));
        }

        return name;
    }
}

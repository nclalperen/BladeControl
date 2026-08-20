using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BladeControl.UI.Controls;

namespace BladeControl.UI.Tests;

/// <summary>
/// Guards the shared design system: the tokens pages bind to, the compact styles' link to
/// their full-size counterparts, and TelemetryChart taking its colours from the theme.
/// These assert structure and resolution, never rendered pixel values.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DesignSystemTests
{
    private static readonly string[] RequiredBrushKeys =
    [
        "WindowBackgroundBrush", "SurfaceBrush", "SurfaceRaisedBrush", "SurfaceHoverBrush",
        "BorderBrush", "BorderStrongBrush",
        "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush",
        "AccentBrush", "AccentSoftBrush",
        "GoodBrush", "WarningBrush", "DangerBrush", "NeutralBrush", "MutedBrush",
        "GoodBackgroundBrush", "WarningBackgroundBrush", "DangerBackgroundBrush",
        "NeutralBackgroundBrush", "MutedBackgroundBrush",
        "ChartPlotBackgroundBrush", "ChartPlotBorderBrush", "ChartGridBrush"
    ];

    private static readonly string[] RequiredCornerRadiusKeys =
    [
        "ControlCornerRadius", "PanelCornerRadius", "CardCornerRadius",
        "WindowCornerRadius", "PillCornerRadius"
    ];

    private static readonly string[] RequiredThicknessKeys =
    [
        "PageContentMargin", "CardGapMargin", "SectionGapMargin",
        "LabelValueMargin", "MetricValueMargin", "ValueCaptionMargin",
        "CompactCardGapMargin", "CompactSectionGapMargin", "CompactEyebrowMargin"
    ];

    /// <summary>
    /// Every interactive control the theme styles must also style the surface a user reads
    /// text off, not only the one they click.
    /// </summary>
    /// <remarks>
    /// ComboBox styled the closed field but not ComboBoxItem, so the popup fell back to system
    /// defaults: in a dark theme that renders near-black text on a near-black list. The items
    /// were present, selectable, and effectively invisible. Styling the closed control is the
    /// easy half; the popup is where the text actually has to be read.
    /// </remarks>
    [TestMethod]
    public void ComboBoxPopupItemsAreStyledAndNotLeftToSystemDefaults() => OnStaThread(() =>
    {
        ResourceDictionary theme = LoadTheme();

        var itemStyle = theme[typeof(ComboBoxItem)] as Style;
        Assert.IsNotNull(
            itemStyle,
            "ComboBoxItem must be styled, or the dropdown inherits unreadable system colours.");

        Setter? foreground = itemStyle!.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.ForegroundProperty);
        Setter? background = itemStyle.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.BackgroundProperty);

        Assert.IsNotNull(foreground, "Popup items need an explicit foreground.");
        Assert.IsNotNull(background, "Popup items need an explicit background.");

        var closed = theme[typeof(ComboBox)] as Style;
        Setter? closedForeground = closed!.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.ForegroundProperty);

        // Compare the colour, not the brush: the two setters parse to separate SolidColorBrush
        // instances, so reference equality would fail even when they render identically.
        Assert.AreEqual(
            ((SolidColorBrush)closedForeground!.Value).Color,
            ((SolidColorBrush)foreground!.Value).Color,
            "The popup must read the same way as the field it drops out of.");
    });

    [TestMethod]
    public void ThemeDefinesEveryTokenThePagesBindTo() => OnStaThread(() =>
    {
        ResourceDictionary theme = LoadTheme();

        foreach (string key in RequiredBrushKeys)
        {
            Assert.IsInstanceOfType<Brush>(theme[key], $"{key} must be a brush.");
        }

        foreach (string key in RequiredCornerRadiusKeys)
        {
            Assert.IsInstanceOfType<CornerRadius>(theme[key], $"{key} must be a CornerRadius.");
        }

        foreach (string key in RequiredThicknessKeys)
        {
            Assert.IsInstanceOfType<Thickness>(theme[key], $"{key} must be a Thickness.");
        }
    });

    [TestMethod]
    public void CompactStylesExtendTheirFullSizeCounterpartsRatherThanForkingTheTheme() =>
        OnStaThread(() =>
        {
            ResourceDictionary theme = LoadTheme();

            AssertBasedOn(theme, "CompactPrimaryButtonStyle", "PrimaryButtonStyle");
            AssertBasedOn(theme, "CompactGhostButtonStyle", "GhostButtonStyle");
            AssertBasedOn(theme, "CompactIconButtonStyle", "GhostButtonStyle");
            AssertBasedOn(theme, "CompactTitleTextStyle", "SectionTitleTextStyle");
            AssertBasedOn(theme, "CompactMetricTextStyle", "MetricTextStyle");
            AssertBasedOn(theme, "CompactCaptionTextStyle", "CaptionTextStyle");
            AssertBasedOn(theme, "CompactStatusTextStyle", "CaptionTextStyle");
        });

    /// <summary>
    /// A named TextBlock style replaces the implicit one instead of extending it, so any
    /// compact text style that forgets Display formatting silently re-rasterises its glyphs.
    /// </summary>
    [TestMethod]
    public void CompactTextStylesKeepDisplayFormattingLikeTheImplicitTextBlockStyle() =>
        OnStaThread(() =>
        {
            ResourceDictionary theme = LoadTheme();
            string[] keys =
            [
                "CompactTitleTextStyle", "CompactMetricTextStyle",
                "CompactCaptionTextStyle", "CompactStatusTextStyle"
            ];

            foreach (string key in keys)
            {
                var style = (Style)theme[key];
                Assert.AreEqual(
                    TextFormattingMode.Display,
                    EffectiveSetterValue(style, TextOptions.TextFormattingModeProperty),
                    $"{key} must restate TextFormattingMode=Display.");
            }
        });

    /// <summary>
    /// The chart must not carry its own copy of the palette; a re-added static brush, pen or
    /// colour constant would drift away from DarkTheme.xaml exactly as it did before.
    /// </summary>
    [TestMethod]
    public void TelemetryChartHoldsNoPrivatePaletteConstants()
    {
        FieldInfo[] offenders = typeof(TelemetryChart)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(Brush) ||
                field.FieldType == typeof(Pen) ||
                field.FieldType == typeof(Color) ||
                typeof(Brush).IsAssignableFrom(field.FieldType))
            .ToArray();

        Assert.AreEqual(
            0,
            offenders.Length,
            "TelemetryChart must resolve colours from the theme, but declares: " +
                string.Join(", ", offenders.Select(field => field.Name)));
    }

    /// <summary>
    /// Proves resolution actually reaches the theme: the same control renders differently
    /// when a theme brush changes, which is what "follows future palette changes" means.
    /// </summary>
    [TestMethod]
    public void TelemetryChartRendersFromTheThemeSoPaletteChangesReachIt() => OnStaThread(() =>
    {
        byte[] baseline = RenderChart(theme => theme);
        byte[] repainted = RenderChart(theme =>
        {
            theme["WindowBackgroundBrush"] = Brushes.Magenta;
            return theme;
        });

        Assert.AreEqual(baseline.Length, repainted.Length);
        CollectionAssert.AreNotEqual(
            baseline,
            repainted,
            "Changing a theme brush must change what TelemetryChart draws.");
    });

    private static byte[] RenderChart(Func<ResourceDictionary, ResourceDictionary> configure)
    {
        var host = new Border { Width = 320, Height = 200 };
        host.Resources.MergedDictionaries.Add(configure(LoadTheme()));
        var chart = new TelemetryChart();
        host.Child = chart;

        host.Measure(new Size(320, 200));
        host.Arrange(new Rect(0, 0, 320, 200));
        host.UpdateLayout();

        var target = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
        target.Render(host);
        byte[] pixels = new byte[320 * 200 * 4];
        target.CopyPixels(pixels, 320 * 4, 0);
        return pixels;
    }

    /// <summary>
    /// The caption style wraps, because captions carry the qualifying prose.
    /// </summary>
    /// <remarks>
    /// Captions are where every surface explains what a reading means and why a control is
    /// unavailable — including the sentence saying a fan value is a command rather than a
    /// tachometer reading. None of the text styles set TextWrapping, so those sentences were
    /// clipped at the panel edge, and a clipped qualifier reads as a stronger claim than the
    /// full one. Eyebrows and metrics are excluded deliberately: short labels and single values
    /// are broken by wrapping, not saved by it.
    /// </remarks>
    [TestMethod]
    public void CaptionTextWrapsAndShortLabelStylesDoNot()
    {
        ResourceDictionary theme = LoadTheme();

        var caption = (Style)theme["CaptionTextStyle"];
        Assert.AreEqual(
            TextWrapping.Wrap,
            Setter(caption, TextBlock.TextWrappingProperty),
            "Caption prose must wrap rather than clip.");

        foreach (string key in new[] { "EyebrowTextStyle", "MetricTextStyle" })
        {
            Assert.IsNull(
                Setter((Style)theme[key], TextBlock.TextWrappingProperty),
                $"{key} labels a single short value and must not wrap.");
        }
    }

    /// <summary>The value a style sets for a property, or null if it sets none.</summary>
    private static object? Setter(Style style, DependencyProperty property)
    {
        for (Style? current = style; current is not null; current = current.BasedOn)
        {
            foreach (SetterBase entry in current.Setters)
            {
                if (entry is Setter setter && setter.Property == property)
                {
                    return setter.Value;
                }
            }
        }

        return null;
    }

    private static ResourceDictionary LoadTheme()
    {
        // These tests load the theme without constructing an Application (only one may
        // exist per AppDomain, and WpfViewSmokeTests owns that one). Registering the pack
        // scheme and naming the resource assembly is what an Application would otherwise
        // do, and resolves to the same assembly it would pick.
        if (!UriParser.IsKnownScheme("pack"))
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        }

        Application.ResourceAssembly ??= typeof(TelemetryChart).Assembly;

        return new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/BladeControl.UI;component/Themes/DarkTheme.xaml",
                UriKind.Absolute)
        };
    }

    private static void AssertBasedOn(ResourceDictionary theme, string compact, string expected)
    {
        var compactStyle = (Style)theme[compact];
        var expectedStyle = (Style)theme[expected];
        Assert.AreSame(
            expectedStyle,
            compactStyle.BasedOn,
            $"{compact} must be BasedOn {expected} so both surfaces share one identity.");
    }

    private static object? EffectiveSetterValue(Style? style, DependencyProperty property)
    {
        for (Style? current = style; current is not null; current = current.BasedOn)
        {
            foreach (SetterBase setterBase in current.Setters)
            {
                if (setterBase is Setter setter && setter.Property == property)
                {
                    return setter.Value;
                }
            }
        }

        return null;
    }

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Design-system test timed out.");
        Assert.IsNull(failure, failure?.ToString());
    }
}

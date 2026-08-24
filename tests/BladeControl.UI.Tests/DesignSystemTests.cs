using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        "TransparentBrush", "BorderBrush", "BorderStrongBrush", "DangerBorderBrush",
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

    /// <summary>
    /// Every control that draws its own chrome must carry an implicit style, so none of them
    /// falls back to the system default in a dark application.
    /// </summary>
    /// <remarks>
    /// WPF compound controls create several more controls inside their templates and popup
    /// trees. The failures caught here have all rendered as system-white chrome: scrollbar
    /// tracks, checkbox glyphs, tooltip and context-menu surfaces, the Expander disclosure
    /// button, the closed ComboBox, and the DataGrid editor and headers. Recolouring the outer
    /// owner cannot reach those parts, so this asserts a Template rather than merely a style.
    /// <para>ScrollViewer is deliberately absent from the list. It draws no chrome of its own:
    /// what a user sees are its ScrollBars, templated above, plus the small square where a
    /// vertical and a horizontal bar meet. Every ScrollViewer in this application sets
    /// <c>HorizontalScrollBarVisibility="Disabled"</c>, so that square is unreachable. A
    /// template was written for it and then removed — giving the bar its own grid column
    /// narrowed the content enough that a Diagnostics label ran into its own value, which is a
    /// visible defect traded for chrome nobody can see. If a surface ever scrolls horizontally,
    /// template it then, and check the layout when doing so.</para>
    /// </remarks>
    [TestMethod]
    public void EveryChromeBearingControlIsTemplatedAndNotLeftToSystemDefaults() =>
        OnStaThread(() =>
        {
            ResourceDictionary theme = LoadTheme();

            Type[] mustBeTemplated =
            [
                typeof(ScrollBar),
                typeof(CheckBox), typeof(Slider), typeof(ToggleButton),
                typeof(TextBox), typeof(ComboBox), typeof(ComboBoxItem), typeof(Expander),
                typeof(ToolTip), typeof(ContextMenu), typeof(MenuItem), typeof(Separator),
                typeof(DataGridColumnHeader), typeof(DataGridRowHeader), typeof(DataGridCell)
            ];

            foreach (Type control in mustBeTemplated)
            {
                var style = theme[control] as Style;
                Assert.IsNotNull(
                    style,
                    $"{control.Name} has no implicit style, so it renders with light system " +
                    "chrome against this theme's dark surfaces.");

                bool templated = style!.Setters.OfType<Setter>().Any(
                    setter => setter.Property == Control.TemplateProperty);
                Assert.IsTrue(
                    templated,
                    $"{control.Name} must be templated, not merely recoloured — the parts that " +
                    "read as bright holes in a dark surface are drawn by the default template.");
            }
        });

    /// <summary>
    /// The keyed checkbox variant must extend the themed one rather than replace it, so it
    /// keeps the dark box glyph and changes only what "subtle" means.
    /// </summary>
    [TestMethod]
    public void SubtleCheckBoxKeepsTheThemedBoxGlyph() => OnStaThread(() =>
    {
        ResourceDictionary theme = LoadTheme();

        var subtle = theme["SubtleCheckBoxStyle"] as Style;
        Assert.IsNotNull(subtle, "SubtleCheckBoxStyle must exist.");
        Assert.IsNotNull(
            subtle!.BasedOn,
            "SubtleCheckBoxStyle must be BasedOn the implicit CheckBox style. Standing alone " +
            "it silently drops the template and the system's white box comes back.");
        Assert.AreEqual(
            typeof(CheckBox),
            subtle.BasedOn!.TargetType,
            "SubtleCheckBoxStyle must extend the CheckBox style, not some other control's.");
    });

    /// <summary>
    /// The DataGrid editor must opt back into the implicit TextBox style because WPF assigns
    /// DataGridTextColumn.DefaultEditingElementStyle directly and otherwise skips it.
    /// </summary>
    /// <remarks>
    /// This catches the actual curve-editor failure: entering a cell created a stock white
    /// TextBox, and conversion failures used the stock square validation outline. A keyed style
    /// that copied only colours would still lose both templates, so the BasedOn link is the
    /// discriminating assertion.
    /// </remarks>
    [TestMethod]
    public void DataGridEditorVariantKeepsTheThemedTextBoxAndValidationChrome() =>
        OnStaThread(() =>
        {
            ResourceDictionary theme = LoadTheme();
            var textBox = (Style)theme[typeof(TextBox)];
            var editor = theme["DataGridEditorTextBoxStyle"] as Style;

            Assert.IsNotNull(editor, "The curve editor needs a keyed in-cell TextBox style.");
            Assert.AreSame(
                textBox,
                editor!.BasedOn,
                "DataGridEditorTextBoxStyle must extend the implicit TextBox style so WPF's " +
                "sealed light default cannot replace its template.");
            Assert.IsInstanceOfType<ControlTemplate>(
                EffectiveSetterValue(editor, Control.TemplateProperty),
                "The in-cell editor must retain the dark TextBox template.");
            Assert.IsInstanceOfType<ControlTemplate>(
                EffectiveSetterValue(editor, Validation.ErrorTemplateProperty),
                "Invalid numeric input must retain the themed validation outline.");
        });

    /// <summary>
    /// The actual TextBox fallback menu uses private framework subclasses, so implicit styles
    /// for ContextMenu and MenuItem cannot theme it. The TextBox must provide public controls
    /// explicitly, with the standard commands still routed to the edited field.
    /// </summary>
    [TestMethod]
    public void TextBoxRightClickMenuUsesThePublicThemedControlsAndKeepsCommandTargets() =>
        OnStaThread(() =>
        {
            ResourceDictionary theme = LoadTheme();
            var textBox = new TextBox { Text = "editable" };
            var window = new Window
            {
                Width = 260,
                Height = 120,
                Content = textBox,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false
            };
            window.Resources.MergedDictionaries.Add(theme);

            try
            {
                window.Show();
                window.UpdateLayout();

                ContextMenu? menu = textBox.ContextMenu;
                Assert.IsNotNull(
                    menu,
                    "A null ContextMenu lets WPF create its private, unthemeable editor menu.");
                Assert.AreEqual(
                    typeof(ContextMenu),
                    menu!.GetType(),
                    "The real editor menu must use the public ContextMenu type that the theme styles.");

                menu.PlacementTarget = textBox;
                menu.IsOpen = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.AreSame(
                    theme[typeof(ContextMenu)],
                    menu.Style,
                    "The opened editor menu must resolve the implicit dark ContextMenu style.");

                MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
                Assert.AreEqual(
                    3,
                    menu.Items.Count,
                    ".NET 8's stock plain-TextBox menu is exactly Cut, Copy and Paste, with no " +
                    "additional commands or separators.");
                CollectionAssert.AreEqual(
                    new object[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste },
                    items.Select(item => item.Command).ToArray(),
                    "Replacing WPF's private menu must retain the standard editing commands.");

                foreach (MenuItem item in items)
                {
                    Assert.AreEqual(
                        typeof(MenuItem),
                        item.GetType(),
                        "Editor commands must use the public MenuItem type that the theme styles.");
                    Assert.AreSame(
                        theme[typeof(MenuItem)],
                        item.Style,
                        "Every real editor command must resolve the implicit dark MenuItem style.");
                    Assert.AreSame(
                        textBox,
                        item.CommandTarget,
                        "Cut, Copy and Paste must still target the TextBox across the popup tree.");
                }

                menu.IsOpen = false;
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>
    /// ComboBox owns keyboard focus; its template ToggleButton is only a mouse hit target.
    /// Leaving that child focusable inserts a second Tab stop for every ComboBox.
    /// </summary>
    [TestMethod]
    public void ComboBoxTemplateContributesExactlyOneKeyboardTabStop() => OnStaThread(() =>
    {
        ResourceDictionary theme = LoadTheme();
        var before = new Button { Content = "Before" };
        var comboBox = new ComboBox
        {
            ItemsSource = new[] { "Alpha", "Beta" },
            SelectedIndex = 0
        };
        var after = new Button { Content = "After" };
        var panel = new StackPanel();
        _ = panel.Children.Add(before);
        _ = panel.Children.Add(comboBox);
        _ = panel.Children.Add(after);

        var window = new Window
        {
            Width = 280,
            Height = 180,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false
        };
        window.Resources.MergedDictionaries.Add(theme);

        try
        {
            window.Show();
            _ = window.Activate();
            window.UpdateLayout();
            _ = comboBox.ApplyTemplate();

            var toggle = comboBox.Template.FindName("DropDownToggle", comboBox) as ToggleButton;
            Assert.IsNotNull(toggle, "The ComboBox template must expose its drop-down toggle.");
            Assert.IsFalse(toggle!.Focusable, "The template toggle must not steal keyboard focus.");
            Assert.IsFalse(toggle.IsTabStop, "The template toggle must not add a second Tab stop.");
            Assert.AreEqual(
                ClickMode.Press,
                toggle.ClickMode,
                "The popup should open on the standard ComboBox press gesture.");

            Assert.IsTrue(before.Focus(), "The focus probe could not focus its leading control.");
            Assert.IsTrue(
                before.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),
                "Tab navigation could not move from the leading control.");
            Assert.AreSame(comboBox, Keyboard.FocusedElement, "The ComboBox must be the next Tab stop.");
            Assert.IsTrue(
                comboBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),
                "Tab navigation could not move past the ComboBox.");
            Assert.AreSame(
                after,
                Keyboard.FocusedElement,
                "Tab must leave the ComboBox in one step instead of stopping on its template child.");
        }
        finally
        {
            window.Close();
        }
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

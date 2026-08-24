using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;
using BladeControl.UI.Views;

namespace BladeControl.UI.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WpfViewSmokeTests
{
    [TestMethod]
    public void ShellAndEveryPageCreateTheirVisualTreesWithoutBindingFailures()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            BladeControl.UI.App? application = null;
            ShellViewModel? shell = null;
            BladeControl.UI.MainWindow? window = null;
            CompactControlViewModel? compactViewModel = null;
            BladeControl.UI.CompactControlWindow? compactWindow = null;
            try
            {
                application = new BladeControl.UI.App();
                application.InitializeComponent();
                var fake = new FakeRuntimeUiClient();
                var connection = new RuntimeConnection(fake, new ImmediateUiDispatcher());
                connection.PollOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                shell = new ShellViewModel(
                    connection,
                    new UiSettings { MinimizeToTray = false },
                    isDesignPreview: true,
                    _ => { });
                window = new BladeControl.UI.MainWindow(
                    shell,
                    new UiSettings { MinimizeToTray = false })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10_000,
                    Top = -10_000,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.Show();
                compactViewModel = new CompactControlViewModel(shell);
                compactWindow = new BladeControl.UI.CompactControlWindow(compactViewModel)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10_000,
                    Top = -10_000,
                    ShowActivated = false
                };
                compactWindow.Show();
                Assert.AreSame(shell.Connection, compactViewModel.Connection);
                compactWindow.Hide();
                Assert.AreEqual(0, fake.StopThermalRequestCount);
                compactWindow.Show();

                foreach (PageViewModel page in shell.Pages)
                {
                    shell.SelectedPage = page;
                    window.UpdateLayout();
                    application.Dispatcher.Invoke(
                        () => { },
                        DispatcherPriority.ApplicationIdle);

                    if (page is FansThermalViewModel)
                    {
                        AssertFansThermalEditorsUseTheThemedTextBox(window, application);
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.CloseExplicitly();
                compactWindow?.CloseExplicitly();
                compactViewModel?.Dispose();
                shell?.Dispose();
                application?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "WPF smoke test timed out.");
        Assert.IsNull(failure, failure?.ToString());
    }

    private static void AssertFansThermalEditorsUseTheThemedTextBox(
        BladeControl.UI.MainWindow window,
        BladeControl.UI.App application)
    {
        FansThermalView? view = FindVisualDescendant<FansThermalView>(window);
        Assert.IsNotNull(view, "Selecting the Fans & Thermal page must load its real compiled view.");

        DataGrid? curveEditor = FindVisualDescendant<DataGrid>(view!);
        Assert.IsNotNull(curveEditor, "The real Fans & Thermal view must contain its curve editor grid.");

        var expectedStyle = (Style)application.FindResource("DataGridEditorTextBoxStyle");
        DataGridTextColumn[] textColumns = curveEditor!.Columns.OfType<DataGridTextColumn>().ToArray();
        DataGridTextColumn[] editableColumns = textColumns.Where(column => !column.IsReadOnly).ToArray();
        DataGridTextColumn[] readOnlyColumns = textColumns.Where(column => column.IsReadOnly).ToArray();

        Assert.AreNotEqual(
            0,
            editableColumns.Length,
            "The real curve editor must expose editable text columns.");
        Assert.AreNotEqual(
            0,
            readOnlyColumns.Length,
            "The probe must include a read-only text column so it does not over-require editor styling.");

        foreach (DataGridTextColumn column in editableColumns)
        {
            Assert.AreSame(
                expectedStyle,
                column.EditingElementStyle,
                $"Editable column '{column.Header}' must opt into DataGridEditorTextBoxStyle; " +
                "DataGridTextColumn otherwise installs WPF's light default editor style.");
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            T? descendant = FindVisualDescendant<T>(VisualTreeHelper.GetChild(root, index));
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}

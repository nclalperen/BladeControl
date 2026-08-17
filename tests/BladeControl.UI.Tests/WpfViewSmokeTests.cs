using System.Windows;
using System.Windows.Threading;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

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
}

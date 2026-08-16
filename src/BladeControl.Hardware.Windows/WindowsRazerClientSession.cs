using BladeControl.Razer;

namespace BladeControl.Hardware.Windows;

public sealed class WindowsRazerClientSession : IDisposable
{
    private readonly WindowsRazerHidTransport _transport;

    private WindowsRazerClientSession(WindowsRazerHidTransport transport)
    {
        _transport = transport;
        Client = new RazerClient(transport);
    }

    public RazerClient Client { get; }

    public RazerDeviceInfo DeviceInfo => _transport.DeviceInfo;

    public static WindowsRazerClientSession Open()
    {
        WindowsRazerHidTransport transport = WindowsRazerHidTransport.Open();
        try
        {
            return new WindowsRazerClientSession(transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    public void Dispose() => _transport.Dispose();
}

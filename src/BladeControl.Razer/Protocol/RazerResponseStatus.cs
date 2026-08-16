namespace BladeControl.Razer.Protocol;

public enum RazerResponseStatus : byte
{
    NewCommand = 0x00,
    Busy = 0x01,
    Success = 0x02,
    Failure = 0x03,
    Timeout = 0x04,
    NotSupported = 0x05
}

internal static class RazerResponseStatusFormatter
{
    internal static string Format(byte status)
    {
        return status switch
        {
            (byte)RazerResponseStatus.NewCommand => "NewCommand",
            (byte)RazerResponseStatus.Busy => "Busy",
            (byte)RazerResponseStatus.Success => "Success",
            (byte)RazerResponseStatus.Failure => "Failure",
            (byte)RazerResponseStatus.Timeout => "Timeout",
            (byte)RazerResponseStatus.NotSupported => "NotSupported",
            _ => $"Unknown(0x{status:X2})"
        };
    }
}

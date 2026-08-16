namespace BladeControl.Razer;

internal interface IFanObservationDelay
{
    void Wait(TimeSpan duration);
}

internal sealed class ThreadFanObservationDelay : IFanObservationDelay
{
    public void Wait(TimeSpan duration)
    {
        Thread.Sleep(duration);
    }
}

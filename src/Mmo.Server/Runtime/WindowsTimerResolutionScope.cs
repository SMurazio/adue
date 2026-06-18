using System.Runtime.InteropServices;

namespace Mmo.Server.Runtime;

internal sealed class WindowsTimerResolutionScope : IDisposable
{
    private const uint TargetPeriodMs = 1;
    private readonly bool _active;

    private WindowsTimerResolutionScope(bool active, uint result)
    {
        _active = active;
        BeginResult = result;
    }

    public bool IsActive => _active;

    public uint BeginResult { get; }

    public static WindowsTimerResolutionScope Begin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsTimerResolutionScope(false, 0);
        }

        try
        {
            var result = TimeBeginPeriod(TargetPeriodMs);
            return new WindowsTimerResolutionScope(result == 0, result);
        }
        catch (DllNotFoundException)
        {
            return new WindowsTimerResolutionScope(false, uint.MaxValue);
        }
        catch (EntryPointNotFoundException)
        {
            return new WindowsTimerResolutionScope(false, uint.MaxValue);
        }
    }

    public void Dispose()
    {
        if (_active && OperatingSystem.IsWindows())
        {
            TimeEndPeriod(TargetPeriodMs);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint periodMs);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint periodMs);
}

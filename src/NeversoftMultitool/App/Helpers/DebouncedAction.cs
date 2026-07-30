using Microsoft.UI.Dispatching;

namespace NeversoftMultitool;

/// <summary>
///     Coalesces a burst of calls into one deferred execution on the owning
///     <see cref="DispatcherQueue" />. Used to persist slider values once per
///     gesture instead of once per tick — a <c>UserSettings</c> write hits the
///     registry and fans out the global <c>Changed</c> event, whose listeners
///     re-probe the Blender install locations and rebuild the animation list,
///     so per-tick writes made volume drags stutter.
/// </summary>
internal sealed class DebouncedAction
{
    private readonly DispatcherQueueTimer _timer;

    internal DebouncedAction(DispatcherQueue dispatcher, TimeSpan delay, Action action)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = delay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => action();
    }

    internal void Invoke()
    {
        _timer.Stop();
        _timer.Start();
    }
}

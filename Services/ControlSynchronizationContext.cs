using System.Windows.Forms;

namespace LocalSendWinForms.Services;

/// <summary>
/// A <see cref="SynchronizationContext"/> that marshals callbacks onto the thread which
/// owns the wrapped control's handle.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="SynchronizationContext.Current"/> is <c>null</c> while
/// <c>Application.Run()</c> has not started, i.e. at the moment <c>AppHost</c> is
/// constructed. Capturing it there yields a plain <see cref="SynchronizationContext"/>,
/// whose <see cref="SynchronizationContext.Post"/> simply queues work onto the thread
/// pool. Any dialog created through that path ends up owned by a worker thread that never
/// pumps messages: it renders, but never responds to input.
/// </para>
/// <para>
/// Routing through <see cref="Control.BeginInvoke"/> makes the UI thread explicit and does
/// not depend on when the control's handle comes into existence.
/// </para>
/// </remarks>
public sealed class ControlSynchronizationContext : SynchronizationContext
{
    private readonly Control _target;

    public ControlSynchronizationContext(Control target) =>
        _target = target ?? throw new ArgumentNullException(nameof(target));

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (d is null) return;
        if (_target.IsDisposed || _target.Disposing) return;

        try { _target.BeginInvoke(d, state); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { /* handle gone mid-post */ }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (d is null) return;
        if (_target.IsDisposed || _target.Disposing) return;

        try { _target.Invoke(d, state); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}

using System.Collections.Concurrent;

namespace UVCControl.Core.Threading;

/// <summary>
/// Runs work items one at a time, in order, on a dedicated background thread.
/// Keeps device I/O off the UI thread and gives thread-affine native handles (COM) a stable home.
/// </summary>
public sealed class SerialExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SerialExecutor(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsCurrentThread)
        {
            Execute(func, tcs);
            return tcs.Task;
        }
        try
        {
            _queue.Add(() => Execute(func, tcs));
        }
        catch (InvalidOperationException)
        {
            tcs.SetException(new ObjectDisposedException(nameof(SerialExecutor)));
        }
        return tcs.Task;
    }

    private static void Execute<T>(Func<T> func, TaskCompletionSource<T> tcs)
    {
        try
        {
            tcs.SetResult(func());
        }
        catch (Exception e)
        {
            tcs.SetException(e);
        }
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable()) action();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(5));
    }
}

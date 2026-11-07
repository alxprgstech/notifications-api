namespace Notify;

// Bounded call shutdown: signaling may be slow or its peer may be stuck, but media cleanup must
// still happen. The caller supplies already captured side effects; this helper never throws.
public static class CallShutdown
{
    public static async Task RunAsync(Func<CancellationToken, Task> signalAgent, Func<CancellationToken, Task> signalPhone, Func<Task> closePhone, Func<Task> closeAgent, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        Task agentTask, phoneTask;
        try { agentTask = signalAgent(cts.Token); } catch { agentTask = Task.CompletedTask; }
        try { phoneTask = signalPhone(cts.Token); } catch { phoneTask = Task.CompletedTask; }
        try
        {
            await Task.WhenAll(agentTask, phoneTask).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            cts.Cancel();
            Observe(agentTask); Observe(phoneTask);
            await Task.WhenAll(Close(closePhone), Close(closeAgent));
        }
    }
    private static async Task Close(Func<Task> close) { try { await close(); } catch { } }
    private static void Observe(Task task)
    {
        if (task.IsCompleted) { _ = task.Exception; return; }
        _ = task.ContinueWith(completed => _ = completed.Exception, TaskScheduler.Default);
    }
}

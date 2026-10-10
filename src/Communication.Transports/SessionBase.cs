using Communication.Core;

namespace Communication.Transports;

public abstract class SessionBase : ICommunicationSession
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _lifetimeLock = new();
    private CancellationTokenSource? _lifetime;
    private Task _receiveTask = Task.CompletedTask;
    private int _state;
    private volatile bool _disposed;
    private long _tx, _rx, _txCount, _rxCount;
    protected CancellationToken Lifetime
    {
        get
        {
            lock (_lifetimeLock)
                return _lifetime?.Token ?? new CancellationToken(true);
        }
    }
    protected SemaphoreSlim SendGate { get; } = new(1, 1);
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);
    public SessionStatistics Statistics => new(Interlocked.Read(ref _tx), Interlocked.Read(ref _rx),
        Interlocked.Read(ref _txCount), Interlocked.Read(ref _rxCount));
    public event Action<ConnectionChange>? StateChanged;
    public event Action<TrafficRecord>? Traffic;

    protected void SetState(ConnectionState state, string detail)
    {
        Volatile.Write(ref _state, (int)state);
        StateChanged?.Invoke(new(state, detail));
    }

    protected void Publish(TrafficDirection direction, PayloadKind kind, ReadOnlySpan<byte> bytes, string endpoint)
    {
        if (direction == TrafficDirection.Tx)
        {
            Interlocked.Add(ref _tx, bytes.Length);
            Interlocked.Increment(ref _txCount);
        }
        else
        {
            Interlocked.Add(ref _rx, bytes.Length);
            Interlocked.Increment(ref _rxCount);
        }
        Traffic?.Invoke(new(DateTimeOffset.Now, direction, kind, bytes.ToArray(), endpoint));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State is ConnectionState.Connected or ConnectionState.Connecting)
                return;
            await _receiveTask.ConfigureAwait(false);
            // Do not replace transport fields while an old write is still unwinding.
            await SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_lifetimeLock)
                {
                    _lifetime?.Dispose();
                    _lifetime = new CancellationTokenSource();
                }
            }
            finally
            {
                SendGate.Release();
            }

            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
            opening.CancelAfter(TimeSpan.FromSeconds(10));
            SetState(ConnectionState.Connecting, "正在建立连接");
            try
            {
                await OpenAsync(opening.Token).ConfigureAwait(false);
                opening.Token.ThrowIfCancellationRequested();
                SetState(ConnectionState.Connected, "连接已就绪");
                _receiveTask = Task.Run(ReceiveGuardedAsync);
            }
            catch (Exception ex)
            {
                Close();
                CancelLifetime();
                SetState(ex is OperationCanceledException ? ConnectionState.Disconnected : ConnectionState.Faulted, ex.Message);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }

    }

    private async Task ReceiveGuardedAsync()
    {
        Exception? failure = null;
        try
        {
            await ReceiveAsync(Lifetime).ConfigureAwait(false);
        }
        catch (Exception ex) when (Lifetime.IsCancellationRequested)
        {
            _ = ex;
        }

        catch (Exception ex)
        {
            failure = ex;
        }

        finally
        {
            var requested = Lifetime.IsCancellationRequested;
            CancelLifetime();
            Close();
            if (!requested)
                SetState(failure is null ? ConnectionState.Disconnected : ConnectionState.Faulted,
                failure?.Message ?? "远端已关闭连接");
        }
    }

    public async Task DisconnectAsync()
    {
        // Cancel opening before waiting for lifecycle ownership.
        CancelLifetime();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Disconnected)
                return;
            SetState(ConnectionState.Disconnecting, "正在断开");
            CancelLifetime();
            Close();
            await _receiveTask.ConfigureAwait(false);
            await SendGate.WaitAsync().ConfigureAwait(false);
            SendGate.Release();
            SetState(ConnectionState.Disconnected, "已主动断开");
        }
        finally
        {
            _lifecycle.Release();
        }

    }

    protected void EnsureConnected()
    {
        if (State != ConnectionState.Connected)
            throw new InvalidOperationException("连接尚未就绪。");
    }
    private void CancelLifetime()
    {
        lock (_lifetimeLock)
            _lifetime?.Cancel();
    }
    protected void FailWrite(Exception exception)
    {
        if (Lifetime.IsCancellationRequested)
            return;
        CancelLifetime();
        Close();
        SetState(ConnectionState.Faulted, $"发送未完成，连接已关闭：{exception.Message}");
    }
    protected abstract Task OpenAsync(CancellationToken token);
    protected abstract Task ReceiveAsync(CancellationToken token);
    protected abstract void Close();
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        // Gates remain valid for operations that raced with shutdown.
    }
}

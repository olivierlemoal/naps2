using System.Threading;
using NAPS2.Scan.Internal;

namespace NAPS2.Scan;

/// <summary>
/// Listens for physical button presses on the device of a profile with ScanOnDeviceButton enabled (SANE only).
/// Listening is suspended while other operations use the device.
/// </summary>
public class DeviceButtonListener
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    private readonly IProfileManager _profileManager;
    private readonly ScanningContext _scanningContext;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private bool _started;
    private int _suspendCount;
    private string? _listeningDeviceId;
    private CancellationTokenSource? _listenCts;
    private Task _listenTask = Task.CompletedTask;

    public DeviceButtonListener(IProfileManager profileManager, ScanningContext scanningContext)
    {
        _profileManager = profileManager;
        _scanningContext = scanningContext;
    }

    /// <summary>
    /// Raised on the UI thread with the profile to scan with.
    /// </summary>
    public event EventHandler<ScanProfile>? ButtonPressed;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _profileManager.ProfilesUpdated += (_, _) => Update().AssertNoAwait();
        Update().AssertNoAwait();
    }

    public void Stop()
    {
        _started = false;
        _listenCts?.Cancel();
    }

    /// <summary>
    /// Stops listening until the returned object is disposed, so the device is free for other operations.
    /// </summary>
    public async Task<IDisposable> Suspend()
    {
        Interlocked.Increment(ref _suspendCount);
        await Update().ConfigureAwait(false);
        return new Suspension(this);
    }

    private void Resume()
    {
        Interlocked.Decrement(ref _suspendCount);
        Update().AssertNoAwait();
    }

    private async Task Update()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var device = _started && Volatile.Read(ref _suspendCount) == 0
                ? GetListeningProfile()?.Device?.ToScanDevice(Driver.Sane)
                : null;
            if (device?.ID == _listeningDeviceId) return;

            _listenCts?.Cancel();
            await _listenTask.ConfigureAwait(false);
            _listeningDeviceId = device?.ID;
            _listenCts = device != null ? new CancellationTokenSource() : null;
            _listenTask = device != null ? Listen(device, _listenCts!.Token) : Task.CompletedTask;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private ScanProfile? GetListeningProfile()
    {
        var validator = new ScanOptionsValidator();
        bool CanListen(ScanProfile profile) =>
            profile.ScanOnDeviceButton &&
            !string.IsNullOrEmpty(profile.Device?.ID) &&
            validator.ValidateDriver(ScanPerformer.ParseDriver(profile.DriverName)) == Driver.Sane;

        var defaultProfile = _profileManager.DefaultProfile;
        return defaultProfile != null && CanListen(defaultProfile)
            ? defaultProfile
            : _profileManager.Profiles.FirstOrDefault(CanListen);
    }

    private async Task Listen(ScanDevice device, CancellationToken cancelToken)
    {
        var options = new ScanOptions { Driver = Driver.Sane, Device = device };
        var controller = new ScanController(_scanningContext);
        while (!cancelToken.IsCancellationRequested)
        {
            try
            {
                await controller.WaitForButtonPress(options, cancelToken).ConfigureAwait(false);
                if (cancelToken.IsCancellationRequested) return;
                Invoker.Current.InvokeDispatch(OnButtonPressed);
                await Task.Delay(DebounceDelay, cancelToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.ErrorException("Error waiting for a scanner button press", ex);
                try
                {
                    await Task.Delay(RetryDelay, cancelToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void OnButtonPressed()
    {
        if (GetListeningProfile() is { } profile)
        {
            ButtonPressed?.Invoke(this, profile);
        }
    }

    private class Suspension(DeviceButtonListener owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Resume();
            }
        }
    }
}

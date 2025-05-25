using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Message.Encoder;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Platform.Interfaces;
using GalaxyBudsClient.Platform.Model;
using GalaxyBudsClient.Platform.Stubs;
using GalaxyBudsClient.Scripting;
using GalaxyBudsClient.Utils;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Task = System.Threading.Tasks.Task;

namespace GalaxyBudsClient.Platform;

public sealed class BluetoothImpl : ReactiveObject, IDisposable
{ 
    private static readonly object Padlock = new();
    private static BluetoothImpl? _instance;
    public static BluetoothImpl Instance
    {
        get
        {
            lock (Padlock)
            {
                return _instance ??= new BluetoothImpl();
            }
        }
    }

    public static void Reallocate()
    {
        Log.Debug("BluetoothImpl: Reallocating");
        _instance?.Dispose();
        DeviceMessageCache.Instance.Clear();
        _instance = null;
        _instance = new BluetoothImpl();
    }

    private readonly IBluetoothService _backend;
    
    public event EventHandler? Connected;
    public event EventHandler? Connecting;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<SppMessage>? MessageReceived;
    public event EventHandler<InvalidPacketException>? InvalidDataReceived;
    public event EventHandler<byte[]>? NewDataReceived;
    public event EventHandler<BluetoothException>? BluetoothError;
    
    public event EventHandler? ConnectedAlternative;
    public event EventHandler? ConnectingAlternative;
    public event EventHandler<string>? DisconnectedAlternative;
    public event EventHandler<SppAlternativeMessage>? MessageReceivedAlternative;
    public event EventHandler<InvalidPacketException>? InvalidDataReceivedAlternative;
    public event EventHandler<byte[]>? NewDataReceivedAlternative;
    public event EventHandler<BluetoothException>? BluetoothErrorAlternative;
    [Reactive] public bool IsConnectedAlternative { private set; get; }

    
    public Models CurrentModel => Device.Current?.Model ?? Models.NULL;
    public IDeviceSpec DeviceSpec => DeviceSpecHelper.FindByModel(CurrentModel) ?? new StubDeviceSpec();
    public static bool HasValidDevice => Settings.Data.Devices.Count > 0 && 
                                         Settings.Data.Devices.Any(x => x.Model != Models.NULL);
    
    /// <summary>
    /// Connection state manager for tracking and validating connection state transitions
    /// </summary>
    public ConnectionStateManager ConnectionStateManager { get; } = new ConnectionStateManager();
    
    /// <summary>
    /// Connection diagnostics for monitoring connection health and troubleshooting
    /// </summary>
    public ConnectionDiagnostics Diagnostics => ConnectionDiagnostics.Instance;
    
    [Reactive] public string DeviceName { private set; get; } = "Galaxy Buds";
    [Reactive] public bool IsConnected { private set; get; }
    [Reactive] public string LastErrorMessage { private set; get; } = string.Empty;
    [Reactive] public bool SuppressDisconnectionEvents { set; get; }
    [Reactive] public bool ShowDummyDevices { set; get; }

    public DeviceManager Device { get; } = new();
    
    [Reactive] public bool AlternativeModeEnabled { private set; get; }
    private readonly List<byte> _incomingData = [];
    private static readonly ConcurrentQueue<byte[]> IncomingQueue = new();
    private readonly CancellationTokenSource _loopCancelSource = new();
    private CancellationTokenSource _connectCancelSource = new();
    private readonly Task? _loop;
    // There is exactly one feature which requires connecting on a different UUID.
    
    /// <summary>
    /// Checks if Bluetooth is enabled on the device
    /// </summary>
    /// <returns>True if Bluetooth is enabled, false otherwise</returns>
    public bool IsBluetoothEnabled()
    {
        try
        {
            return _backend.IsBluetoothEnabled();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BluetoothImpl: Error checking Bluetooth status");
            return false;
        }
    }
    
    /// <summary>
    /// Attempts to enable Bluetooth on the device
    /// </summary>
    /// <returns>True if Bluetooth was successfully enabled or was already enabled, false if it failed or is not supported</returns>
    public async Task<bool> EnableBluetoothAsync()
    {
        try
        {
            return await _backend.EnableBluetoothAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BluetoothImpl: Error enabling Bluetooth");
            return false;
        }
    }

    private BluetoothImpl()
    {
        IBluetoothService? backend = null;
        
        try
        {
            // We don't want to initialize the backend in design mode. It would conflict with the actual application.
            if (!Design.IsDesignMode)
            {
                backend = PlatformImpl.Creator.CreateBluetoothService();
            }
        }
        catch (PlatformNotSupportedException)
        {
            Log.Error("BluetoothImpl: Critical error while preparing bluetooth backend");
        }

        if (backend == null)
        {
            Log.Warning("BluetoothImpl: Using Dummy.BluetoothService");
            backend = new DummyBluetoothService();
        }
        
        _backend = backend;
        _loop = Task.Run(DataConsumerLoop, _loopCancelSource.Token);
            
        _backend.Connecting += (_, _) =>
        {
            if (AlternativeModeEnabled)
                ConnectingAlternative?.Invoke(this, EventArgs.Empty);
            else
                Connecting?.Invoke(this, EventArgs.Empty);
        };
        _backend.BluetoothErrorAsync += (_, exception) => OnBluetoothError(exception); 
        _backend.NewDataAvailable += OnNewDataAvailable;
        _backend.RfcommConnected += OnRfcommConnected;
        _backend.Disconnected += OnDisconnected;

        EventDispatcher.Instance.EventReceived += OnEventReceived;
        MessageReceived += SppMessageReceiver.Instance.MessageReceiver;
        InvalidDataReceived += OnInvalidDataReceived;
    }

    public bool SetAltMode(bool altMode)
    {
        if (AlternativeModeEnabled == altMode)
        {
            return true;
        }

        if (!AlternativeModeEnabled && IsConnected)
        {
            Log.Error("BluetoothImpl: cannot enable alt mode while buds connected");
            return false;
        }
        if (AlternativeModeEnabled && IsConnectedAlternative)
        {
            Log.Error("BluetoothImpl: cannot disable alt mode while buds alt connected");
            return false;
        }

        AlternativeModeEnabled = altMode;
        return true;
    }

    public async void Dispose()
    {
        try
        {
            await _backend.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BluetoothImpl.Dispose: Error while disconnecting");
        }

        MessageReceived -= SppMessageReceiver.Instance.MessageReceiver;
        EventDispatcher.Instance.EventReceived -= OnEventReceived;
        
        await _loopCancelSource.CancelAsync();
        await Task.Delay(50);

        try
        {
            _loop?.Dispose();
            _loopCancelSource.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BluetoothImpl.Dispose: Error while disposing children");
        }
    }
    
    private async void OnEventReceived(Event e, object? arg)
    {
        if (e == Event.Connect && !IsConnected)
        {
            await ConnectAsync();
        }
    }
    
    private void OnInvalidDataReceived(object? sender, InvalidPacketException e)
    {
        LastErrorMessage = e.ErrorCode.ToString();
        if (IsConnected)
        {
            _ = DisconnectAsync()
                .ContinueWith(_ => Task.Delay(500))
                .ContinueWith(_ => ConnectAsync());
        }
    }

    private void OnBluetoothError(BluetoothException exception)
    {
        if (AlternativeModeEnabled)
        {
            LastErrorMessage = exception.ErrorMessage ?? exception.Message;
            IsConnectedAlternative = false;
            BluetoothErrorAlternative?.Invoke(this, exception);
            return;
        }
        if (SuppressDisconnectionEvents) 
            return;
        
        LastErrorMessage = exception.ErrorMessage ?? exception.Message;
        IsConnected = false;
        BluetoothError?.Invoke(this, exception);
        DeviceMessageCache.Instance.Clear();
    }
        
    /// <summary>
    /// Fetches a list of available Bluetooth devices.
    /// </summary>
    /// <exception cref="BluetoothException">Thrown if the Bluetooth is temporarily unavailable</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown if the Bluetooth is permanently unavailable</exception>
    public async Task<IEnumerable<BluetoothDevice>> GetDevicesAsync()
    {
        if (ShowDummyDevices)
        {
            return (await _backend.GetDevicesAsync()).Concat(BluetoothDevice.DummyDevices());
        }
        return await _backend.GetDevicesAsync();
    }

    private async Task<string> GetDeviceNameAsync()
    {
        var fallbackName = CurrentModel.GetModelMetadataAttribute()?.Name ?? Strings.Unknown;
        try
        {
            var devices = await _backend.GetDevicesAsync();
            var device = devices.FirstOrDefault(d => d.Address == Device.Current?.MacAddress);
            return device?.Name ?? fallbackName;
        }
        catch (BluetoothException ex)
        {
            Log.Error(ex, "BluetoothImpl.GetDeviceName: Error while fetching device name");
            return fallbackName;
        }
    }

    public async Task<bool> ConnectAsync(Device? device = null, bool alternative = false)
    {
        // Record connection attempt in diagnostics
        Diagnostics.RecordConnectionAttempt();
        
        // Validate connection mode
        if (alternative != AlternativeModeEnabled)
        {
            Log.Error("BluetoothImpl: Connection attempt in wrong mode {Alternative}", alternative);
            Diagnostics.RecordFailedConnection("Connection attempt in wrong mode");
            return false;
        }
        
        // Check if we're already in a connecting state
        if (ConnectionStateManager.CurrentState == ConnectionStates.Connecting ||
            ConnectionStateManager.CurrentState == ConnectionStates.Reconnecting)
        {
            Log.Warning("BluetoothImpl: Connection already in progress, state: {State}", 
                ConnectionStateManager.CurrentState);
            return false;
        }
        
        // Update connection state
        ConnectionStateManager.SetConnecting();
        
        // Create new cancellation token source if the previous one has already been used
        if(_connectCancelSource.IsCancellationRequested)
            _connectCancelSource = new CancellationTokenSource();
        
        // Add connection timeout
        _connectCancelSource.CancelAfter(TimeSpan.FromSeconds(30));
        
        device ??= Device.Current;

        if (!HasValidDevice || device == null)
        {
            Log.Error("BluetoothImpl: Connection attempt without valid device");
            ConnectionStateManager.SetError("No valid device configured");
            Diagnostics.RecordFailedConnection("No valid device configured");
            return false;
        }
        
        // Check if already connected in the requested mode
        if ((alternative && IsConnectedAlternative) || (!alternative && IsConnected))
        {
            Log.Information("BluetoothImpl: Already connected in the requested mode");
            ConnectionStateManager.SetConnected();
            return true;
        }
        
        // Trigger connecting event
        if (alternative)
            ConnectingAlternative?.Invoke(this, EventArgs.Empty);
        else
            Connecting?.Invoke(this, EventArgs.Empty);
        
        /* Load from configuration */
        int retryCount = 0;
        const int maxRetries = 3;
        TimeSpan delay = TimeSpan.FromMilliseconds(500);
        
        while (retryCount < maxRetries)
        {
            try
            {
                var uuid = AlternativeModeEnabled ? Uuids.SmepSpp.ToString() : DeviceSpec.ServiceUuid.ToString();
                if (uuid == null)
                {
                    var ex = new BluetoothException(BluetoothException.ErrorCodes.UnsupportedDevice,
                        "BluetoothImpl: Connection attempt without valid UUID (alt mode enabled but UUID unset?)");
                    
                    ConnectionStateManager.SetError(ex.Message);
                    Diagnostics.RecordFailedConnection(ex.Message);
                    OnBluetoothError(ex);
                    return false;
                }
                
                // Validate device name before connection
                try
                {
                    DeviceName = await GetDeviceNameAsync();
                    device.Name = DeviceName;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "BluetoothImpl: Failed to get device name, using default");
                    DeviceName = device.Name ?? "Galaxy Buds";
                }
                
                Log.Information("BluetoothImpl: Connecting to {DeviceName} ({MacAddress}), attempt {RetryCount}/{MaxRetries}", 
                    DeviceName, device.MacAddress, retryCount + 1, maxRetries);
                
                await _backend.ConnectAsync(device.MacAddress, uuid, _connectCancelSource.Token);
                
                // Verify connection was successful
                if (_backend.IsStreamConnected)
                {
                    Log.Information("BluetoothImpl: Successfully connected to {DeviceName}", DeviceName);
                    
                    // Update connection state
                    ConnectionStateManager.SetConnected();
                    
                    // Record successful connection in diagnostics
                    Diagnostics.RecordSuccessfulConnection(DeviceName);
                    
                    return true;
                }
                
                // If we reach here, connection was not successful despite no exception
                var connEx = new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, 
                    "Connection appeared successful but stream is not connected");
                
                ConnectionStateManager.SetError(connEx.Message);
                Diagnostics.RecordFailedConnection(connEx.Message);
                throw connEx;
            }
            catch (BluetoothException ex)
            {
                retryCount++;
                if (retryCount >= maxRetries)
                {
                    Log.Error(ex, "BluetoothImpl: Connection failed after {MaxRetries} attempts", maxRetries);
                    ConnectionStateManager.SetError($"Connection failed after {maxRetries} attempts: {ex.Message}");
                    Diagnostics.RecordFailedConnection(ex.Message);
                    OnBluetoothError(ex);
                    return false;
                }
                
                Log.Warning(ex, "BluetoothImpl: Connection attempt {RetryCount} failed, retrying in {Delay}ms", 
                    retryCount, delay.TotalMilliseconds);
                
                // Exponential backoff
                await Task.Delay(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
            catch (TaskCanceledException)
            {
                Log.Warning("BluetoothImpl: Connection task cancelled");
                ConnectionStateManager.SetError("Connection task cancelled");
                Diagnostics.RecordFailedConnection("Connection task cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BluetoothImpl: Unexpected error during connection");
                var btEx = new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, ex.Message);
                ConnectionStateManager.SetError($"Unexpected error: {ex.Message}");
                Diagnostics.RecordFailedConnection(ex.Message);
                OnBluetoothError(btEx);
                return false;
            }
        }
        
        // If we get here, all retries failed
        ConnectionStateManager.SetError("Connection failed after all retry attempts");
        Diagnostics.RecordFailedConnection("Connection failed after all retry attempts");
        return false;
    }

    public async Task DisconnectAsync(bool alternative = false)
    {
        // Update connection state
        if (!ConnectionStateManager.SetDisconnecting())
        {
            Log.Warning("BluetoothImpl: Invalid state transition to Disconnecting from {State}", 
                ConnectionStateManager.CurrentState);
        }
        
        // Record disconnection in diagnostics
        Diagnostics.RecordDisconnection("User requested disconnect");
        
        if (!alternative && AlternativeModeEnabled)
        {
            Disconnected?.Invoke(this, "User requested disconnect while alt mode enabled");
            IsConnected = false;
            ConnectionStateManager.SetDisconnected();
            return;
        }
        if (alternative && !AlternativeModeEnabled)
        {
            Disconnected?.Invoke(this, "User requested alt disconnect while alt mode disabled");
            IsConnectedAlternative = false;
            ConnectionStateManager.SetDisconnected();
            return;
        }
        
        // Create a timeout for the disconnect operation
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        try
        {
            // Cancel any ongoing connection attempt
            try
            {
                await _connectCancelSource.CancelAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BluetoothImpl: Error while cancelling connection attempt");
            }
            
            // Clear any pending data in the queue
            lock (IncomingQueue)
            {
                while (IncomingQueue.TryDequeue(out _)) { }
            }

            // Attempt to disconnect with timeout
            var disconnectTask = _backend.DisconnectAsync();
            
            // Wait for disconnect or timeout
            if (await Task.WhenAny(disconnectTask, Task.Delay(5000, timeoutCts.Token)) != disconnectTask)
            {
                Log.Warning("BluetoothImpl: Disconnect operation timed out after 5 seconds");
            }
            
            // Update state and notify regardless of timeout
            if (alternative)
            {
                IsConnectedAlternative = false;
                DisconnectedAlternative?.Invoke(this, "User requested disconnect");
            }
            else
            {
                IsConnected = false;
                Disconnected?.Invoke(this, "User requested disconnect");
            }
            
            // Update connection state
            ConnectionStateManager.SetDisconnected();
            
            // Stop heartbeat monitoring
            Diagnostics.StopHeartbeat();
            
            LastErrorMessage = string.Empty;
            
            // Ensure we're really disconnected
            if (_backend.IsStreamConnected)
            {
                Log.Warning("BluetoothImpl: Backend reports still connected after disconnect attempt");
                // Force disconnect by recreating the backend if possible
                try
                {
                    if (!Design.IsDesignMode)
                    {
                        var newBackend = PlatformImpl.Creator.CreateBluetoothService();
                        if (newBackend != null)
                        {
                            // Transfer event handlers to new backend
                            newBackend.Connecting += (_, _) => _backend.Connecting?.Invoke(_, _);
                            newBackend.BluetoothErrorAsync += (_, exception) => _backend.BluetoothErrorAsync?.Invoke(_, exception);
                            newBackend.NewDataAvailable += (_, data) => _backend.NewDataAvailable?.Invoke(_, data);
                            newBackend.RfcommConnected += (_, _) => _backend.RfcommConnected?.Invoke(_, _);
                            newBackend.Disconnected += (_, reason) => _backend.Disconnected?.Invoke(_, reason);
                            
                            _backend = newBackend;
                            Log.Information("BluetoothImpl: Backend recreated after disconnect issues");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "BluetoothImpl: Failed to recreate backend after disconnect issues");
                }
            }
        }
        catch (BluetoothException ex)
        {
            Log.Error(ex, "BluetoothImpl: Error during disconnect");
            OnBluetoothError(ex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BluetoothImpl: Unexpected error during disconnect");
            OnBluetoothError(new BluetoothException(BluetoothException.ErrorCodes.Unknown, ex.Message));
        }
        finally
        {
            // Ensure connection state is updated even if an exception occurred
            if (alternative)
            {
                IsConnectedAlternative = false;
            }
            else
            {
                IsConnected = false;
            }
        }
    }

    public async Task SendAsync(SppMessage msg)
    {
        // Validate connection mode
        if (AlternativeModeEnabled)
        {
            Log.Warning("BluetoothImpl: Attempted to send message in alternative mode");
            return;
        }
        
        // Validate connection state
        if (!IsConnected)
        {
            Log.Warning("BluetoothImpl: Attempted to send message while disconnected");
            return;
        }
        
        // Validate backend connection
        if (!_backend.IsStreamConnected)
        {
            Log.Warning("BluetoothImpl: Backend reports disconnected but IsConnected is true, fixing state");
            IsConnected = false;
            OnBluetoothError(new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, 
                "Connection state mismatch detected"));
            return;
        }

        // Create a timeout for the send operation
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        try
        {
            Log.Verbose("<< Outgoing: {Msg}", msg);
            
            // Apply message hooks
            foreach(var hook in ScriptManager.Instance.MessageHooks)
            {
                hook.OnMessageSend(ref msg);
            }

            // Encode the message
            var raw = msg.Encode(false);
            
            // Apply raw stream hooks
            foreach(var hook in ScriptManager.Instance.RawStreamHooks)
            {
                hook.OnRawDataSend(ref raw);
            }
            
            // Send with timeout
            var sendTask = _backend.SendAsync(raw);
            
            if (await Task.WhenAny(sendTask, Task.Delay(5000, timeoutCts.Token)) != sendTask)
            {
                throw new BluetoothException(BluetoothException.ErrorCodes.TimedOut, 
                    "Send operation timed out after 5 seconds");
            }
            
            // Wait for the actual task to complete
            await sendTask;
        }
        catch (BluetoothException ex)
        {
            Log.Error(ex, "BluetoothImpl: Error sending message {MsgId}", msg.Id);
            OnBluetoothError(ex);
            
            // Attempt to reconnect if we get a send failure
            if (ex.ErrorCode == BluetoothException.ErrorCodes.SendFailed)
            {
                Log.Information("BluetoothImpl: Attempting to reconnect after send failure");
                _ = Task.Run(async () => {
                    await DisconnectAsync();
                    await Task.Delay(1000);
                    await ConnectAsync();
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BluetoothImpl: Unexpected error sending message {MsgId}", msg.Id);
            OnBluetoothError(new BluetoothException(BluetoothException.ErrorCodes.SendFailed, ex.Message));
        }
    }

    public async Task SendAltAsync(SppAlternativeMessage msg)
    {
        // Validate connection mode
        if (!AlternativeModeEnabled)
        {
            Log.Warning("BluetoothImpl: Attempted to send alternative message in normal mode");
            return;
        }
        
        // Validate connection state
        if (!IsConnectedAlternative)
        {
            Log.Warning("BluetoothImpl: Attempted to send alternative message while disconnected");
            return;
        }
        
        // Validate backend connection
        if (!_backend.IsStreamConnected)
        {
            Log.Warning("BluetoothImpl: Backend reports disconnected but IsConnectedAlternative is true, fixing state");
            IsConnectedAlternative = false;
            OnBluetoothError(new BluetoothException(BluetoothException.ErrorCodes.ConnectFailed, 
                "Alternative connection state mismatch detected"));
            return;
        }
        
        // Create a timeout for the send operation
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        try
        {
            // Encode the message
            var data = msg.Msg.Encode(true);
            Log.Verbose("<< Outgoing (alt): {Msg}", msg);
            
            // Send with timeout
            var sendTask = _backend.SendAsync(data);
            
            if (await Task.WhenAny(sendTask, Task.Delay(5000, timeoutCts.Token)) != sendTask)
            {
                throw new BluetoothException(BluetoothException.ErrorCodes.TimedOut, 
                    "Alternative send operation timed out after 5 seconds");
            }
            
            // Wait for the actual task to complete
            await sendTask;
        }
        catch (BluetoothException ex)
        {
            Log.Error(ex, "BluetoothImpl: Error sending alternative message");
            OnBluetoothError(ex);
            
            // Attempt to reconnect if we get a send failure
            if (ex.ErrorCode == BluetoothException.ErrorCodes.SendFailed)
            {
                Log.Information("BluetoothImpl: Attempting to reconnect after alternative send failure");
                _ = Task.Run(async () => {
                    await DisconnectAsync(true);
                    await Task.Delay(1000);
                    await ConnectAsync(null, true);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BluetoothImpl: Unexpected error sending alternative message");
            OnBluetoothError(new BluetoothException(BluetoothException.ErrorCodes.SendFailed, ex.Message));
        }
    }
        
    public async Task SendResponseAsync(MsgIds id, params byte[]? payload)
    {
        await SendAsync(new SppMessage{Id = id, Payload = payload ?? [], Type = MsgTypes.Response});
    }

    public async Task SendRequestAsync(MsgIds id, params byte[]? payload)
    {
        await SendAsync(new SppMessage{Id = id, Payload = payload ?? [], Type = MsgTypes.Request});
    }
        
    public async Task SendRequestAsync(MsgIds id, bool payload)
    {
        await SendRequestAsync(id, payload ? [0x01] : [0x00]);
    }

    public async Task SendAsync(BaseMessageEncoder encoder)
    {
        await SendAsync(encoder.Encode());
    }

    public void UnregisterDevice(Device? device = null)
    {
        if (AlternativeModeEnabled)
        {
            Log.Error("Unregister in alt mode");
            return;
        }
        var mac = device?.MacAddress ?? Device.Current?.MacAddress;
        var toRemove = Settings.Data.Devices.FirstOrDefault(x => x.MacAddress == mac);
        if (toRemove == null)
            return;
        
        // Disconnect if the device is currently connected
        if (mac == Device.Current?.MacAddress)
            _ = DisconnectAsync();

        Settings.Data.Devices.Remove(toRemove);
        DeviceMessageCache.Instance.Clear();
        
        Device.Current = Settings.Data.Devices.FirstOrDefault();
        if (PlatformUtils.IsDesktop)
        {
            BatteryHistoryManager.Instance.DeleteDatabaseForDevice(toRemove);
            
            
        }
    }
    
    private void OnDisconnected(object? sender, string reason)
    {
        if (AlternativeModeEnabled)
        {
            LastErrorMessage = Strings.Connlost;
            IsConnectedAlternative = false;
            DisconnectedAlternative?.Invoke(this, reason);
        }
        else if (!SuppressDisconnectionEvents)
        {
            LastErrorMessage = Strings.Connlost;
            IsConnected = false;
            Disconnected?.Invoke(this, reason);
            DeviceMessageCache.Instance.Clear();
        }
    }

    private void OnRfcommConnected(object? sender, EventArgs e)
    {
        if(!HasValidDevice)
        {
            Log.Error("BluetoothImpl: Suppressing Connected event, device not properly registered");
            return;
        }
        
        _ = Task.Delay(150).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (AlternativeModeEnabled)
                {
                    IsConnectedAlternative = true;
                    ConnectedAlternative?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    IsConnected = true;
                    Connected?.Invoke(this, EventArgs.Empty);
                }
            });
        });
    }
    
    private void OnNewDataAvailable(object? sender, byte[] frame)
    {
        /* Discard data if not properly registered */
        if (!HasValidDevice)
        {
            return;
        }

        if (AlternativeModeEnabled)
        {
            IsConnectedAlternative = true;
            NewDataReceivedAlternative?.Invoke(this, frame);
        }
        else
        {
            IsConnected = true;
            NewDataReceived?.Invoke(this, frame);
        }

        IncomingQueue.Enqueue(frame);
    }
    
    private void DataConsumerLoop()
    {
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;
        
        while (true)
        {
            try
            {
                // Check for cancellation
                _loopCancelSource.Token.ThrowIfCancellationRequested();
                
                // Wait a bit before processing next batch
                Task.Delay(50).Wait(_loopCancelSource.Token);
                
                // Skip processing if not connected
                if (!IsConnected && !IsConnectedAlternative)
                {
                    // Clear any pending data if we're not connected
                    lock (IncomingQueue)
                    {
                        if (!IncomingQueue.IsEmpty)
                        {
                            Log.Debug("BluetoothImpl: Clearing {Count} queued data frames while disconnected", 
                                IncomingQueue.Count);
                            while (IncomingQueue.TryDequeue(out _)) { }
                        }
                    }
                    
                    _incomingData.Clear();
                    continue;
                }
                
                // Process any queued data
                List<byte> dataToProcess = new List<byte>();
                
                lock (IncomingQueue)
                {
                    if (IncomingQueue.IsEmpty) continue;
                    
                    // Copy data to a local list to minimize lock time
                    while (IncomingQueue.TryDequeue(out var frame))
                    {
                        if (frame != null && frame.Length > 0)
                        {
                            dataToProcess.AddRange(frame);
                        }
                    }
                }
                
                // Skip if no data to process
                if (dataToProcess.Count == 0) continue;
                
                // Add to our main buffer
                _incomingData.AddRange(dataToProcess);
                
                // Prevent buffer from growing too large (possible memory leak)
                if (_incomingData.Count > 10000)
                {
                    Log.Warning("BluetoothImpl: Data buffer exceeded 10KB, truncating to prevent memory issues");
                    _incomingData.RemoveRange(0, _incomingData.Count - 5000);
                }
                
                // Process the data
                ProcessDataBlock(_incomingData, CurrentModel);
                
                // Reset error counter on successful processing
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                Log.Information("BluetoothImpl: Data consumer loop cancelled");
                _incomingData.Clear();
                throw;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Log.Error(ex, "BluetoothImpl: Error in data consumer loop ({Count}/{Max})", 
                    consecutiveErrors, maxConsecutiveErrors);
                
                // If we have too many consecutive errors, try to recover
                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    Log.Warning("BluetoothImpl: Too many consecutive errors in data consumer loop, attempting recovery");
                    consecutiveErrors = 0;
                    _incomingData.Clear();
                    
                    // Try to reconnect in a separate task to avoid blocking the loop
                    _ = Task.Run(async () => {
                        try
                        {
                            if (IsConnected)
                            {
                                await DisconnectAsync();
                                await Task.Delay(1000);
                                await ConnectAsync();
                            }
                            else if (IsConnectedAlternative)
                            {
                                await DisconnectAsync(true);
                                await Task.Delay(1000);
                                await ConnectAsync(null, true);
                            }
                        }
                        catch (Exception reconnectEx)
                        {
                            Log.Error(reconnectEx, "BluetoothImpl: Failed to recover from data consumer errors");
                        }
                    });
                }
                
                // Short delay to prevent tight error loop
                try
                {
                    Task.Delay(500, _loopCancelSource.Token).Wait();
                }
                catch (OperationCanceledException)
                {
                    _incomingData.Clear();
                    throw;
                }
            }
        }
    }

    public void ProcessDataBlock(List<byte> data, Models targetModel)
    {
        // Validate input data
        if (data == null || data.Count == 0)
        {
            Log.Warning("BluetoothImpl: Attempted to process empty data block");
            return;
        }
        
        try
        {
            // Create a copy of the data to prevent modification during parsing
            var dataCopy = new List<byte>(data);
            
            // Decode messages from the raw data
            var messages = SppMessage.DecodeRawChunk(dataCopy, targetModel, AlternativeModeEnabled);
            
            // Process each decoded message
            foreach (var message in messages)
            {
                // Validate message before processing
                if (message == null)
                {
                    Log.Warning("BluetoothImpl: Null message received, skipping");
                    continue;
                }
                
                // Validate message ID is within expected range
                if (!Enum.IsDefined(typeof(MsgIds), message.Id) && (int)message.Id != 0)
                {
                    Log.Warning("BluetoothImpl: Message with invalid ID received: {MsgId}, skipping", message.Id);
                    continue;
                }
                
                try
                {
                    if (AlternativeModeEnabled)
                    {
                        var altMessage = new SppAlternativeMessage(message);
                        Log.Verbose(">> Incoming (alt): {Msg}", altMessage);
                        MessageReceivedAlternative?.Invoke(this, altMessage);
                    }
                    else
                    {
                        Log.Verbose(">> Incoming: {Msg}", message);
                        MessageReceived?.Invoke(this, message);
                    }
                }
                catch (Exception eventEx)
                {
                    Log.Error(eventEx, "BluetoothImpl: Error in message event handler");
                }
            }
        }
        catch (InvalidPacketException ex)
        {
            // Provide detailed error information based on error code
            string errorDetail = ex.ErrorCode switch
            {
                InvalidPacketException.ErrorCodes.Som => "Start of message marker missing",
                InvalidPacketException.ErrorCodes.Eom => "End of message marker missing",
                InvalidPacketException.ErrorCodes.Checksum => "Checksum validation failed",
                InvalidPacketException.ErrorCodes.SizeMismatch => "Message size mismatch",
                InvalidPacketException.ErrorCodes.TooSmall => "Message too small to be valid",
                InvalidPacketException.ErrorCodes.OutOfRange => "Message data out of range",
                InvalidPacketException.ErrorCodes.Overflow => "Buffer overflow detected",
                _ => "Unknown packet error"
            };
            
            Log.Error(ex, "BluetoothImpl: Invalid packet received: {ErrorCode} - {ErrorDetail}", 
                ex.ErrorCode, errorDetail);
            
            // Notify appropriate listeners about invalid data
            if (AlternativeModeEnabled)
            {
                InvalidDataReceivedAlternative?.Invoke(this, ex);
            }
            else
            {
                InvalidDataReceived?.Invoke(this, ex);
            }
            
            // Clear the first part of the buffer to try to resync
            if (data.Count > 10)
            {
                data.RemoveRange(0, Math.Min(10, data.Count));
            }
            else
            {
                data.Clear();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BluetoothImpl: Unexpected error while processing data block");
            
            // Try to recover by clearing the data buffer
            data.Clear();
        }
    }
}
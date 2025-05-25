using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Message.Decoder;
using GalaxyBudsClient.Message.Encoder;
using GalaxyBudsClient.Model.Firmware;
using GalaxyBudsClient.Platform;
using Serilog;

namespace GalaxyBudsClient.Message;

public class FirmwareTransferManager
{
    private static readonly object Padlock = new();
    private static FirmwareTransferManager? _instance;
    public static FirmwareTransferManager Instance
    {
        get
        {
            lock (Padlock)
            {
                return _instance ??= new FirmwareTransferManager();
            }
        }
    }
        
    public enum States
    {
        Ready,
        PreparingUpdate,
        VerifyingFirmware,
        CheckingDeviceHealth,
        BackingUpFirmware,
        InitializingSession,
        Uploading,
        VerifyingUpdate,
        Finalizing,
        RecoveryMode
    }

    private States _state;
    public States State
    {
        private set
        {
            var old = _state;
            _state = value;
            // Only notify on change
            if (old != value)
            {
                StateChanged?.Invoke(this, value);
            }
        }
        get => _state;
    }

    public event EventHandler<FirmwareTransferException>? Error; 
    public event EventHandler<FirmwareProgressEventArgs>? ProgressChanged; 
    public event EventHandler<States>? StateChanged; 
    public event EventHandler? Finished; 
    public event EventHandler<short>? MtuChanged; 
    public event EventHandler<short>? CurrentSegmentIdChanged; 
    public event EventHandler<FirmwareBlockChangedEventArgs>? CurrentBlockChanged; 
        
    private readonly Timer _sessionTimeout;
    private readonly Timer _controlTimeout;

    private int _mtuSize;
    private int _currentSegment;
    private int _currentProgress;
    private long _lastSegmentOffset;
    private bool _lastFragment;
    private FirmwareBinary? _binary;

    public FirmwareTransferManager()
    {
        _sessionTimeout = new Timer(20000);
        _controlTimeout = new Timer(20000);
        _sessionTimeout.Elapsed += OnSessionTimeoutElapsed;
        _controlTimeout.Elapsed += OnControlTimeoutElapsed;

        Error += (sender, exception) =>
        {
            Log.Error(exception, "FirmwareTransferManager.OnError");
            Cancel();
        };
        StateChanged += (sender, state) => Log.Debug("FirmwareTransferManager: Status changed to {State}", state);
            
        BluetoothImpl.Instance.Disconnected += (sender, s) =>
        {
            if (_binary != null)
            {
                Log.Debug("FirmwareTransferManager: Disconnected. Transfer cancelled");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.Disconnected,
                    Strings.FwFailConnection));
            }
        };
        BluetoothImpl.Instance.BluetoothError += (sender, exception) =>
        {
            if (_binary != null)
            {
                Log.Debug("FirmwareTransferManager: Bluetooth error. Transfer cancelled");
                Cancel();
            }
        };
            
        SppMessageReceiver.Instance.AnyMessageDecoded += OnMessageDecoded;
    }

    private async void OnMessageDecoded(object? sender, BaseMessageDecoder? e)
    {
        if (_binary == null)
        {
            return;
        }
            
        switch (e)
        {
            case FotaSessionDecoder session:
            {
                Log.Debug("FirmwareTransferManager.OnMessageReceived: Session result is {Code}", session.ResultCode);
                
                _sessionTimeout.Stop();
                if (session.ResultCode != 0)
                {
                    Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.SessionFail, 
                        string.Format(Strings.FwFailSession, session.ResultCode)));
                }
                else
                {
                    _controlTimeout.Start();
                }

                break;
            }
            case FotaControlDecoder control:
                Log.Debug("FirmwareTransferManager.OnMessageReceived: Control block has CID: {Id}", control.ControlId);
                switch (control.ControlId)
                {
                    case FirmwareConstants.ControlIds.SendMtu:
                        _controlTimeout.Stop();
                        _mtuSize = control.MtuSize;
                        MtuChanged?.Invoke(this, control.MtuSize);

                        await BluetoothImpl.Instance.SendAsync(new FotaControlEncoder
                        {
                            ControlId = control.ControlId,
                            Parameter = control.MtuSize
                        });
                        Log.Debug("FirmwareTransferManager.OnMessageReceived: MTU size set to {MtuSize}", control.MtuSize);
                        break;
                    case FirmwareConstants.ControlIds.ReadyToDownload:
                        _currentSegment = control.Id;
                        CurrentSegmentIdChanged?.Invoke(this, control.Id);
                        
                        await BluetoothImpl.Instance.SendAsync(new FotaControlEncoder
                        {
                            ControlId = control.ControlId,
                            Parameter = control.Id
                        });
                        Log.Debug("FirmwareTransferManager.OnMessageReceived: Ready to download segment {Id}", control.Id);
                        break;
                }
                break;
            case FotaDownloadDataDecoder download:
                State = States.Uploading;

                var segment = _binary.GetSegmentById(_currentSegment);
                CurrentBlockChanged?.Invoke(this, new FirmwareBlockChangedEventArgs(_currentSegment, (int)download.ReceivedOffset, 
                    (int)download.ReceivedOffset + _mtuSize * download.RequestPacketNumber, download.RequestPacketNumber, (int?)segment?.Size ?? 0, (int?)segment?.Crc32 ?? 0));

                for (byte i2 = 0; i2 < download.RequestPacketNumber; i2++)
                {   
                    var downloadEncoder = new FotaDownloadDataEncoder
                    {
                        Binary = _binary,
                        EntryId = _currentSegment,
                        Offset = (int)download.ReceivedOffset + _mtuSize * i2,
                        MtuSize = _mtuSize
                    };
                    _lastFragment = downloadEncoder.IsLastFragment();
                    _lastSegmentOffset = downloadEncoder.Offset;
                        
                    await BluetoothImpl.Instance.SendAsync(downloadEncoder);
                }
                break;
            case FotaUpdateDecoder update:
                switch (update.UpdateId)
                {
                    case FirmwareConstants.UpdateIds.Percent:
                        _currentProgress = update.Percent;
                        ProgressChanged?.Invoke(this, new FirmwareProgressEventArgs(
                            _currentProgress, 
                            (long)Math.Round(_binary.TotalSize * (_currentProgress / 100f)), 
                            _binary.TotalSize));
                        Log.Debug("FirmwareTransferManager.OnMessageReceived: Copy progress: {Percent}% ({Done}KB/{TotalSize}KB)", 
                            update.Percent, 
                            (long)Math.Round(_binary.TotalSize * (_currentProgress / 100f)) / 1000f, 
                            _binary.TotalSize / 1000f);
                        break;
                    case FirmwareConstants.UpdateIds.StateChange:
                        await BluetoothImpl.Instance.SendResponseAsync(MsgIds.FOTA_UPDATE, 1);
                        Log.Debug("FirmwareTransferManager.OnMessageReceived: State changed: {State}, result code: {ResultCode}", 
                            update.State, update.ResultCode);

                        if (update.State == 0)
                        {
                            Log.Debug("FirmwareTransferManager.OnMessageReceived: Transfer complete (FOTA_STATE_CHANGE). The device will now proceed with the flashing process on its own.");
                            Finished?.Invoke(this, EventArgs.Empty);
                            Cancel();
                        }
                        else
                        {
                            Log.Debug("FirmwareTransferManager.OnMessageReceived: Copy failed, result code: {Code}", update.ResultCode);
                            Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.CopyFail, 
                                string.Format(Strings.FwFailCopy, update.ResultCode)));
                        }
                        break;
                }
                break;
            case FotaResultDecoder result:
                await BluetoothImpl.Instance.SendResponseAsync(MsgIds.FOTA_RESULT, 1);
                Log.Debug("FirmwareTransferManager.OnMessageReceived: Finished. Result: {Result}, error code: {Code}", 
                    result.Result, result.ErrorCode);

                if (result.Result == 0)
                {
                    Log.Debug("FirmwareTransferManager.OnMessageReceived: Transfer complete (FOTA_RESULT). The device will now proceed with the flashing process on its own.");
                    Finished?.Invoke(this, EventArgs.Empty);
                    Cancel();
                }
                else
                {
                    Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.VerifyFail, 
                        string.Format(Strings.FwFailVerify, result.ErrorCode)));
                }
                break;
        }
    }

    public async Task Install(FirmwareBinary binary)
    {
        try
        {
            // Initialize cancellation token
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;
            
            // Record start time for diagnostics
            _startTime = DateTime.Now;
            _retryCount = 0;
            
            // Update state to preparing
            UpdateState(States.PreparingUpdate);
            
            // Validate Bluetooth connection
            if (!BluetoothImpl.Instance.IsConnected)
            {
                Log.Error("FirmwareTransferManager: Attempted to install firmware while disconnected");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.Disconnected,
                    Strings.FwFailConnectionPrecheck));
                return;
            }
            
            // Validate backend connection
            if (!BluetoothImpl.Instance.Backend.IsStreamConnected)
            {
                Log.Error("FirmwareTransferManager: Backend reports disconnected but IsConnected is true");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.Disconnected,
                    "Backend reports disconnected but IsConnected is true"));
                return;
            }

            // Validate firmware transfer state
            if (State != States.PreparingUpdate)
            {
                Log.Error("FirmwareTransferManager: Attempted to install firmware while another operation is in progress");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.InProgress,
                    Strings.FwFailPending));
                return;
            }
            
            // Validate firmware binary
            if (binary == null || binary.Data == null || binary.Data.Length == 0)
            {
                Log.Error("FirmwareTransferManager: Invalid firmware binary provided");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.InvalidBinary,
                    "Invalid firmware binary provided"));
                return;
            }
            
            // Validate firmware binary size
            if (binary.Data.Length > 2 * 1024 * 1024) // 2MB max
            {
                Log.Error("FirmwareTransferManager: Firmware binary too large: {Size} bytes", binary.Data.Length);
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.InvalidBinary,
                    $"Firmware binary too large: {binary.Data.Length} bytes"));
                return;
            }
            
            // Verify firmware integrity
            UpdateState(States.VerifyingFirmware);
            StatusMessageChanged?.Invoke(this, "Verifying firmware integrity...");
            
            if (!await binary.VerifyIntegrity())
            {
                Log.Error("FirmwareTransferManager: Firmware integrity verification failed");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.IntegrityCheckFail,
                    "Firmware integrity verification failed. The firmware file may be corrupted."));
                return;
            }
            
            // Check device health
            UpdateState(States.CheckingDeviceHealth);
            StatusMessageChanged?.Invoke(this, "Checking device health...");
            
            // Start health check timeout
            _healthCheckTimeout.Start();
            
            // Check battery level
            var batteryLevel = BluetoothImpl.Instance.DeviceSpec.BatteryL;
            var batteryLevelR = BluetoothImpl.Instance.DeviceSpec.BatteryR;
            var batteryLevelCase = BluetoothImpl.Instance.DeviceSpec.BatteryCase;
            
            // Stop health check timeout
            _healthCheckTimeout.Stop();
            
            // Ensure battery level is sufficient (at least 30%)
            if (batteryLevel < 30 || (batteryLevelR > 0 && batteryLevelR < 30))
            {
                Log.Error("FirmwareTransferManager: Battery level too low for firmware update: L={Left}%, R={Right}%", 
                    batteryLevel, batteryLevelR);
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.BatteryTooLow,
                    $"Battery level too low for firmware update. Please charge your earbuds to at least 30% before updating. Current levels: L={batteryLevel}%, R={batteryLevelR}%"));
                return;
            }
            
            // Check if device is in use (playing music, on call, etc.)
            var deviceStatus = BluetoothImpl.Instance.DeviceSpec.Status;
            if (deviceStatus.HasFlag(DeviceStatusFlags.AudioPlaying) || 
                deviceStatus.HasFlag(DeviceStatusFlags.CallActive))
            {
                Log.Error("FirmwareTransferManager: Device is currently in use");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.DeviceInUse,
                    "Device is currently in use (playing audio or on a call). Please stop all audio playback and calls before updating."));
                return;
            }
            
            // Backup current firmware info
            UpdateState(States.BackingUpFirmware);
            StatusMessageChanged?.Invoke(this, "Backing up current firmware information...");
            
            await FirmwareIntegrityVerifier.Instance.BackupCurrentFirmware(_backupPath);
            
            // Save recovery info
            await FirmwareRecoveryManager.Instance.SaveRecoveryInfo(binary);
            
            // Reset state variables
            _mtuSize = 0;
            _currentSegment = 0;
            _lastSegmentOffset = 0;
            _lastFragment = false;
            _binary = binary;
            
            // Update state
            UpdateState(States.InitializingSession);
            
            Log.Information("FirmwareTransferManager: Starting firmware installation, size: {Size} bytes, version: {Version}, model: {Model}", 
                binary.Data.Length, binary.Version, binary.Model);
            
            // Register for Bluetooth error events during firmware update
            BluetoothImpl.Instance.BluetoothError += OnBluetoothError;
            BluetoothImpl.Instance.Disconnected += OnDisconnected;
            
            // Start session timeout timer
            _sessionTimeout.Start();
            
            // Start overall transfer timeout
            _transferTimeout.Start();
            
            // Send firmware open request
            await BluetoothImpl.Instance.SendRequestAsync(MsgIds.FOTA_OPEN, _binary.SerializeTable());
            
            Log.Debug("FirmwareTransferManager: Sent FOTA_OPEN request");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("FirmwareTransferManager: Firmware installation cancelled by user");
            Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.Unknown,
                "Firmware installation cancelled by user"));
            
            // Reset state
            Cancel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FirmwareTransferManager: Error starting firmware installation");
            Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.Unknown,
                $"Error starting firmware installation: {ex.Message}"));
            
            // Reset state
            Cancel();
        }
    }
    
    private void OnBluetoothError(object? sender, BluetoothException ex)
    {
        if (State == States.Ready)
            return;
            
        Log.Error("FirmwareTransferManager: Bluetooth error during firmware update: {ErrorCode}", ex.ErrorCode);
        Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.BluetoothError,
            $"Bluetooth error during firmware update: {ex.ErrorCode}"));
        
        Cancel();
    }
    
    private void OnDisconnected(object? sender, string reason)
    {
        if (State == States.Ready)
            return;
            
        Log.Error("FirmwareTransferManager: Disconnected during firmware update: {Reason}", reason);
        Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.Disconnected,
            $"Disconnected during firmware update: {reason}"));
        
        Cancel();
    }

    public bool IsInProgress()
    {
        return State != States.Ready;
    }
        
    public async void Cancel()
    {
        Log.Information("FirmwareTransferManager: Cancelling firmware transfer operation");
        
        try
        {
            // Cancel any pending operations
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
            
            // Unregister event handlers
            BluetoothImpl.Instance.BluetoothError -= OnBluetoothError;
            BluetoothImpl.Instance.Disconnected -= OnDisconnected;
            
            // Reset state variables
            _binary = null;
            _mtuSize = 0;
            _currentSegment = 0;
            _lastSegmentOffset = 0;
            _lastFragment = false;
            _isRecoveryMode = false;
                
            // Update state
            UpdateState(States.Ready);
                
            // Stop all timers
            _sessionTimeout.Stop();
            _controlTimeout.Stop();
            _transferTimeout.Stop();
            _healthCheckTimeout.Stop();
            _verificationTimeout.Stop();
            
            // Try to send abort command if connected
            if (BluetoothImpl.Instance.IsConnected && BluetoothImpl.Instance.Backend.IsStreamConnected)
            {
                try
                {
                    Log.Debug("FirmwareTransferManager: Sending FOTA_ABORT command");
                    await BluetoothImpl.Instance.SendRequestAsync(MsgIds.FOTA_ABORT);
                    await Task.Delay(500); // Give device time to process abort
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "FirmwareTransferManager: Failed to send abort command: {Error}", ex.Message);
                }
            }
    
            // Reconnect to reset device state
            try
            {
                Log.Debug("FirmwareTransferManager: Disconnecting to reset device state");
                await BluetoothImpl.Instance.DisconnectAsync();
                
                // Wait before reconnecting
                await Task.Delay(1000);
                
                Log.Debug("FirmwareTransferManager: Reconnecting after firmware operation");
                bool reconnected = await BluetoothImpl.Instance.ConnectAsync();
                
                if (!reconnected)
                {
                    Log.Warning("FirmwareTransferManager: Failed to reconnect after firmware operation");
                    
                    // Try one more time with longer delay and exponential backoff
                    await Task.Delay(2000);
                    
                    // Try with increased timeout
                    reconnected = await BluetoothImpl.Instance.ConnectAsync(timeout: 10000);
                    
                    if (!reconnected)
                    {
                        Log.Error("FirmwareTransferManager: Failed to reconnect after multiple attempts");
                        
                        // Notify user that manual reconnection may be needed
                        StatusMessageChanged?.Invoke(this, "Failed to reconnect. You may need to manually reconnect your device.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareTransferManager: Error during reconnection after firmware operation");
            }
            finally
            {
                // Dispose cancellation token source
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                
                // Log operation duration
                var duration = DateTime.Now - _startTime;
                Log.Information("FirmwareTransferManager: Operation duration: {Duration}", duration);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FirmwareTransferManager: Error during cancel operation");
            
            // Make sure state is reset even if an error occurs
            try
            {
                UpdateState(States.Ready);
                
                // Stop all timers as a safety measure
                _sessionTimeout.Stop();
                _controlTimeout.Stop();
                _transferTimeout.Stop();
                _healthCheckTimeout.Stop();
                _verificationTimeout.Stop();
            }
            catch
            {
                // Ignore errors in emergency cleanup
            }
        }
        
        Log.Information("FirmwareTransferManager: Firmware transfer operation cancelled");
    }

    private void OnSessionTimeoutElapsed(object? sender, ElapsedEventArgs e)
    {
        Log.Error("FirmwareTransferManager: Session timeout elapsed after {Interval}ms", _sessionTimeout.Interval);
        
        try
        {
            // Stop the timer to prevent multiple triggers
            _sessionTimeout.Stop();
            
            // Notify about the timeout
            Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.SessionTimeout, 
                Strings.FwFailSessionTimeout));
            
            // Cancel the operation
            Cancel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FirmwareTransferManager: Error handling session timeout");
            
            // Ensure operation is cancelled even if error occurs
            try
            {
                Cancel();
            }
            catch
            {
                // Ignore any errors during emergency cancel
            }
        }
    } 
        
    private void OnControlTimeoutElapsed(object? sender, ElapsedEventArgs e)
    {
        Log.Error("FirmwareTransferManager: Control timeout elapsed after {Interval}ms", _controlTimeout.Interval);
        
        try
        {
            // Stop the timer to prevent multiple triggers
            _controlTimeout.Stop();
            
            // Notify about the timeout
            Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.ControlTimeout, 
                Strings.FwFailControlTimeout));
            
            // Cancel the operation
            Cancel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FirmwareTransferManager: Error handling control timeout");
            
            // Ensure operation is cancelled even if error occurs
            try
            {
                Cancel();
            }
            catch
            {
                // Ignore any errors during emergency cancel
            }
        }
    }
}
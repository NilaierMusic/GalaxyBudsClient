using System;
using System.IO;
using System.Threading.Tasks;
using GalaxyBudsClient.Platform;
using GalaxyBudsClient.Utils;
using Serilog;

namespace GalaxyBudsClient.Model.Firmware
{
    /// <summary>
    /// Manages recovery operations for interrupted firmware updates
    /// </summary>
    public class FirmwareRecoveryManager
    {
        private static readonly object Padlock = new();
        private static FirmwareRecoveryManager? _instance;
        
        public static FirmwareRecoveryManager Instance
        {
            get
            {
                lock (Padlock)
                {
                    return _instance ??= new FirmwareRecoveryManager();
                }
            }
        }
        
        // Events
        public event EventHandler<FirmwareTransferException>? Error;
        public event EventHandler<string>? RecoveryStarted;
        public event EventHandler<string>? RecoveryProgress;
        public event EventHandler<bool>? RecoveryCompleted;
        
        // Recovery state
        private bool _isRecoveryInProgress;
        private FirmwareBinary? _recoveryBinary;
        private string _recoveryReason = string.Empty;
        private DateTime _recoveryStartTime;
        
        // Recovery settings
        private readonly string _recoveryDir = Path.Combine(
            PlatformUtils.CombinePaths(PlatformUtils.GetConfigDirectory(), "recovery"));
        
        private FirmwareRecoveryManager()
        {
            // Create recovery directory if it doesn't exist
            if (!Directory.Exists(_recoveryDir))
            {
                try
                {
                    Directory.CreateDirectory(_recoveryDir);
                    Log.Information("FirmwareRecoveryManager: Created recovery directory at {Path}", _recoveryDir);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "FirmwareRecoveryManager: Failed to create recovery directory");
                }
            }
        }
        
        /// <summary>
        /// Checks if a device is in recovery mode
        /// </summary>
        public async Task<bool> IsDeviceInRecoveryMode()
        {
            try
            {
                if (!BluetoothImpl.Instance.IsConnected)
                {
                    return false;
                }
                
                // Check if device is in recovery mode based on device status
                var deviceStatus = BluetoothImpl.Instance.DeviceSpec.Status;
                
                // Check for specific indicators of recovery mode
                // This is device-specific and may need to be adjusted
                if (deviceStatus.HasFlag(DeviceStatusFlags.FirmwareUpdateInProgress) ||
                    deviceStatus.HasFlag(DeviceStatusFlags.Disconnected) ||
                    BluetoothImpl.Instance.DeviceSpec.FwVersion.Contains("RECOVERY"))
                {
                    Log.Warning("FirmwareRecoveryManager: Device appears to be in recovery mode");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareRecoveryManager: Error checking if device is in recovery mode");
                return false;
            }
        }
        
        /// <summary>
        /// Detects if a previous firmware update was interrupted
        /// </summary>
        public async Task<bool> DetectInterruptedUpdate()
        {
            try
            {
                if (!BluetoothImpl.Instance.IsConnected)
                {
                    return false;
                }
                
                // Check if device is in recovery mode
                if (await IsDeviceInRecoveryMode())
                {
                    _recoveryReason = "Device is in recovery mode";
                    return true;
                }
                
                // Check for recovery file
                var recoveryFile = Path.Combine(_recoveryDir, "recovery_info.json");
                if (File.Exists(recoveryFile))
                {
                    _recoveryReason = "Recovery file found";
                    return true;
                }
                
                // Check device status for partial update
                var deviceStatus = BluetoothImpl.Instance.DeviceSpec.Status;
                if (deviceStatus.HasFlag(DeviceStatusFlags.FirmwareUpdateInProgress))
                {
                    _recoveryReason = "Device reports firmware update in progress";
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareRecoveryManager: Error detecting interrupted update");
                return false;
            }
        }
        
        /// <summary>
        /// Saves recovery information for the current firmware update
        /// </summary>
        public async Task SaveRecoveryInfo(FirmwareBinary binary)
        {
            if (binary == null)
            {
                return;
            }
            
            try
            {
                // Create recovery info
                var recoveryInfo = new
                {
                    Timestamp = DateTime.Now,
                    BuildName = binary.BuildName,
                    Version = binary.Version,
                    Model = binary.Model.ToString(),
                    Checksum = binary.Checksum,
                    BinaryPath = Path.Combine(_recoveryDir, $"firmware_{binary.Checksum}.bin")
                };
                
                // Save recovery info to file
                var recoveryFile = Path.Combine(_recoveryDir, "recovery_info.json");
                await File.WriteAllTextAsync(recoveryFile, 
                    System.Text.Json.JsonSerializer.Serialize(recoveryInfo, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
                
                // Save firmware binary
                await File.WriteAllBytesAsync(recoveryInfo.BinaryPath, binary.Data);
                
                Log.Information("FirmwareRecoveryManager: Saved recovery info for firmware {BuildName}", binary.BuildName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareRecoveryManager: Error saving recovery info");
            }
        }
        
        /// <summary>
        /// Clears recovery information after successful update
        /// </summary>
        public void ClearRecoveryInfo()
        {
            try
            {
                var recoveryFile = Path.Combine(_recoveryDir, "recovery_info.json");
                if (File.Exists(recoveryFile))
                {
                    File.Delete(recoveryFile);
                    Log.Information("FirmwareRecoveryManager: Cleared recovery info");
                }
                
                // Clean up old firmware binaries
                foreach (var file in Directory.GetFiles(_recoveryDir, "firmware_*.bin"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "FirmwareRecoveryManager: Failed to delete recovery binary: {Path}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareRecoveryManager: Error clearing recovery info");
            }
        }
        
        /// <summary>
        /// Starts recovery mode for an interrupted firmware update
        /// </summary>
        public async Task<bool> StartRecovery()
        {
            if (_isRecoveryInProgress)
            {
                Log.Warning("FirmwareRecoveryManager: Recovery already in progress");
                return false;
            }
            
            try
            {
                _isRecoveryInProgress = true;
                _recoveryStartTime = DateTime.Now;
                
                // Notify recovery started
                RecoveryStarted?.Invoke(this, _recoveryReason);
                
                Log.Information("FirmwareRecoveryManager: Starting recovery process. Reason: {Reason}", _recoveryReason);
                
                // Load recovery info
                var recoveryFile = Path.Combine(_recoveryDir, "recovery_info.json");
                if (!File.Exists(recoveryFile))
                {
                    throw new FirmwareTransferException(FirmwareTransferException.ErrorCodes.RecoveryFailed, 
                        "Recovery file not found");
                }
                
                // Parse recovery info
                var recoveryInfoJson = await File.ReadAllTextAsync(recoveryFile);
                var recoveryInfo = System.Text.Json.JsonSerializer.Deserialize<dynamic>(recoveryInfoJson);
                
                if (recoveryInfo == null)
                {
                    throw new FirmwareTransferException(FirmwareTransferException.ErrorCodes.RecoveryFailed, 
                        "Invalid recovery info");
                }
                
                // Load firmware binary
                string binaryPath = recoveryInfo.GetProperty("BinaryPath").GetString();
                if (!File.Exists(binaryPath))
                {
                    throw new FirmwareTransferException(FirmwareTransferException.ErrorCodes.RecoveryFailed, 
                        "Recovery firmware binary not found");
                }
                
                // Load the firmware binary
                var binaryData = await File.ReadAllBytesAsync(binaryPath);
                var buildName = recoveryInfo.GetProperty("BuildName").GetString();
                
                // Create firmware binary
                _recoveryBinary = new FirmwareBinary(binaryData, buildName, true);
                
                // Verify firmware integrity
                RecoveryProgress?.Invoke(this, "Verifying firmware integrity...");
                if (!await _recoveryBinary.VerifyIntegrity())
                {
                    throw new FirmwareTransferException(FirmwareTransferException.ErrorCodes.IntegrityCheckFail, 
                        "Recovery firmware integrity check failed");
                }
                
                // Check if device is connected
                if (!BluetoothImpl.Instance.IsConnected)
                {
                    RecoveryProgress?.Invoke(this, "Connecting to device...");
                    
                    // Try to connect to the device
                    if (!await BluetoothImpl.Instance.ConnectAsync())
                    {
                        throw new FirmwareTransferException(FirmwareTransferException.ErrorCodes.Disconnected, 
                            "Failed to connect to device for recovery");
                    }
                }
                
                // Start firmware update
                RecoveryProgress?.Invoke(this, "Starting firmware recovery...");
                await FirmwareTransferManager.Instance.Install(_recoveryBinary);
                
                // Wait for firmware update to complete
                int timeoutSeconds = 300; // 5 minutes timeout
                int elapsedSeconds = 0;
                bool completed = false;
                
                while (elapsedSeconds < timeoutSeconds)
                {
                    if (!FirmwareTransferManager.Instance.IsInProgress())
                    {
                        completed = true;
                        break;
                    }
                    
                    await Task.Delay(1000);
                    elapsedSeconds++;
                    
                    if (elapsedSeconds % 10 == 0)
                    {
                        RecoveryProgress?.Invoke(this, $"Recovery in progress... ({elapsedSeconds}s)");
                    }
                }
                
                if (!completed)
                {
                    FirmwareTransferManager.Instance.Cancel();
                    throw new FirmwareTransferException(FirmwareTransferException.ErrorCodes.TransferTimeout, 
                        "Recovery firmware update timed out");
                }
                
                // Clear recovery info
                ClearRecoveryInfo();
                
                // Notify recovery completed
                RecoveryCompleted?.Invoke(this, true);
                
                Log.Information("FirmwareRecoveryManager: Recovery process completed successfully");
                return true;
            }
            catch (FirmwareTransferException ex)
            {
                Log.Error(ex, "FirmwareRecoveryManager: Recovery failed: {ErrorCode}", ex.ErrorCode);
                Error?.Invoke(this, ex);
                RecoveryCompleted?.Invoke(this, false);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareRecoveryManager: Unexpected error during recovery");
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.RecoveryFailed, 
                    $"Recovery failed: {ex.Message}"));
                RecoveryCompleted?.Invoke(this, false);
                return false;
            }
            finally
            {
                _isRecoveryInProgress = false;
            }
        }
        
        /// <summary>
        /// Checks if recovery is in progress
        /// </summary>
        public bool IsRecoveryInProgress()
        {
            return _isRecoveryInProgress;
        }
    }
}
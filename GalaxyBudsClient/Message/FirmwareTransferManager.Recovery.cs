using System;
using System.Threading.Tasks;
using GalaxyBudsClient.Model.Firmware;
using Serilog;

namespace GalaxyBudsClient.Message
{
    // Partial class containing recovery methods for FirmwareTransferManager
    public partial class FirmwareTransferManager
    {
        /// <summary>
        /// Checks if the device needs recovery from an interrupted firmware update
        /// </summary>
        public async Task<bool> CheckForRecoveryMode()
        {
            try
            {
                if (!BluetoothImpl.Instance.IsConnected)
                {
                    Log.Warning("FirmwareTransferManager: Cannot check for recovery mode while disconnected");
                    return false;
                }
                
                // Check if a previous update was interrupted
                bool needsRecovery = await FirmwareRecoveryManager.Instance.DetectInterruptedUpdate();
                
                if (needsRecovery)
                {
                    Log.Warning("FirmwareTransferManager: Device needs recovery from interrupted update");
                    _isRecoveryMode = true;
                    UpdateState(States.RecoveryMode);
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareTransferManager: Error checking for recovery mode");
                return false;
            }
        }
        
        /// <summary>
        /// Starts recovery mode for an interrupted firmware update
        /// </summary>
        public async Task<bool> StartRecoveryMode()
        {
            if (!_isRecoveryMode)
            {
                Log.Warning("FirmwareTransferManager: Cannot start recovery mode when not in recovery state");
                return false;
            }
            
            try
            {
                Log.Information("FirmwareTransferManager: Starting recovery mode");
                
                // Initialize cancellation token
                _cancellationTokenSource = new CancellationTokenSource();
                
                // Record start time for diagnostics
                _startTime = DateTime.Now;
                
                // Start recovery process
                bool success = await FirmwareRecoveryManager.Instance.StartRecovery();
                
                if (success)
                {
                    Log.Information("FirmwareTransferManager: Recovery completed successfully");
                    
                    // Clear recovery mode
                    _isRecoveryMode = false;
                    UpdateState(States.Ready);
                    
                    return true;
                }
                else
                {
                    Log.Error("FirmwareTransferManager: Recovery failed");
                    
                    // Reset state
                    _isRecoveryMode = false;
                    UpdateState(States.Ready);
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareTransferManager: Error during recovery");
                
                // Reset state
                _isRecoveryMode = false;
                UpdateState(States.Ready);
                
                return false;
            }
        }
        
        /// <summary>
        /// Checks if the device is currently in recovery mode
        /// </summary>
        public bool IsInRecoveryMode()
        {
            return _isRecoveryMode;
        }
    }
}
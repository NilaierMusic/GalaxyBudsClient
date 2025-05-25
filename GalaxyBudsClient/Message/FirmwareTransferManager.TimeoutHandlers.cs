using System;
using System.Timers;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Model.Firmware;
using Serilog;

namespace GalaxyBudsClient.Message
{
    // Partial class containing timeout handlers for FirmwareTransferManager
    public partial class FirmwareTransferManager
    {
        private void OnTransferTimeoutElapsed(object? sender, ElapsedEventArgs e)
        {
            Log.Error("FirmwareTransferManager: Transfer timeout elapsed after {Interval}ms", _transferTimeout.Interval);
            
            try
            {
                // Stop the timer to prevent multiple triggers
                _transferTimeout.Stop();
                
                // Notify about the timeout
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.TransferTimeout, 
                    "The firmware transfer timed out after " + (_transferTimeout.Interval / 1000) + " seconds. " +
                    "This could be due to poor Bluetooth connection quality or device issues."));
                
                // Cancel the operation
                Cancel();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareTransferManager: Error handling transfer timeout");
                
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
        
        private void OnHealthCheckTimeoutElapsed(object? sender, ElapsedEventArgs e)
        {
            Log.Error("FirmwareTransferManager: Health check timeout elapsed after {Interval}ms", _healthCheckTimeout.Interval);
            
            try
            {
                // Stop the timer to prevent multiple triggers
                _healthCheckTimeout.Stop();
                
                // Notify about the timeout
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.DeviceBusy, 
                    "The device health check timed out. The device may be busy or unresponsive."));
                
                // Cancel the operation
                Cancel();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareTransferManager: Error handling health check timeout");
                
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
        
        private void OnVerificationTimeoutElapsed(object? sender, ElapsedEventArgs e)
        {
            Log.Error("FirmwareTransferManager: Verification timeout elapsed after {Interval}ms", _verificationTimeout.Interval);
            
            try
            {
                // Stop the timer to prevent multiple triggers
                _verificationTimeout.Stop();
                
                // Notify about the timeout
                Error?.Invoke(this, new FirmwareTransferException(FirmwareTransferException.ErrorCodes.VerifyFail, 
                    "The firmware verification process timed out. The device may be busy verifying the update."));
                
                // Cancel the operation
                Cancel();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareTransferManager: Error handling verification timeout");
                
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
}
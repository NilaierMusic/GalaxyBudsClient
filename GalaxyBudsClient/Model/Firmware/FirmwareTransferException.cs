using System;

namespace GalaxyBudsClient.Model.Firmware;

public class FirmwareTransferException : Exception
{
    public enum ErrorCodes
    {
        // Timeout errors
        SessionTimeout,
        ControlTimeout,
        CopyTimeout,
        TransferTimeout,
            
        // Process errors
        ParseFail,
        SessionFail,
        CopyFail,
        VerifyFail,
        IntegrityCheckFail,
        SignatureVerificationFail,
        DeviceRejectedFirmware,
        
        // Device state errors
        BatteryLow,
        BatteryTooLow,
        InProgress,
        Disconnected,
        DeviceBusy,
        DeviceInUse,
        
        // Input validation errors
        InvalidBinary,
        UnsupportedFirmwareVersion,
        DowngradePrevented,
        ModelMismatch,
        
        // Connection errors
        BluetoothError,
        ConnectionLost,
        ConnectionQualityTooLow,
        
        // Recovery errors
        RecoveryFailed,
        PartialUpdateDetected,
        
        // Unknown errors
        Unknown
    }
        
    public readonly string ErrorName;
    public readonly string ErrorMessage;
    public readonly ErrorCodes ErrorCode;

    public FirmwareTransferException(FirmwareParseException ex)
        : base($"{ex.ErrorCode.ToString()}: {ex.ErrorMessage}", ex)
    {
        ErrorName = ex.ErrorName;
        ErrorMessage = ex.ErrorMessage;
        ErrorCode = ErrorCodes.ParseFail;
    }
        
    public FirmwareTransferException(ErrorCodes code, string message)
        : base($"{code.ToString()}: {message}")
    {
        ErrorName = code.ToString();
        ErrorMessage = message;
        ErrorCode = code;
    }
}
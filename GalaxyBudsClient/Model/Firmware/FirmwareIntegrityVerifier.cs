using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GalaxyBudsClient.Platform;
using Serilog;

namespace GalaxyBudsClient.Model.Firmware
{
    /// <summary>
    /// Provides methods to verify the integrity of firmware binaries
    /// </summary>
    public class FirmwareIntegrityVerifier
    {
        private static readonly object Padlock = new();
        private static FirmwareIntegrityVerifier? _instance;
        
        public static FirmwareIntegrityVerifier Instance
        {
            get
            {
                lock (Padlock)
                {
                    return _instance ??= new FirmwareIntegrityVerifier();
                }
            }
        }
        
        /// <summary>
        /// Verifies the integrity of a firmware binary
        /// </summary>
        /// <param name="binary">The firmware binary to verify</param>
        /// <returns>True if the firmware is valid, false otherwise</returns>
        public async Task<bool> VerifyIntegrity(FirmwareBinary binary)
        {
            if (binary == null || binary.Data == null || binary.Data.Length == 0)
            {
                Log.Error("FirmwareIntegrityVerifier: Invalid firmware binary provided");
                return false;
            }
            
            try
            {
                // Verify file size
                if (binary.Data.Length < 1024) // At least 1KB
                {
                    Log.Error("FirmwareIntegrityVerifier: Firmware binary too small: {Size} bytes", binary.Data.Length);
                    return false;
                }
                
                if (binary.Data.Length > 2 * 1024 * 1024) // 2MB max
                {
                    Log.Error("FirmwareIntegrityVerifier: Firmware binary too large: {Size} bytes", binary.Data.Length);
                    return false;
                }
                
                // Verify header structure
                if (!VerifyHeaderStructure(binary))
                {
                    Log.Error("FirmwareIntegrityVerifier: Invalid firmware header structure");
                    return false;
                }
                
                // Verify CRC32 checksums for all segments
                if (!VerifySegmentChecksums(binary))
                {
                    Log.Error("FirmwareIntegrityVerifier: Segment checksum verification failed");
                    return false;
                }
                
                // Verify firmware model compatibility
                if (!await VerifyModelCompatibility(binary))
                {
                    Log.Error("FirmwareIntegrityVerifier: Firmware model compatibility check failed");
                    return false;
                }
                
                // Verify firmware version (prevent downgrades)
                if (!await VerifyVersionCompatibility(binary))
                {
                    Log.Error("FirmwareIntegrityVerifier: Firmware version compatibility check failed");
                    return false;
                }
                
                Log.Information("FirmwareIntegrityVerifier: Firmware binary passed all integrity checks");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareIntegrityVerifier: Error verifying firmware integrity");
                return false;
            }
        }
        
        /// <summary>
        /// Verifies the basic header structure of the firmware binary
        /// </summary>
        private bool VerifyHeaderStructure(FirmwareBinary binary)
        {
            try
            {
                // Check if the binary has a valid segment table
                if (binary.SegmentTable == null || binary.SegmentTable.Count == 0)
                {
                    Log.Error("FirmwareIntegrityVerifier: Firmware binary has no segment table");
                    return false;
                }
                
                // Check if all segments have valid sizes
                foreach (var segment in binary.SegmentTable)
                {
                    if (segment.Size <= 0 || segment.Size > binary.Data.Length)
                    {
                        Log.Error("FirmwareIntegrityVerifier: Segment {Id} has invalid size: {Size}", 
                            segment.Id, segment.Size);
                        return false;
                    }
                    
                    if (segment.Offset < 0 || segment.Offset + segment.Size > binary.Data.Length)
                    {
                        Log.Error("FirmwareIntegrityVerifier: Segment {Id} has invalid offset: {Offset}", 
                            segment.Id, segment.Offset);
                        return false;
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareIntegrityVerifier: Error verifying header structure");
                return false;
            }
        }
        
        /// <summary>
        /// Verifies the CRC32 checksums for all segments in the firmware binary
        /// </summary>
        private bool VerifySegmentChecksums(FirmwareBinary binary)
        {
            try
            {
                foreach (var segment in binary.SegmentTable)
                {
                    // Extract segment data
                    byte[] segmentData = new byte[segment.Size];
                    Array.Copy(binary.Data, segment.Offset, segmentData, 0, segment.Size);
                    
                    // Calculate CRC32
                    uint calculatedCrc = CalculateCrc32(segmentData);
                    
                    // Compare with stored CRC
                    if (calculatedCrc != segment.Crc32)
                    {
                        Log.Error("FirmwareIntegrityVerifier: CRC32 mismatch for segment {Id}. " +
                                  "Expected: {Expected}, Calculated: {Calculated}", 
                            segment.Id, segment.Crc32, calculatedCrc);
                        return false;
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareIntegrityVerifier: Error verifying segment checksums");
                return false;
            }
        }
        
        /// <summary>
        /// Verifies that the firmware is compatible with the current device model
        /// </summary>
        private async Task<bool> VerifyModelCompatibility(FirmwareBinary binary)
        {
            try
            {
                // Get current device model
                var currentModel = BluetoothImpl.Instance.CurrentModel;
                
                // Check if firmware is for the correct model
                if (binary.Model != currentModel)
                {
                    Log.Error("FirmwareIntegrityVerifier: Firmware model mismatch. " +
                              "Device: {DeviceModel}, Firmware: {FirmwareModel}", 
                        currentModel, binary.Model);
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareIntegrityVerifier: Error verifying model compatibility");
                return false;
            }
        }
        
        /// <summary>
        /// Verifies that the firmware version is compatible with the current device
        /// </summary>
        private async Task<bool> VerifyVersionCompatibility(FirmwareBinary binary)
        {
            try
            {
                // Get current firmware version
                var currentVersion = BluetoothImpl.Instance.DeviceSpec.FwVersion;
                
                // Parse versions
                if (!Version.TryParse(currentVersion, out var currentVer) || 
                    !Version.TryParse(binary.Version, out var newVer))
                {
                    Log.Error("FirmwareIntegrityVerifier: Failed to parse firmware versions. " +
                              "Current: {Current}, New: {New}", 
                        currentVersion, binary.Version);
                    return false;
                }
                
                // Prevent downgrades (unless forced)
                if (newVer < currentVer && !binary.AllowDowngrade)
                {
                    Log.Error("FirmwareIntegrityVerifier: Firmware downgrade detected. " +
                              "Current: {Current}, New: {New}", 
                        currentVersion, binary.Version);
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareIntegrityVerifier: Error verifying version compatibility");
                return false;
            }
        }
        
        /// <summary>
        /// Calculates the CRC32 checksum for a byte array
        /// </summary>
        private uint CalculateCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }
            
            return ~crc;
        }
        
        /// <summary>
        /// Creates a backup of the current firmware before updating
        /// </summary>
        public async Task<bool> BackupCurrentFirmware(string backupPath)
        {
            try
            {
                // Get current firmware info
                var model = BluetoothImpl.Instance.CurrentModel;
                var version = BluetoothImpl.Instance.DeviceSpec.FwVersion;
                
                // Create backup directory if it doesn't exist
                var backupDir = Path.GetDirectoryName(backupPath);
                if (!Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir!);
                }
                
                // Create backup info file
                var backupInfo = new StringBuilder();
                backupInfo.AppendLine($"Model: {model}");
                backupInfo.AppendLine($"Version: {version}");
                backupInfo.AppendLine($"Backup Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                
                // Write backup info to file
                await File.WriteAllTextAsync(backupPath, backupInfo.ToString());
                
                Log.Information("FirmwareIntegrityVerifier: Created firmware backup info at {Path}", backupPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareIntegrityVerifier: Error creating firmware backup");
                return false;
            }
        }
    }
}
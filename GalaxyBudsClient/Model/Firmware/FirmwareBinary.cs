using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Utils;
using Sentry;
using Serilog;

namespace GalaxyBudsClient.Model.Firmware;

public class FirmwareBinary
{
    public const long FotaBinMagic = 0xCAFECAFE;
    public const long FotaBinMagicCombination = 0x42434F4D;

    public long Magic { get; }
    public string BuildName { get; }
    public long SegmentsCount { get; }
    public long TotalSize { get; }
    public int Crc32 { get; }
    public FirmwareSegment[] Segments { get; }
    public Models? DetectedModel { get; }
    
    // Enhanced properties for better firmware management
    public string Version { get; private set; } = "Unknown";
    public Models Model { get; private set; } = Models.NULL;
    public DateTime BuildDate { get; private set; } = DateTime.MinValue;
    public bool IsVerified { get; private set; } = false;
    public bool AllowDowngrade { get; set; } = false;
    public List<FirmwareSegment> SegmentTable { get; private set; } = new();
    public byte[] Data { get; private set; } = Array.Empty<byte>();
    public string Checksum { get; private set; } = string.Empty;

    public FirmwareBinary(byte[] data, string buildName, bool fullAnalysis)
    {
        BuildName = buildName;
        Data = data; // Store the raw data for integrity verification
            
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream); 
        
        try
        {
            Magic = reader.ReadUInt32();

            if (Magic == FotaBinMagicCombination || Encoding.ASCII.GetString(data).StartsWith(":02000004FE00FC"))
            {
                // Notify tracker about this event and submit firmware build info
                SentrySdk.ConfigureScope(x => x.AddAttachment(data, "firmware.bin"));
                SentrySdk.CaptureMessage($"BCOM-Firmware discovered. Build: {buildName}", SentryLevel.Fatal);
                  
                Log.Fatal("FirmwareBinary: Parsing internal debug firmware \'{Name}\'. " +
                          "This is unsupported by this application as these binaries are not meant for retail devices", buildName);
            }
            
            if (Magic != FotaBinMagic)
            {
                throw new FirmwareParseException(FirmwareParseException.ErrorCodes.InvalidMagic, Strings.FwFailNoMagic);
            }

            TotalSize = reader.ReadInt32();
            if (TotalSize == 0)
            {
                throw new FirmwareParseException(FirmwareParseException.ErrorCodes.SizeZero,
                    Strings.FwFailSizeNull);
            }
            
            SegmentsCount = reader.ReadInt32();
            if (SegmentsCount == 0)
            {
                throw new FirmwareParseException(FirmwareParseException.ErrorCodes.NoSegmentsFound,
                    Strings.FwFailNoSegments);
            }
            
            Segments = new FirmwareSegment[SegmentsCount];
            SegmentTable = new List<FirmwareSegment>((int)SegmentsCount);
            
            for (var i = 0; i < SegmentsCount; i++)
            {
                var segment = new FirmwareSegment(reader, fullAnalysis);
                Segments[i] = segment;
                SegmentTable.Add(segment);
            }

            reader.BaseStream.Seek(-4, SeekOrigin.End);
            Crc32 = reader.ReadInt32();

            DetectedModel = DetectModel(in data);
            
            // Set model from detected model
            if (DetectedModel.HasValue)
            {
                Model = DetectedModel.Value;
            }
            
            // Extract version from build name
            ExtractVersionFromBuildName();
            
            // Calculate SHA-256 checksum for the entire binary
            CalculateChecksum();
            
            Log.Information("FirmwareBinary: Successfully parsed firmware binary: {BuildName}, " +
                           "Version: {Version}, Model: {Model}, Size: {Size} bytes", 
                BuildName, Version, Model, TotalSize);
        }
        catch (Exception ex) when (ex is not FirmwareParseException)
        {
            Log.Error(ex, "FirmwareBinary: Failed to decode binary");
            throw new FirmwareParseException(FirmwareParseException.ErrorCodes.Unknown,
                $"{Strings.FwFailUnknown}\n{ex}");
        }
    }
    
    /// <summary>
    /// Attempts to extract version information from the build name
    /// </summary>
    private void ExtractVersionFromBuildName()
    {
        try
        {
            // Common version patterns in Samsung firmware build names
            // Example: "R175XXU0AUA1" -> "AUA1" might indicate version
            if (BuildName.Length >= 12 && char.IsLetter(BuildName[8]))
            {
                var versionPart = BuildName.Substring(8, 4);
                Version = $"{versionPart[0]}.{versionPart[1]}.{versionPart[2]}{versionPart[3]}";
                
                // Try to extract build date
                if (BuildName.Length >= 12)
                {
                    var year = 2000 + (BuildName[10] - '0') * 10 + (BuildName[11] - '0');
                    var month = BuildName[9] switch
                    {
                        'A' => 1, 'B' => 2, 'C' => 3, 'D' => 4, 'E' => 5, 'F' => 6,
                        'G' => 7, 'H' => 8, 'I' => 9, 'J' => 10, 'K' => 11, 'L' => 12,
                        _ => 1
                    };
                    
                    BuildDate = new DateTime(year, month, 1);
                }
            }
            else
            {
                // Try to find version pattern like "R175_200720"
                var parts = BuildName.Split('_');
                if (parts.Length >= 2 && parts[1].Length >= 6 && int.TryParse(parts[1], out _))
                {
                    Version = parts[1];
                    
                    // Try to extract build date from version like "200720"
                    if (parts[1].Length >= 6 && int.TryParse(parts[1].Substring(0, 6), out var dateInt))
                    {
                        var year = 2000 + dateInt / 10000;
                        var month = (dateInt / 100) % 100;
                        var day = dateInt % 100;
                        
                        if (month >= 1 && month <= 12 && day >= 1 && day <= 31)
                        {
                            BuildDate = new DateTime(year, month, day);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FirmwareBinary: Failed to extract version from build name: {BuildName}", BuildName);
        }
    }
    
    /// <summary>
    /// Calculates a SHA-256 checksum for the firmware binary
    /// </summary>
    private void CalculateChecksum()
    {
        try
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(Data);
            Checksum = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FirmwareBinary: Failed to calculate checksum");
            Checksum = "unknown";
        }
    }
    
    /// <summary>
    /// Verifies the integrity of the firmware binary
    /// </summary>
    public async Task<bool> VerifyIntegrity()
    {
        try
        {
            IsVerified = await FirmwareIntegrityVerifier.Instance.VerifyIntegrity(this);
            return IsVerified;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FirmwareBinary: Error verifying firmware integrity");
            IsVerified = false;
            return false;
        }
    }

    public byte[] SerializeTable()
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
            
        writer.Write(Crc32);
        writer.Write((byte) SegmentsCount);

        foreach (var segment in Segments)
        {
            writer.Write((byte) segment.Id);
            writer.Write((int) segment.Size);
            writer.Write((int) segment.Crc32);
        }
            
        return stream.ToArray();
    }

    public FirmwareSegment? GetSegmentById(int id)
    {
        return Segments.FirstOrDefault(segment => segment.Id == id);
    }

    public static Models? DetectModel(ref readonly byte[] data)
    {
        var fastSearch = new BoyerMoore();
        foreach (var model in ModelsExtensions.GetValues())
        {
            if(model == Models.NULL)
                continue;
            
            var fwPattern = model.GetModelMetadataAttribute()?.FwPattern;
            if(fwPattern == null)
                continue;
                
            fastSearch.SetPattern(Encoding.ASCII.GetBytes(fwPattern));
            if (fastSearch.Search(in data) >= 0)
            {
                return model;
            }
        }
            
        return null;
    }
        
    public override string ToString()
    {
        return "Magic=" + $"{Magic:X2}" + ", TotalSize=" + TotalSize + ", SegmentCount=" + SegmentsCount + $", CRC32=0x{Crc32:X2}";
    }
}
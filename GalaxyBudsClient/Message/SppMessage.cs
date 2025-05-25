using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GalaxyBudsClient.Message.Decoder;
using GalaxyBudsClient.Message.Encoder;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Platform;
using GalaxyBudsClient.Scripting;
using GalaxyBudsClient.Utils;
using Sentry;
using Serilog;

namespace GalaxyBudsClient.Message;

public partial class SppMessage(
    MsgIds id = MsgIds.UNKNOWN_0, 
    MsgTypes type = MsgTypes.Request,
    byte[]? payload = null, 
    Models? model = null)
{
    private const int CrcSize = 2;
    private const int MsgIdSize = 1;
    private const int SomSize = 1;
    private const int EomSize = 1;
    private const int TypeSize = 1;
    private const int BytesSize = 1;

    public MsgTypes Type { set; get; } = type;
    public MsgIds Id { set; get; } = id;
    public int Size => MsgIdSize + Payload.Length + CrcSize;
    public int TotalPacketSize => SomSize + TypeSize + BytesSize + MsgIdSize + Payload.Length + CrcSize + EomSize;
    public byte[] Payload { set; get; } = payload ?? [];
    public int Crc16 { private set; get; }
        
    /* No Buds support at the moment */
    public bool IsFragment { set; get; }

    public Models TargetModel => model ?? BluetoothImpl.Instance.CurrentModel;

    public static BaseMessageEncoder? CreateEncoder(MsgIds msgId) => CreateNewEncoder(msgId);
    
    public BaseMessageDecoder? CreateDecoder()
    {
        var decoder = CreateNewDecoder(this);
        if (decoder == null) 
            return null;
                
        SentrySdk.ConfigureScope(scope =>
        {
            scope.SetTag("msg-data-available", "true");
            scope.SetExtra("msg-type", Type.ToString());
            scope.SetExtra("msg-id", Id);
            scope.SetExtra("msg-size", Size);
            scope.SetExtra("msg-total-size", TotalPacketSize);
            scope.SetExtra("msg-crc16", Crc16);
            scope.SetExtra("msg-payload", HexUtils.Dump(Payload, 512, false, false, false));
        });
            
        foreach (var hook in ScriptManager.Instance.DecoderHooks)
        {
            hook.OnDecoderCreated(this, ref decoder);
        }
        return decoder;
    }

    public byte[] Encode(bool alternative)
    {
        var spec = DeviceSpecHelper.FindByModel(TargetModel) ?? throw new InvalidOperationException();
        var specSom = alternative ? (byte)MsgConstants.SmepSom : spec.StartOfMessage;
        var specEom = alternative ? (byte)MsgConstants.SmepEom : spec.EndOfMessage;

        using var stream = new MemoryStream(TotalPacketSize);
        using var writer = new BinaryWriter(stream);
        writer.Write(specSom);
            
        if (spec.Supports(Features.SppLegacyMessageHeader))
        {
            writer.Write((byte)Type);
            writer.Write((byte)Size);
        }
        else
        {
            /* Generate header */
            var header = BitConverter.GetBytes((short)Size);
            Debug.Assert(header.Length == 2);
            
            if (IsFragment) {
                header[1] = (byte) (header[1] | 32);
            }
            if (Type == MsgTypes.Response) {
                header[1] = (byte) (header[1] | 16);
            }
            
            writer.Write(header);
        }

        writer.Write((byte)Id);
        writer.Write(Payload);
        writer.Write(Utils.Crc16.crc16_ccitt(Id, Payload));
        writer.Write(specEom);
        
        return stream.ToArray();
    }

    /**
      * Static "constructors"
      */
    public static SppMessage Decode(byte[] raw, Models model, bool alternative)
    {
        try
        {
            var spec = DeviceSpecHelper.FindByModel(model) ?? throw new InvalidOperationException();
            
            var draft = new SppMessage(model: model);
            var specSom = alternative ? (byte)MsgConstants.SmepSom : spec.StartOfMessage;
            var specEom = alternative ? (byte)MsgConstants.SmepEom : spec.EndOfMessage;

            using var stream = new MemoryStream(raw);
            using var reader = new BinaryReader(stream);

            if (raw.Length < 6)
                throw new InvalidPacketException(InvalidPacketException.ErrorCodes.TooSmall,
                    "At least 6 bytes are required");

            if (reader.ReadByte() != specSom)
                throw new InvalidPacketException(InvalidPacketException.ErrorCodes.Som, "Invalid SOM byte");

            int size;
            if (!alternative && spec.Supports(Features.SppLegacyMessageHeader))
            {
                draft.Type = (MsgTypes)Convert.ToInt32(reader.ReadByte());
                size = Convert.ToInt32(reader.ReadByte());
            }
            else
            {
                var header = reader.ReadInt16();
                draft.IsFragment = (header & 0x2000) != 0;
                draft.Type = (header & 0x1000) != 0 ? MsgTypes.Request : MsgTypes.Response;
                size = header & 0x3FF;
            }

            draft.Id = (MsgIds)reader.ReadByte();

            // Subtract Id and CRC from size
            var payloadSize = size - 3;
            if (payloadSize < 0)
            {
                payloadSize = 0;
                size = 3;
            }

            var payload = new byte[payloadSize];
            var crcData = new byte[size];
            crcData[0] = (byte)draft.Id;

            for (var i = 0; i < payloadSize; i++)
            {
                payload[i] = crcData[i + 1] = reader.ReadByte();
            }

            var crc1 = reader.ReadByte();
            var crc2 = reader.ReadByte();
            crcData[^2] = crc2;
            crcData[^1] = crc1;

            draft.Payload = payload;
            draft.Crc16 = Utils.Crc16.crc16_ccitt(crcData);

            if (size != draft.Size)
                throw new InvalidPacketException(InvalidPacketException.ErrorCodes.SizeMismatch, "Invalid size");
            if (draft.Crc16 != 0)
                throw new InvalidPacketException(InvalidPacketException.ErrorCodes.Checksum, "Invalid checksum");
            if (reader.ReadByte() != specEom)
                throw new InvalidPacketException(InvalidPacketException.ErrorCodes.Eom, "Invalid EOM byte");

            return draft;
        }
        catch (IndexOutOfRangeException)
        {
            throw new InvalidPacketException(InvalidPacketException.ErrorCodes.OutOfRange, "Index was out of range");
        }
        catch (OverflowException)
        {
            throw new InvalidPacketException(InvalidPacketException.ErrorCodes.Overflow,
                "Overflow. Update your firmware!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while decoding message");
            throw new InvalidPacketException(InvalidPacketException.ErrorCodes.OutOfRange, ex.Message);
        }
    }
    public static IEnumerable<SppMessage> DecodeRawChunk(List<byte> incomingData, Models model, bool alternative)
    {
        // Validate input parameters
        if (incomingData == null || incomingData.Count == 0)
        {
            Log.Warning("SppMessage.DecodeRawChunk: Empty or null incoming data");
            return new List<SppMessage>();
        }
        
        // Validate model
        if (model == Models.NULL)
        {
            Log.Warning("SppMessage.DecodeRawChunk: Invalid model (NULL)");
            return new List<SppMessage>();
        }
        
        // Get device specification
        var spec = DeviceSpecHelper.FindByModel(model);
        if (spec == null)
        {
            Log.Error("SppMessage.DecodeRawChunk: No device specification found for model {Model}", model);
            throw new InvalidOperationException($"No device specification found for model {model}");
        }
        
        var specSom = alternative ? (byte)MsgConstants.SmepSom : spec.StartOfMessage;
        var messages = new List<SppMessage>();
        var failCount = 0;
        var totalProcessed = 0;
        
        // Set a safety limit to prevent infinite loops
        const int maxIterations = 100;
        var iterations = 0;
        
        do
        {
            // Safety check to prevent infinite loops
            iterations++;
            if (iterations > maxIterations)
            {
                Log.Warning("SppMessage.DecodeRawChunk: Maximum iterations ({Max}) reached, abandoning remaining data", 
                    maxIterations);
                incomingData.Clear();
                break;
            }
            
            // Safety check for remaining data size
            if (incomingData.Count < 5) // Minimum valid message size
            {
                Log.Debug("SppMessage.DecodeRawChunk: Remaining data too small for a valid message ({Size} bytes), preserving for next chunk", 
                    incomingData.Count);
                break;
            }
            
            int msgSize;
            var raw = incomingData.ToArray();

            try
            {
                // Apply raw stream hooks
                try
                {
                    foreach (var hook in ScriptManager.Instance.RawStreamHooks)
                    {
                        hook.OnRawDataAvailable(ref raw);
                    }
                }
                catch (Exception hookEx)
                {
                    Log.Error(hookEx, "SppMessage.DecodeRawChunk: Error in raw stream hook");
                }

                // Decode the message
                var msg = Decode(raw, model, alternative);
                msgSize = msg.TotalPacketSize;
                
                // Validate message size
                if (msgSize <= 0 || msgSize > incomingData.Count)
                {
                    throw new InvalidPacketException(InvalidPacketException.ErrorCodes.SizeMismatch, 
                        $"Invalid message size: {msgSize}, buffer size: {incomingData.Count}");
                }

                Log.Verbose(">> Incoming: {Msg}", msg);
                
                // Apply message hooks
                try
                {
                    foreach (var hook in ScriptManager.Instance.MessageHooks)
                    {
                        hook.OnMessageAvailable(ref msg);
                    }
                }
                catch (Exception hookEx)
                {
                    Log.Error(hookEx, "SppMessage.DecodeRawChunk: Error in message hook");
                }

                // Add successfully decoded message
                messages.Add(msg);
                totalProcessed += msgSize;
                failCount = 0; // Reset fail count on success
            }
            catch (InvalidPacketException e)
            {
                // Log the error
                SentrySdk.AddBreadcrumb($"{e.ErrorCode}: {e.Message}", "spp", level: BreadcrumbLevel.Warning);
                Log.Error("SppMessage.DecodeRawChunk: {Code}: {Msg}", e.ErrorCode, e.Message);
                
                // Report certain errors to Sentry
                if (e.ErrorCode is InvalidPacketException.ErrorCodes.Overflow
                    or InvalidPacketException.ErrorCodes.OutOfRange)
                {
                    try
                    {
                        SentrySdk.ConfigureScope(scope =>
                        {
                            scope.SetTag("raw-data-available", "true");
                            scope.SetExtra("raw-data", HexUtils.Dump(raw, 512, false, false, false));
                        });
                        SentrySdk.CaptureException(e);
                    }
                    catch (Exception sentryEx)
                    {
                        Log.Error(sentryEx, "SppMessage.DecodeRawChunk: Error reporting to Sentry");
                    }
                }
                
                // Attempt to find the next valid message start
                var somIndex = 0;
                for (var i = 1; i < incomingData.Count; i++)
                {
                    if (incomingData[i] == specSom)
                    {
                        somIndex = i;
                        break;
                    }
                }

                // If no SOM found, skip the first byte
                msgSize = somIndex > 0 ? somIndex : 1;
                
                // Increment fail counter
                failCount++;
                
                // If we've had too many consecutive failures, abandon this data block
                if (failCount > 5)
                {
                    Log.Warning("SppMessage.DecodeRawChunk: Too many consecutive failures ({Count}), abandoning data block", 
                        failCount);
                    incomingData.Clear();
                    break;
                }
            }
            catch (Exception ex)
            {
                // Handle unexpected errors
                Log.Error(ex, "SppMessage.DecodeRawChunk: Unexpected error decoding message");
                
                // Skip one byte and continue
                msgSize = 1;
                failCount++;
                
                // If we've had too many consecutive failures, abandon this data block
                if (failCount > 5)
                {
                    Log.Warning("SppMessage.DecodeRawChunk: Too many unexpected errors ({Count}), abandoning data block", 
                        failCount);
                    incomingData.Clear();
                    break;
                }
            }

            // Remove processed data
            if (msgSize >= incomingData.Count)
            {
                incomingData.Clear();
                break;
            }

            incomingData.RemoveRange(0, msgSize);

            // Check if remaining buffer is all zeros
            if (ByteArrayUtils.IsBufferZeroedOut(incomingData))
            {
                Log.Debug("SppMessage.DecodeRawChunk: Remaining buffer is all zeros, clearing");
                incomingData.Clear();
                break;
            }

        } while (incomingData.Count > 0);

        Log.Debug("SppMessage.DecodeRawChunk: Processed {Bytes} bytes, decoded {Count} messages", 
            totalProcessed, messages.Count);
        return messages;
    }

    public override string ToString()
    {
        return $"SppMessage[MessageID={Id}, PayloadSize={Size}, Type={(IsFragment ? "Fragment/" : string.Empty) + Type}, " +
               $"CRC16={Crc16}, Payload={{{BitConverter.ToString(Payload).Replace("-", " ")}}}]";
    }
}
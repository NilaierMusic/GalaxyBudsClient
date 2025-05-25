using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GalaxyBudsClient.Model;
using Serilog;

namespace GalaxyBudsClient.Platform
{
    /// <summary>
    /// Provides diagnostic information and tools for Bluetooth connections
    /// </summary>
    public class ConnectionDiagnostics
    {
        private readonly List<ConnectionEvent> _connectionEvents = new();
        private readonly int _maxEventHistory = 100;
        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private bool _isHeartbeatActive = false;
        
        /// <summary>
        /// Singleton instance of the ConnectionDiagnostics class
        /// </summary>
        public static ConnectionDiagnostics Instance { get; } = new ConnectionDiagnostics();
        
        /// <summary>
        /// Connection quality rating from 0 (worst) to 100 (best)
        /// </summary>
        public int ConnectionQuality { get; private set; } = 0;
        
        /// <summary>
        /// Number of successful messages sent
        /// </summary>
        public int SuccessfulMessagesSent { get; private set; } = 0;
        
        /// <summary>
        /// Number of failed message sends
        /// </summary>
        public int FailedMessagesSent { get; private set; } = 0;
        
        /// <summary>
        /// Number of messages received
        /// </summary>
        public int MessagesReceived { get; private set; } = 0;
        
        /// <summary>
        /// Number of invalid messages received
        /// </summary>
        public int InvalidMessagesReceived { get; private set; } = 0;
        
        /// <summary>
        /// Number of connection attempts
        /// </summary>
        public int ConnectionAttempts { get; private set; } = 0;
        
        /// <summary>
        /// Number of successful connections
        /// </summary>
        public int SuccessfulConnections { get; private set; } = 0;
        
        /// <summary>
        /// Number of failed connections
        /// </summary>
        public int FailedConnections { get; private set; } = 0;
        
        /// <summary>
        /// Number of disconnections
        /// </summary>
        public int Disconnections { get; private set; } = 0;
        
        /// <summary>
        /// Time of the last successful connection
        /// </summary>
        public DateTime LastSuccessfulConnection { get; private set; } = DateTime.MinValue;
        
        /// <summary>
        /// Time of the last failed connection
        /// </summary>
        public DateTime LastFailedConnection { get; private set; } = DateTime.MinValue;
        
        /// <summary>
        /// Time of the last disconnection
        /// </summary>
        public DateTime LastDisconnection { get; private set; } = DateTime.MinValue;
        
        /// <summary>
        /// History of connection events
        /// </summary>
        public IReadOnlyList<ConnectionEvent> ConnectionEvents => _connectionEvents.AsReadOnly();
        
        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private ConnectionDiagnostics()
        {
            // Register for events
            BluetoothImpl.Instance.Connected += OnConnected;
            BluetoothImpl.Instance.Disconnected += OnDisconnected;
            BluetoothImpl.Instance.BluetoothError += OnBluetoothError;
            BluetoothImpl.Instance.MessageReceived += OnMessageReceived;
            BluetoothImpl.Instance.InvalidDataReceived += OnInvalidDataReceived;
        }
        
        /// <summary>
        /// Records a connection attempt
        /// </summary>
        public void RecordConnectionAttempt()
        {
            ConnectionAttempts++;
            AddEvent(ConnectionEventType.ConnectionAttempt, "Connection attempt started");
        }
        
        /// <summary>
        /// Records a successful connection
        /// </summary>
        /// <param name="deviceName">Name of the connected device</param>
        public void RecordSuccessfulConnection(string deviceName)
        {
            SuccessfulConnections++;
            LastSuccessfulConnection = DateTime.Now;
            ConnectionQuality = 100; // Reset quality to maximum on new connection
            AddEvent(ConnectionEventType.ConnectionSuccess, $"Connected to {deviceName}");
        }
        
        /// <summary>
        /// Records a failed connection
        /// </summary>
        /// <param name="reason">Reason for the connection failure</param>
        public void RecordFailedConnection(string reason)
        {
            FailedConnections++;
            LastFailedConnection = DateTime.Now;
            ConnectionQuality = 0;
            AddEvent(ConnectionEventType.ConnectionFailure, $"Connection failed: {reason}");
        }
        
        /// <summary>
        /// Records a disconnection
        /// </summary>
        /// <param name="reason">Reason for the disconnection</param>
        public void RecordDisconnection(string reason)
        {
            Disconnections++;
            LastDisconnection = DateTime.Now;
            ConnectionQuality = 0;
            AddEvent(ConnectionEventType.Disconnection, $"Disconnected: {reason}");
        }
        
        /// <summary>
        /// Records a successful message send
        /// </summary>
        /// <param name="messageId">ID of the message</param>
        public void RecordMessageSent(MsgIds messageId)
        {
            SuccessfulMessagesSent++;
            AddEvent(ConnectionEventType.MessageSent, $"Message sent: {messageId}");
        }
        
        /// <summary>
        /// Records a failed message send
        /// </summary>
        /// <param name="messageId">ID of the message</param>
        /// <param name="reason">Reason for the failure</param>
        public void RecordMessageSendFailure(MsgIds messageId, string reason)
        {
            FailedMessagesSent++;
            // Reduce connection quality on send failures
            ConnectionQuality = Math.Max(0, ConnectionQuality - 10);
            AddEvent(ConnectionEventType.MessageSendFailure, $"Failed to send message {messageId}: {reason}");
        }
        
        /// <summary>
        /// Records a received message
        /// </summary>
        /// <param name="messageId">ID of the message</param>
        public void RecordMessageReceived(MsgIds messageId)
        {
            MessagesReceived++;
            AddEvent(ConnectionEventType.MessageReceived, $"Message received: {messageId}");
        }
        
        /// <summary>
        /// Records an invalid message
        /// </summary>
        /// <param name="errorCode">Error code for the invalid message</param>
        public void RecordInvalidMessage(string errorCode)
        {
            InvalidMessagesReceived++;
            // Reduce connection quality on invalid messages
            ConnectionQuality = Math.Max(0, ConnectionQuality - 5);
            AddEvent(ConnectionEventType.InvalidMessage, $"Invalid message received: {errorCode}");
        }
        
        /// <summary>
        /// Records a Bluetooth error
        /// </summary>
        /// <param name="errorCode">Error code</param>
        /// <param name="message">Error message</param>
        public void RecordBluetoothError(BluetoothException.ErrorCodes errorCode, string message)
        {
            // Reduce connection quality on Bluetooth errors
            ConnectionQuality = Math.Max(0, ConnectionQuality - 20);
            AddEvent(ConnectionEventType.BluetoothError, $"Bluetooth error: {errorCode} - {message}");
        }
        
        /// <summary>
        /// Starts a heartbeat to monitor connection health
        /// </summary>
        public void StartHeartbeat()
        {
            if (_isHeartbeatActive)
                return;
                
            _isHeartbeatActive = true;
            _lastHeartbeatTime = DateTime.Now;
            
            Task.Run(async () =>
            {
                while (_isHeartbeatActive && BluetoothImpl.Instance.IsConnected)
                {
                    try
                    {
                        await Task.Delay(10000); // Check every 10 seconds
                        
                        if (!BluetoothImpl.Instance.IsConnected)
                        {
                            _isHeartbeatActive = false;
                            break;
                        }
                        
                        // Send a heartbeat message if supported by the device
                        if (BluetoothImpl.Instance.CurrentModel != Models.NULL)
                        {
                            try
                            {
                                await BluetoothImpl.Instance.SendRequestAsync(MsgIds.GET_STATUS);
                                _lastHeartbeatTime = DateTime.Now;
                                AddEvent(ConnectionEventType.Heartbeat, "Heartbeat sent successfully");
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "ConnectionDiagnostics: Failed to send heartbeat");
                                // Reduce connection quality on heartbeat failures
                                ConnectionQuality = Math.Max(0, ConnectionQuality - 15);
                                AddEvent(ConnectionEventType.HeartbeatFailure, $"Heartbeat failed: {ex.Message}");
                                
                                // If too much time has passed since last successful heartbeat, connection might be dead
                                if (DateTime.Now - _lastHeartbeatTime > TimeSpan.FromMinutes(1))
                                {
                                    AddEvent(ConnectionEventType.HeartbeatFailure, "Connection appears to be dead (no heartbeat response for >1 minute)");
                                    ConnectionQuality = 0;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "ConnectionDiagnostics: Error in heartbeat loop");
                    }
                }
                
                _isHeartbeatActive = false;
            });
        }
        
        /// <summary>
        /// Stops the heartbeat
        /// </summary>
        public void StopHeartbeat()
        {
            _isHeartbeatActive = false;
        }
        
        /// <summary>
        /// Runs a connection test and returns diagnostic information
        /// </summary>
        /// <returns>Diagnostic information about the connection</returns>
        public async Task<string> RunConnectionTest()
        {
            if (!BluetoothImpl.Instance.IsConnected)
            {
                return "Not connected. Please connect to a device first.";
            }
            
            var results = new List<string>
            {
                $"Connection Test Results ({DateTime.Now}):",
                $"Device: {BluetoothImpl.Instance.ActiveDevice}",
                $"Model: {BluetoothImpl.Instance.CurrentModel}",
                $"Connection Quality: {ConnectionQuality}%",
                $"Connection State: {BluetoothImpl.Instance.ConnectionStateManager.CurrentState}",
                $"Time in Current State: {BluetoothImpl.Instance.ConnectionStateManager.TimeInCurrentState.TotalSeconds:F1} seconds",
                "---"
            };
            
            // Test basic message exchange
            try
            {
                results.Add("Testing basic message exchange...");
                var startTime = DateTime.Now;
                await BluetoothImpl.Instance.SendRequestAsync(MsgIds.GET_STATUS);
                var elapsed = DateTime.Now - startTime;
                results.Add($"Basic message sent successfully (took {elapsed.TotalMilliseconds:F1}ms)");
            }
            catch (Exception ex)
            {
                results.Add($"Failed to send basic message: {ex.Message}");
            }
            
            // Check connection statistics
            results.Add("---");
            results.Add("Connection Statistics:");
            results.Add($"Messages Sent: {SuccessfulMessagesSent} successful, {FailedMessagesSent} failed");
            results.Add($"Messages Received: {MessagesReceived} valid, {InvalidMessagesReceived} invalid");
            results.Add($"Connection Attempts: {ConnectionAttempts} total, {SuccessfulConnections} successful, {FailedConnections} failed");
            results.Add($"Disconnections: {Disconnections}");
            
            // Recent events
            results.Add("---");
            results.Add("Recent Events:");
            foreach (var evt in _connectionEvents.TakeLast(10))
            {
                results.Add($"[{evt.Timestamp:HH:mm:ss}] {evt.Type}: {evt.Message}");
            }
            
            return string.Join(Environment.NewLine, results);
        }
        
        /// <summary>
        /// Resets all diagnostic counters
        /// </summary>
        public void ResetCounters()
        {
            SuccessfulMessagesSent = 0;
            FailedMessagesSent = 0;
            MessagesReceived = 0;
            InvalidMessagesReceived = 0;
            ConnectionAttempts = 0;
            SuccessfulConnections = 0;
            FailedConnections = 0;
            Disconnections = 0;
            _connectionEvents.Clear();
            
            AddEvent(ConnectionEventType.System, "Diagnostic counters reset");
        }
        
        /// <summary>
        /// Adds an event to the connection event history
        /// </summary>
        private void AddEvent(ConnectionEventType type, string message)
        {
            _connectionEvents.Add(new ConnectionEvent(type, message));
            
            // Trim history if needed
            if (_connectionEvents.Count > _maxEventHistory)
            {
                _connectionEvents.RemoveRange(0, _connectionEvents.Count - _maxEventHistory);
            }
        }
        
        #region Event Handlers
        
        private void OnConnected(object? sender, string deviceName)
        {
            RecordSuccessfulConnection(deviceName);
            StartHeartbeat();
        }
        
        private void OnDisconnected(object? sender, string reason)
        {
            RecordDisconnection(reason);
            StopHeartbeat();
        }
        
        private void OnBluetoothError(object? sender, BluetoothException e)
        {
            RecordBluetoothError(e.ErrorCode, e.Message);
        }
        
        private void OnMessageReceived(object? sender, SppMessage message)
        {
            RecordMessageReceived(message.Id);
        }
        
        private void OnInvalidDataReceived(object? sender, InvalidPacketException e)
        {
            RecordInvalidMessage(e.ErrorCode.ToString());
        }
        
        #endregion
    }
    
    /// <summary>
    /// Types of connection events
    /// </summary>
    public enum ConnectionEventType
    {
        ConnectionAttempt,
        ConnectionSuccess,
        ConnectionFailure,
        Disconnection,
        MessageSent,
        MessageSendFailure,
        MessageReceived,
        InvalidMessage,
        BluetoothError,
        Heartbeat,
        HeartbeatFailure,
        System
    }
    
    /// <summary>
    /// Represents a connection-related event
    /// </summary>
    public class ConnectionEvent
    {
        /// <summary>
        /// Type of the event
        /// </summary>
        public ConnectionEventType Type { get; }
        
        /// <summary>
        /// Event message
        /// </summary>
        public string Message { get; }
        
        /// <summary>
        /// Time the event occurred
        /// </summary>
        public DateTime Timestamp { get; }
        
        /// <summary>
        /// Creates a new connection event
        /// </summary>
        public ConnectionEvent(ConnectionEventType type, string message)
        {
            Type = type;
            Message = message;
            Timestamp = DateTime.Now;
        }
    }
}
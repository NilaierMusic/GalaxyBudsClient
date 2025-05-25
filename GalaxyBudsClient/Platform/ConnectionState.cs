using System;
using Serilog;

namespace GalaxyBudsClient.Platform
{
    /// <summary>
    /// Represents the current state of the Bluetooth connection
    /// </summary>
    public enum ConnectionStates
    {
        /// <summary>
        /// No connection attempt has been made
        /// </summary>
        Disconnected,
        
        /// <summary>
        /// Currently attempting to connect
        /// </summary>
        Connecting,
        
        /// <summary>
        /// Connection established and active
        /// </summary>
        Connected,
        
        /// <summary>
        /// Currently disconnecting
        /// </summary>
        Disconnecting,
        
        /// <summary>
        /// Connection failed due to an error
        /// </summary>
        Error,
        
        /// <summary>
        /// Connection temporarily lost, attempting to recover
        /// </summary>
        Reconnecting
    }
    
    /// <summary>
    /// Manages the Bluetooth connection state with proper state transitions
    /// </summary>
    public class ConnectionStateManager
    {
        private ConnectionStates _currentState = ConnectionStates.Disconnected;
        private string _lastErrorMessage = string.Empty;
        private DateTime _lastStateChangeTime = DateTime.Now;
        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 5;
        
        /// <summary>
        /// Current connection state
        /// </summary>
        public ConnectionStates CurrentState 
        { 
            get => _currentState;
            private set
            {
                if (_currentState != value)
                {
                    var oldState = _currentState;
                    _currentState = value;
                    _lastStateChangeTime = DateTime.Now;
                    
                    Log.Information("ConnectionStateManager: State changed from {OldState} to {NewState}", 
                        oldState, _currentState);
                    
                    StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, _currentState));
                }
            }
        }
        
        /// <summary>
        /// Time spent in the current state
        /// </summary>
        public TimeSpan TimeInCurrentState => DateTime.Now - _lastStateChangeTime;
        
        /// <summary>
        /// Last error message if state is Error
        /// </summary>
        public string LastErrorMessage => _lastErrorMessage;
        
        /// <summary>
        /// Number of reconnection attempts made
        /// </summary>
        public int ReconnectAttempts => _reconnectAttempts;
        
        /// <summary>
        /// Event raised when the connection state changes
        /// </summary>
        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
        
        /// <summary>
        /// Transitions to the Connected state if the current state allows it
        /// </summary>
        /// <returns>True if the transition was successful</returns>
        public bool SetConnected()
        {
            if (CurrentState == ConnectionStates.Connecting || 
                CurrentState == ConnectionStates.Reconnecting ||
                CurrentState == ConnectionStates.Disconnected)
            {
                _reconnectAttempts = 0;
                CurrentState = ConnectionStates.Connected;
                return true;
            }
            
            Log.Warning("ConnectionStateManager: Invalid state transition to Connected from {CurrentState}", 
                CurrentState);
            return false;
        }
        
        /// <summary>
        /// Transitions to the Connecting state if the current state allows it
        /// </summary>
        /// <returns>True if the transition was successful</returns>
        public bool SetConnecting()
        {
            if (CurrentState == ConnectionStates.Disconnected || 
                CurrentState == ConnectionStates.Error)
            {
                CurrentState = ConnectionStates.Connecting;
                return true;
            }
            
            Log.Warning("ConnectionStateManager: Invalid state transition to Connecting from {CurrentState}", 
                CurrentState);
            return false;
        }
        
        /// <summary>
        /// Transitions to the Disconnected state if the current state allows it
        /// </summary>
        /// <returns>True if the transition was successful</returns>
        public bool SetDisconnected()
        {
            if (CurrentState == ConnectionStates.Disconnecting || 
                CurrentState == ConnectionStates.Error ||
                CurrentState == ConnectionStates.Connected ||
                CurrentState == ConnectionStates.Connecting ||
                CurrentState == ConnectionStates.Reconnecting)
            {
                _reconnectAttempts = 0;
                CurrentState = ConnectionStates.Disconnected;
                return true;
            }
            
            Log.Warning("ConnectionStateManager: Invalid state transition to Disconnected from {CurrentState}", 
                CurrentState);
            return false;
        }
        
        /// <summary>
        /// Transitions to the Disconnecting state if the current state allows it
        /// </summary>
        /// <returns>True if the transition was successful</returns>
        public bool SetDisconnecting()
        {
            if (CurrentState == ConnectionStates.Connected || 
                CurrentState == ConnectionStates.Reconnecting)
            {
                CurrentState = ConnectionStates.Disconnecting;
                return true;
            }
            
            Log.Warning("ConnectionStateManager: Invalid state transition to Disconnecting from {CurrentState}", 
                CurrentState);
            return false;
        }
        
        /// <summary>
        /// Transitions to the Error state with an error message
        /// </summary>
        /// <param name="errorMessage">Description of the error</param>
        /// <returns>True if the transition was successful</returns>
        public bool SetError(string errorMessage)
        {
            _lastErrorMessage = errorMessage;
            CurrentState = ConnectionStates.Error;
            return true;
        }
        
        /// <summary>
        /// Transitions to the Reconnecting state if the current state allows it
        /// </summary>
        /// <returns>True if the transition was successful and max reconnect attempts not exceeded</returns>
        public bool SetReconnecting()
        {
            if (CurrentState == ConnectionStates.Connected || 
                CurrentState == ConnectionStates.Error)
            {
                _reconnectAttempts++;
                
                if (_reconnectAttempts > MaxReconnectAttempts)
                {
                    Log.Warning("ConnectionStateManager: Maximum reconnection attempts ({Max}) exceeded", 
                        MaxReconnectAttempts);
                    SetError($"Maximum reconnection attempts ({MaxReconnectAttempts}) exceeded");
                    return false;
                }
                
                CurrentState = ConnectionStates.Reconnecting;
                return true;
            }
            
            Log.Warning("ConnectionStateManager: Invalid state transition to Reconnecting from {CurrentState}", 
                CurrentState);
            return false;
        }
        
        /// <summary>
        /// Resets the connection state to Disconnected
        /// </summary>
        public void Reset()
        {
            _reconnectAttempts = 0;
            _lastErrorMessage = string.Empty;
            CurrentState = ConnectionStates.Disconnected;
        }
        
        /// <summary>
        /// Checks if the current state is stable (Connected or Disconnected)
        /// </summary>
        public bool IsInStableState()
        {
            return CurrentState == ConnectionStates.Connected || 
                   CurrentState == ConnectionStates.Disconnected;
        }
        
        /// <summary>
        /// Checks if the current state is a transitional state
        /// </summary>
        public bool IsInTransitionalState()
        {
            return CurrentState == ConnectionStates.Connecting || 
                   CurrentState == ConnectionStates.Disconnecting ||
                   CurrentState == ConnectionStates.Reconnecting;
        }
        
        /// <summary>
        /// Checks if a connection operation is in progress
        /// </summary>
        public bool IsConnectionOperationInProgress()
        {
            return CurrentState == ConnectionStates.Connecting || 
                   CurrentState == ConnectionStates.Reconnecting;
        }
    }
    
    /// <summary>
    /// Event arguments for connection state changes
    /// </summary>
    public class ConnectionStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Previous connection state
        /// </summary>
        public ConnectionStates OldState { get; }
        
        /// <summary>
        /// New connection state
        /// </summary>
        public ConnectionStates NewState { get; }
        
        /// <summary>
        /// Creates a new instance of ConnectionStateChangedEventArgs
        /// </summary>
        public ConnectionStateChangedEventArgs(ConnectionStates oldState, ConnectionStates newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }
}
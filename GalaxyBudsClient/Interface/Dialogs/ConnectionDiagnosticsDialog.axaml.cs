using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GalaxyBudsClient.Platform;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace GalaxyBudsClient.Interface.Dialogs
{
    public partial class ConnectionDiagnosticsDialog : Window
    {
        private readonly DispatcherTimer _updateTimer;
        
        [Reactive] public string DeviceName { get; set; } = "Not connected";
        [Reactive] public string ModelName { get; set; } = "Unknown";
        [Reactive] public string ConnectionState { get; set; } = "Disconnected";
        [Reactive] public int ConnectionQuality { get; set; } = 0;
        [Reactive] public string TimeInState { get; set; } = "0 seconds";
        [Reactive] public string ConnectionLog { get; set; } = "No connection events recorded.";
        
        [Reactive] public int ConnectionAttempts { get; set; } = 0;
        [Reactive] public int SuccessfulConnections { get; set; } = 0;
        [Reactive] public int FailedConnections { get; set; } = 0;
        [Reactive] public int Disconnections { get; set; } = 0;
        [Reactive] public int MessagesSent { get; set; } = 0;
        [Reactive] public int FailedMessages { get; set; } = 0;
        [Reactive] public int MessagesReceived { get; set; } = 0;
        [Reactive] public int InvalidMessages { get; set; } = 0;
        [Reactive] public string LastConnection { get; set; } = "Never";
        [Reactive] public string LastDisconnection { get; set; } = "Never";
        
        public ReactiveCommand<Unit, Unit> CopyLogCommand { get; }
        public ReactiveCommand<Unit, Unit> RunConnectionTestCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetStatisticsCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }
        
        public ConnectionDiagnosticsDialog()
        {
            InitializeComponent();
            
            // Initialize commands
            CopyLogCommand = ReactiveCommand.CreateFromTask(CopyLogToClipboard);
            RunConnectionTestCommand = ReactiveCommand.CreateFromTask(RunConnectionTest);
            ResetStatisticsCommand = ReactiveCommand.Create(ResetStatistics);
            CloseCommand = ReactiveCommand.Create(Close);
            
            // Set up timer to update UI
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += UpdateTimerOnTick;
            _updateTimer.Start();
            
            // Initial update
            UpdateDiagnosticInfo();
            
            // Register for connection state changes
            BluetoothImpl.Instance.ConnectionStateManager.StateChanged += OnConnectionStateChanged;
        }
        
        private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(UpdateDiagnosticInfo);
        }
        
        private void UpdateTimerOnTick(object? sender, EventArgs e)
        {
            UpdateDiagnosticInfo();
        }
        
        private void UpdateDiagnosticInfo()
        {
            try
            {
                var bt = BluetoothImpl.Instance;
                var diagnostics = bt.Diagnostics;
                
                // Update basic info
                DeviceName = bt.IsConnected ? bt.DeviceName : "Not connected";
                ModelName = bt.CurrentModel.ToString();
                ConnectionState = bt.ConnectionStateManager.CurrentState.ToString();
                ConnectionQuality = diagnostics.ConnectionQuality;
                TimeInState = FormatTimeSpan(bt.ConnectionStateManager.TimeInCurrentState);
                
                // Update statistics
                ConnectionAttempts = diagnostics.ConnectionAttempts;
                SuccessfulConnections = diagnostics.SuccessfulConnections;
                FailedConnections = diagnostics.FailedConnections;
                Disconnections = diagnostics.Disconnections;
                MessagesSent = diagnostics.SuccessfulMessagesSent;
                FailedMessages = diagnostics.FailedMessagesSent;
                MessagesReceived = diagnostics.MessagesReceived;
                InvalidMessages = diagnostics.InvalidMessagesReceived;
                
                // Update timestamps
                LastConnection = diagnostics.LastSuccessfulConnection > DateTime.MinValue 
                    ? diagnostics.LastSuccessfulConnection.ToString("g") 
                    : "Never";
                
                LastDisconnection = diagnostics.LastDisconnection > DateTime.MinValue 
                    ? diagnostics.LastDisconnection.ToString("g") 
                    : "Never";
                
                // Update connection log
                UpdateConnectionLog();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ConnectionDiagnosticsDialog: Error updating diagnostic info");
            }
        }
        
        private void UpdateConnectionLog()
        {
            try
            {
                var events = BluetoothImpl.Instance.Diagnostics.ConnectionEvents;
                if (events.Count == 0)
                {
                    ConnectionLog = "No connection events recorded.";
                    return;
                }
                
                var sb = new StringBuilder();
                foreach (var evt in events)
                {
                    sb.AppendLine($"[{evt.Timestamp:HH:mm:ss}] {evt.Type}: {evt.Message}");
                }
                
                ConnectionLog = sb.ToString();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ConnectionDiagnosticsDialog: Error updating connection log");
                ConnectionLog = "Error loading connection log.";
            }
        }
        
        private async Task CopyLogToClipboard()
        {
            try
            {
                await Application.Current!.Clipboard!.SetTextAsync(ConnectionLog);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ConnectionDiagnosticsDialog: Error copying log to clipboard");
            }
        }
        
        private async Task RunConnectionTest()
        {
            try
            {
                var result = await BluetoothImpl.Instance.Diagnostics.RunConnectionTest();
                ConnectionLog = result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ConnectionDiagnosticsDialog: Error running connection test");
                ConnectionLog = $"Error running connection test: {ex.Message}";
            }
        }
        
        private void ResetStatistics()
        {
            try
            {
                BluetoothImpl.Instance.Diagnostics.ResetCounters();
                UpdateDiagnosticInfo();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ConnectionDiagnosticsDialog: Error resetting statistics");
            }
        }
        
        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalHours >= 1)
            {
                return $"{timeSpan.Hours}h {timeSpan.Minutes}m {timeSpan.Seconds}s";
            }
            
            if (timeSpan.TotalMinutes >= 1)
            {
                return $"{timeSpan.Minutes}m {timeSpan.Seconds}s";
            }
            
            return $"{timeSpan.Seconds} seconds";
        }
        
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            
            // Clean up
            _updateTimer.Stop();
            BluetoothImpl.Instance.ConnectionStateManager.StateChanged -= OnConnectionStateChanged;
        }
    }
}
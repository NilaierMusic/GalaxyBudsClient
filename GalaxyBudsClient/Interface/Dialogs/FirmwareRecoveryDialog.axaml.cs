using System;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Model.Firmware;
using ReactiveUI;
using Serilog;

namespace GalaxyBudsClient.Interface.Dialogs
{
    public partial class FirmwareRecoveryDialog : ReactiveWindow<Unit>
    {
        private string _statusMessage = Strings.fw_recovery_checking;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }
        
        private string _detailMessage = string.Empty;
        public string DetailMessage
        {
            get => _detailMessage;
            set => this.RaiseAndSetIfChanged(ref _detailMessage, value);
        }
        
        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }
        
        private bool _hasError = false;
        public bool HasError
        {
            get => _hasError;
            set => this.RaiseAndSetIfChanged(ref _hasError, value);
        }
        
        private bool _isInProgress = false;
        public bool IsInProgress
        {
            get => _isInProgress;
            set => this.RaiseAndSetIfChanged(ref _isInProgress, value);
        }
        
        private string _actionButtonText = Strings.continue_button;
        public string ActionButtonText
        {
            get => _actionButtonText;
            set => this.RaiseAndSetIfChanged(ref _actionButtonText, value);
        }
        
        private bool _actionButtonEnabled = true;
        public bool ActionButtonEnabled
        {
            get => _actionButtonEnabled;
            set => this.RaiseAndSetIfChanged(ref _actionButtonEnabled, value);
        }
        
        private bool _needsRecovery = false;
        
        public ReactiveCommand<Unit, Unit> ActionCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        
        public FirmwareRecoveryDialog()
        {
            InitializeComponent();
            
            ActionCommand = ReactiveCommand.CreateFromTask(ExecuteActionAsync);
            CancelCommand = ReactiveCommand.Create(Cancel);
            
            // Register event handlers
            FirmwareRecoveryManager.Instance.RecoveryStarted += OnRecoveryStarted;
            FirmwareRecoveryManager.Instance.RecoveryProgress += OnRecoveryProgress;
            FirmwareRecoveryManager.Instance.RecoveryCompleted += OnRecoveryCompleted;
            FirmwareRecoveryManager.Instance.Error += OnRecoveryError;
            
            // Check for recovery mode when dialog is shown
            this.Opened += async (sender, args) => await CheckForRecoveryAsync();
            
            // Unregister event handlers when dialog is closed
            this.Closed += (sender, args) =>
            {
                FirmwareRecoveryManager.Instance.RecoveryStarted -= OnRecoveryStarted;
                FirmwareRecoveryManager.Instance.RecoveryProgress -= OnRecoveryProgress;
                FirmwareRecoveryManager.Instance.RecoveryCompleted -= OnRecoveryCompleted;
                FirmwareRecoveryManager.Instance.Error -= OnRecoveryError;
            };
        }
        
        private async Task CheckForRecoveryAsync()
        {
            try
            {
                StatusMessage = Strings.fw_recovery_checking;
                DetailMessage = Strings.fw_recovery_checking_desc;
                IsInProgress = true;
                ActionButtonEnabled = false;
                
                // Check if device needs recovery
                _needsRecovery = await FirmwareTransferManager.Instance.CheckForRecoveryMode();
                
                if (_needsRecovery)
                {
                    StatusMessage = Strings.fw_recovery_needed;
                    DetailMessage = Strings.fw_recovery_needed_desc;
                    ActionButtonText = Strings.fw_recovery_start;
                }
                else
                {
                    StatusMessage = Strings.fw_recovery_not_needed;
                    DetailMessage = Strings.fw_recovery_not_needed_desc;
                    ActionButtonText = Strings.ok;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareRecoveryDialog: Error checking for recovery mode");
                
                StatusMessage = Strings.fw_recovery_error;
                DetailMessage = Strings.fw_recovery_error_desc;
                ErrorMessage = ex.Message;
                HasError = true;
                ActionButtonText = Strings.ok;
            }
            finally
            {
                IsInProgress = false;
                ActionButtonEnabled = true;
            }
        }
        
        private async Task ExecuteActionAsync()
        {
            if (!_needsRecovery)
            {
                // No recovery needed, close dialog
                Close();
                return;
            }
            
            try
            {
                StatusMessage = Strings.fw_recovery_starting;
                DetailMessage = Strings.fw_recovery_starting_desc;
                IsInProgress = true;
                ActionButtonEnabled = false;
                HasError = false;
                
                // Start recovery process
                await FirmwareTransferManager.Instance.StartRecoveryMode();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmwareRecoveryDialog: Error starting recovery mode");
                
                StatusMessage = Strings.fw_recovery_error;
                DetailMessage = Strings.fw_recovery_error_desc;
                ErrorMessage = ex.Message;
                HasError = true;
                ActionButtonText = Strings.ok;
                IsInProgress = false;
                ActionButtonEnabled = true;
            }
        }
        
        private void Cancel()
        {
            if (IsInProgress && FirmwareRecoveryManager.Instance.IsRecoveryInProgress())
            {
                // Cancel recovery process
                FirmwareTransferManager.Instance.Cancel();
            }
            
            Close();
        }
        
        private void OnRecoveryStarted(object? sender, string reason)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = Strings.fw_recovery_in_progress;
                DetailMessage = reason;
                IsInProgress = true;
                ActionButtonEnabled = false;
            });
        }
        
        private void OnRecoveryProgress(object? sender, string progress)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                DetailMessage = progress;
            });
        }
        
        private void OnRecoveryCompleted(object? sender, bool success)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (success)
                {
                    StatusMessage = Strings.fw_recovery_success;
                    DetailMessage = Strings.fw_recovery_success_desc;
                }
                else
                {
                    StatusMessage = Strings.fw_recovery_failed;
                    DetailMessage = Strings.fw_recovery_failed_desc;
                }
                
                IsInProgress = false;
                ActionButtonEnabled = true;
                ActionButtonText = Strings.ok;
            });
        }
        
        private void OnRecoveryError(object? sender, FirmwareTransferException ex)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = Strings.fw_recovery_error;
                DetailMessage = Strings.fw_recovery_error_desc;
                ErrorMessage = ex.ErrorMessage;
                HasError = true;
                IsInProgress = false;
                ActionButtonEnabled = true;
                ActionButtonText = Strings.ok;
            });
        }
        
        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            DataContext = this;
        }
    }
}
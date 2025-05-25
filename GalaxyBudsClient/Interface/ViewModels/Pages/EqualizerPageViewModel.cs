using System.ComponentModel;
using Avalonia.Controls;
using FluentIcons.Common;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Interface.Pages;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Message.Decoder;
using GalaxyBudsClient.Message.Encoder;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Platform;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace GalaxyBudsClient.Interface.ViewModels.Pages;

public class EqualizerPageViewModel : MainPageViewModelBase
{
    // Flag to track if we're using custom EQ settings that should be preserved
    private bool _hasCustomEqSettings = false;
    // Store the last received EQ mode from the device to detect changes
    private int _lastReceivedEqMode = -1;
    // Flag to prevent initial status update from overriding custom settings
    private bool _isInitialized = false;

    public EqualizerPageViewModel()
    {
        SppMessageReceiver.Instance.ExtendedStatusUpdate += OnExtendedStatusUpdate;
        PropertyChanged += OnPropertyChanged;
    }

    private async void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(IsEqEnabled) or nameof(EqPreset):
                // When user changes EQ settings, mark as custom
                _hasCustomEqSettings = true;
                
                try
                {
                    await BluetoothImpl.Instance.SendAsync(new SetEqualizerEncoder
                    {
                        IsEnabled = IsEqEnabled,
                        Preset = EqPreset
                    });
                    EventDispatcher.Instance.Dispatch(Event.UpdateTrayIcon);
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "Failed to send equalizer settings to device");
                }
                break;
                
            case nameof(StereoBalance):
                try
                {
                    await BluetoothImpl.Instance.SendRequestAsync(MsgIds.SET_HEARING_ENHANCEMENTS, (byte)StereoBalance);
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "Failed to send stereo balance settings to device");
                }
                break;
        }
    }

    protected override void OnEventReceived(Event type, object? parameter)
    {
        switch (type)
        {
            case Event.EqualizerToggle:
                IsEqEnabled = !IsEqEnabled;
                _hasCustomEqSettings = true;
                EventDispatcher.Instance.Dispatch(Event.UpdateTrayIcon);
                break;
                
            case Event.EqualizerNextPreset:
            {
                IsEqEnabled = true;
                EqPreset++;
                if (EqPreset > MaximumEqPreset)
                {
                    EqPreset = 0;
                }
                _hasCustomEqSettings = true;
                break;
            }
        }
    }

    private void OnExtendedStatusUpdate(object? sender, ExtendedStatusUpdateDecoder e)
    {
        // If we have custom settings and this isn't the first update, preserve them
        if (_hasCustomEqSettings && _isInitialized)
        {
            // Only update if the device EQ mode has actually changed from what we last saw
            // This prevents overriding custom settings when just opening the EQ page
            bool eqModeChanged = false;
            
            if (BluetoothImpl.Instance.CurrentModel == Models.Buds)
            {
                eqModeChanged = _lastReceivedEqMode != e.EqualizerMode;
            }
            else
            {
                eqModeChanged = _lastReceivedEqMode != e.EqualizerMode;
            }
            
            if (!eqModeChanged)
            {
                // Update stereo balance which doesn't affect EQ preset
                using var suppressor = SuppressChangeNotifications();
                StereoBalance = e.HearingEnhancements;
                return;
            }
        }
        
        using var suppressor = SuppressChangeNotifications();
        
        if (BluetoothImpl.Instance.CurrentModel == Models.Buds)
        {
            IsEqEnabled = e.EqualizerEnabled;
				
            var preset = e.EqualizerMode;
            _lastReceivedEqMode = e.EqualizerMode;
            
            if (preset > MaximumEqPreset)
            {
                /* 0 - 4: regular presets, 5 - 9: presets used when Dolby Atmos is enabled on the phone
                   There is no audible difference. */
                preset -= 5;
            }

            EqPreset = preset;
        }
        else
        {
            _lastReceivedEqMode = e.EqualizerMode;
            IsEqEnabled = e.EqualizerMode != 0;
            
            // If EQ disabled, set to Dynamic (2) by default
            EqPreset = e.EqualizerMode == 0 ? 2 : e.EqualizerMode - 1;
        }
        
        StereoBalance = e.HearingEnhancements;
        
        // Mark as initialized after first status update
        _isInitialized = true;
    }

    public override Control CreateView() => new EqualizerPage { DataContext = this };
    
    [Reactive] public bool IsEqEnabled { set; get; }
    [Reactive] public int EqPreset { set; get; }
    [Reactive] public int StereoBalance { set; get; }

    public int MaximumEqPreset => 4;
    public override string TitleKey => Keys.EqHeader;
    public override Symbol IconKey => Symbol.DeviceEq;
    public override bool ShowsInFooter => false;
}

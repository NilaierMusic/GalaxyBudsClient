using System.Diagnostics.CodeAnalysis;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Platform.Interfaces;
#if Linux
using GalaxyBudsClient.Platform.Linux;
#endif
#if OSX
using GalaxyBudsClient.Platform.OSX;
#endif
#if Windows
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Platform.Windows;
using GalaxyBudsClient.Platform.WindowsRT;
#endif
using GalaxyBudsClient.Platform.Stubs;
using Serilog;

namespace GalaxyBudsClient.Platform;

public static class PlatformImpl
{
    [SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global")] 
    public static IPlatformImplCreator Creator = new DummyPlatformImplCreator();

    public static IDesktopServices DesktopServices { private set; get; }
    public static IHotkeyBroadcast HotkeyBroadcast { private set; get; }
    public static IHotkeyReceiver HotkeyReceiver { private set; get; }
    public static IMediaKeyRemote MediaKeyRemote { private set; get; }
    public static IOfficialAppDetector OfficialAppDetector { private set; get; }

    static PlatformImpl()
    {
        try
        {
            // Initialize platform-specific implementations
#if Windows
            if (PlatformUtils.IsWindows)
            {
                try
                {
                    SwitchWindowsBackend();
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "PlatformImpl: Failed to initialize Windows backend, falling back to dummy implementation");
                    Creator = new DummyPlatformImplCreator();
                }
            }
#endif
#if Linux
            if (PlatformUtils.IsLinux)
            {
                try
                {
                    Creator = new LinuxPlatformImplCreator();
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "PlatformImpl: Failed to initialize Linux backend, falling back to dummy implementation");
                    Creator = new DummyPlatformImplCreator();
                }
            }
#endif
#if OSX
            if (PlatformUtils.IsOSX)
            {
                try
                {
                    Creator = new OsxPlatformImplCreator();
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "PlatformImpl: Failed to initialize macOS backend, falling back to dummy implementation");
                    Creator = new DummyPlatformImplCreator();
                }
            }
#endif
            
            Log.Information("PlatformImpl: Using {Platform}", Creator.GetType().Name);
            
            // Register for event dispatching
            try
            {
                EventDispatcher.Instance.EventReceived += OnEventReceived;
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to register for event dispatching");
            }
            
            // Create platform-specific service implementations with fallbacks
            try
            {
                DesktopServices = Creator.CreateDesktopServices() ?? new DummyDesktopServices();
                Log.Debug("PlatformImpl: Initialized DesktopServices: {Type}", DesktopServices.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create DesktopServices, using dummy implementation");
                DesktopServices = new DummyDesktopServices();
            }
            
            try
            {
                HotkeyBroadcast = Creator.CreateHotkeyBroadcast() ?? new DummyHotkeyBroadcast();
                Log.Debug("PlatformImpl: Initialized HotkeyBroadcast: {Type}", HotkeyBroadcast.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create HotkeyBroadcast, using dummy implementation");
                HotkeyBroadcast = new DummyHotkeyBroadcast();
            }
            
            try
            {
                HotkeyReceiver = Creator.CreateHotkeyReceiver() ?? new DummyHotkeyReceiver();
                Log.Debug("PlatformImpl: Initialized HotkeyReceiver: {Type}", HotkeyReceiver.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create HotkeyReceiver, using dummy implementation");
                HotkeyReceiver = new DummyHotkeyReceiver();
            }
            
            try
            {
                MediaKeyRemote = Creator.CreateMediaKeyRemote() ?? new DummyMediaKeyRemote();
                Log.Debug("PlatformImpl: Initialized MediaKeyRemote: {Type}", MediaKeyRemote.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create MediaKeyRemote, using dummy implementation");
                MediaKeyRemote = new DummyMediaKeyRemote();
            }
            
            try
            {
                OfficialAppDetector = Creator.CreateOfficialAppDetector() ?? new DummyOfficialAppDetector();
                Log.Debug("PlatformImpl: Initialized OfficialAppDetector: {Type}", OfficialAppDetector.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create OfficialAppDetector, using dummy implementation");
                OfficialAppDetector = new DummyOfficialAppDetector();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "PlatformImpl: Critical error during initialization");
            
            // Ensure all services have at least dummy implementations
            DesktopServices ??= new DummyDesktopServices();
            HotkeyBroadcast ??= new DummyHotkeyBroadcast();
            HotkeyReceiver ??= new DummyHotkeyReceiver();
            MediaKeyRemote ??= new DummyMediaKeyRemote();
            OfficialAppDetector ??= new DummyOfficialAppDetector();
        }
    }

    public static void InjectExternalBackend(IPlatformImplCreator platformImplCreator)
    {
        if (platformImplCreator == null)
        {
            Log.Error("PlatformImpl: Attempted to inject null platform implementation creator");
            return;
        }
        
        Log.Information("PlatformImpl: Injecting external backend: {Type}", platformImplCreator.GetType().Name);
        
        try
        {
            // Store the new creator
            Creator = platformImplCreator;
            
            // Create new service implementations with fallbacks
            try
            {
                DesktopServices = Creator.CreateDesktopServices() ?? new DummyDesktopServices();
                Log.Debug("PlatformImpl: Initialized DesktopServices: {Type}", DesktopServices.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create DesktopServices, using dummy implementation");
                DesktopServices = new DummyDesktopServices();
            }
            
            try
            {
                HotkeyBroadcast = Creator.CreateHotkeyBroadcast() ?? new DummyHotkeyBroadcast();
                Log.Debug("PlatformImpl: Initialized HotkeyBroadcast: {Type}", HotkeyBroadcast.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create HotkeyBroadcast, using dummy implementation");
                HotkeyBroadcast = new DummyHotkeyBroadcast();
            }
            
            try
            {
                HotkeyReceiver = Creator.CreateHotkeyReceiver() ?? new DummyHotkeyReceiver();
                Log.Debug("PlatformImpl: Initialized HotkeyReceiver: {Type}", HotkeyReceiver.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create HotkeyReceiver, using dummy implementation");
                HotkeyReceiver = new DummyHotkeyReceiver();
            }
            
            try
            {
                MediaKeyRemote = Creator.CreateMediaKeyRemote() ?? new DummyMediaKeyRemote();
                Log.Debug("PlatformImpl: Initialized MediaKeyRemote: {Type}", MediaKeyRemote.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create MediaKeyRemote, using dummy implementation");
                MediaKeyRemote = new DummyMediaKeyRemote();
            }
            
            try
            {
                OfficialAppDetector = Creator.CreateOfficialAppDetector() ?? new DummyOfficialAppDetector();
                Log.Debug("PlatformImpl: Initialized OfficialAppDetector: {Type}", OfficialAppDetector.GetType().Name);
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to create OfficialAppDetector, using dummy implementation");
                OfficialAppDetector = new DummyOfficialAppDetector();
            }
            
            // Reallocate Bluetooth implementation
            try
            {
                BluetoothImpl.Reallocate();
                Log.Information("PlatformImpl: Successfully reallocated Bluetooth implementation");
            }
            catch (System.Exception ex)
            {
                Log.Error(ex, "PlatformImpl: Failed to reallocate Bluetooth implementation");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "PlatformImpl: Critical error during external backend injection");
        }
    }
    
    public static void SwitchWindowsBackend()
    {
#if Windows
        Log.Information("PlatformImpl: Switching Windows backend");
        
        try
        {
            // Check for Windows RT support
            bool useWinRt = Settings.Data.UseBluetoothWinRt && PlatformUtils.IsWindowsContractsSdkSupported;
            
            if (useWinRt)
            {
                Log.Information("PlatformImpl: Using Windows RT backend (Bluetooth LE)");
                try
                {
                    Creator = new WindowsRtPlatformImplCreator();
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "PlatformImpl: Failed to create Windows RT backend, falling back to standard Windows backend");
                    Creator = new WindowsPlatformImplCreator();
                }
            }
            else
            {
                Log.Information("PlatformImpl: Using standard Windows backend (32feet.NET)");
                Creator = new WindowsPlatformImplCreator();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "PlatformImpl: Failed to switch Windows backend, using dummy implementation");
            Creator = new DummyPlatformImplCreator();
        }
#endif
    } 
    
    private static void OnEventReceived(Event e, object? arg)
    {
        try
        {
            Log.Debug("PlatformImpl: Received event: {Event}", e);
            
            switch (e)
            {
                case Event.Play:
                    try
                    {
                        MediaKeyRemote.Play();
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error(ex, "PlatformImpl: Failed to execute Play command");
                    }
                    break;
                    
                case Event.Pause:
                    try
                    {
                        MediaKeyRemote.Pause();
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error(ex, "PlatformImpl: Failed to execute Pause command");
                    }
                    break;
                    
                case Event.TogglePlayPause:
                    try
                    {
                        MediaKeyRemote.PlayPause();
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error(ex, "PlatformImpl: Failed to execute PlayPause command");
                    }
                    break;
                    
                default:
                    Log.Debug("PlatformImpl: Unhandled event: {Event}", e);
                    break;
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "PlatformImpl: Error handling event: {Event}", e);
        }
    }
}
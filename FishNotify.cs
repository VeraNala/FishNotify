using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Dalamud.Game;

namespace FishNotify;

public sealed class FishNotifyPlugin : IDalamudPlugin
{
    private IDalamudPluginInterface PluginInterface { get; }
    private IChatGui Chat { get; }
    private IPluginLog PluginLog { get; }
    private IGameInteropProvider GameInteropProvider { get; }
    private ISigScanner SigScanner { get; }
    private IFramework Framework { get; }

    private Configuration _configuration;
    private bool _settingsVisible;
    private uint _fishCount;
    
    // Memory location for bite type
    private IntPtr _tugTypeAddress;
    
    // Bite types (from AutoHook)
    private enum BiteType : byte
    {
        Unknown = 0,
        Weak = 36,      // Light tug (!)
        Strong = 37,    // Medium tug (!!)
        Legendary = 38, // Heavy tug (!!!)
        None = 255
    }
    
    private BiteType _lastBite = BiteType.None;
    private bool _fishingState = false;

    public FishNotifyPlugin(
        IDalamudPluginInterface pluginInterface,
        IChatGui chat,
        IPluginLog pluginLog,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        IFramework framework)
    {
        PluginInterface = pluginInterface;
        Chat = chat;
        PluginLog = pluginLog;
        GameInteropProvider = gameInteropProvider;
        SigScanner = sigScanner;
        Framework = framework;
        
        _configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Find the tug type address using the signature from AutoHook
        // This signature points to the memory location that holds the current bite type
        try
        {
            // Use ISigScanner to find the signature
            var sigAddress = SigScanner.GetStaticAddressFromSig("48 8D 35 ?? ?? ?? ?? 4C 8B CE");
            
            if (sigAddress != IntPtr.Zero)
            {
                _tugTypeAddress = sigAddress;
                PluginLog.Information($"Found tug type address at {_tugTypeAddress:X}");
            }
            else
            {
                PluginLog.Warning("Could not find tug type signature");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Failed to find tug type signature");
        }

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= OnDrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
    }
    
    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (_tugTypeAddress == IntPtr.Zero)
            return;
        
        try
        {
            var currentBite = *(BiteType*)_tugTypeAddress;
            
            // Check if we're fishing (bite type is not None)
            var currentlyFishing = currentBite != BiteType.None;
            
            // Detect when fishing starts
            if (!_fishingState && currentlyFishing)
            {
                _fishingState = true;
                _lastBite = BiteType.Unknown;
            }
            // Detect when fishing ends
            else if (_fishingState && !currentlyFishing)
            {
                _fishingState = false;
                _lastBite = BiteType.None;
                Sounds.Stop();
            }
            
            // Detect bite changes while fishing
            if (_fishingState && currentBite != _lastBite && currentBite != BiteType.Unknown && currentBite != BiteType.None)
            {
                OnBite(currentBite);
                _lastBite = currentBite;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Error reading bite type");
        }
    }
    
    private void OnBite(BiteType bite)
    {
        _fishCount++;
        
        switch (bite)
        {
            case BiteType.Weak:
                // light tug (!)
                Sounds.PlaySound(Resources.Info);
                SendChatAlert("light");
                PluginLog.Debug("Fish bite: Light tug");
                break;

            case BiteType.Strong:
                // medium tug (!!)
                Sounds.PlaySound(Resources.Alert);
                SendChatAlert("medium");
                PluginLog.Debug("Fish bite: Medium tug");
                break;

            case BiteType.Legendary:
                // heavy tug (!!!)
                Sounds.PlaySound(Resources.Alarm);
                SendChatAlert("heavy");
                PluginLog.Debug("Fish bite: Heavy tug");
                break;
        }
    }

    private void SendChatAlert(string size)
    {
        if (!_configuration.ChatAlerts)
        {
            return;
        }

        SeString message = new SeStringBuilder()
            .AddUiForeground(514)
            .Append("[FishNotify]")
            .AddUiForegroundOff()
            .Append(" You hook a fish with a ")
            .AddUiForeground(514)
            .Append(size)
            .AddUiForegroundOff()
            .Append(" bite.")
            .Build();
        Chat.Print(message);
    }

    private void OnDrawUI()
    {
        if (!_settingsVisible)
            return;

        if (ImGui.Begin("FishNotify", ref _settingsVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var chatAlerts = _configuration.ChatAlerts;
            if (ImGui.Checkbox("Show chat message on hooking a fish", ref chatAlerts))
            {
                _configuration.ChatAlerts = chatAlerts;
                PluginInterface.SavePluginConfig(_configuration);
            }

            if (_tugTypeAddress != IntPtr.Zero)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, $"Status: {(_fishCount == 0 ? "Ready (not triggered yet)" : $"OK ({_fishCount} fish hooked)")}");
                
                if (_fishingState)
                {
                    ImGui.Text($"Currently fishing - Last bite: {_lastBite}");
                }
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "Status: Failed to initialize (signature not found)");
            }
            
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Note: Using memory reading for bite detection");
        }
        ImGui.End();
    }

    private void OnOpenConfigUi()
    {
        _settingsVisible = !_settingsVisible;
    }
}

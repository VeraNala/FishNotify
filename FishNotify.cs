using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace FishNotify;

public enum BiteType : byte
{
    Unknown = 0,
    Weak = 36,
    Strong = 37,
    Legendary = 38,
    None = 255,
}

public sealed class FishNotifyPlugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IChatGui _chat;
    private readonly IPluginLog _pluginLog;
    private readonly ISigScanner _sigScanner;
    private readonly IFramework _framework;

    private readonly Configuration _configuration;
    private bool _settingsVisible;
    private uint _fishCount;

    private IntPtr _tugTypeAddress = IntPtr.Zero;
    private FishingState _lastFishingState = FishingState.None;

    // Signature from AutoHook plugin
    private const string TugTypeSignature = "48 8D 35 ?? ?? ?? ?? 4C 8B CE";

    public FishNotifyPlugin(
        IDalamudPluginInterface pluginInterface,
        IChatGui chat,
        IPluginLog pluginLog,
        ISigScanner sigScanner,
        IFramework framework)
    {
        _pluginInterface = pluginInterface;
        _chat = chat;
        _pluginLog = pluginLog;
        _sigScanner = sigScanner;
        _framework = framework;

        _configuration = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _pluginInterface.UiBuilder.Draw += OnDrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        _framework.Update += OnFrameworkUpdate;

        try
        {
            _tugTypeAddress = _sigScanner.GetStaticAddressFromSig(TugTypeSignature);
            _pluginLog.Debug($"Found TugType address: {_tugTypeAddress:X}");
        }
        catch (Exception e)
        {
            _pluginLog.Error(e, "Could not find TugType signature");
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.Draw -= OnDrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
    }

    private static unsafe FishingState GetFishingState()
    {
        var ef = EventFramework.Instance();
        if (ef == null) return FishingState.None;
        var handler = ef->EventHandlerModule.FishingEventHandler;
        if (handler == null) return FishingState.None;
        return handler->State;
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (_tugTypeAddress == IntPtr.Zero)
            return;

        var currentFishingState = GetFishingState();

        // Log state changes
        if (currentFishingState != _lastFishingState)
        {
            _pluginLog.Debug($"FishNotify: State changed from {_lastFishingState} to {currentFishingState}");
        }

        // Only trigger when state transitions TO FishingState.Bite
        if (currentFishingState == FishingState.Bite && _lastFishingState != FishingState.Bite)
        {
            var currentBite = *(BiteType*)_tugTypeAddress;
            if (currentBite != BiteType.None && currentBite != BiteType.Unknown)
            {
                OnBite(currentBite);
            }
        }

        _lastFishingState = currentFishingState;
    }

    private void OnBite(BiteType bite)
    {
        ++_fishCount;

        switch (bite)
        {
            case BiteType.Weak:
                Sounds.PlaySound(Resources.Info);
                SendChatAlert("light");
                break;

            case BiteType.Strong:
                Sounds.PlaySound(Resources.Alert);
                SendChatAlert("medium");
                break;

            case BiteType.Legendary:
                Sounds.PlaySound(Resources.Alarm);
                SendChatAlert("heavy");
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
        _chat.Print(message);
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
                _pluginInterface.SavePluginConfig(_configuration);
            }

            if (_tugTypeAddress != IntPtr.Zero)
                ImGui.TextColored(ImGuiColors.HealerGreen, $"Status: {(_fishCount == 0 ? "Ready (not triggered yet)" : $"OK ({_fishCount} fish hooked)")}");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, "Status: Could not find tug signature :(");
        }
        ImGui.End();
    }

    private void OnOpenConfigUi()
    {
        _settingsVisible = !_settingsVisible;
    }
}

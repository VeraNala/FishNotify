using Dalamud;
using Dalamud.Game.Gui;
using Dalamud.Game.Network;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using ImGuiNET;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FishNotify
{
    public sealed class FishNotifyPlugin : IDalamudPlugin
    {
        public string Name => "FishNotify";

        [PluginService]
        [RequiredVersion("1.0")]
        private DalamudPluginInterface PluginInterface { get; set; } = null!;

        [PluginService]
        private GameNetwork Network { get; set; } = null!;

        [PluginService]
        public static ChatGui Chat { get; set; } = null!;

        private Configuration configuration;
        private bool settingsVisible;
        private int expectedOpCode = -1;
        private uint fishCount = 0;

        public FishNotifyPlugin()
        {
            configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            configuration.Initialize(PluginInterface);

            Network.NetworkMessage += OnNetworkMessage;
            PluginInterface.UiBuilder.Draw += OnDrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;

            var client = new HttpClient();
            client.GetStringAsync("https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/master/opcodes.min.json")
                .ContinueWith(ExtractOpCode);
        }

        public void Dispose()
        {
            Network.NetworkMessage -= OnNetworkMessage;
            PluginInterface.UiBuilder.Draw -= OnDrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        }

        private void ExtractOpCode(Task<string> task)
        {
            try
            {
                var regions = JsonConvert.DeserializeObject<List<OpcodeRegion>>(task.Result);
                if (regions == null)
                {
                    PluginLog.Warning("No regions found in opcode list");
                    return;
                }

                var region = regions.Find(r => r.Region == GetGameRegion());
                if (region == null || region.Lists == null)
                {
                    PluginLog.Warning("No global region found in opcode list");
                    return;
                }

                if (!region.Lists.TryGetValue("ServerZoneIpcType", out List<OpcodeList>? serverZoneIpcTypes) || serverZoneIpcTypes == null)
                {
                    PluginLog.Warning("No ServerZoneIpcType in opcode list");
                    return;
                }

                var eventPlay = serverZoneIpcTypes.Find(opcode => opcode.Name == "EventPlay");
                if (eventPlay == null)
                {
                    PluginLog.Warning("No EventPlay opcode in ServerZoneIpcType");
                    return;
                }

                expectedOpCode = eventPlay.Opcode;
                PluginLog.Debug($"Found EventPlay opcode {expectedOpCode:X4}");
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "Could not download/extract opcodes: {}", e.Message);
            }
        }

        /// <summary>
        /// Both ClientLanguage.Korean and ClientLanguage.ChineseSimplified have value 4, but the string representation differs depending on the dalamud fork.
        /// </summary>
        /// <returns></returns>
        private string GetGameRegion()
        {
            return Enum.GetName(typeof(ClientLanguage), 4) switch
            {
                "ChineseSimplified" => "CN",
                "Korean" => "KR",
                _ => "Global",
            };
        }

        private void OnNetworkMessage(IntPtr dataPtr, ushort opCode, uint sourceActorId, uint targetActorId, NetworkMessageDirection direction)
        {
            if (direction != NetworkMessageDirection.ZoneDown || opCode != expectedOpCode)
                return;

            var data = new byte[32];
            Marshal.Copy(dataPtr, data, 0, data.Length);

            int eventId = BitConverter.ToInt32(data, 8);
            short scene = BitConverter.ToInt16(data, 12);
            int param5 = BitConverter.ToInt32(data, 28);

            // Fishing event?
            if (eventId != 0x00150001)
                return;

            // Fish hooked?
            if (scene != 5)
                return;

            switch (param5)
            {

                case 0x124:
                    // light tug (!)
                    ++fishCount;
                    Sounds.PlaySound(Resources.Info);
                    SendChatAlert("light");
                    break;

                case 0x125:
                    // medium tug (!!)
                    ++fishCount;
                    Sounds.PlaySound(Resources.Alert);
                    SendChatAlert("medium");
                    break;

                case 0x126:
                    // heavy tug (!!!)
                    ++fishCount;
                    Sounds.PlaySound(Resources.Alarm);
                    SendChatAlert("heavy");
                    break;

                default:
                    Sounds.Stop();
                    break;
            }
        }

        private void SendChatAlert(string size)
        {
            if (!configuration.ChatAlerts)
            {
                return;
            }

            SeString message = new SeStringBuilder()
                .AddUiForeground(514)
                .Append("[FishNotify]")
                .AddUiForegroundOff()
                .Append($" You hook a fish with a ")
                .AddUiForeground(514)
                .Append(size)
                .AddUiForegroundOff()
                .Append(" bite.")
                .Build();
            Chat.Print(message);
        }

        private void OnDrawUI()
        {
            if (!settingsVisible)
                return;

            if (ImGui.Begin("FishNotify", ref this.settingsVisible, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var chatAlerts = configuration.ChatAlerts;
                if (ImGui.Checkbox("Show chat message on hooking a fish", ref chatAlerts))
                {
                    configuration.ChatAlerts = chatAlerts;
                    configuration.Save();
                }

                if (expectedOpCode > -1)
                    ImGui.TextColored(ImGuiColors.HealerGreen, $"Status: {(fishCount == 0 ? "Unknown (not triggered yet)" : $"OK ({fishCount} fish hooked)")}, opcode = {expectedOpCode:X}");
                else
                    ImGui.TextColored(ImGuiColors.DalamudRed, "Status: No opcode :(");
            }
            ImGui.End();
        }

        private void OnOpenConfigUi()
        {
            settingsVisible = !settingsVisible;
        }
    }

    public class OpcodeRegion
    {
        public string? Version { get; set; }
        public string? Region { get; set; }
        public Dictionary<string, List<OpcodeList>>? Lists { get; set; }
    }

    public class OpcodeList
    {
        public string? Name { get; set; }
        public ushort Opcode { get; set; }
    }
}

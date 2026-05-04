using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindBroadcastBrain", "Devi8d0ne", "1.0.0")]
    [Description("Controlled server broadcast and narration layer for the WorldMind plugin ecosystem.")]
    public class WorldMindBroadcastBrain : RustPlugin
    {
        private const string PermissionAdmin = "worldmindbroadcastbrain.admin";
        private const string PermissionUse = "worldmindbroadcastbrain.use";
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";

        private const string Dv8dAscii = @"
DDDDDDDDDD    VVVV        VVVV     888888      DDDDDDDDDD
DDDDDDDDDDD    VVVV      VVVV    88888888     DDDDDDDDDDD
DD      DDD     VVVV    VVVV    88      88    DD      DDD
DD      DDD      VVVV  VVVV      88888888     DD      DDD
DD      DDD       VVVVVVVV      88      88    DD      DDD
DDDDDDDDDDD        VVVVVV       88888888     DDDDDDDDDDD
DDDDDDDDDD          VVVV         888888      DDDDDDDDDD
";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindEventBrain;
        [PluginReference] private Plugin WorldMindRaidBrain;
        [PluginReference] private Plugin WorldMindDiscordMind;
        [PluginReference] private Plugin WorldMindAdminMind;

        private PluginConfig _config;
        private StoredData _data;
        private double _lastAutoBroadcastUnix;
        private double _lastManualBroadcastUnix;

        #region Oxide

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionUse, this);
            LoadPluginConfig();
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (_config.General.PrintAsciiOnLoad)
            {
                Puts(Dv8dAscii);
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindBroadcastBrain");
            }

            if (_config.AutomaticBroadcasts.Enabled)
            {
                timer.Every(Math.Max(60f, _config.AutomaticBroadcasts.CheckIntervalSeconds), CheckAutomaticBroadcast);
            }

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            Puts("WorldMindBroadcastBrain loaded.");
        }

        private void Unload()
        {
            SaveData();
        }

        #endregion

        #region Commands

        [ChatCommand("wmbroadcast")]
        private void CmdBroadcast(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                if (!HasAdmin(player)) return;
                Reply(player,
                    "WorldMindBroadcastBrain commands:\n" +
                    "/wmbroadcast status\n" +
                    "/wmbroadcast say <message>\n" +
                    "/wmbroadcast ask <notes>\n" +
                    "/wmbroadcast local <radius> <message>\n" +
                    "/wmbroadcast preset <presetKey>\n" +
                    "/wmbroadcast presets\n" +
                    "/wmbroadcast history [count]\n" +
                    "/wmbroadcast reload\n" +
                    "/wmbroadcast save\n" +
                    "/wmbroadcast clear");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (!HasAdmin(player)) return;

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "say")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmbroadcast say <message>");
                    return;
                }

                string message = string.Join(" ", args.Skip(1).ToArray());
                BroadcastManual(player, message, "manual");
                return;
            }

            if (sub == "ask")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmbroadcast ask <notes>");
                    return;
                }

                string notes = string.Join(" ", args.Skip(1).ToArray());
                GenerateWorldMindBroadcast(player, notes, "manual_ai");
                return;
            }

            if (sub == "local")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmbroadcast local <radius> <message>");
                    return;
                }

                float radius;
                if (!float.TryParse(args[1], out radius))
                {
                    Reply(player, "Radius must be a number.");
                    return;
                }

                string message = string.Join(" ", args.Skip(2).ToArray());
                BroadcastLocal(player.transform.position, radius, message, "local_manual");
                Reply(player, $"Local broadcast sent within {radius}m.");
                return;
            }

            if (sub == "preset")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmbroadcast preset <presetKey>");
                    return;
                }

                SendPreset(player, args[1]);
                return;
            }

            if (sub == "presets")
            {
                Reply(player, BuildPresetsText());
                return;
            }

            if (sub == "history")
            {
                int count = 10;
                if (args.Length >= 2) int.TryParse(args[1], out count);
                Reply(player, BuildHistoryText(Mathf.Clamp(count, 1, 30)));
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindBroadcastBrain reloaded.");
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindBroadcastBrain data saved.");
                return;
            }

            if (sub == "clear")
            {
                _data.History.Clear();
                SaveData();
                Reply(player, "Broadcast history cleared.");
                return;
            }

            Reply(player, "Unknown command. Use /wmbroadcast for help.");
        }

        [ConsoleCommand("worldmindbroadcast.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindbroadcast.say")]
        private void ConsoleSay(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            string message = arg.Args == null || arg.Args.Length == 0 ? "" : string.Join(" ", arg.Args);
            if (string.IsNullOrWhiteSpace(message))
            {
                arg.ReplyWith("Usage: worldmindbroadcast.say <message>");
                return;
            }

            BroadcastToAll(message, "console_manual", "console");
            arg.ReplyWith("Broadcast sent.");
        }

        [ConsoleCommand("worldmindbroadcast.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindBroadcastBrain reloaded.");
        }

        #endregion

        #region Public hooks

        private object WorldMindBroadcastBrain_Broadcast(string message, string source)
        {
            return BroadcastToAll(message, source ?? "external", "hook");
        }

        private object WorldMindBroadcastBrain_BroadcastLocal(Vector3 position, float radius, string message, string source)
        {
            BroadcastLocal(position, radius, message, source ?? "external");
            return true;
        }

        private object WorldMindBroadcastBrain_GenerateBroadcast(string notes, string source)
        {
            GenerateWorldMindBroadcast(null, notes, source ?? "external_ai");
            return true;
        }

        private object WorldMindBroadcastBrain_GetHistory(int count)
        {
            return _data.History.OrderByDescending(x => x.TimestampUtc).Take(Mathf.Clamp(count, 1, 100)).ToList();
        }

        private object WorldMindBroadcastBrain_GetStatus()
        {
            return new Dictionary<string, object>
            {
                ["worldMindLinked"] = WorldMindV2 != null,
                ["historyCount"] = _data.History.Count,
                ["automaticEnabled"] = _config.AutomaticBroadcasts.Enabled,
                ["lastAutoBroadcastUnix"] = _lastAutoBroadcastUnix,
                ["lastManualBroadcastUnix"] = _lastManualBroadcastUnix
            };
        }

        #endregion

        #region Broadcast logic

        private void BroadcastManual(BasePlayer admin, string message, string source)
        {
            double now = UnixNow();

            if (now - _lastManualBroadcastUnix < _config.General.ManualBroadcastCooldownSeconds)
            {
                Reply(admin, "Manual broadcast cooldown is active.");
                return;
            }

            _lastManualBroadcastUnix = now;
            bool sent = BroadcastToAll(message, source, admin == null ? "unknown" : admin.displayName);
            Reply(admin, sent ? "Broadcast sent." : "Broadcast blocked or empty.");
        }

        private bool BroadcastToAll(string message, string source, string author)
        {
            message = CleanBroadcast(message);
            if (string.IsNullOrWhiteSpace(message)) return false;

            string final = FormatBroadcast(message);

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                player.ChatMessage(final);
            }

            RecordBroadcast(message, source, author, "global", 0f, Vector3.zero);

            if (_config.Discord.SendBroadcastsToDiscord && WorldMindDiscordMind != null)
                WorldMindDiscordMind.Call("WorldMindDiscordMind_SendEventSummary", message);

            return true;
        }

        private void BroadcastLocal(Vector3 position, float radius, string message, string source)
        {
            message = CleanBroadcast(message);
            if (string.IsNullOrWhiteSpace(message)) return;

            radius = Mathf.Max(1f, radius);
            string final = FormatBroadcast(message);

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;

                if (Vector3.Distance(player.transform.position, position) <= radius)
                    player.ChatMessage(final);
            }

            RecordBroadcast(message, source, "local", "local", radius, position);
        }

        private void SendPreset(BasePlayer admin, string presetKey)
        {
            BroadcastPreset preset;
            if (!_config.Presets.TryGetValue(presetKey, out preset) || preset == null)
            {
                Reply(admin, "Preset not found.");
                return;
            }

            if (!preset.Enabled)
            {
                Reply(admin, "Preset is disabled.");
                return;
            }

            if (preset.UseWorldMind)
            {
                GenerateWorldMindBroadcast(admin, preset.PromptOrMessage, $"preset:{presetKey}");
                return;
            }

            BroadcastManual(admin, preset.PromptOrMessage, $"preset:{presetKey}");
        }

        private void GenerateWorldMindBroadcast(BasePlayer admin, string notes, string source)
        {
            if (WorldMindV2 == null)
            {
                if (admin != null) Reply(admin, "WorldMindV2 is not loaded.");
                return;
            }

            string prompt =
                "You are WorldMind creating a short server broadcast for a generic Rust server.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, factions, custom economy, DeepSea, rewards, or server-specific commands.\n" +
                "Do not claim events are happening unless included in the notes. Keep it under 35 words.\n" +
                "Tone: atmospheric, Rust-aware, not admin tutorial.\n" +
                $"Notes: {notes}\n";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindBroadcastBrain", source ?? "broadcast");
                string message = result == null ? "" : result.ToString();

                if (string.IsNullOrWhiteSpace(message))
                {
                    if (admin != null) Reply(admin, "WorldMind returned no broadcast.");
                    return;
                }

                bool sent = BroadcastToAll(message, source ?? "worldmind", admin == null ? "system" : admin.displayName);

                if (admin != null)
                    Reply(admin, sent ? "WorldMind broadcast sent." : "WorldMind broadcast blocked.");
            }
            catch (Exception ex)
            {
                if (admin != null) Reply(admin, $"WorldMind broadcast failed: {ex.Message}");
            }
        }

        private void CheckAutomaticBroadcast()
        {
            if (!_config.AutomaticBroadcasts.Enabled) return;
            if (BasePlayer.activePlayerList.Count < _config.AutomaticBroadcasts.MinimumOnlinePlayers) return;

            double now = UnixNow();
            if (now - _lastAutoBroadcastUnix < _config.AutomaticBroadcasts.IntervalMinutes * 60)
                return;

            _lastAutoBroadcastUnix = now;

            if (_config.AutomaticBroadcasts.UseWorldMind)
            {
                GenerateWorldMindBroadcast(null, _config.AutomaticBroadcasts.WorldMindPrompt, "automatic_ai");
                return;
            }

            List<BroadcastPreset> eligible = _config.Presets.Values.Where(x => x.Enabled && !x.UseWorldMind).ToList();
            if (eligible.Count == 0) return;

            BroadcastPreset preset = eligible[UnityEngine.Random.Range(0, eligible.Count)];
            BroadcastToAll(preset.PromptOrMessage, "automatic_preset", "system");
        }

        private string FormatBroadcast(string message)
        {
            return $"{_config.Display.ChatPrefix} {message}";
        }

        private string CleanBroadcast(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";

            string clean = message
                .Replace("@everyone", "@\u200beveryone")
                .Replace("@here", "@\u200bhere")
                .Trim();

            if (_config.Display.StripNewLines)
                clean = clean.Replace("\r", " ").Replace("\n", " ");

            if (_config.Display.MaxBroadcastLength > 0 && clean.Length > _config.Display.MaxBroadcastLength)
                clean = clean.Substring(0, _config.Display.MaxBroadcastLength - 3) + "...";

            return clean;
        }

        private void RecordBroadcast(string message, string source, string author, string scope, float radius, Vector3 position)
        {
            BroadcastRecord record = new BroadcastRecord
            {
                Message = message ?? "",
                Source = source ?? "",
                Author = author ?? "",
                Scope = scope ?? "global",
                Radius = radius,
                PositionX = position.x,
                PositionY = position.y,
                PositionZ = position.z,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };

            _data.History.Add(record);

            while (_data.History.Count > _config.History.KeepBroadcastHistory)
                _data.History.RemoveAt(0);

            SaveData();
            RecordWorldMindEvent("broadcast_sent", record);
        }

        #endregion

        #region WorldMind/event

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (!_config.WorldMindIntegration.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindBroadcastBrain",
                    ["eventType"] = eventType,
                    ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
                    ["payloadJson"] = JsonConvert.SerializeObject(payload)
                };

                WorldMindV2.Call("WorldMind_RecordEvent", packet);
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    Puts($"WorldMind_RecordEvent failed: {ex.Message}");
            }
        }

        #endregion

        #region Reporting

        private string GetStatusText()
        {
            return
                "WorldMindBroadcastBrain status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"EventBrain linked: {(WorldMindEventBrain != null ? "yes" : "no")}\n" +
                $"DiscordMind linked: {(WorldMindDiscordMind != null ? "yes" : "no")}\n" +
                $"Automatic broadcasts: {_config.AutomaticBroadcasts.Enabled}\n" +
                $"Broadcast history: {_data.History.Count}\n" +
                $"Presets: {_config.Presets.Count}";
        }

        private string BuildPresetsText()
        {
            if (_config.Presets.Count == 0)
                return "No presets configured.";

            List<string> lines = new List<string> { "Broadcast presets:" };

            foreach (KeyValuePair<string, BroadcastPreset> kvp in _config.Presets.OrderBy(x => x.Key))
                lines.Add($"- {kvp.Key}: enabled={kvp.Value.Enabled}, worldmind={kvp.Value.UseWorldMind}");

            return string.Join("\n", lines.ToArray());
        }

        private string BuildHistoryText(int count)
        {
            List<BroadcastRecord> records = _data.History
                .OrderByDescending(x => x.TimestampUtc)
                .Take(count)
                .ToList();

            if (records.Count == 0)
                return "No broadcast history.";

            List<string> lines = new List<string> { "Broadcast history:" };

            foreach (BroadcastRecord record in records)
                lines.Add($"- {record.TimestampUtc} | {record.Source} | {record.Message}");

            return string.Join("\n", lines.ToArray());
        }

        #endregion

        #region Helpers

        private double UnixNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind BroadcastBrain]</color> {message}");
        }

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return false;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin))
                return true;

            Reply(player, "You do not have permission to use that command.");
            return false;
        }

        #endregion

        #region Config/Data

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.Default();
        }

        private void LoadPluginConfig()
        {
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config was null.");

                _config.EnsureDefaults();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not read config. Creating default config. Error: {ex.Message}");
                LoadDefaultConfig();
                SaveConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (_data == null)
                    _data = new StoredData();

                _data.EnsureDefaults();
            }
            catch
            {
                _data = new StoredData();
            }
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private class PluginConfig
        {
            [JsonProperty("General")]
            public GeneralSettings General = new GeneralSettings();

            [JsonProperty("Display")]
            public DisplaySettings Display = new DisplaySettings();

            [JsonProperty("Automatic Broadcasts")]
            public AutomaticBroadcastSettings AutomaticBroadcasts = new AutomaticBroadcastSettings();

            [JsonProperty("History")]
            public HistorySettings History = new HistorySettings();

            [JsonProperty("Discord Integration")]
            public DiscordSettings Discord = new DiscordSettings();

            [JsonProperty("WorldMind Integration")]
            public WorldMindIntegrationSettings WorldMindIntegration = new WorldMindIntegrationSettings();

            [JsonProperty("Presets")]
            public Dictionary<string, BroadcastPreset> Presets = new Dictionary<string, BroadcastPreset>();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.EnsureDefaults();
                return config;
            }

            public void EnsureDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (Display == null) Display = new DisplaySettings();
                if (AutomaticBroadcasts == null) AutomaticBroadcasts = new AutomaticBroadcastSettings();
                if (History == null) History = new HistorySettings();
                if (Discord == null) Discord = new DiscordSettings();
                if (WorldMindIntegration == null) WorldMindIntegration = new WorldMindIntegrationSettings();
                if (Presets == null) Presets = new Dictionary<string, BroadcastPreset>();

                if (!Presets.ContainsKey("weathered_island"))
                {
                    Presets["weathered_island"] = new BroadcastPreset
                    {
                        Enabled = true,
                        UseWorldMind = false,
                        PromptOrMessage = "The island is awake. Move smart, stash smarter."
                    };
                }

                if (!Presets.ContainsKey("quiet_warning"))
                {
                    Presets["quiet_warning"] = new BroadcastPreset
                    {
                        Enabled = true,
                        UseWorldMind = false,
                        PromptOrMessage = "Quiet does not mean safe. It usually means someone is listening."
                    };
                }

                if (!Presets.ContainsKey("ai_atmosphere"))
                {
                    Presets["ai_atmosphere"] = new BroadcastPreset
                    {
                        Enabled = false,
                        UseWorldMind = true,
                        PromptOrMessage = "Create a short atmospheric Rust survival broadcast."
                    };
                }
            }
        }

        private class GeneralSettings
        {
            [JsonProperty("PrintAsciiOnLoad")]
            public bool PrintAsciiOnLoad = true;

            [JsonProperty("Debug")]
            public bool Debug = false;

            [JsonProperty("AutoSaveSeconds")]
            public float AutoSaveSeconds = 300f;

            [JsonProperty("ManualBroadcastCooldownSeconds")]
            public float ManualBroadcastCooldownSeconds = 15f;
        }

        private class DisplaySettings
        {
            [JsonProperty("ChatPrefix")]
            public string ChatPrefix = "<color=#d7b46a>[WorldMind]</color>";

            [JsonProperty("MaxBroadcastLength")]
            public int MaxBroadcastLength = 220;

            [JsonProperty("StripNewLines")]
            public bool StripNewLines = true;
        }

        private class AutomaticBroadcastSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("UseWorldMind")]
            public bool UseWorldMind = false;

            [JsonProperty("CheckIntervalSeconds")]
            public float CheckIntervalSeconds = 60f;

            [JsonProperty("IntervalMinutes")]
            public float IntervalMinutes = 30f;

            [JsonProperty("MinimumOnlinePlayers")]
            public int MinimumOnlinePlayers = 1;

            [JsonProperty("WorldMindPrompt")]
            public string WorldMindPrompt = "Create a short atmospheric Rust survival broadcast. Do not mention unconfigured server features.";
        }

        private class HistorySettings
        {
            [JsonProperty("KeepBroadcastHistory")]
            public int KeepBroadcastHistory = 100;
        }

        private class DiscordSettings
        {
            [JsonProperty("SendBroadcastsToDiscord")]
            public bool SendBroadcastsToDiscord = false;
        }

        private class WorldMindIntegrationSettings
        {
            [JsonProperty("RecordEventsToWorldMind")]
            public bool RecordEventsToWorldMind = true;
        }

        private class BroadcastPreset
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("UseWorldMind")]
            public bool UseWorldMind = false;

            [JsonProperty("PromptOrMessage")]
            public string PromptOrMessage = "";
        }

        private class StoredData
        {
            [JsonProperty("History")]
            public List<BroadcastRecord> History = new List<BroadcastRecord>();

            public void EnsureDefaults()
            {
                if (History == null)
                    History = new List<BroadcastRecord>();
            }
        }

        public class BroadcastRecord
        {
            public string Message = "";
            public string Source = "";
            public string Author = "";
            public string Scope = "";
            public float Radius = 0f;
            public float PositionX = 0f;
            public float PositionY = 0f;
            public float PositionZ = 0f;
            public string TimestampUtc = "";
        }

        #endregion
    }
}

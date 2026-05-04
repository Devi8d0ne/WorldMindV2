using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindMemoryAudit", "Devi8d0ne", "1.0.1")]
    [Description("Admin-only memory, facts, timeline, and context audit helper for the WorldMind plugin ecosystem.")]
    public class WorldMindMemoryAudit : RustPlugin
    {
        private const string PermissionAdmin = "worldmindmemoryaudit.admin";
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
        [PluginReference] private Plugin WorldMindPlayerBrain;
        [PluginReference] private Plugin WorldMindAdminMind;
        [PluginReference] private Plugin WorldMindShopBrain;
        [PluginReference] private Plugin WorldMindQuestBrain;
        [PluginReference] private Plugin WorldMindProviderBrain;
        [PluginReference] private Plugin WorldMindDiscordMind;

        private PluginConfig _config;
        private StoredData _data;

        #region Oxide

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            LoadPluginConfig();
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (_config.General.PrintAsciiOnLoad)
            {
                Puts(Dv8dAscii);
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindMemoryAudit");
            }

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            Puts("WorldMindMemoryAudit loaded.");
        }

        private void Unload()
        {
            SaveData();
        }

        #endregion

        #region Commands

        [ChatCommand("wmaudit")]
        private void CmdAudit(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasAdmin(player)) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMindMemoryAudit commands:\n" +
                    "/wmaudit status\n" +
                    "/wmaudit plugins\n" +
                    "/wmaudit facts\n" +
                    "/wmaudit fact <key>\n" +
                    "/wmaudit setfact <key> <value>\n" +
                    "/wmaudit forgetfact <key>\n" +
                    "/wmaudit player <steamId/name>\n" +
                    "/wmaudit context <steamId/name>\n" +
                    "/wmaudit events [count]\n" +
                    "/wmaudit providers\n" +
                    "/wmaudit ask <question>\n" +
                    "/wmaudit save\n" +
                    "/wmaudit reload");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "plugins")
            {
                Reply(player, BuildPluginStatusText());
                return;
            }

            if (sub == "facts")
            {
                Reply(player, BuildFactsText());
                return;
            }

            if (sub == "fact")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmaudit fact <key>");
                    return;
                }

                Reply(player, GetFactText(args[1]));
                return;
            }

            if (sub == "setfact")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmaudit setfact <key> <value>");
                    return;
                }

                string key = args[1];
                string value = string.Join(" ", args.Skip(2).ToArray());
                SetFact(key, value, player.displayName);
                Reply(player, $"Fact set: {key}");
                return;
            }

            if (sub == "forgetfact")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmaudit forgetfact <key>");
                    return;
                }

                bool removed = _data.ManualFacts.Remove(args[1]);
                SaveData();
                Reply(player, removed ? $"Forgot fact: {args[1]}" : "Fact not found.");
                return;
            }

            if (sub == "player")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmaudit player <steamId/name>");
                    return;
                }

                Reply(player, BuildPlayerAuditText(args[1]));
                return;
            }

            if (sub == "context")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmaudit context <steamId/name>");
                    return;
                }

                Reply(player, BuildContextPreview(args[1]));
                return;
            }

            if (sub == "events")
            {
                int count = 15;
                if (args.Length >= 2) int.TryParse(args[1], out count);
                Reply(player, BuildRecentEventsText(Mathf.Clamp(count, 1, 50)));
                return;
            }

            if (sub == "providers")
            {
                Reply(player, BuildProviderAuditText());
                return;
            }

            if (sub == "ask")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmaudit ask <question>");
                    return;
                }

                string question = string.Join(" ", args.Skip(1).ToArray());
                AskAuditQuestion(player, question);
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindMemoryAudit data saved.");
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindMemoryAudit reloaded.");
                return;
            }

            Reply(player, "Unknown command. Use /wmaudit for help.");
        }

        [ConsoleCommand("worldmindaudit.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindaudit.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindMemoryAudit reloaded.");
        }

        #endregion

        #region Public hooks

        private object WorldMindMemoryAudit_RecordEvent(Dictionary<string, object> packet)
        {
            if (packet == null) return false;

            AuditEvent evt = new AuditEvent
            {
                Plugin = GetString(packet, "plugin", "external"),
                EventType = GetString(packet, "eventType", "external"),
                Summary = GetString(packet, "summary", ""),
                PayloadJson = GetString(packet, "payloadJson", JsonConvert.SerializeObject(packet)),
                TimestampUtc = GetString(packet, "timestampUtc", DateTime.UtcNow.ToString("o"))
            };

            RecordAuditEvent(evt);
            return true;
        }

        private object WorldMindMemoryAudit_SetFact(string key, string value, string source)
        {
            SetFact(key, value, source);
            return true;
        }

        private object WorldMindMemoryAudit_GetFact(string key)
        {
            AuditFact fact;
            return _data.ManualFacts.TryGetValue(key ?? "", out fact) ? fact : null;
        }

        private object WorldMindMemoryAudit_GetFacts()
        {
            return _data.ManualFacts;
        }

        private object WorldMindMemoryAudit_GetContextPreview(string playerIdOrName)
        {
            return BuildContextPacket(playerIdOrName);
        }

        private object WorldMindMemoryAudit_GetRecentEvents(int count)
        {
            return _data.RecentEvents.OrderByDescending(x => x.TimestampUtc).Take(Mathf.Clamp(count, 1, 100)).ToList();
        }

        #endregion

        #region Core

        private void SetFact(string key, string value, string source)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            _data.ManualFacts[key] = new AuditFact
            {
                Key = key,
                Value = value ?? "",
                Source = source ?? "manual",
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            };

            SaveData();

            TryCallWorldMind("WorldMind_SetServerFact", key, value);
        }

        private void RecordAuditEvent(AuditEvent evt)
        {
            if (evt == null) return;

            if (string.IsNullOrWhiteSpace(evt.TimestampUtc))
                evt.TimestampUtc = DateTime.UtcNow.ToString("o");

            _data.RecentEvents.Add(evt);

            while (_data.RecentEvents.Count > _config.Reporting.KeepRecentEvents)
                _data.RecentEvents.RemoveAt(0);

            SaveData();
        }

        private object BuildContextPacket(string playerIdOrName)
        {
            Dictionary<string, object> packet = new Dictionary<string, object>
            {
                ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
                ["query"] = playerIdOrName ?? "",
                ["manualFacts"] = _data.ManualFacts,
                ["pluginStatus"] = GetPluginStatuses(),
                ["recentAuditEvents"] = _data.RecentEvents.OrderByDescending(x => x.TimestampUtc).Take(_config.Reporting.ContextRecentEventLimit).ToList()
            };

            object playerProfile = GetPlayerProfile(playerIdOrName);
            if (playerProfile != null)
                packet["playerProfile"] = playerProfile;

            object adminProfile = GetAdminProfile(playerIdOrName);
            if (adminProfile != null)
                packet["adminProfile"] = adminProfile;

            object questSummary = GetQuestSummary(playerIdOrName);
            if (questSummary != null)
                packet["questSummary"] = questSummary;

            object economySummary = GetShopSummary();
            if (economySummary != null)
                packet["economySummary"] = economySummary;

            object providerStatus = GetProviderStatus();
            if (providerStatus != null)
                packet["providerStatus"] = providerStatus;

            return packet;
        }

        #endregion

        #region Plugin reads

        private object GetPlayerProfile(string query)
        {
            if (WorldMindPlayerBrain == null || string.IsNullOrWhiteSpace(query)) return null;

            try
            {
                object profile = WorldMindPlayerBrain.Call("WorldMindPlayerBrain_GetProfile", query);
                if (profile != null) return profile;

                object summary = WorldMindPlayerBrain.Call("WorldMindPlayerBrain_GetSummary", query);
                return summary;
            }
            catch
            {
                return null;
            }
        }

        private object GetAdminProfile(string query)
        {
            if (WorldMindAdminMind == null || string.IsNullOrWhiteSpace(query)) return null;

            try
            {
                return WorldMindAdminMind.Call("WorldMindAdminMind_GetPlayerAdminProfile", query);
            }
            catch
            {
                return null;
            }
        }

        private object GetQuestSummary(string query)
        {
            if (WorldMindQuestBrain == null || string.IsNullOrWhiteSpace(query)) return null;

            try
            {
                return WorldMindQuestBrain.Call("WorldMindQuestBrain_GetQuestSummary", query);
            }
            catch
            {
                return null;
            }
        }

        private object GetShopSummary()
        {
            if (WorldMindShopBrain == null) return null;

            try
            {
                return WorldMindShopBrain.Call("WorldMindShopBrain_GetEconomySummary");
            }
            catch
            {
                return null;
            }
        }

        private object GetProviderStatus()
        {
            if (WorldMindProviderBrain == null) return null;

            try
            {
                return WorldMindProviderBrain.Call("WorldMindProviderBrain_GetStatus");
            }
            catch
            {
                return null;
            }
        }

        private object TryCallWorldMind(string hook, params object[] args)
        {
            if (WorldMindV2 == null) return null;

            try
            {
                return WorldMindV2.Call(hook, args);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region WorldMind ask

        private void AskAuditQuestion(BasePlayer admin, string question)
        {
            if (WorldMindV2 == null)
            {
                Reply(admin, "WorldMindV2 is not loaded.");
                return;
            }

            string prompt =
                "You are WorldMind answering an admin-only memory/context audit question for a generic Rust server.\n" +
                "Do not invent facts. Do not assume VIP, Discord, WarMode, kits, homes, teleport, custom economy, factions, or server-specific systems unless present in the context.\n" +
                "If evidence is missing, say what is missing. Keep the answer practical and concise.\n" +
                $"Admin question: {question}\n" +
                $"Audit context JSON:\n{JsonConvert.SerializeObject(BuildContextPacket(string.Empty), Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindMemoryAudit", "audit_question");
                string message = result == null ? "" : result.ToString();
                Reply(admin, string.IsNullOrWhiteSpace(message) ? "WorldMind returned no audit answer." : message);
            }
            catch (Exception ex)
            {
                Reply(admin, $"WorldMind audit failed: {ex.Message}");
            }
        }

        #endregion

        #region Formatting

        private string GetStatusText()
        {
            return
                "WorldMindMemoryAudit status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"Manual facts: {_data.ManualFacts.Count}\n" +
                $"Recent audit events: {_data.RecentEvents.Count}\n" +
                $"PlayerBrain linked: {(WorldMindPlayerBrain != null ? "yes" : "no")}\n" +
                $"AdminMind linked: {(WorldMindAdminMind != null ? "yes" : "no")}\n" +
                $"ShopBrain linked: {(WorldMindShopBrain != null ? "yes" : "no")}\n" +
                $"QuestBrain linked: {(WorldMindQuestBrain != null ? "yes" : "no")}\n" +
                $"ProviderBrain linked: {(WorldMindProviderBrain != null ? "yes" : "no")}";
        }

        private string BuildPluginStatusText()
        {
            List<string> lines = new List<string> { "WorldMind plugin links:" };

            foreach (PluginStatus status in GetPluginStatuses())
                lines.Add($"- {status.Name}: {(status.Loaded ? "loaded" : "missing")}");

            return string.Join("\n", lines.ToArray());
        }

        private List<PluginStatus> GetPluginStatuses()
        {
            return new List<PluginStatus>
            {
                new PluginStatus("WorldMindV2", WorldMindV2 != null),
                new PluginStatus("WorldMindPlayerBrain", WorldMindPlayerBrain != null),
                new PluginStatus("WorldMindAdminMind", WorldMindAdminMind != null),
                new PluginStatus("WorldMindShopBrain", WorldMindShopBrain != null),
                new PluginStatus("WorldMindQuestBrain", WorldMindQuestBrain != null),
                new PluginStatus("WorldMindProviderBrain", WorldMindProviderBrain != null),
                new PluginStatus("WorldMindDiscordMind", WorldMindDiscordMind != null)
            };
        }

        private string BuildFactsText()
        {
            if (_data.ManualFacts.Count == 0)
                return "No manual audit facts stored.";

            List<string> lines = new List<string> { "Manual audit facts:" };

            foreach (AuditFact fact in _data.ManualFacts.Values.OrderBy(x => x.Key).Take(_config.Reporting.FactListLimit))
                lines.Add($"- {fact.Key}: {CleanText(fact.Value, 120)}");

            return string.Join("\n", lines.ToArray());
        }

        private string GetFactText(string key)
        {
            AuditFact fact;
            if (!_data.ManualFacts.TryGetValue(key, out fact))
                return $"Fact not found: {key}";

            return $"{fact.Key}\nValue: {fact.Value}\nSource: {fact.Source}\nUpdated UTC: {fact.UpdatedUtc}";
        }

        private string BuildPlayerAuditText(string query)
        {
            object packet = BuildContextPacket(query);
            return CleanText(JsonConvert.SerializeObject(packet, Formatting.Indented), _config.Reporting.MaxChatOutputLength);
        }

        private string BuildContextPreview(string query)
        {
            object packet = BuildContextPacket(query);
            return CleanText(JsonConvert.SerializeObject(packet, Formatting.Indented), _config.Reporting.MaxChatOutputLength);
        }

        private string BuildRecentEventsText(int count)
        {
            List<AuditEvent> events = _data.RecentEvents.OrderByDescending(x => x.TimestampUtc).Take(count).ToList();

            if (events.Count == 0)
                return "No audit events recorded.";

            List<string> lines = new List<string> { "Recent audit events:" };

            foreach (AuditEvent evt in events)
                lines.Add($"- {evt.TimestampUtc} | {evt.Plugin} | {evt.EventType} | {CleanText(evt.Summary, 90)}");

            return string.Join("\n", lines.ToArray());
        }

        private string BuildProviderAuditText()
        {
            object providerStatus = GetProviderStatus();
            if (providerStatus == null)
                return "WorldMindProviderBrain is not loaded or returned no status.";

            return CleanText(JsonConvert.SerializeObject(providerStatus, Formatting.Indented), _config.Reporting.MaxChatOutputLength);
        }

        #endregion

        #region Helpers

        private string GetString(Dictionary<string, object> packet, string key, string fallback)
        {
            if (packet == null) return fallback;

            object value;
            return packet.TryGetValue(key, out value) && value != null ? value.ToString() : fallback;
        }

        private string CleanText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string clean = value.Trim();

            if (maxLength > 0 && clean.Length > maxLength)
                clean = clean.Substring(0, maxLength - 3) + "...";

            return clean;
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind MemoryAudit]</color> {message}");
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

            [JsonProperty("Reporting")]
            public ReportingSettings Reporting = new ReportingSettings();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.EnsureDefaults();
                return config;
            }

            public void EnsureDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (Reporting == null) Reporting = new ReportingSettings();
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
        }

        private class ReportingSettings
        {
            [JsonProperty("KeepRecentEvents")]
            public int KeepRecentEvents = 250;

            [JsonProperty("ContextRecentEventLimit")]
            public int ContextRecentEventLimit = 20;

            [JsonProperty("FactListLimit")]
            public int FactListLimit = 50;

            [JsonProperty("MaxChatOutputLength")]
            public int MaxChatOutputLength = 1800;
        }

        private class StoredData
        {
            [JsonProperty("ManualFacts")]
            public Dictionary<string, AuditFact> ManualFacts = new Dictionary<string, AuditFact>();

            [JsonProperty("RecentEvents")]
            public List<AuditEvent> RecentEvents = new List<AuditEvent>();

            public void EnsureDefaults()
            {
                if (ManualFacts == null) ManualFacts = new Dictionary<string, AuditFact>();
                if (RecentEvents == null) RecentEvents = new List<AuditEvent>();
            }
        }

        public class AuditFact
        {
            public string Key = "";
            public string Value = "";
            public string Source = "";
            public string UpdatedUtc = "";
        }

        public class AuditEvent
        {
            public string Plugin = "";
            public string EventType = "";
            public string Summary = "";
            public string PayloadJson = "";
            public string TimestampUtc = "";
        }

        public class PluginStatus
        {
            public string Name;
            public bool Loaded;

            public PluginStatus(string name, bool loaded)
            {
                Name = name;
                Loaded = loaded;
            }
        }

        #endregion
    }
}

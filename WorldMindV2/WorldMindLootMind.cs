/*
DDDDDD     VV        VV    8888888     DDDDDD
DD   DD     VV      VV    88     88    DD   DD
DD    DD     VV    VV     88     88    DD    DD
DD    DD      VV  VV       8888888     DD    DD
DD    DD       VVVV       88     88    DD    DD
DD   DD         VV        88     88    DD   DD
DDDDDD          VV         8888888     DDDDDD

Made with love by Deviated Systems
Author: Devi8d0ne
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindLootMind", "Devi8d0ne", "1.0.0")]
    [Description("WorldMind companion plugin that turns generic Rust inventory and loot context into short player-facing loot advice.")]
    public class WorldMindLootMind : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";
        private const string DV8DAsciiTag = @"
DDDDDD     VV        VV    8888888     DDDDDD
DD   DD     VV      VV    88     88    DD   DD
DD    DD     VV    VV     88     88    DD    DD
DD    DD      VV  VV       8888888     DD    DD
DD    DD       VVVV       88     88    DD    DD
DD   DD         VV        88     88    DD   DD
DDDDDD          VV         8888888     DDDDDD
";

        private const string PermissionAdmin = "worldmindlootmind.admin";
        private const string PermissionUse = "worldmindlootmind.use";

        [PluginReference] private Plugin WorldMindV2;

        private PluginConfig _config;
        private readonly Dictionary<string, double> _lastAdvice = new Dictionary<string, double>();
        private readonly Dictionary<string, int> _adviceCounts = new Dictionary<string, int>();

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionUse, this);
            LoadConfigValues();
            PrintStartup();
        }

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.Default();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("Config was null after read.");
                _config.Normalize();
            }
            catch (Exception ex)
            {
                PrintError("Config read failed. Existing config was NOT overwritten. Runtime defaults are being used for this session only. Error: " + ex.Message);
                _config = PluginConfig.Default();
                _config.Normalize();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadConfigValues()
        {
            LoadConfig();
        }

        private void PrintStartup()
        {
            if (_config.General.PrintAsciiOnLoad)
                Puts(DV8DAsciiTag);

            Puts("WorldMindLootMind loaded. " + MadeWithLoveTag);
            Puts("WorldMind bridge: " + (WorldMindV2 == null ? "not found" : "found") + ". Commands: /lootmind and /wmloot");
        }

        #endregion

        #region Commands

        [ChatCommand("lootmind")]
        private void CmdLootMind(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!_config.General.Enabled)
            {
                Reply(player, "LootMind is disabled.");
                return;
            }

            if (_config.General.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                Reply(player, "No permission.");
                return;
            }

            if (!CanAdvise(player.UserIDString, true))
            {
                Reply(player, "LootMind is cooling down. Try again shortly.");
                return;
            }

            LootSnapshot snapshot = BuildLootSnapshot(player, "manual_scan");
            SendLootAdvice(player, snapshot, true);
        }

        [ChatCommand("wmloot")]
        private void CmdAdmin(BasePlayer player, string command, string[] args)
        {
            if (player != null && !IsAdmin(player))
            {
                Reply(player, "No permission.");
                return;
            }

            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            if (sub == "reload")
            {
                LoadConfigValues();
                Reply(player, "WorldMindLootMind config reloaded.");
                return;
            }

            if (sub == "test")
            {
                if (player == null)
                {
                    Puts("Run /wmloot test in-game so the test has a player target.");
                    return;
                }

                LootSnapshot snapshot = BuildLootSnapshot(player, "manual_admin_test");
                SendLootAdvice(player, snapshot, true);
                return;
            }

            Reply(player,
                "WorldMindLootMind status\n" +
                "Enabled: " + _config.General.Enabled + "\n" +
                "WorldMind: " + (WorldMindV2 == null ? "not found" : "found") + "\n" +
                "Use WorldMind: " + _config.WorldMind.UseWorldMind + "\n" +
                "Require Use Permission: " + _config.General.RequirePermission + "\n" +
                "Auto Advice: " + _config.General.AutoAdviceOnLoot + "\n" +
                "Cooldown Seconds: " + _config.General.AdviceCooldownSeconds + "\n" +
                "Minimum Score: " + _config.Scoring.MinimumScoreForAutoAdvice + "\n" +
                "Advice This Session: " + TotalAdvice());
        }

        #endregion

        #region Hooks

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (!_config.General.Enabled || !_config.General.AutoAdviceOnLoot) return;
            if (container == null || item == null) return;

            BasePlayer player = container.playerOwner;
            if (player == null || !player.userID.IsSteamId() || !player.IsConnected) return;
            if (_config.General.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermissionUse)) return;
            if (!CanAdvise(player.UserIDString, false)) return;

            LootSnapshot snapshot = BuildLootSnapshot(player, "item_added");
            snapshot.TriggerItem = BuildItemSummary(item);

            if (snapshot.Score < _config.Scoring.MinimumScoreForAutoAdvice) return;
            if (_config.Filters.IgnoreSmallStacks && item.amount < _config.Filters.MinimumTriggerStackAmount && GetItemWeight(item.info.shortname) <= 0) return;

            SendLootAdvice(player, snapshot, false);
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (!_config.General.Enabled || !_config.General.RecordLootOnDeath) return;
            if (player == null || !player.userID.IsSteamId()) return;

            LootSnapshot snapshot = BuildLootSnapshot(player, "player_death_inventory_snapshot");
            RecordEvent(player, "loot_snapshot_on_death", snapshot);
        }

        #endregion

        #region Loot Flow

        private LootSnapshot BuildLootSnapshot(BasePlayer player, string trigger)
        {
            LootSnapshot snapshot = new LootSnapshot
            {
                PlayerId = player.UserIDString,
                PlayerName = player.displayName,
                Trigger = trigger,
                Location = DescribeLocation(player.transform.position),
                Health = Mathf.RoundToInt(player.health),
                Score = 0,
                TotalItems = 0,
                Inventory = new List<ItemSummary>(),
                TopItems = new List<ItemSummary>()
            };

            AddContainerItems(snapshot, player.inventory.containerMain, "main");
            AddContainerItems(snapshot, player.inventory.containerBelt, "belt");
            AddContainerItems(snapshot, player.inventory.containerWear, "wear");

            snapshot.TotalItems = snapshot.Inventory.Sum(x => x.Amount);
            snapshot.TopItems = snapshot.Inventory
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Amount)
                .Take(_config.WorldMind.MaxItemsSentToWorldMind)
                .ToList();

            snapshot.RiskLevel = snapshot.Score >= _config.Scoring.HighRiskScore ? "high" : snapshot.Score >= _config.Scoring.MediumRiskScore ? "medium" : "low";
            return snapshot;
        }

        private void AddContainerItems(LootSnapshot snapshot, ItemContainer container, string containerName)
        {
            if (container == null || container.itemList == null) return;
            foreach (Item item in container.itemList)
            {
                if (item == null || item.info == null) continue;
                ItemSummary summary = BuildItemSummary(item);
                summary.Container = containerName;
                snapshot.Inventory.Add(summary);
                snapshot.Score += summary.Score;
            }
        }

        private ItemSummary BuildItemSummary(Item item)
        {
            string shortname = item.info == null ? "unknown" : item.info.shortname;
            string displayName = item.info == null || item.info.displayName == null ? shortname : item.info.displayName.english;
            string category = item.info == null ? "unknown" : item.info.category.ToString();
            int amount = item.amount;
            int weight = GetItemWeight(shortname);
            if (weight <= 0) weight = GetCategoryWeight(category);

            return new ItemSummary
            {
                Shortname = shortname,
                DisplayName = string.IsNullOrEmpty(displayName) ? shortname : displayName,
                Category = category,
                Amount = amount,
                Score = Mathf.Max(0, amount * Mathf.Max(1, weight))
            };
        }

        private void SendLootAdvice(BasePlayer player, LootSnapshot snapshot, bool manual)
        {
            if (player == null || !player.IsConnected || snapshot == null) return;

            CountAdvice(player.UserIDString);
            RecordEvent(player, "loot_advice_requested", snapshot);

            if (!_config.WorldMind.UseWorldMind || WorldMindV2 == null)
            {
                Reply(player, BuildFallbackAdvice(snapshot));
                return;
            }

            Dictionary<string, object> request = BuildWorldMindRequest(snapshot, manual);
            Action<string> callback = message =>
            {
                if (player == null || !player.IsConnected) return;
                if (string.IsNullOrEmpty(message) || message.StartsWith("WorldMind is disabled") || message.StartsWith("LM endpoint is not configured"))
                {
                    Reply(player, BuildFallbackAdvice(snapshot));
                    return;
                }

                Reply(player, TrimMessage(message, _config.WorldMind.MaxAdviceCharacters));
            };

            object called = WorldMindV2.Call("WorldMind_AskText", request, callback);
            if (called == null)
                Reply(player, BuildFallbackAdvice(snapshot));
        }

        private Dictionary<string, object> BuildWorldMindRequest(LootSnapshot snapshot, bool manual)
        {
            return new Dictionary<string, object>
            {
                ["Plugin"] = Name,
                ["EventType"] = manual ? "manual_loot_advice" : "auto_loot_advice",
                ["PlayerId"] = snapshot.PlayerId,
                ["PlayerName"] = snapshot.PlayerName,
                ["Tone"] = _config.WorldMind.RequestTone,
                ["Urgency"] = snapshot.RiskLevel == "high" ? 3 : snapshot.RiskLevel == "medium" ? 2 : 1,
                ["Truth"] = new Dictionary<string, object>
                {
                    ["task"] = "Write one short Rust loot/inventory advice message for the player. Plain text only. Do not mention server commands, Discord, VIP, PvP modes, custom economy, custom events, or server-specific systems unless WorldMind server facts/config explicitly allow them.",
                    ["maxCharacters"] = _config.WorldMind.MaxAdviceCharacters,
                    ["loot"] = snapshot.ToTruthDictionary()
                }
            };
        }

        private string BuildFallbackAdvice(LootSnapshot snapshot)
        {
            if (snapshot == null) return "LootMind: inventory scan unavailable.";
            if (snapshot.RiskLevel == "high") return "LootMind: high-value inventory detected. Consider banking loot before taking another fight.";
            if (snapshot.RiskLevel == "medium") return "LootMind: useful loot detected. Check your route and avoid carrying it longer than needed.";
            return "LootMind: inventory risk looks low. Keep moving and watch your surroundings.";
        }

        #endregion

        #region Helpers

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return true;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin);
        }

        private void Reply(BasePlayer player, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (player == null) Puts(message);
            else SendReply(player, "<color=#c7b99a>[LootMind]</color> " + message);
        }

        private bool CanAdvise(string playerId, bool manual)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            double now = Interface.Oxide.Now;
            double last;
            double cooldown = manual ? _config.General.ManualAdviceCooldownSeconds : _config.General.AdviceCooldownSeconds;
            if (_lastAdvice.TryGetValue(playerId, out last) && now - last < cooldown)
                return false;
            _lastAdvice[playerId] = now;
            return true;
        }

        private void CountAdvice(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            int count;
            _adviceCounts.TryGetValue(playerId, out count);
            _adviceCounts[playerId] = count + 1;
        }

        private int TotalAdvice()
        {
            return _adviceCounts.Values.Sum();
        }

        private int GetItemWeight(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return 0;
            int weight;
            return _config.Scoring.ItemWeights.TryGetValue(shortname, out weight) ? weight : 0;
        }

        private int GetCategoryWeight(string category)
        {
            if (string.IsNullOrEmpty(category)) return 1;
            int weight;
            return _config.Scoring.CategoryWeights.TryGetValue(category, out weight) ? weight : 1;
        }

        private string DescribeLocation(Vector3 position)
        {
            object result = WorldMindV2 == null ? null : WorldMindV2.Call("WorldMind_DescribeLocation", position);
            if (result is string && !string.IsNullOrEmpty((string)result)) return (string)result;
            return "x " + Mathf.RoundToInt(position.x) + ", y " + Mathf.RoundToInt(position.y) + ", z " + Mathf.RoundToInt(position.z);
        }

        private void RecordEvent(BasePlayer player, string eventType, LootSnapshot snapshot)
        {
            if (!_config.WorldMind.RecordEventsToWorldMind || WorldMindV2 == null || player == null || snapshot == null) return;
            Dictionary<string, object> truth = snapshot.ToTruthDictionary();
            WorldMindV2.Call("WorldMind_RecordEvent", Name, eventType, player.UserIDString, truth);
        }

        private string TrimMessage(string message, int maxChars)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Replace("\r", " ").Replace("\n", " ").Trim();
            if (maxChars <= 0 || message.Length <= maxChars) return message;
            return message.Substring(0, maxChars).TrimEnd() + "...";
        }

        #endregion

        #region Data Models

        private class PluginConfig
        {
            [JsonProperty("General")]
            public GeneralConfig General = new GeneralConfig();

            [JsonProperty("WorldMind Bridge")]
            public WorldMindConfig WorldMind = new WorldMindConfig();

            [JsonProperty("Loot Filters")]
            public FilterConfig Filters = new FilterConfig();

            [JsonProperty("Loot Scoring")]
            public ScoringConfig Scoring = new ScoringConfig();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.Normalize();
                return config;
            }

            public void Normalize()
            {
                if (General == null) General = new GeneralConfig();
                if (WorldMind == null) WorldMind = new WorldMindConfig();
                if (Filters == null) Filters = new FilterConfig();
                if (Scoring == null) Scoring = new ScoringConfig();
                Scoring.Normalize();
            }
        }

        private class GeneralConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Print DV8D ASCII On Load")]
            public bool PrintAsciiOnLoad = true;

            [JsonProperty("Require worldmindlootmind.use Permission For Player Advice")]
            public bool RequirePermission = false;

            [JsonProperty("Auto Advice When Loot Score Is High")]
            public bool AutoAdviceOnLoot = true;

            [JsonProperty("Record Inventory Snapshot On Death")]
            public bool RecordLootOnDeath = true;

            [JsonProperty("Auto Advice Cooldown Seconds")]
            public float AdviceCooldownSeconds = 180f;

            [JsonProperty("Manual /lootmind Cooldown Seconds")]
            public float ManualAdviceCooldownSeconds = 30f;
        }

        private class WorldMindConfig
        {
            [JsonProperty("Use WorldMindV2 If Loaded")]
            public bool UseWorldMind = true;

            [JsonProperty("Record Loot Events To WorldMind Timeline")]
            public bool RecordEventsToWorldMind = true;

            [JsonProperty("WorldMind Request Tone")]
            public string RequestTone = "concise Rust survival advisor";

            [JsonProperty("Max Advice Characters")]
            public int MaxAdviceCharacters = 180;

            [JsonProperty("Max Items Sent To WorldMind")]
            public int MaxItemsSentToWorldMind = 10;
        }

        private class FilterConfig
        {
            [JsonProperty("Ignore Small Stacks Unless Item Has Explicit Weight")]
            public bool IgnoreSmallStacks = true;

            [JsonProperty("Minimum Trigger Stack Amount")]
            public int MinimumTriggerStackAmount = 10;
        }

        private class ScoringConfig
        {
            [JsonProperty("Minimum Score For Auto Advice")]
            public int MinimumScoreForAutoAdvice = 300;

            [JsonProperty("Medium Risk Score")]
            public int MediumRiskScore = 300;

            [JsonProperty("High Risk Score")]
            public int HighRiskScore = 900;

            [JsonProperty("Item Weights By Shortname")]
            public Dictionary<string, int> ItemWeights = new Dictionary<string, int>();

            [JsonProperty("Category Weights")]
            public Dictionary<string, int> CategoryWeights = new Dictionary<string, int>();

            public void Normalize()
            {
                if (ItemWeights == null) ItemWeights = new Dictionary<string, int>();
                if (CategoryWeights == null) CategoryWeights = new Dictionary<string, int>();

                AddDefaultItem("scrap", 3);
                AddDefaultItem("metal.refined", 4);
                AddDefaultItem("sulfur", 2);
                AddDefaultItem("sulfur.ore", 1);
                AddDefaultItem("diesel_barrel", 80);
                AddDefaultItem("techparts", 35);
                AddDefaultItem("targeting.computer", 60);
                AddDefaultItem("cctv.camera", 50);
                AddDefaultItem("rifle.ak", 160);
                AddDefaultItem("rifle.lr300", 140);
                AddDefaultItem("lmg.m249", 300);
                AddDefaultItem("explosive.timed", 220);
                AddDefaultItem("rocket.launcher", 200);
                AddDefaultItem("ammo.rocket.basic", 120);

                AddDefaultCategory("Weapon", 8);
                AddDefaultCategory("Construction", 2);
                AddDefaultCategory("Resources", 1);
                AddDefaultCategory("Component", 4);
                AddDefaultCategory("Ammunition", 3);
                AddDefaultCategory("Medical", 2);
                AddDefaultCategory("Attire", 3);
            }

            private void AddDefaultItem(string shortname, int weight)
            {
                if (!ItemWeights.ContainsKey(shortname)) ItemWeights[shortname] = weight;
            }

            private void AddDefaultCategory(string category, int weight)
            {
                if (!CategoryWeights.ContainsKey(category)) CategoryWeights[category] = weight;
            }
        }

        private class LootSnapshot
        {
            public string PlayerId;
            public string PlayerName;
            public string Trigger;
            public string Location;
            public int Health;
            public int Score;
            public int TotalItems;
            public string RiskLevel;
            public ItemSummary TriggerItem;
            public List<ItemSummary> Inventory;
            public List<ItemSummary> TopItems;

            public Dictionary<string, object> ToTruthDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["playerName"] = PlayerName,
                    ["trigger"] = Trigger,
                    ["location"] = Location,
                    ["health"] = Health,
                    ["score"] = Score,
                    ["riskLevel"] = RiskLevel,
                    ["totalItems"] = TotalItems,
                    ["triggerItem"] = TriggerItem == null ? null : TriggerItem.ToTruthDictionary(),
                    ["topItems"] = TopItems == null ? new List<Dictionary<string, object>>() : TopItems.Select(x => x.ToTruthDictionary()).ToList()
                };
            }
        }

        private class ItemSummary
        {
            public string Shortname;
            public string DisplayName;
            public string Category;
            public string Container;
            public int Amount;
            public int Score;

            public Dictionary<string, object> ToTruthDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["shortname"] = Shortname,
                    ["displayName"] = DisplayName,
                    ["category"] = Category,
                    ["container"] = Container,
                    ["amount"] = Amount,
                    ["score"] = Score
                };
            }
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindItemBrain", "Devi8d0ne", "1.0.0")]
    [Description("Shared item lookup, category, and value context layer for the WorldMind plugin ecosystem.")]
    public class WorldMindItemBrain : RustPlugin
    {
        private const string PermissionAdmin = "worldminditembrain.admin";
        private const string PermissionUse = "worldminditembrain.use";
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

        private PluginConfig _config;
        private StoredData _data;

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
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindItemBrain");
            }

            BuildLocalCache();
            Puts($"WorldMindItemBrain loaded. Cached {_data.ItemCache.Count} items.");
        }

        private void Unload()
        {
            SaveData();
        }

        #endregion

        #region Commands

        [ChatCommand("wmitem")]
        private void CmdItem(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMindItemBrain commands:\n" +
                    "/wmitem status\n" +
                    "/wmitem lookup <shortname or display name>\n" +
                    "/wmitem score <shortname>\n" +
                    "/wmitem inventory\n" +
                    "/wmitem reload\n" +
                    "/wmitem rebuild");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                if (!HasAdmin(player)) return;
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "reload")
            {
                if (!HasAdmin(player)) return;
                LoadPluginConfig();
                LoadData();
                BuildLocalCache();
                Reply(player, "WorldMindItemBrain reloaded.");
                return;
            }

            if (sub == "rebuild")
            {
                if (!HasAdmin(player)) return;
                BuildLocalCache(true);
                SaveData();
                Reply(player, $"Item cache rebuilt. Cached {_data.ItemCache.Count} items.");
                return;
            }

            if (sub == "lookup")
            {
                if (!CanUse(player)) return;
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmitem lookup <shortname or display name>");
                    return;
                }

                string query = string.Join(" ", args.Skip(1).ToArray());
                ItemPacket packet = LookupItem(query);
                Reply(player, packet == null ? $"No item found for: {query}" : FormatPacket(packet));
                return;
            }

            if (sub == "score")
            {
                if (!CanUse(player)) return;
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmitem score <shortname>");
                    return;
                }

                string shortname = args[1];
                ItemPacket packet = LookupItem(shortname);
                if (packet == null)
                {
                    Reply(player, $"No item found for: {shortname}");
                    return;
                }

                Reply(player, $"{packet.DisplayName} ({packet.ShortName})\nCategory: {packet.Category}\nWorldMind value score: {packet.ValueScore}");
                return;
            }

            if (sub == "inventory")
            {
                if (!CanUse(player)) return;
                InventoryScore score = ScoreInventory(player);
                Reply(player, FormatInventoryScore(score));
                return;
            }

            Reply(player, "Unknown command. Use /wmitem for help.");
        }

        [ConsoleCommand("worldminditem.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            arg.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldminditem.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            BuildLocalCache();
            arg?.ReplyWith("WorldMindItemBrain reloaded.");
        }

        #endregion

        #region Hooks exposed to other plugins

        private object WorldMindItemBrain_LookupItem(string query)
        {
            return LookupItem(query);
        }

        private object WorldMindItemBrain_GetItemPacket(string query)
        {
            return LookupItem(query);
        }

        private object WorldMindItemBrain_GetDisplayName(string shortname)
        {
            ItemPacket packet = LookupItem(shortname);
            return packet == null ? shortname : packet.DisplayName;
        }

        private object WorldMindItemBrain_GetCategory(string shortname)
        {
            ItemPacket packet = LookupItem(shortname);
            return packet == null ? "Unknown" : packet.Category;
        }

        private object WorldMindItemBrain_ScoreItemShortName(string shortname)
        {
            ItemPacket packet = LookupItem(shortname);
            return packet == null ? 0 : packet.ValueScore;
        }

        private object WorldMindItemBrain_ScoreInventory(BasePlayer player)
        {
            return ScoreInventory(player);
        }

        private object WorldMindItemBrain_GetCacheSummary()
        {
            return new Dictionary<string, object>
            {
                ["cachedItems"] = _data.ItemCache.Count,
                ["customOverrides"] = _config.ValueOverrides.Count,
                ["externalProvidersEnabled"] = _config.ExternalProviders.RustTools.Enabled || _config.ExternalProviders.RustItemApi.Enabled
            };
        }

        #endregion

        #region Core item logic

        private void BuildLocalCache(bool force = false)
        {
            if (!force && _data.ItemCache.Count > 0 && !_config.General.RebuildCacheOnLoad)
                return;

            _data.ItemCache.Clear();

            foreach (ItemDefinition def in ItemManager.itemList)
            {
                if (def == null || string.IsNullOrEmpty(def.shortname)) continue;

                ItemPacket packet = CreatePacketFromDefinition(def);
                _data.ItemCache[def.shortname] = packet;
            }

            SaveData();
        }

        private ItemPacket LookupItem(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            string normalized = query.Trim().ToLowerInvariant();

            ItemPacket direct;
            if (_data.ItemCache.TryGetValue(normalized, out direct))
                return ApplyOverrides(direct);

            ItemDefinition byShortname = ItemManager.FindItemDefinition(normalized);
            if (byShortname != null)
            {
                ItemPacket packet = CreatePacketFromDefinition(byShortname);
                _data.ItemCache[packet.ShortName] = packet;
                return ApplyOverrides(packet);
            }

            ItemPacket byDisplay = _data.ItemCache.Values.FirstOrDefault(x =>
                !string.IsNullOrEmpty(x.DisplayName) &&
                x.DisplayName.ToLowerInvariant().Contains(normalized));

            if (byDisplay != null)
                return ApplyOverrides(byDisplay);

            ItemDefinition byList = ItemManager.itemList.FirstOrDefault(x =>
                x != null &&
                (
                    (!string.IsNullOrEmpty(x.shortname) && x.shortname.ToLowerInvariant().Contains(normalized)) ||
                    (x.displayName != null && !string.IsNullOrEmpty(x.displayName.english) && x.displayName.english.ToLowerInvariant().Contains(normalized))
                ));

            if (byList == null) return null;

            ItemPacket found = CreatePacketFromDefinition(byList);
            _data.ItemCache[found.ShortName] = found;
            return ApplyOverrides(found);
        }

        private ItemPacket CreatePacketFromDefinition(ItemDefinition def)
        {
            string shortname = def.shortname;
            string displayName = def.displayName == null || string.IsNullOrEmpty(def.displayName.english)
                ? shortname
                : def.displayName.english;

            string category = GuessCategory(shortname, displayName, def.category.ToString());
            int baseScore = ScoreByCategory(category);
            int keywordScore = ScoreByKeyword(shortname, displayName);
            int finalScore = Mathf.Clamp(baseScore + keywordScore, 0, _config.Scoring.MaximumScore);

            return new ItemPacket
            {
                ShortName = shortname,
                DisplayName = displayName,
                RustCategory = def.category.ToString(),
                Category = category,
                ValueScore = finalScore,
                Stackable = def.stackable,
                ItemId = def.itemid,
                Source = "local-rust-item-definition"
            };
        }

        private ItemPacket ApplyOverrides(ItemPacket source)
        {
            if (source == null) return null;

            ItemValueOverride overrideEntry;
            if (!_config.ValueOverrides.TryGetValue(source.ShortName, out overrideEntry) || overrideEntry == null)
                return source;

            ItemPacket copy = source.Clone();

            if (!string.IsNullOrWhiteSpace(overrideEntry.DisplayName))
                copy.DisplayName = overrideEntry.DisplayName.Trim();

            if (!string.IsNullOrWhiteSpace(overrideEntry.Category))
                copy.Category = overrideEntry.Category.Trim();

            if (overrideEntry.ValueScore >= 0)
                copy.ValueScore = Mathf.Clamp(overrideEntry.ValueScore, 0, _config.Scoring.MaximumScore);

            copy.Source = "local-rust-item-definition-with-owner-override";
            return copy;
        }

        private string GuessCategory(string shortname, string displayName, string rustCategory)
        {
            string text = $"{shortname} {displayName} {rustCategory}".ToLowerInvariant();

            if (ContainsAny(text, "rifle", "pistol", "shotgun", "smg", "lmg", "launcher", "bow", "crossbow", "ammo", "grenade", "explosive", "rocket", "c4", "satchel"))
                return "Weapons/Combat";

            if (ContainsAny(text, "armor", "helmet", "facemask", "chestplate", "roadsign", "hazmat", "gloves", "boots", "pants", "hoodie", "jacket"))
                return "Armor/Clothing";

            if (ContainsAny(text, "scrap", "metal.refined", "metal.fragments", "hq.metal", "sulfur", "charcoal", "wood", "stones", "cloth", "leather", "fuel.lowgrade", "crude.oil"))
                return "Resources";

            if (ContainsAny(text, "syringe", "largemedkit", "bandage", "antirad", "radiation", "tea"))
                return "Medical/Consumable";

            if (ContainsAny(text, "door", "wall", "floor", "foundation", "barricade", "gate", "lock", "cupboard", "ladder", "building.planner", "hammer"))
                return "Building/Base";

            if (ContainsAny(text, "card", "fuse", "diesel", "supply.signal", "targeting.computer", "cctv.camera", "laptop"))
                return "Monument/Utility";

            if (ContainsAny(text, "fish", "meat", "corn", "pumpkin", "mushroom", "apple", "water", "can.", "chocolate", "granolabar"))
                return "Food/Water";

            if (ContainsAny(text, "vehicle", "car", "engine", "module", "tire", "horse", "boat", "submarine", "copilothat"))
                return "Vehicle/Transport";

            if (ContainsAny(text, "electrical", "battery", "switch", "solar", "wire", "splitter", "branch", "turret", "sam", "sensor"))
                return "Electrical/Defense";

            return string.IsNullOrEmpty(rustCategory) ? "General" : rustCategory;
        }

        private int ScoreByCategory(string category)
        {
            int score;
            return _config.Scoring.CategoryScores.TryGetValue(category, out score) ? score : _config.Scoring.DefaultScore;
        }

        private int ScoreByKeyword(string shortname, string displayName)
        {
            string text = $"{shortname} {displayName}".ToLowerInvariant();
            int score = 0;

            foreach (KeyValuePair<string, int> kvp in _config.Scoring.KeywordScores)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                if (text.Contains(kvp.Key.ToLowerInvariant()))
                    score += kvp.Value;
            }

            return score;
        }

        private InventoryScore ScoreInventory(BasePlayer player)
        {
            InventoryScore score = new InventoryScore
            {
                PlayerId = player.UserIDString,
                PlayerName = player.displayName,
                TotalValueScore = 0,
                TotalItemStacks = 0,
                HighValueStacks = 0,
                TopItems = new List<ItemStackScore>()
            };

            if (player.inventory == null) return score;

            List<Item> items = new List<Item>();
            CollectItems(player.inventory.containerMain, items);
            CollectItems(player.inventory.containerBelt, items);
            CollectItems(player.inventory.containerWear, items);

            foreach (Item item in items)
            {
                if (item == null || item.info == null) continue;

                ItemPacket packet = LookupItem(item.info.shortname);
                if (packet == null) continue;

                int stackScore = packet.ValueScore * Math.Max(1, item.amount);
                score.TotalValueScore += stackScore;
                score.TotalItemStacks++;

                if (packet.ValueScore >= _config.Scoring.HighValueItemThreshold)
                    score.HighValueStacks++;

                score.TopItems.Add(new ItemStackScore
                {
                    ShortName = packet.ShortName,
                    DisplayName = packet.DisplayName,
                    Amount = item.amount,
                    Category = packet.Category,
                    ValueScore = packet.ValueScore,
                    StackValueScore = stackScore
                });
            }

            score.TopItems = score.TopItems
                .OrderByDescending(x => x.StackValueScore)
                .Take(_config.General.InventoryTopItemLimit)
                .ToList();

            score.RiskLabel = GetRiskLabel(score.TotalValueScore, score.HighValueStacks);

            RecordWorldMindEvent("inventory_scored", score);

            return score;
        }

        private void CollectItems(ItemContainer container, List<Item> result)
        {
            if (container == null || result == null) return;

            foreach (Item item in container.itemList)
            {
                if (item != null) result.Add(item);
            }
        }

        private string GetRiskLabel(int totalScore, int highValueStacks)
        {
            if (totalScore >= _config.Scoring.CriticalInventoryScore || highValueStacks >= 5)
                return "Critical";

            if (totalScore >= _config.Scoring.HighInventoryScore || highValueStacks >= 3)
                return "High";

            if (totalScore >= _config.Scoring.MediumInventoryScore || highValueStacks >= 1)
                return "Medium";

            return "Low";
        }

        private bool ContainsAny(string text, params string[] terms)
        {
            if (string.IsNullOrEmpty(text) || terms == null) return false;

            foreach (string term in terms)
            {
                if (!string.IsNullOrEmpty(term) && text.Contains(term))
                    return true;
            }

            return false;
        }

        #endregion

        #region WorldMind bridge

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (!_config.WorldMindIntegration.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindItemBrain",
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

        #region Formatting

        private string FormatPacket(ItemPacket packet)
        {
            return
                $"{packet.DisplayName} ({packet.ShortName})\n" +
                $"Category: {packet.Category}\n" +
                $"Rust Category: {packet.RustCategory}\n" +
                $"Value Score: {packet.ValueScore}\n" +
                $"Stack Size: {packet.Stackable}\n" +
                $"Source: {packet.Source}";
        }

        private string FormatInventoryScore(InventoryScore score)
        {
            List<string> lines = new List<string>
            {
                $"Inventory risk: {score.RiskLabel}",
                $"Total value score: {score.TotalValueScore}",
                $"High-value stacks: {score.HighValueStacks}",
                "Top items:"
            };

            if (score.TopItems.Count == 0)
            {
                lines.Add("- none");
            }
            else
            {
                foreach (ItemStackScore item in score.TopItems)
                {
                    lines.Add($"- {item.DisplayName} x{item.Amount} | {item.Category} | {item.StackValueScore}");
                }
            }

            return string.Join("\n", lines.ToArray());
        }

        private string GetStatusText()
        {
            return
                "WorldMindItemBrain status\n" +
                $"Cached items: {_data.ItemCache.Count}\n" +
                $"Owner overrides: {_config.ValueOverrides.Count}\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"RustTools enabled: {_config.ExternalProviders.RustTools.Enabled}\n" +
                $"RustItemApi enabled: {_config.ExternalProviders.RustItemApi.Enabled}\n" +
                $"Record events: {_config.WorldMindIntegration.RecordEventsToWorldMind}";
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind ItemBrain]</color> {message}");
        }

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return false;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin))
                return true;

            Reply(player, "You do not have permission to use that command.");
            return false;
        }

        private bool CanUse(BasePlayer player)
        {
            if (player == null) return false;

            if (!_config.General.RequireUsePermission)
                return true;

            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionUse))
                return true;

            Reply(player, "You do not have permission to use this.");
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
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private class PluginConfig
        {
            [JsonProperty("General")]
            public GeneralSettings General = new GeneralSettings();

            [JsonProperty("WorldMind Integration")]
            public WorldMindIntegrationSettings WorldMindIntegration = new WorldMindIntegrationSettings();

            [JsonProperty("Scoring")]
            public ScoringSettings Scoring = new ScoringSettings();

            [JsonProperty("Optional External Providers - disabled by default")]
            public ExternalProviderSettings ExternalProviders = new ExternalProviderSettings();

            [JsonProperty("Owner Item Overrides - shortname keyed")]
            public Dictionary<string, ItemValueOverride> ValueOverrides = new Dictionary<string, ItemValueOverride>();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.EnsureDefaults();
                return config;
            }

            public void EnsureDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (WorldMindIntegration == null) WorldMindIntegration = new WorldMindIntegrationSettings();
                if (Scoring == null) Scoring = new ScoringSettings();
                if (ExternalProviders == null) ExternalProviders = new ExternalProviderSettings();
                if (ValueOverrides == null) ValueOverrides = new Dictionary<string, ItemValueOverride>();

                Scoring.EnsureDefaults();
                ExternalProviders.EnsureDefaults();

                if (!ValueOverrides.ContainsKey("scrap"))
                    ValueOverrides["scrap"] = new ItemValueOverride { DisplayName = "Scrap", Category = "Resources", ValueScore = 10 };

                if (!ValueOverrides.ContainsKey("explosives"))
                    ValueOverrides["explosives"] = new ItemValueOverride { DisplayName = "Explosives", Category = "Weapons/Combat", ValueScore = 90 };

                if (!ValueOverrides.ContainsKey("rifle.ak"))
                    ValueOverrides["rifle.ak"] = new ItemValueOverride { DisplayName = "Assault Rifle", Category = "Weapons/Combat", ValueScore = 85 };
            }
        }

        private class GeneralSettings
        {
            [JsonProperty("PrintAsciiOnLoad")]
            public bool PrintAsciiOnLoad = true;

            [JsonProperty("Debug")]
            public bool Debug = false;

            [JsonProperty("RequireUsePermission")]
            public bool RequireUsePermission = false;

            [JsonProperty("RebuildCacheOnLoad")]
            public bool RebuildCacheOnLoad = false;

            [JsonProperty("InventoryTopItemLimit")]
            public int InventoryTopItemLimit = 8;
        }

        private class WorldMindIntegrationSettings
        {
            [JsonProperty("RecordEventsToWorldMind")]
            public bool RecordEventsToWorldMind = true;
        }

        private class ScoringSettings
        {
            [JsonProperty("DefaultScore")]
            public int DefaultScore = 5;

            [JsonProperty("MaximumScore")]
            public int MaximumScore = 100;

            [JsonProperty("HighValueItemThreshold")]
            public int HighValueItemThreshold = 50;

            [JsonProperty("MediumInventoryScore")]
            public int MediumInventoryScore = 500;

            [JsonProperty("HighInventoryScore")]
            public int HighInventoryScore = 1500;

            [JsonProperty("CriticalInventoryScore")]
            public int CriticalInventoryScore = 3000;

            [JsonProperty("CategoryScores")]
            public Dictionary<string, int> CategoryScores = new Dictionary<string, int>();

            [JsonProperty("KeywordScores")]
            public Dictionary<string, int> KeywordScores = new Dictionary<string, int>();

            public void EnsureDefaults()
            {
                if (CategoryScores == null) CategoryScores = new Dictionary<string, int>();
                if (KeywordScores == null) KeywordScores = new Dictionary<string, int>();

                AddCategory("Weapons/Combat", 45);
                AddCategory("Armor/Clothing", 30);
                AddCategory("Resources", 15);
                AddCategory("Medical/Consumable", 18);
                AddCategory("Building/Base", 20);
                AddCategory("Monument/Utility", 35);
                AddCategory("Food/Water", 5);
                AddCategory("Vehicle/Transport", 25);
                AddCategory("Electrical/Defense", 25);
                AddCategory("General", 5);

                AddKeyword("c4", 50);
                AddKeyword("rocket", 45);
                AddKeyword("explosive", 45);
                AddKeyword("satchel", 35);
                AddKeyword("rifle", 25);
                AddKeyword("launcher", 35);
                AddKeyword("lmg", 25);
                AddKeyword("scrap", 10);
                AddKeyword("hq", 25);
                AddKeyword("metal.refined", 25);
                AddKeyword("diesel", 25);
                AddKeyword("targeting.computer", 40);
                AddKeyword("cctv.camera", 35);
                AddKeyword("supply.signal", 45);
            }

            private void AddCategory(string key, int value)
            {
                if (!CategoryScores.ContainsKey(key))
                    CategoryScores[key] = value;
            }

            private void AddKeyword(string key, int value)
            {
                if (!KeywordScores.ContainsKey(key))
                    KeywordScores[key] = value;
            }
        }

        private class ExternalProviderSettings
        {
            [JsonProperty("RustTools")]
            public ProviderSettings RustTools = new ProviderSettings
            {
                Enabled = false,
                BaseUrl = "https://rusttools.xyz",
                ApiKey = "",
                UseForItemLookup = false,
                CacheHours = 24
            };

            [JsonProperty("RustItemApi")]
            public ProviderSettings RustItemApi = new ProviderSettings
            {
                Enabled = false,
                BaseUrl = "",
                ApiKey = "",
                UseForItemLookup = false,
                CacheHours = 24
            };

            public void EnsureDefaults()
            {
                if (RustTools == null) RustTools = new ProviderSettings { BaseUrl = "https://rusttools.xyz" };
                if (RustItemApi == null) RustItemApi = new ProviderSettings();
            }
        }

        private class ProviderSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("BaseUrl")]
            public string BaseUrl = "";

            [JsonProperty("ApiKey")]
            public string ApiKey = "";

            [JsonProperty("UseForItemLookup")]
            public bool UseForItemLookup = false;

            [JsonProperty("CacheHours")]
            public int CacheHours = 24;
        }

        private class ItemValueOverride
        {
            [JsonProperty("DisplayName")]
            public string DisplayName = "";

            [JsonProperty("Category")]
            public string Category = "";

            [JsonProperty("ValueScore - set -1 to keep automatic score")]
            public int ValueScore = -1;
        }

        private class StoredData
        {
            [JsonProperty("ItemCache")]
            public Dictionary<string, ItemPacket> ItemCache = new Dictionary<string, ItemPacket>();

            public void EnsureDefaults()
            {
                if (ItemCache == null)
                    ItemCache = new Dictionary<string, ItemPacket>();
            }
        }

        public class ItemPacket
        {
            public string ShortName;
            public string DisplayName;
            public string RustCategory;
            public string Category;
            public int ValueScore;
            public int Stackable;
            public int ItemId;
            public string Source;

            public ItemPacket Clone()
            {
                return new ItemPacket
                {
                    ShortName = ShortName,
                    DisplayName = DisplayName,
                    RustCategory = RustCategory,
                    Category = Category,
                    ValueScore = ValueScore,
                    Stackable = Stackable,
                    ItemId = ItemId,
                    Source = Source
                };
            }
        }

        public class InventoryScore
        {
            public string PlayerId;
            public string PlayerName;
            public int TotalValueScore;
            public int TotalItemStacks;
            public int HighValueStacks;
            public string RiskLabel;
            public List<ItemStackScore> TopItems = new List<ItemStackScore>();
        }

        public class ItemStackScore
        {
            public string ShortName;
            public string DisplayName;
            public int Amount;
            public string Category;
            public int ValueScore;
            public int StackValueScore;
        }

        #endregion
    }
}

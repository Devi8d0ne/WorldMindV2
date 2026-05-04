using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindShopBrain", "Devi8d0ne", "1.0.0")]
    [Description("Generic shop/economy intelligence and reporting layer for the WorldMind plugin ecosystem.")]
    public class WorldMindShopBrain : RustPlugin
    {
        private const string PermissionAdmin = "worldmindshopbrain.admin";
        private const string PermissionUse = "worldmindshopbrain.use";
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
        [PluginReference] private Plugin WorldMindItemBrain;

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
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindShopBrain");
            }

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            if (_config.General.EnablePeriodicWorldMindSummary && _config.General.PeriodicSummaryMinutes > 0)
            {
                timer.Every(Math.Max(300f, _config.General.PeriodicSummaryMinutes * 60f), () =>
                {
                    TryGenerateWorldMindSummary("periodic economy summary", false, null);
                });
            }

            Puts("WorldMindShopBrain loaded.");
        }

        private void Unload()
        {
            SaveData();
        }

        #endregion

        #region Commands

        [ChatCommand("wmshop")]
        private void CmdShop(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMindShopBrain commands:\n" +
                    "/wmshop status\n" +
                    "/wmshop summary\n" +
                    "/wmshop ask\n" +
                    "/wmshop top\n" +
                    "/wmshop record <buy|sell|earn|spend> <shortname/currency> <amount> [value]\n" +
                    "/wmshop player <steamId>\n" +
                    "/wmshop reload\n" +
                    "/wmshop save\n" +
                    "/wmshop reset");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (!HasAdmin(player)) return;

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "summary")
            {
                Reply(player, BuildEconomySummaryText());
                return;
            }

            if (sub == "ask")
            {
                TryGenerateWorldMindSummary("admin requested economy review", true, player);
                return;
            }

            if (sub == "top")
            {
                Reply(player, BuildTopItemsText());
                return;
            }

            if (sub == "player")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmshop player <steamId>");
                    return;
                }

                Reply(player, BuildPlayerSummaryText(args[1]));
                return;
            }

            if (sub == "record")
            {
                if (args.Length < 4)
                {
                    Reply(player, "Usage: /wmshop record <buy|sell|earn|spend> <shortname/currency> <amount> [value]");
                    return;
                }

                string type = args[1];
                string itemOrCurrency = args[2];

                int amount;
                if (!int.TryParse(args[3], out amount))
                {
                    Reply(player, "Amount must be a whole number.");
                    return;
                }

                double value = 0;
                if (args.Length >= 5)
                    double.TryParse(args[4], out value);

                ShopTransaction tx = new ShopTransaction
                {
                    SourcePlugin = "manual-admin-command",
                    TransactionType = type,
                    PlayerId = player.UserIDString,
                    PlayerName = player.displayName,
                    ItemShortName = itemOrCurrency,
                    CurrencyName = itemOrCurrency,
                    Amount = amount,
                    UnitValue = value,
                    TotalValue = value * amount,
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    Notes = "Manual admin test transaction"
                };

                RecordTransaction(tx);
                Reply(player, "Transaction recorded.");
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindShopBrain reloaded.");
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindShopBrain data saved.");
                return;
            }

            if (sub == "reset")
            {
                _data = new StoredData();
                SaveData();
                Reply(player, "WorldMindShopBrain data reset.");
                return;
            }

            Reply(player, "Unknown command. Use /wmshop for help.");
        }

        [ConsoleCommand("worldmindshop.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindshop.summary")]
        private void ConsoleSummary(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(BuildEconomySummaryText());
        }

        [ConsoleCommand("worldmindshop.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindShopBrain reloaded.");
        }

        #endregion

        #region Public hooks for other plugins

        private object WorldMindShopBrain_RecordTransaction(Dictionary<string, object> packet)
        {
            ShopTransaction tx = TransactionFromDictionary(packet);
            RecordTransaction(tx);
            return true;
        }

        private object WorldMindShopBrain_RecordTransactionJson(string json)
        {
            try
            {
                ShopTransaction tx = JsonConvert.DeserializeObject<ShopTransaction>(json);
                RecordTransaction(tx);
                return true;
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    Puts($"RecordTransactionJson failed: {ex.Message}");

                return false;
            }
        }

        private object WorldMindShopBrain_RecordItemBuy(string playerId, string playerName, string itemShortName, int amount, double totalValue, string sourcePlugin)
        {
            RecordTransaction(new ShopTransaction
            {
                SourcePlugin = sourcePlugin ?? "unknown",
                TransactionType = "buy",
                PlayerId = playerId ?? "",
                PlayerName = playerName ?? "",
                ItemShortName = itemShortName ?? "",
                Amount = amount,
                TotalValue = totalValue,
                UnitValue = amount <= 0 ? 0 : totalValue / amount,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            });

            return true;
        }

        private object WorldMindShopBrain_RecordItemSell(string playerId, string playerName, string itemShortName, int amount, double totalValue, string sourcePlugin)
        {
            RecordTransaction(new ShopTransaction
            {
                SourcePlugin = sourcePlugin ?? "unknown",
                TransactionType = "sell",
                PlayerId = playerId ?? "",
                PlayerName = playerName ?? "",
                ItemShortName = itemShortName ?? "",
                Amount = amount,
                TotalValue = totalValue,
                UnitValue = amount <= 0 ? 0 : totalValue / amount,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            });

            return true;
        }

        private object WorldMindShopBrain_RecordCurrencyChange(string playerId, string playerName, string currencyName, double amount, string reason, string sourcePlugin)
        {
            RecordTransaction(new ShopTransaction
            {
                SourcePlugin = sourcePlugin ?? "unknown",
                TransactionType = amount >= 0 ? "earn" : "spend",
                PlayerId = playerId ?? "",
                PlayerName = playerName ?? "",
                CurrencyName = currencyName ?? "currency",
                Amount = (int)Math.Abs(amount),
                TotalValue = amount,
                UnitValue = 1,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Notes = reason ?? ""
            });

            return true;
        }

        private object WorldMindShopBrain_GetEconomySummary()
        {
            return BuildEconomySummaryPacket();
        }

        private object WorldMindShopBrain_GetEconomySummaryText()
        {
            return BuildEconomySummaryText();
        }

        private object WorldMindShopBrain_GetTopItems()
        {
            return _data.ItemStats.Values
                .OrderByDescending(x => x.TotalTransactions)
                .Take(_config.Reporting.TopItemLimit)
                .ToList();
        }

        private object WorldMindShopBrain_GetPlayerEconomySummary(string steamId)
        {
            return GetPlayerStats(steamId);
        }

        #endregion

        #region Core

        private void RecordTransaction(ShopTransaction tx)
        {
            if (tx == null) return;

            tx.Normalize();

            if (string.IsNullOrWhiteSpace(tx.TimestampUtc))
                tx.TimestampUtc = DateTime.UtcNow.ToString("o");

            _data.TotalTransactions++;
            _data.LastTransactionUtc = tx.TimestampUtc;

            if (_config.Reporting.KeepRecentTransactions)
            {
                _data.RecentTransactions.Add(tx);

                int limit = Math.Max(10, _config.Reporting.RecentTransactionLimit);
                while (_data.RecentTransactions.Count > limit)
                    _data.RecentTransactions.RemoveAt(0);
            }

            UpdateItemStats(tx);
            UpdatePlayerStats(tx);
            UpdateCurrencyStats(tx);

            if (_config.WorldMindIntegration.RecordEventsToWorldMind)
                RecordWorldMindEvent("economy_transaction", tx);

            if (_config.General.Debug)
                Puts($"Recorded transaction: {tx.TransactionType} {tx.ItemShortName}/{tx.CurrencyName} x{tx.Amount} value={tx.TotalValue}");
        }

        private void UpdateItemStats(ShopTransaction tx)
        {
            if (string.IsNullOrWhiteSpace(tx.ItemShortName)) return;

            string key = tx.ItemShortName.ToLowerInvariant();

            ShopItemStats stats;
            if (!_data.ItemStats.TryGetValue(key, out stats))
            {
                stats = new ShopItemStats
                {
                    ItemShortName = key,
                    DisplayName = GetItemDisplayName(key),
                    Category = GetItemCategory(key)
                };
                _data.ItemStats[key] = stats;
            }

            stats.TotalTransactions++;
            stats.TotalAmount += Math.Max(0, tx.Amount);
            stats.TotalValue += tx.TotalValue;
            stats.LastSeenUtc = tx.TimestampUtc;

            string type = tx.TransactionType.ToLowerInvariant();
            if (type == "buy" || type == "purchase")
            {
                stats.BuyCount++;
                stats.BuyAmount += Math.Max(0, tx.Amount);
                stats.BuyValue += tx.TotalValue;
            }
            else if (type == "sell")
            {
                stats.SellCount++;
                stats.SellAmount += Math.Max(0, tx.Amount);
                stats.SellValue += tx.TotalValue;
            }
        }

        private void UpdatePlayerStats(ShopTransaction tx)
        {
            if (string.IsNullOrWhiteSpace(tx.PlayerId)) return;

            PlayerEconomyStats stats;
            if (!_data.PlayerStats.TryGetValue(tx.PlayerId, out stats))
            {
                stats = new PlayerEconomyStats
                {
                    PlayerId = tx.PlayerId,
                    PlayerName = tx.PlayerName
                };
                _data.PlayerStats[tx.PlayerId] = stats;
            }

            if (!string.IsNullOrWhiteSpace(tx.PlayerName))
                stats.PlayerName = tx.PlayerName;

            stats.TotalTransactions++;
            stats.TotalValue += tx.TotalValue;
            stats.LastSeenUtc = tx.TimestampUtc;

            string type = tx.TransactionType.ToLowerInvariant();
            if (type == "buy" || type == "purchase" || type == "spend")
                stats.TotalSpent += Math.Abs(tx.TotalValue);

            if (type == "sell" || type == "earn")
                stats.TotalEarned += Math.Abs(tx.TotalValue);

            if (!string.IsNullOrWhiteSpace(tx.ItemShortName))
            {
                int current;
                stats.ItemCounts.TryGetValue(tx.ItemShortName, out current);
                stats.ItemCounts[tx.ItemShortName] = current + Math.Max(0, tx.Amount);
            }
        }

        private void UpdateCurrencyStats(ShopTransaction tx)
        {
            if (string.IsNullOrWhiteSpace(tx.CurrencyName)) return;

            string key = tx.CurrencyName.ToLowerInvariant();

            CurrencyStats stats;
            if (!_data.CurrencyStats.TryGetValue(key, out stats))
            {
                stats = new CurrencyStats { CurrencyName = key };
                _data.CurrencyStats[key] = stats;
            }

            stats.TotalTransactions++;
            stats.TotalMoved += tx.TotalValue;
            stats.LastSeenUtc = tx.TimestampUtc;
        }

        private ShopTransaction TransactionFromDictionary(Dictionary<string, object> packet)
        {
            if (packet == null) return new ShopTransaction();

            ShopTransaction tx = new ShopTransaction
            {
                SourcePlugin = GetString(packet, "sourcePlugin", "unknown"),
                TransactionType = GetString(packet, "transactionType", "unknown"),
                PlayerId = GetString(packet, "playerId", ""),
                PlayerName = GetString(packet, "playerName", ""),
                ItemShortName = GetString(packet, "itemShortName", ""),
                CurrencyName = GetString(packet, "currencyName", ""),
                Notes = GetString(packet, "notes", ""),
                TimestampUtc = GetString(packet, "timestampUtc", DateTime.UtcNow.ToString("o")),
                Amount = GetInt(packet, "amount", 0),
                UnitValue = GetDouble(packet, "unitValue", 0),
                TotalValue = GetDouble(packet, "totalValue", 0)
            };

            if (Math.Abs(tx.TotalValue) < 0.0001 && Math.Abs(tx.UnitValue) > 0.0001 && tx.Amount != 0)
                tx.TotalValue = tx.UnitValue * tx.Amount;

            return tx;
        }

        private string GetString(Dictionary<string, object> packet, string key, string fallback)
        {
            object value;
            if (packet.TryGetValue(key, out value) && value != null)
                return value.ToString();

            return fallback;
        }

        private int GetInt(Dictionary<string, object> packet, string key, int fallback)
        {
            object value;
            if (!packet.TryGetValue(key, out value) || value == null)
                return fallback;

            int parsed;
            if (int.TryParse(value.ToString(), out parsed))
                return parsed;

            return fallback;
        }

        private double GetDouble(Dictionary<string, object> packet, string key, double fallback)
        {
            object value;
            if (!packet.TryGetValue(key, out value) || value == null)
                return fallback;

            double parsed;
            if (double.TryParse(value.ToString(), out parsed))
                return parsed;

            return fallback;
        }

        private string GetItemDisplayName(string shortname)
        {
            if (string.IsNullOrWhiteSpace(shortname)) return "";

            try
            {
                if (WorldMindItemBrain != null)
                {
                    object result = WorldMindItemBrain.Call("WorldMindItemBrain_GetDisplayName", shortname);
                    if (result != null && !string.IsNullOrWhiteSpace(result.ToString()))
                        return result.ToString();
                }
            }
            catch { }

            ItemDefinition def = ItemManager.FindItemDefinition(shortname);
            if (def != null && def.displayName != null && !string.IsNullOrEmpty(def.displayName.english))
                return def.displayName.english;

            return shortname;
        }

        private string GetItemCategory(string shortname)
        {
            if (string.IsNullOrWhiteSpace(shortname)) return "Unknown";

            try
            {
                if (WorldMindItemBrain != null)
                {
                    object result = WorldMindItemBrain.Call("WorldMindItemBrain_GetCategory", shortname);
                    if (result != null && !string.IsNullOrWhiteSpace(result.ToString()))
                        return result.ToString();
                }
            }
            catch { }

            return "Unknown";
        }

        private PlayerEconomyStats GetPlayerStats(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return null;

            PlayerEconomyStats stats;
            return _data.PlayerStats.TryGetValue(steamId, out stats) ? stats : null;
        }

        #endregion

        #region WorldMind

        private void TryGenerateWorldMindSummary(string reason, bool replyToAdmin, BasePlayer admin)
        {
            string prompt = BuildWorldMindEconomyPrompt(reason);

            if (WorldMindV2 == null)
            {
                if (replyToAdmin && admin != null)
                    Reply(admin, "WorldMindV2 is not loaded. Basic summary:\n" + BuildEconomySummaryText());

                return;
            }

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindShopBrain", "economy_summary");
                string message = result == null ? "" : result.ToString();

                if (string.IsNullOrWhiteSpace(message))
                    message = BuildEconomySummaryText();

                _data.LastWorldMindSummaryUtc = DateTime.UtcNow.ToString("o");
                _data.LastWorldMindSummary = message;
                SaveData();

                if (replyToAdmin && admin != null)
                    Reply(admin, message);

                RecordWorldMindEvent("economy_summary_generated", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["summary"] = message
                });
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    Puts($"WorldMind summary failed: {ex.Message}");

                if (replyToAdmin && admin != null)
                    Reply(admin, "WorldMind summary failed. Basic summary:\n" + BuildEconomySummaryText());
            }
        }

        private string BuildWorldMindEconomyPrompt(string reason)
        {
            EconomySummaryPacket packet = BuildEconomySummaryPacket();

            return
                "You are WorldMind analyzing generic Rust server economy/shop activity.\n" +
                "Do not assume Server Rewards, RP, VIP, Discord, kits, homes, teleport, WarMode, factions, or any server-specific economy unless explicitly present in the data.\n" +
                "Give a concise admin-facing economy summary with useful observations only. Do not invent missing facts.\n" +
                "Include possible risks like inflation, overused items, dead items, or suspicious volume only if supported by data.\n" +
                $"Reason: {reason}\n" +
                $"Economy data JSON:\n{JsonConvert.SerializeObject(packet, Formatting.Indented)}";
        }

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindShopBrain",
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

        private EconomySummaryPacket BuildEconomySummaryPacket()
        {
            List<ShopItemStats> topItems = _data.ItemStats.Values
                .OrderByDescending(x => x.TotalTransactions)
                .Take(_config.Reporting.TopItemLimit)
                .ToList();

            List<PlayerEconomyStats> topPlayers = _data.PlayerStats.Values
                .OrderByDescending(x => x.TotalTransactions)
                .Take(_config.Reporting.TopPlayerLimit)
                .ToList();

            List<CurrencyStats> currencies = _data.CurrencyStats.Values
                .OrderByDescending(x => x.TotalTransactions)
                .Take(_config.Reporting.TopCurrencyLimit)
                .ToList();

            return new EconomySummaryPacket
            {
                TotalTransactions = _data.TotalTransactions,
                LastTransactionUtc = _data.LastTransactionUtc,
                ItemCountTracked = _data.ItemStats.Count,
                PlayerCountTracked = _data.PlayerStats.Count,
                CurrencyCountTracked = _data.CurrencyStats.Count,
                TopItems = topItems,
                TopPlayers = topPlayers,
                TopCurrencies = currencies,
                RecentTransactions = _data.RecentTransactions.Take(Math.Max(0, _config.Reporting.SummaryRecentTransactionLimit)).ToList()
            };
        }

        private string BuildEconomySummaryText()
        {
            EconomySummaryPacket packet = BuildEconomySummaryPacket();

            List<string> lines = new List<string>
            {
                "WorldMindShopBrain economy summary",
                $"Total transactions: {packet.TotalTransactions}",
                $"Tracked items: {packet.ItemCountTracked}",
                $"Tracked players: {packet.PlayerCountTracked}",
                $"Tracked currencies: {packet.CurrencyCountTracked}",
                $"Last transaction UTC: {(string.IsNullOrWhiteSpace(packet.LastTransactionUtc) ? "none" : packet.LastTransactionUtc)}",
                "",
                "Top items:"
            };

            if (packet.TopItems.Count == 0)
            {
                lines.Add("- none yet");
            }
            else
            {
                foreach (ShopItemStats item in packet.TopItems)
                    lines.Add($"- {item.DisplayName} ({item.ItemShortName}) | tx={item.TotalTransactions} | amount={item.TotalAmount} | value={Math.Round(item.TotalValue, 2)}");
            }

            lines.Add("");
            lines.Add("Top players:");

            if (packet.TopPlayers.Count == 0)
            {
                lines.Add("- none yet");
            }
            else
            {
                foreach (PlayerEconomyStats player in packet.TopPlayers)
                    lines.Add($"- {player.PlayerName} ({player.PlayerId}) | tx={player.TotalTransactions} | spent={Math.Round(player.TotalSpent, 2)} | earned={Math.Round(player.TotalEarned, 2)}");
            }

            if (!string.IsNullOrWhiteSpace(_data.LastWorldMindSummary))
            {
                lines.Add("");
                lines.Add("Last WorldMind summary:");
                lines.Add(_data.LastWorldMindSummary);
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildTopItemsText()
        {
            List<ShopItemStats> items = _data.ItemStats.Values
                .OrderByDescending(x => x.TotalTransactions)
                .Take(_config.Reporting.TopItemLimit)
                .ToList();

            if (items.Count == 0)
                return "No item transactions recorded yet.";

            List<string> lines = new List<string> { "Top shop/economy items:" };

            foreach (ShopItemStats item in items)
            {
                lines.Add($"- {item.DisplayName} ({item.ItemShortName}) | category={item.Category} | buys={item.BuyCount} | sells={item.SellCount} | total={item.TotalTransactions}");
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildPlayerSummaryText(string steamId)
        {
            PlayerEconomyStats stats = GetPlayerStats(steamId);

            if (stats == null)
                return $"No economy stats found for {steamId}.";

            List<string> lines = new List<string>
            {
                $"Economy summary for {stats.PlayerName} ({stats.PlayerId})",
                $"Transactions: {stats.TotalTransactions}",
                $"Total value moved: {Math.Round(stats.TotalValue, 2)}",
                $"Total spent: {Math.Round(stats.TotalSpent, 2)}",
                $"Total earned: {Math.Round(stats.TotalEarned, 2)}",
                $"Last seen UTC: {stats.LastSeenUtc}",
                "Top item counts:"
            };

            foreach (KeyValuePair<string, int> kvp in stats.ItemCounts.OrderByDescending(x => x.Value).Take(8))
                lines.Add($"- {kvp.Key}: {kvp.Value}");

            return string.Join("\n", lines.ToArray());
        }

        private string GetStatusText()
        {
            return
                "WorldMindShopBrain status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"WorldMindItemBrain linked: {(WorldMindItemBrain != null ? "yes" : "no")}\n" +
                $"Total transactions: {_data.TotalTransactions}\n" +
                $"Tracked items: {_data.ItemStats.Count}\n" +
                $"Tracked players: {_data.PlayerStats.Count}\n" +
                $"Tracked currencies: {_data.CurrencyStats.Count}\n" +
                $"Recent transaction storage: {_config.Reporting.KeepRecentTransactions}\n" +
                $"Record events to WorldMind: {_config.WorldMindIntegration.RecordEventsToWorldMind}";
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind ShopBrain]</color> {message}");
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

            [JsonProperty("WorldMind Integration")]
            public WorldMindIntegrationSettings WorldMindIntegration = new WorldMindIntegrationSettings();

            [JsonProperty("Reporting")]
            public ReportingSettings Reporting = new ReportingSettings();

            [JsonProperty("Generic Economy Labels")]
            public EconomyLabels EconomyLabels = new EconomyLabels();

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
                if (Reporting == null) Reporting = new ReportingSettings();
                if (EconomyLabels == null) EconomyLabels = new EconomyLabels();
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

            [JsonProperty("EnablePeriodicWorldMindSummary")]
            public bool EnablePeriodicWorldMindSummary = false;

            [JsonProperty("PeriodicSummaryMinutes")]
            public float PeriodicSummaryMinutes = 60f;
        }

        private class WorldMindIntegrationSettings
        {
            [JsonProperty("RecordEventsToWorldMind")]
            public bool RecordEventsToWorldMind = true;
        }

        private class ReportingSettings
        {
            [JsonProperty("KeepRecentTransactions")]
            public bool KeepRecentTransactions = true;

            [JsonProperty("RecentTransactionLimit")]
            public int RecentTransactionLimit = 100;

            [JsonProperty("SummaryRecentTransactionLimit")]
            public int SummaryRecentTransactionLimit = 10;

            [JsonProperty("TopItemLimit")]
            public int TopItemLimit = 10;

            [JsonProperty("TopPlayerLimit")]
            public int TopPlayerLimit = 10;

            [JsonProperty("TopCurrencyLimit")]
            public int TopCurrencyLimit = 5;
        }

        private class EconomyLabels
        {
            [JsonProperty("DefaultCurrencyName")]
            public string DefaultCurrencyName = "currency";

            [JsonProperty("DefaultValueLabel")]
            public string DefaultValueLabel = "value";

            [JsonProperty("Notes")]
            public string Notes = "Generic economy tracking only. Configure external shop plugins to call WorldMindShopBrain hooks.";
        }

        private class StoredData
        {
            [JsonProperty("TotalTransactions")]
            public long TotalTransactions = 0;

            [JsonProperty("LastTransactionUtc")]
            public string LastTransactionUtc = "";

            [JsonProperty("LastWorldMindSummaryUtc")]
            public string LastWorldMindSummaryUtc = "";

            [JsonProperty("LastWorldMindSummary")]
            public string LastWorldMindSummary = "";

            [JsonProperty("ItemStats")]
            public Dictionary<string, ShopItemStats> ItemStats = new Dictionary<string, ShopItemStats>();

            [JsonProperty("PlayerStats")]
            public Dictionary<string, PlayerEconomyStats> PlayerStats = new Dictionary<string, PlayerEconomyStats>();

            [JsonProperty("CurrencyStats")]
            public Dictionary<string, CurrencyStats> CurrencyStats = new Dictionary<string, CurrencyStats>();

            [JsonProperty("RecentTransactions")]
            public List<ShopTransaction> RecentTransactions = new List<ShopTransaction>();

            public void EnsureDefaults()
            {
                if (ItemStats == null) ItemStats = new Dictionary<string, ShopItemStats>();
                if (PlayerStats == null) PlayerStats = new Dictionary<string, PlayerEconomyStats>();
                if (CurrencyStats == null) CurrencyStats = new Dictionary<string, CurrencyStats>();
                if (RecentTransactions == null) RecentTransactions = new List<ShopTransaction>();
            }
        }

        public class ShopTransaction
        {
            public string SourcePlugin = "";
            public string TransactionType = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public string ItemShortName = "";
            public string CurrencyName = "";
            public int Amount = 0;
            public double UnitValue = 0;
            public double TotalValue = 0;
            public string TimestampUtc = "";
            public string Notes = "";

            public void Normalize()
            {
                if (SourcePlugin == null) SourcePlugin = "";
                if (TransactionType == null) TransactionType = "";
                if (PlayerId == null) PlayerId = "";
                if (PlayerName == null) PlayerName = "";
                if (ItemShortName == null) ItemShortName = "";
                if (CurrencyName == null) CurrencyName = "";
                if (TimestampUtc == null) TimestampUtc = "";
                if (Notes == null) Notes = "";

                SourcePlugin = SourcePlugin.Trim();
                TransactionType = TransactionType.Trim().ToLowerInvariant();
                ItemShortName = ItemShortName.Trim().ToLowerInvariant();
                CurrencyName = CurrencyName.Trim().ToLowerInvariant();

                if (string.IsNullOrWhiteSpace(TransactionType))
                    TransactionType = "unknown";

                if (Math.Abs(TotalValue) < 0.0001 && Math.Abs(UnitValue) > 0.0001 && Amount != 0)
                    TotalValue = UnitValue * Amount;
            }
        }

        public class ShopItemStats
        {
            public string ItemShortName = "";
            public string DisplayName = "";
            public string Category = "";
            public int TotalTransactions = 0;
            public int TotalAmount = 0;
            public double TotalValue = 0;
            public int BuyCount = 0;
            public int BuyAmount = 0;
            public double BuyValue = 0;
            public int SellCount = 0;
            public int SellAmount = 0;
            public double SellValue = 0;
            public string LastSeenUtc = "";
        }

        public class PlayerEconomyStats
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public int TotalTransactions = 0;
            public double TotalValue = 0;
            public double TotalSpent = 0;
            public double TotalEarned = 0;
            public string LastSeenUtc = "";
            public Dictionary<string, int> ItemCounts = new Dictionary<string, int>();
        }

        public class CurrencyStats
        {
            public string CurrencyName = "";
            public int TotalTransactions = 0;
            public double TotalMoved = 0;
            public string LastSeenUtc = "";
        }

        public class EconomySummaryPacket
        {
            public long TotalTransactions;
            public string LastTransactionUtc;
            public int ItemCountTracked;
            public int PlayerCountTracked;
            public int CurrencyCountTracked;
            public List<ShopItemStats> TopItems = new List<ShopItemStats>();
            public List<PlayerEconomyStats> TopPlayers = new List<PlayerEconomyStats>();
            public List<CurrencyStats> TopCurrencies = new List<CurrencyStats>();
            public List<ShopTransaction> RecentTransactions = new List<ShopTransaction>();
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindDiscordMind", "Devi8d0ne", "1.1.0")]
    [Description("Deviated Systems Discord webhook bridge for the WorldMind plugin ecosystem.")]
    public class WorldMindDiscordMind : RustPlugin
    {
        private const string PermissionAdmin = "worldminddiscordmind.admin";
        private const string PermissionUse = "worldminddiscordmind.use";
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
        [PluginReference] private Plugin WorldMind;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Queue<DiscordQueueItem> _queue = new Queue<DiscordQueueItem>();
        private bool _sending;
        private Timer _queueTimer;
        private Timer _heartbeatTimer;

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
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindDiscordMind");
            }

            StartTimers();

            Puts($"WorldMindDiscordMind loaded. Enabled={_config.Webhook.Enabled} DefaultWebhook={HasDefaultWebhook()} Channels={CountConfiguredChannels()} Debug={_config.General.Debug}");

            if (_config.General.Debug)
                PrintStatusToConsole();
        }

        private void Unload()
        {
            _queueTimer?.Destroy();
            _heartbeatTimer?.Destroy();
            SaveData();
        }

        #endregion

        #region Commands

        [ChatCommand("wmdiscord")]
        private void CmdDiscord(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMindDiscordMind commands:\n" +
                    "/wmdiscord status\n" +
                    "/wmdiscord test [category]\n" +
                    "/wmdiscord send <message>\n" +
                    "/wmdiscord summary <notes>\n" +
                    "/wmdiscord queue\n" +
                    "/wmdiscord debug\n" +
                    "/wmdiscord reload\n" +
                    "/wmdiscord clear");
                return;
            }

            if (!HasAdmin(player)) return;

            string sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "test")
            {
                string category = args.Length >= 2 ? args[1].ToLowerInvariant() : "test";
                bool queued = QueueDiscordMessage("WorldMind Discord Test", BuildTestMessage(category), category, true, category);
                Reply(player, queued ? $"Test message queued for category '{category}'." : $"Test failed to queue for category '{category}'. Check console/data error.");
                return;
            }

            if (sub == "send")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmdiscord send <message>");
                    return;
                }

                string message = string.Join(" ", args.Skip(1).ToArray());
                bool queued = QueueDiscordMessage("WorldMind Manual Message", message, "manual", true, "default");
                Reply(player, queued ? "Message queued." : "Message failed to queue.");
                return;
            }

            if (sub == "summary")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmdiscord summary <notes>");
                    return;
                }

                string notes = string.Join(" ", args.Skip(1).ToArray());
                GenerateWorldMindDiscordSummary(notes, player);
                return;
            }

            if (sub == "queue")
            {
                Reply(player, GetQueueText());
                return;
            }

            if (sub == "debug")
            {
                _config.General.Debug = !_config.General.Debug;
                Reply(player, $"Debug is now {_config.General.Debug}. This does not auto-save config.");
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                StartTimers();
                Reply(player, "WorldMindDiscordMind reloaded without rewriting owner config.");
                return;
            }

            if (sub == "clear")
            {
                _queue.Clear();
                Reply(player, "Discord queue cleared.");
                return;
            }

            Reply(player, "Unknown command. Use /wmdiscord for help.");
        }

        [ConsoleCommand("worldminddiscord.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldminddiscord.test")]
        private void ConsoleTest(ConsoleSystem.Arg arg)
        {
            string category = "test";
            if (arg != null && arg.Args != null && arg.Args.Length >= 1 && !string.IsNullOrWhiteSpace(arg.Args[0]))
                category = arg.Args[0].ToLowerInvariant();

            bool queued = QueueDiscordMessage("WorldMind Discord Test", BuildTestMessage(category), category, true, category);
            arg?.ReplyWith(queued ? $"Test message queued for category '{category}'." : $"Test failed to queue for category '{category}'. LastError={_data.LastError}");
        }

        [ConsoleCommand("worldminddiscord.queue")]
        private void ConsoleQueue(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetQueueText());
        }

        [ConsoleCommand("worldminddiscord.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            StartTimers();
            arg?.ReplyWith("WorldMindDiscordMind reloaded without rewriting owner config.");
        }

        #endregion

        #region Public hooks for WorldMind ecosystem

        private object WorldMindDiscordMind_SendMessage(string title, string message, string category)
        {
            return QueueDiscordMessage(title, message, category, false, category);
        }

        private object WorldMindDiscordMind_SendMessageToChannel(string channelKey, string title, string message, string category)
        {
            return QueueDiscordMessage(title, message, category, false, channelKey);
        }

        private object WorldMindDiscordMind_SendEvent(Dictionary<string, object> packet)
        {
            return SendPacket(packet, false);
        }

        private object WorldMindDiscordMind_SendEventJson(string json)
        {
            return SendJsonPacket(json, false);
        }

        private object WorldMindDiscordMind_RecordEvent(Dictionary<string, object> packet)
        {
            return SendPacket(packet, false);
        }

        private object WorldMindDiscordMind_RecordEventJson(string json)
        {
            return SendJsonPacket(json, false);
        }

        private object WorldMindDiscordMind_SendNpcEvent(Dictionary<string, object> packet)
        {
            return SendPacket(WithCategory(packet, "npc"), false);
        }

        private object WorldMindDiscordMind_SendDeathEvent(Dictionary<string, object> packet)
        {
            return SendPacket(WithCategory(packet, "death"), false);
        }

        private object WorldMindDiscordMind_SendThreatEvent(Dictionary<string, object> packet)
        {
            return SendPacket(WithCategory(packet, "threat"), false);
        }

        private object WorldMindDiscordMind_SendRaidEvent(Dictionary<string, object> packet)
        {
            return SendPacket(WithCategory(packet, "raid"), false);
        }

        private object WorldMindDiscordMind_SendEconomyEvent(Dictionary<string, object> packet)
        {
            return SendPacket(WithCategory(packet, "economy"), false);
        }

        private object WorldMindDiscordMind_SendAdminEvent(Dictionary<string, object> packet)
        {
            return SendPacket(WithCategory(packet, "admin"), false);
        }

        private object WorldMindDiscordMind_SendServerEvent(Dictionary<string, object> packet)
        {
            return SendPacket(WithCategory(packet, "server"), false);
        }

        private object WorldMindDiscordMind_SendRaidSummary(string message)
        {
            return QueueDiscordMessage("Raid Summary", message, "raid", false, "raid");
        }

        private object WorldMindDiscordMind_SendDeathHighlight(string message)
        {
            return QueueDiscordMessage("Death Highlight", message, "death", false, "death");
        }

        private object WorldMindDiscordMind_SendNpcLine(string message)
        {
            return QueueDiscordMessage("NPC Intelligence", message, "npc", false, "npc");
        }

        private object WorldMindDiscordMind_SendThreatAlert(string message)
        {
            return QueueDiscordMessage("Threat Alert", message, "threat", false, "threat");
        }

        private object WorldMindDiscordMind_SendEventSummary(string message)
        {
            return QueueDiscordMessage("Event Summary", message, "event", false, "event");
        }

        private object WorldMindDiscordMind_SendAdminSummary(string message)
        {
            return QueueDiscordMessage("Admin Summary", message, "admin", false, "admin");
        }

        // Alias hooks for plugins that call the bridge by generic WorldMind-style names.
        private object WorldMindDiscord_SendMessage(string title, string message, string category)
        {
            return WorldMindDiscordMind_SendMessage(title, message, category);
        }

        private object WorldMindDiscord_SendEvent(Dictionary<string, object> packet)
        {
            return WorldMindDiscordMind_SendEvent(packet);
        }

        private object WorldMindDiscord_SendEventJson(string json)
        {
            return WorldMindDiscordMind_SendEventJson(json);
        }

        private object WorldMind_SendDiscordMessage(string title, string message, string category)
        {
            return WorldMindDiscordMind_SendMessage(title, message, category);
        }

        private object WorldMind_SendDiscordEvent(Dictionary<string, object> packet)
        {
            return WorldMindDiscordMind_SendEvent(packet);
        }

        private object WorldMindDiscordMind_GetStatus()
        {
            return new Dictionary<string, object>
            {
                ["enabled"] = _config.Webhook.Enabled,
                ["hasDefaultWebhook"] = HasDefaultWebhook(),
                ["configuredChannels"] = CountConfiguredChannels(),
                ["queueLength"] = _queue.Count,
                ["sending"] = _sending,
                ["queuedCount"] = _data.QueuedCount,
                ["sentCount"] = _data.SentCount,
                ["failedCount"] = _data.FailedCount,
                ["lastSendUtc"] = _data.LastSendUtc,
                ["lastError"] = _data.LastError
            };
        }

        #endregion

        #region Packet routing

        private bool SendJsonPacket(string json, bool force)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                Dictionary<string, object> packet = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                return SendPacket(packet, force);
            }
            catch (Exception ex)
            {
                SetError($"Event JSON parse failed: {ex.Message}");
                return false;
            }
        }

        private bool SendPacket(Dictionary<string, object> packet, bool force)
        {
            if (packet == null)
            {
                SetError("Null event packet.");
                return false;
            }

            string category = NormalizeCategory(GetPacketString(packet, "category", GetPacketString(packet, "type", "event")));
            string channelKey = NormalizeChannel(GetPacketString(packet, "channelKey", GetPacketString(packet, "channel", category)));

            if (!CategoryEnabled(category) && !force)
            {
                if (_config.General.Debug)
                    Puts($"Event ignored: category '{category}' disabled.");
                return false;
            }

            string title = GetPacketString(packet, "title", BuildTitleForCategory(category));
            string message = GetPacketString(packet, "message", "");

            if (string.IsNullOrWhiteSpace(message))
                message = BuildMessageFromPacket(packet, category);

            return QueueDiscordMessage(title, message, category, force, channelKey);
        }

        private Dictionary<string, object> WithCategory(Dictionary<string, object> packet, string category)
        {
            Dictionary<string, object> copy = packet == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(packet);

            if (!copy.ContainsKey("category")) copy["category"] = category;
            if (!copy.ContainsKey("channelKey")) copy["channelKey"] = category;
            return copy;
        }

        private string BuildMessageFromPacket(Dictionary<string, object> packet, string category)
        {
            string player = GetPacketString(packet, "player", GetPacketString(packet, "playerName", ""));
            string target = GetPacketString(packet, "target", GetPacketString(packet, "targetName", ""));
            string npc = GetPacketString(packet, "npc", GetPacketString(packet, "npcName", GetPacketString(packet, "speaker", "")));
            string line = GetPacketString(packet, "line", GetPacketString(packet, "text", ""));
            string location = GetPacketString(packet, "location", GetPacketString(packet, "monument", ""));
            string grid = GetPacketString(packet, "grid", "");
            string weapon = GetPacketString(packet, "weapon", "");
            string distance = GetPacketString(packet, "distance", GetPacketString(packet, "distanceMeters", ""));
            string summary = GetPacketString(packet, "summary", "");
            string eventType = GetPacketString(packet, "eventType", GetPacketString(packet, "event", category));

            List<string> parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(summary)) parts.Add(summary);
            if (!string.IsNullOrWhiteSpace(line)) parts.Add(line);

            if (category == "npc")
            {
                string who = string.IsNullOrWhiteSpace(npc) ? "NPC" : npc;
                if (!string.IsNullOrWhiteSpace(player)) parts.Add($"{who} reacted to {player}.");
                else parts.Add($"{who} reacted to island movement.");
            }
            else if (category == "death")
            {
                if (!string.IsNullOrWhiteSpace(player) && !string.IsNullOrWhiteSpace(target)) parts.Add($"{target} died to {player}.");
                else if (!string.IsNullOrWhiteSpace(player)) parts.Add($"Death event involving {player}.");
                else parts.Add("Death event recorded.");
            }
            else if (category == "threat")
            {
                if (!string.IsNullOrWhiteSpace(player)) parts.Add($"Threat read for {player}.");
                else parts.Add("Threat event recorded.");
            }
            else
            {
                parts.Add($"WorldMind event recorded: {eventType}.");
            }

            if (!string.IsNullOrWhiteSpace(weapon)) parts.Add($"Weapon: {weapon}.");
            if (!string.IsNullOrWhiteSpace(distance)) parts.Add($"Distance: {distance}.");
            if (!string.IsNullOrWhiteSpace(location)) parts.Add($"Location: {location}.");
            if (!string.IsNullOrWhiteSpace(grid)) parts.Add($"Grid: {grid}.");

            string result = string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray());

            if (string.IsNullOrWhiteSpace(result) || result.Equals("true", StringComparison.OrdinalIgnoreCase) || result.Equals("false", StringComparison.OrdinalIgnoreCase))
                result = JsonConvert.SerializeObject(packet, Formatting.Indented);

            return result;
        }

        private string BuildTitleForCategory(string category)
        {
            DiscordCategorySettings settings;
            if (_config.Categories.Items.TryGetValue(category, out settings) && settings != null && !string.IsNullOrWhiteSpace(settings.Title))
                return settings.Title;

            switch (category)
            {
                case "npc": return "NPC Intelligence";
                case "death": return "Death Highlight";
                case "threat": return "Threat Sense";
                case "raid": return "Raid Intelligence";
                case "economy": return "Economy Signal";
                case "admin": return "Admin Signal";
                case "heartbeat": return "WorldMind Heartbeat";
                case "test": return "WorldMind Discord Test";
                default: return "WorldMind Event";
            }
        }

        #endregion

        #region Queue / Discord

        private bool QueueDiscordMessage(string title, string message, string category, bool force, string channelKey = "")
        {
            category = NormalizeCategory(category);
            channelKey = NormalizeChannel(string.IsNullOrWhiteSpace(channelKey) ? category : channelKey);

            if (!_config.Webhook.Enabled && !force)
            {
                SetError("Webhook disabled; message not queued.");
                return false;
            }

            if (IsBadOutput(message))
            {
                SetError("Message was empty or invalid output.");
                return false;
            }

            string webhook = GetWebhookForChannel(channelKey, category);
            if (string.IsNullOrWhiteSpace(webhook))
            {
                SetError($"No Discord webhook configured for channel/category '{channelKey}/{category}'.");
                if (_config.General.Debug) Puts(_data.LastError);
                return false;
            }

            DiscordQueueItem item = new DiscordQueueItem
            {
                Title = CleanText(title, 220),
                Message = CleanText(message, _config.Webhook.MaxMessageLength),
                Category = category,
                ChannelKey = channelKey,
                WebhookUrl = webhook,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };

            if (_queue.Count >= Math.Max(1, _config.Queue.MaxQueuedMessages))
                _queue.Dequeue();

            _queue.Enqueue(item);
            _data.QueuedCount++;
            SaveData();

            if (_config.General.Debug)
                Puts($"Queued Discord message: category={category}, channel={channelKey}, title={item.Title}, queue={_queue.Count}");

            return true;
        }

        private string GetWebhookForChannel(string channelKey, string category)
        {
            string key = NormalizeChannel(channelKey);
            string cat = NormalizeCategory(category);

            DiscordChannel channel;
            if (!string.IsNullOrWhiteSpace(key) && _config.Webhook.OptionalChannelWebhooks.TryGetValue(key, out channel) && channel != null && channel.Enabled && !string.IsNullOrWhiteSpace(channel.WebhookUrl))
                return channel.WebhookUrl;

            if (!string.IsNullOrWhiteSpace(cat) && _config.Webhook.OptionalChannelWebhooks.TryGetValue(cat, out channel) && channel != null && channel.Enabled && !string.IsNullOrWhiteSpace(channel.WebhookUrl))
                return channel.WebhookUrl;

            return _config.Webhook.WebhookUrl;
        }

        private void StartTimers()
        {
            _queueTimer?.Destroy();
            _heartbeatTimer?.Destroy();

            _queueTimer = timer.Every(Math.Max(1f, _config.Queue.QueueTickSeconds), ProcessQueue);

            if (_config.ScheduledSummaries.EnableServerHeartbeat && _config.ScheduledSummaries.ServerHeartbeatMinutes > 0)
                _heartbeatTimer = timer.Every(Math.Max(300f, _config.ScheduledSummaries.ServerHeartbeatMinutes * 60f), SendHeartbeatSummary);
        }

        private void ProcessQueue()
        {
            if (_sending || _queue.Count <= 0) return;
            if (!_config.Webhook.Enabled && !_config.General.AllowForcedTestWhenDisabled) return;

            DiscordQueueItem item = _queue.Dequeue();
            SendDiscordWebhook(item);
        }

        private void SendDiscordWebhook(DiscordQueueItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.WebhookUrl)) return;

            _sending = true;
            string payload = BuildDiscordPayload(item);

            if (_config.General.Debug)
                Puts($"Sending Discord webhook: category={item.Category}, channel={item.ChannelKey}, attempt={item.Attempts + 1}");

            webrequest.Enqueue(
                item.WebhookUrl,
                payload,
                (code, response) =>
                {
                    _sending = false;

                    if (code >= 200 && code < 300)
                    {
                        _data.SentCount++;
                        _data.LastSendUtc = DateTime.UtcNow.ToString("o");
                        _data.LastError = "";
                        AddRecentLog(item, true, code, "");

                        if (_config.General.Debug)
                            Puts($"Discord POST success: HTTP {code} category={item.Category}");
                    }
                    else
                    {
                        _data.FailedCount++;
                        _data.LastError = $"Discord HTTP {code}: {response}";
                        AddRecentLog(item, false, code, response);

                        if (_config.General.Debug)
                            PrintWarning($"Discord POST failed: HTTP {code} category={item.Category} response={CleanText(response ?? "", 500)}");

                        if (_config.Queue.RequeueOnFailure && item.Attempts + 1 < _config.Queue.MaxAttempts)
                        {
                            item.Attempts++;
                            _queue.Enqueue(item);
                        }
                    }

                    SaveData();
                },
                this,
                RequestMethod.POST,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            );
        }

        private string BuildDiscordPayload(DiscordQueueItem item)
        {
            string username = string.IsNullOrWhiteSpace(_config.Webhook.Username) ? "WorldMind" : _config.Webhook.Username;
            string avatar = _config.Webhook.AvatarUrl ?? "";

            DiscordPayload payload = new DiscordPayload
            {
                username = username,
                avatar_url = avatar,
                embeds = new List<DiscordEmbed>
                {
                    new DiscordEmbed
                    {
                        title = item.Title,
                        description = item.Message,
                        color = GetColorForCategory(item.Category),
                        footer = new DiscordFooter { text = BuildFooter(item) },
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }
            };

            return JsonConvert.SerializeObject(payload);
        }

        private string BuildFooter(DiscordQueueItem item)
        {
            string footer = string.IsNullOrWhiteSpace(_config.Webhook.FooterText) ? "WorldMind" : _config.Webhook.FooterText;
            if (!_config.Webhook.IncludeCategoryInFooter) return footer;
            return $"{footer} • {item.Category}";
        }

        private int GetColorForCategory(string category)
        {
            category = NormalizeCategory(category);

            DiscordCategorySettings categorySettings;
            if (_config.Categories.Items.TryGetValue(category, out categorySettings) && categorySettings != null && categorySettings.ColorDecimal > 0)
                return categorySettings.ColorDecimal;

            int value;
            if (_config.Webhook.CategoryColors.TryGetValue(category, out value))
                return value;

            return _config.Webhook.DefaultEmbedColor;
        }

        private void AddRecentLog(DiscordQueueItem item, bool success, int statusCode, string response)
        {
            if (!_config.Queue.KeepRecentDeliveryLog) return;

            _data.RecentDeliveryLog.Add(new DeliveryLogEntry
            {
                Title = item.Title,
                Category = item.Category,
                ChannelKey = item.ChannelKey,
                Success = success,
                StatusCode = statusCode,
                Response = CleanText(response ?? "", 500),
                TimestampUtc = DateTime.UtcNow.ToString("o")
            });

            while (_data.RecentDeliveryLog.Count > Math.Max(1, _config.Queue.RecentDeliveryLogLimit))
                _data.RecentDeliveryLog.RemoveAt(0);
        }

        #endregion

        #region WorldMind summaries

        private void GenerateWorldMindDiscordSummary(string notes, BasePlayer admin)
        {
            string prompt =
                "You are WorldMind creating a short Discord-ready update for Deviated Playgrounds, a chaotic hybrid Rust sandbox.\n" +
                "Voice: sharp, player-facing, tactical, Rust-aware, slightly sarcastic, not corporate.\n" +
                "Do not invent player counts, rules, commands, VIP details, events, rewards, or systems unless directly provided.\n" +
                "Keep it concise. No markdown tables. No backend/config/AI talk.\n" +
                $"Owner notes: {notes}\n";

            string message = AskWorldMind(prompt, "discord_summary");

            if (string.IsNullOrWhiteSpace(message))
            {
                Reply(admin, "WorldMind returned an empty summary or is not linked.");
                return;
            }

            bool queued = QueueDiscordMessage("WorldMind Server Update", message, "summary", true, "summary");
            Reply(admin, queued ? "WorldMind Discord summary queued." : "Summary generated but failed to queue.");
        }

        private void SendHeartbeatSummary()
        {
            if (!_config.Webhook.Enabled) return;

            string prompt =
                "Create one short Discord heartbeat for Deviated Playgrounds WorldMind.\n" +
                "Say WorldMind is online and watching configured systems. Do not invent activity, features, commands, events, or player counts.\n" +
                "Tone: sharp, tactical, Rust-aware. Keep it brief.\n";

            string message = AskWorldMind(prompt, "heartbeat");

            if (string.IsNullOrWhiteSpace(message))
                message = "WorldMind is online. Deviated Playgrounds telemetry is watching configured systems.";

            QueueDiscordMessage("WorldMind Heartbeat", message, "heartbeat", false, "heartbeat");
        }

        private string AskWorldMind(string prompt, string purpose)
        {
            foreach (string hook in _config.WorldMind.RequestHookNames)
            {
                if (string.IsNullOrWhiteSpace(hook)) continue;

                try
                {
                    object result = null;

                    if (WorldMindV2 != null)
                        result = WorldMindV2.Call(hook, prompt, "WorldMindDiscordMind", purpose);

                    if ((result == null || string.IsNullOrWhiteSpace(result.ToString())) && WorldMind != null)
                        result = WorldMind.Call(hook, prompt, "WorldMindDiscordMind", purpose);

                    string text = result == null ? "" : result.ToString();
                    if (!IsBadOutput(text))
                        return CleanText(text, _config.Webhook.MaxMessageLength);
                }
                catch (Exception ex)
                {
                    if (_config.General.Debug)
                        Puts($"WorldMind hook {hook} failed: {ex.Message}");
                }
            }

            return "";
        }

        #endregion

        #region Helpers

        private string GetStatusText()
        {
            return
                "WorldMindDiscordMind status\n" +
                $"Enabled: {_config.Webhook.Enabled}\n" +
                $"Default webhook configured: {HasDefaultWebhook()}\n" +
                $"Configured optional channels: {CountConfiguredChannels()}\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"WorldMind linked: {(WorldMind != null ? "yes" : "no")}\n" +
                $"Queue length: {_queue.Count}\n" +
                $"Sending: {_sending}\n" +
                $"Queued count: {_data.QueuedCount}\n" +
                $"Sent count: {_data.SentCount}\n" +
                $"Failed count: {_data.FailedCount}\n" +
                $"Last send UTC: {(string.IsNullOrWhiteSpace(_data.LastSendUtc) ? "none" : _data.LastSendUtc)}\n" +
                $"Last error: {(string.IsNullOrWhiteSpace(_data.LastError) ? "none" : _data.LastError)}";
        }

        private string GetQueueText()
        {
            return $"Queue length: {_queue.Count}\nSending: {_sending}\nQueued: {_data.QueuedCount}\nSent: {_data.SentCount}\nFailed: {_data.FailedCount}\nLast error: {(string.IsNullOrWhiteSpace(_data.LastError) ? "none" : _data.LastError)}";
        }

        private void PrintStatusToConsole()
        {
            Puts(GetStatusText().Replace("\n", " | "));
        }

        private bool HasDefaultWebhook()
        {
            return !string.IsNullOrWhiteSpace(_config.Webhook.WebhookUrl) && _config.Webhook.WebhookUrl.Contains("/api/webhooks/");
        }

        private int CountConfiguredChannels()
        {
            if (_config.Webhook.OptionalChannelWebhooks == null) return 0;
            return _config.Webhook.OptionalChannelWebhooks.Count(x => x.Value != null && x.Value.Enabled && !string.IsNullOrWhiteSpace(x.Value.WebhookUrl));
        }

        private string BuildTestMessage(string category)
        {
            return $"WorldMindDiscordMind test succeeded for `{NormalizeCategory(category)}`. Discord bridge is alive on Deviated Playgrounds.";
        }

        private bool CategoryEnabled(string category)
        {
            category = NormalizeCategory(category);
            DiscordCategorySettings settings;
            if (_config.Categories.Items.TryGetValue(category, out settings) && settings != null)
                return settings.Enabled;
            return true;
        }

        private string NormalizeCategory(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "event";
            return value.Trim().ToLowerInvariant().Replace(" ", "_");
        }

        private string NormalizeChannel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "default";
            return value.Trim().ToLowerInvariant().Replace(" ", "_");
        }

        private bool IsBadOutput(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string v = value.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "{}" || v == "[]") return true;
            return false;
        }

        private string CleanText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string clean = value
                .Replace("@everyone", "@\u200beveryone")
                .Replace("@here", "@\u200bhere")
                .Trim();

            if (maxLength > 0 && clean.Length > maxLength)
                clean = clean.Substring(0, Math.Max(0, maxLength - 3)) + "...";

            return clean;
        }

        private string GetPacketString(Dictionary<string, object> packet, string key, string fallback)
        {
            if (packet == null || string.IsNullOrWhiteSpace(key)) return fallback;

            object value;
            if (packet.TryGetValue(key, out value) && value != null)
                return value.ToString();

            return fallback;
        }

        private void SetError(string error)
        {
            _data.FailedCount++;
            _data.LastError = error;
            SaveData();
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind Discord]</color> {message}");
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

                _config.EnsureRuntimeDefaults();
                // Do not SaveConfig() here. Owner-edited config must not be rewritten or reverted on reload.
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
                if (_data == null) _data = new StoredData();
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

            [JsonProperty("WorldMind Hook Settings")]
            public WorldMindHookSettings WorldMind = new WorldMindHookSettings();

            [JsonProperty("Discord Webhook - disabled until owner configures it")]
            public WebhookSettings Webhook = new WebhookSettings();

            [JsonProperty("WorldMind Event Categories")]
            public CategoryCollection Categories = new CategoryCollection();

            [JsonProperty("Queue and Delivery")]
            public QueueSettings Queue = new QueueSettings();

            [JsonProperty("Scheduled Summaries")]
            public ScheduledSummarySettings ScheduledSummaries = new ScheduledSummarySettings();

            public static PluginConfig Default()
            {
                PluginConfig config = new PluginConfig();
                config.EnsureRuntimeDefaults();
                return config;
            }

            public void EnsureRuntimeDefaults()
            {
                if (General == null) General = new GeneralSettings();
                if (WorldMind == null) WorldMind = new WorldMindHookSettings();
                if (Webhook == null) Webhook = new WebhookSettings();
                if (Categories == null) Categories = new CategoryCollection();
                if (Queue == null) Queue = new QueueSettings();
                if (ScheduledSummaries == null) ScheduledSummaries = new ScheduledSummarySettings();

                WorldMind.EnsureDefaults();
                Webhook.EnsureDefaults();
                Categories.EnsureDefaults();
            }
        }

        private class GeneralSettings
        {
            [JsonProperty("PrintAsciiOnLoad")]
            public bool PrintAsciiOnLoad = true;

            [JsonProperty("Debug")]
            public bool Debug = true;

            [JsonProperty("AllowForcedTestWhenDisabled")]
            public bool AllowForcedTestWhenDisabled = true;
        }

        private class WorldMindHookSettings
        {
            [JsonProperty("RequestHookNames")]
            public List<string> RequestHookNames = new List<string>();

            public void EnsureDefaults()
            {
                if (RequestHookNames == null) RequestHookNames = new List<string>();
                Add("WorldMind_AskText");
                Add("WorldMindV2_AskText");
                Add("WorldMindV2_RequestLine");
                Add("WorldMind_RequestLine");
                Add("WorldMindV2_Ask");
            }

            private void Add(string value)
            {
                if (!RequestHookNames.Contains(value)) RequestHookNames.Add(value);
            }
        }

        private class WebhookSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("WebhookUrl")]
            public string WebhookUrl = "";

            [JsonProperty("Username")]
            public string Username = "WorldMind // Deviated Playgrounds";

            [JsonProperty("AvatarUrl")]
            public string AvatarUrl = "";

            [JsonProperty("FooterText")]
            public string FooterText = "WorldMind • Deviated Playgrounds";

            [JsonProperty("IncludeCategoryInFooter")]
            public bool IncludeCategoryInFooter = true;

            [JsonProperty("MaxMessageLength")]
            public int MaxMessageLength = 1800;

            [JsonProperty("DefaultEmbedColorDecimal")]
            public int DefaultEmbedColor = 14136426;

            [JsonProperty("CategoryColorsDecimal")]
            public Dictionary<string, int> CategoryColors = new Dictionary<string, int>();

            [JsonProperty("OptionalChannelWebhooks")]
            public Dictionary<string, DiscordChannel> OptionalChannelWebhooks = new Dictionary<string, DiscordChannel>();

            public void EnsureDefaults()
            {
                if (CategoryColors == null) CategoryColors = new Dictionary<string, int>();
                if (OptionalChannelWebhooks == null) OptionalChannelWebhooks = new Dictionary<string, DiscordChannel>();

                AddColor("npc", 14398378);
                AddColor("death", 10038562);
                AddColor("threat", 15105570);
                AddColor("raid", 15158332);
                AddColor("event", 3447003);
                AddColor("economy", 15844367);
                AddColor("admin", 10181046);
                AddColor("server", 9807270);
                AddColor("summary", 3066993);
                AddColor("heartbeat", 9807270);
                AddColor("test", 14136426);
                AddColor("manual", 14136426);

                AddChannel("npc");
                AddChannel("death");
                AddChannel("threat");
                AddChannel("raid");
                AddChannel("event");
                AddChannel("economy");
                AddChannel("admin");
                AddChannel("server");
                AddChannel("summary");
                AddChannel("heartbeat");
                AddChannel("test");
            }

            private void AddColor(string key, int value)
            {
                if (!CategoryColors.ContainsKey(key)) CategoryColors[key] = value;
            }

            private void AddChannel(string key)
            {
                if (!OptionalChannelWebhooks.ContainsKey(key)) OptionalChannelWebhooks[key] = new DiscordChannel();
            }
        }

        private class DiscordChannel
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("WebhookUrl")]
            public string WebhookUrl = "";
        }

        private class CategoryCollection
        {
            [JsonProperty("Items")]
            public Dictionary<string, DiscordCategorySettings> Items = new Dictionary<string, DiscordCategorySettings>();

            public void EnsureDefaults()
            {
                if (Items == null) Items = new Dictionary<string, DiscordCategorySettings>();

                Add("npc", "NPC Intelligence", true, 14398378);
                Add("death", "Death Highlight", true, 10038562);
                Add("threat", "Threat Sense", true, 15105570);
                Add("raid", "Raid Intelligence", true, 15158332);
                Add("event", "World Event", true, 3447003);
                Add("economy", "Economy Signal", true, 15844367);
                Add("admin", "Admin Signal", true, 10181046);
                Add("server", "Server Signal", true, 9807270);
                Add("summary", "WorldMind Server Update", true, 3066993);
                Add("heartbeat", "WorldMind Heartbeat", true, 9807270);
                Add("test", "WorldMind Discord Test", true, 14136426);
                Add("manual", "WorldMind Manual Message", true, 14136426);
            }

            private void Add(string key, string title, bool enabled, int color)
            {
                DiscordCategorySettings existing;
                if (!Items.TryGetValue(key, out existing) || existing == null)
                {
                    Items[key] = new DiscordCategorySettings
                    {
                        Enabled = enabled,
                        Title = title,
                        ColorDecimal = color
                    };
                    return;
                }

                if (string.IsNullOrWhiteSpace(existing.Title)) existing.Title = title;
                if (existing.ColorDecimal <= 0) existing.ColorDecimal = color;
            }
        }

        private class DiscordCategorySettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Title")]
            public string Title = "WorldMind Event";

            [JsonProperty("ColorDecimal")]
            public int ColorDecimal = 14136426;
        }

        private class QueueSettings
        {
            [JsonProperty("QueueTickSeconds")]
            public float QueueTickSeconds = 2f;

            [JsonProperty("MaxQueuedMessages")]
            public int MaxQueuedMessages = 100;

            [JsonProperty("RequeueOnFailure")]
            public bool RequeueOnFailure = true;

            [JsonProperty("MaxAttempts")]
            public int MaxAttempts = 2;

            [JsonProperty("KeepRecentDeliveryLog")]
            public bool KeepRecentDeliveryLog = true;

            [JsonProperty("RecentDeliveryLogLimit")]
            public int RecentDeliveryLogLimit = 50;
        }

        private class ScheduledSummarySettings
        {
            [JsonProperty("EnableServerHeartbeat")]
            public bool EnableServerHeartbeat = false;

            [JsonProperty("ServerHeartbeatMinutes")]
            public float ServerHeartbeatMinutes = 120f;
        }

        private class StoredData
        {
            [JsonProperty("QueuedCount")]
            public long QueuedCount = 0;

            [JsonProperty("SentCount")]
            public long SentCount = 0;

            [JsonProperty("FailedCount")]
            public long FailedCount = 0;

            [JsonProperty("LastSendUtc")]
            public string LastSendUtc = "";

            [JsonProperty("LastError")]
            public string LastError = "";

            [JsonProperty("RecentDeliveryLog")]
            public List<DeliveryLogEntry> RecentDeliveryLog = new List<DeliveryLogEntry>();

            public void EnsureDefaults()
            {
                if (RecentDeliveryLog == null) RecentDeliveryLog = new List<DeliveryLogEntry>();
            }
        }

        private class DiscordQueueItem
        {
            public string Title = "";
            public string Message = "";
            public string Category = "";
            public string ChannelKey = "";
            public string WebhookUrl = "";
            public string CreatedUtc = "";
            public int Attempts = 0;
        }

        private class DeliveryLogEntry
        {
            public string Title = "";
            public string Category = "";
            public string ChannelKey = "";
            public bool Success = false;
            public int StatusCode = 0;
            public string Response = "";
            public string TimestampUtc = "";
        }

        private class DiscordPayload
        {
            public string username;
            public string avatar_url;
            public List<DiscordEmbed> embeds;
        }

        private class DiscordEmbed
        {
            public string title;
            public string description;
            public int color;
            public DiscordFooter footer;
            public string timestamp;
        }

        private class DiscordFooter
        {
            public string text;
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindAdminMind", "Devi8d0ne", "1.0.0")]
    [Description("Admin-only activity, report, and server intelligence layer for the WorldMind plugin ecosystem.")]
    public class WorldMindAdminMind : RustPlugin
    {
        private const string PermissionAdmin = "worldmindadminmind.admin";
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
        [PluginReference] private Plugin WorldMindShopBrain;
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
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindAdminMind");
            }

            timer.Every(Math.Max(60f, _config.General.AutoSaveSeconds), SaveData);

            if (_config.ScheduledReports.EnablePeriodicAdminSummary && _config.ScheduledReports.PeriodicAdminSummaryMinutes > 0)
            {
                timer.Every(Math.Max(300f, _config.ScheduledReports.PeriodicAdminSummaryMinutes * 60f), () =>
                {
                    GenerateAdminSummary("periodic admin summary", null, false);
                });
            }

            Puts("WorldMindAdminMind loaded.");
        }

        private void Unload()
        {
            SaveData();
        }

        #endregion

        #region Passive hooks

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            TrackPlayer(player);
            AddActivity("player_connected", player.UserIDString, player.displayName, "", 1, new Dictionary<string, object>
            {
                ["address"] = player.net?.connection?.ipaddress ?? ""
            });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;

            AddActivity("player_disconnected", player.UserIDString, player.displayName, reason ?? "", 1, null);
        }

        private void OnPlayerReported(BasePlayer reporter, string targetName, string targetId, string subject, string message, string type)
        {
            string reporterId = reporter == null ? "" : reporter.UserIDString;
            string reporterName = reporter == null ? "Unknown" : reporter.displayName;

            AdminReport report = new AdminReport
            {
                ReporterId = reporterId,
                ReporterName = reporterName,
                TargetId = targetId ?? "",
                TargetName = targetName ?? "",
                Subject = subject ?? "",
                Message = message ?? "",
                Type = type ?? "",
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Status = "open"
            };

            RecordReport(report);
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return null;
            if (!_config.ChatMonitoring.TrackChatSignals) return null;

            ChatSignal signal = AnalyzeChatSignal(player, message);
            if (signal != null)
            {
                _data.ChatSignals.Add(signal);
                TrimList(_data.ChatSignals, _config.ChatMonitoring.KeepRecentChatSignals);
                AddActivity("chat_signal", player.UserIDString, player.displayName, signal.Reason, signal.Weight, null);
            }

            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            BasePlayer victim = entity as BasePlayer;
            if (victim == null || info == null) return;

            BasePlayer attacker = info.InitiatorPlayer;

            if (attacker != null && attacker != victim)
            {
                AddActivity("player_kill", attacker.UserIDString, attacker.displayName, $"Killed {victim.displayName}", 2, new Dictionary<string, object>
                {
                    ["victimId"] = victim.UserIDString,
                    ["victimName"] = victim.displayName,
                    ["weapon"] = info.WeaponPrefab == null ? "" : info.WeaponPrefab.ShortPrefabName,
                    ["distance"] = Vector3.Distance(attacker.transform.position, victim.transform.position)
                });

                AddActivity("player_death", victim.UserIDString, victim.displayName, $"Killed by {attacker.displayName}", 2, new Dictionary<string, object>
                {
                    ["attackerId"] = attacker.UserIDString,
                    ["attackerName"] = attacker.displayName
                });
            }
            else
            {
                AddActivity("player_death", victim.UserIDString, victim.displayName, "Non-player death", 1, null);
            }
        }

        #endregion

        #region Commands

        [ChatCommand("wmadmin")]
        private void CmdAdmin(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasAdmin(player)) return;

            if (args == null || args.Length == 0)
            {
                Reply(player,
                    "WorldMindAdminMind commands:\n" +
                    "/wmadmin status\n" +
                    "/wmadmin summary\n" +
                    "/wmadmin ask <admin question>\n" +
                    "/wmadmin reports\n" +
                    "/wmadmin report <targetId/name> <message>\n" +
                    "/wmadmin close <reportId> [note]\n" +
                    "/wmadmin player <steamId/name>\n" +
                    "/wmadmin activity [count]\n" +
                    "/wmadmin plugins\n" +
                    "/wmadmin reload\n" +
                    "/wmadmin save\n" +
                    "/wmadmin clear");
                return;
            }

            string sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                Reply(player, GetStatusText());
                return;
            }

            if (sub == "summary")
            {
                GenerateAdminSummary("admin requested summary", player, true);
                return;
            }

            if (sub == "ask")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmadmin ask <admin question>");
                    return;
                }

                string question = string.Join(" ", args.Skip(1).ToArray());
                AskAdminQuestion(question, player);
                return;
            }

            if (sub == "reports")
            {
                Reply(player, BuildReportsText());
                return;
            }

            if (sub == "report")
            {
                if (args.Length < 3)
                {
                    Reply(player, "Usage: /wmadmin report <targetId/name> <message>");
                    return;
                }

                string target = args[1];
                string message = string.Join(" ", args.Skip(2).ToArray());

                AdminReport report = new AdminReport
                {
                    ReporterId = player.UserIDString,
                    ReporterName = player.displayName,
                    TargetId = target,
                    TargetName = target,
                    Subject = "Manual admin note",
                    Message = message,
                    Type = "admin_note",
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    Status = "open"
                };

                RecordReport(report);
                Reply(player, $"Report/note recorded: {report.ReportId}");
                return;
            }

            if (sub == "close")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmadmin close <reportId> [note]");
                    return;
                }

                string reportId = args[1];
                string note = args.Length > 2 ? string.Join(" ", args.Skip(2).ToArray()) : "";
                CloseReport(reportId, player.displayName, note);
                Reply(player, $"Closed report if found: {reportId}");
                return;
            }

            if (sub == "player")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmadmin player <steamId/name>");
                    return;
                }

                Reply(player, BuildPlayerAdminText(args[1]));
                return;
            }

            if (sub == "activity")
            {
                int count = 15;
                if (args.Length >= 2) int.TryParse(args[1], out count);
                Reply(player, BuildRecentActivityText(Mathf.Clamp(count, 1, 50)));
                return;
            }

            if (sub == "plugins")
            {
                Reply(player, BuildPluginStatusText());
                return;
            }

            if (sub == "reload")
            {
                LoadPluginConfig();
                LoadData();
                Reply(player, "WorldMindAdminMind reloaded.");
                return;
            }

            if (sub == "save")
            {
                SaveData();
                Reply(player, "WorldMindAdminMind data saved.");
                return;
            }

            if (sub == "clear")
            {
                _data = new StoredData();
                SaveData();
                Reply(player, "WorldMindAdminMind data cleared.");
                return;
            }

            Reply(player, "Unknown command. Use /wmadmin for help.");
        }

        [ConsoleCommand("worldmindadmin.status")]
        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg?.ReplyWith(GetStatusText());
        }

        [ConsoleCommand("worldmindadmin.summary")]
        private void ConsoleSummary(ConsoleSystem.Arg arg)
        {
            string summary = BuildLocalAdminSummary();
            arg?.ReplyWith(summary);
        }

        [ConsoleCommand("worldmindadmin.reload")]
        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadPluginConfig();
            LoadData();
            arg?.ReplyWith("WorldMindAdminMind reloaded.");
        }

        #endregion

        #region Public hooks

        private object WorldMindAdminMind_RecordReport(Dictionary<string, object> packet)
        {
            if (packet == null) return false;

            AdminReport report = new AdminReport
            {
                ReporterId = GetString(packet, "reporterId", ""),
                ReporterName = GetString(packet, "reporterName", ""),
                TargetId = GetString(packet, "targetId", ""),
                TargetName = GetString(packet, "targetName", ""),
                Subject = GetString(packet, "subject", ""),
                Message = GetString(packet, "message", ""),
                Type = GetString(packet, "type", "external"),
                TimestampUtc = GetString(packet, "timestampUtc", DateTime.UtcNow.ToString("o")),
                Status = "open"
            };

            RecordReport(report);
            return report.ReportId;
        }

        private object WorldMindAdminMind_RecordActivity(Dictionary<string, object> packet)
        {
            if (packet == null) return false;

            AddActivity(
                GetString(packet, "activityType", "external"),
                GetString(packet, "playerId", ""),
                GetString(packet, "playerName", ""),
                GetString(packet, "summary", ""),
                GetInt(packet, "weight", 1),
                packet
            );

            return true;
        }

        private object WorldMindAdminMind_GetAdminSummary()
        {
            return BuildAdminPacket();
        }

        private object WorldMindAdminMind_GetAdminSummaryText()
        {
            return BuildLocalAdminSummary();
        }

        private object WorldMindAdminMind_GetOpenReports()
        {
            return _data.Reports.Where(x => x.Status == "open").ToList();
        }

        private object WorldMindAdminMind_GetPlayerAdminProfile(string query)
        {
            return GetTrackedPlayer(query);
        }

        #endregion

        #region Core tracking

        private void TrackPlayer(BasePlayer player)
        {
            if (player == null) return;

            TrackedPlayer tracked;
            if (!_data.Players.TryGetValue(player.UserIDString, out tracked))
            {
                tracked = new TrackedPlayer
                {
                    PlayerId = player.UserIDString,
                    PlayerName = player.displayName,
                    FirstSeenUtc = DateTime.UtcNow.ToString("o")
                };
                _data.Players[player.UserIDString] = tracked;
            }

            tracked.PlayerName = player.displayName;
            tracked.LastSeenUtc = DateTime.UtcNow.ToString("o");
            tracked.ConnectionCount++;
        }

        private void AddActivity(string type, string playerId, string playerName, string summary, int weight, Dictionary<string, object> extra)
        {
            if (string.IsNullOrWhiteSpace(type)) type = "unknown";

            AdminActivity activity = new AdminActivity
            {
                ActivityType = type,
                PlayerId = playerId ?? "",
                PlayerName = playerName ?? "",
                Summary = summary ?? "",
                Weight = weight,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                ExtraJson = extra == null ? "" : JsonConvert.SerializeObject(extra)
            };

            _data.RecentActivity.Add(activity);
            TrimList(_data.RecentActivity, _config.Reporting.KeepRecentActivity);

            if (!string.IsNullOrWhiteSpace(playerId))
            {
                TrackedPlayer tracked;
                if (!_data.Players.TryGetValue(playerId, out tracked))
                {
                    tracked = new TrackedPlayer
                    {
                        PlayerId = playerId,
                        PlayerName = playerName ?? "",
                        FirstSeenUtc = DateTime.UtcNow.ToString("o")
                    };
                    _data.Players[playerId] = tracked;
                }

                if (!string.IsNullOrWhiteSpace(playerName))
                    tracked.PlayerName = playerName;

                tracked.LastSeenUtc = DateTime.UtcNow.ToString("o");
                tracked.ActivityScore += Math.Max(0, weight);

                int current;
                tracked.ActivityCounts.TryGetValue(type, out current);
                tracked.ActivityCounts[type] = current + 1;
            }

            RecordWorldMindEvent("admin_activity", activity);
        }

        private void RecordReport(AdminReport report)
        {
            if (report == null) return;

            report.ReportId = string.IsNullOrWhiteSpace(report.ReportId)
                ? $"R-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{UnityEngine.Random.Range(1000, 9999)}"
                : report.ReportId;

            if (string.IsNullOrWhiteSpace(report.TimestampUtc))
                report.TimestampUtc = DateTime.UtcNow.ToString("o");

            if (string.IsNullOrWhiteSpace(report.Status))
                report.Status = "open";

            _data.Reports.Add(report);
            TrimList(_data.Reports, _config.Reporting.KeepRecentReports);

            AddActivity("admin_report", report.TargetId, report.TargetName, report.Message, _config.Reporting.ReportActivityWeight, new Dictionary<string, object>
            {
                ["reportId"] = report.ReportId,
                ["reporterId"] = report.ReporterId,
                ["reporterName"] = report.ReporterName,
                ["subject"] = report.Subject,
                ["type"] = report.Type
            });

            RecordWorldMindEvent("admin_report", report);
            SaveData();
        }

        private void CloseReport(string reportId, string closedBy, string note)
        {
            if (string.IsNullOrWhiteSpace(reportId)) return;

            AdminReport report = _data.Reports.FirstOrDefault(x => string.Equals(x.ReportId, reportId, StringComparison.OrdinalIgnoreCase));
            if (report == null) return;

            report.Status = "closed";
            report.ClosedBy = closedBy ?? "";
            report.ClosedNote = note ?? "";
            report.ClosedUtc = DateTime.UtcNow.ToString("o");

            RecordWorldMindEvent("admin_report_closed", report);
            SaveData();
        }

        private ChatSignal AnalyzeChatSignal(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return null;

            string lower = message.ToLowerInvariant();

            foreach (string term in _config.ChatMonitoring.FlaggedTerms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;

                if (lower.Contains(term.ToLowerInvariant()))
                {
                    return new ChatSignal
                    {
                        PlayerId = player.UserIDString,
                        PlayerName = player.displayName,
                        MessagePreview = CleanText(message, _config.ChatMonitoring.MaxStoredChatPreviewLength),
                        Reason = $"Matched configured term: {term}",
                        Weight = _config.ChatMonitoring.FlaggedTermWeight,
                        TimestampUtc = DateTime.UtcNow.ToString("o")
                    };
                }
            }

            return null;
        }

        #endregion

        #region WorldMind

        private void GenerateAdminSummary(string reason, BasePlayer admin, bool reply)
        {
            string local = BuildLocalAdminSummary();

            if (WorldMindV2 == null)
            {
                if (reply && admin != null) Reply(admin, local);
                return;
            }

            string prompt =
                "You are WorldMind creating an admin-only Rust server operations summary.\n" +
                "Important: never recommend auto-ban, auto-kick, auto-punish, or irreversible moderation. Only summarize and suggest what admins may review manually.\n" +
                "Do not assume VIP, Discord, WarMode, kits, homes, teleport, factions, custom economy, or server-specific systems unless present in the data.\n" +
                "Be concise. Focus on reports, repeated patterns, suspicious clusters, and server health signals. State uncertainty clearly.\n" +
                $"Reason: {reason}\n" +
                $"Admin data JSON:\n{JsonConvert.SerializeObject(BuildAdminPacket(), Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindAdminMind", "admin_summary");
                string message = result == null ? "" : result.ToString();

                if (string.IsNullOrWhiteSpace(message))
                    message = local;

                _data.LastWorldMindSummary = message;
                _data.LastWorldMindSummaryUtc = DateTime.UtcNow.ToString("o");
                SaveData();

                if (reply && admin != null)
                    Reply(admin, message);

                if (_config.Discord.SendAdminSummariesToDiscord && WorldMindDiscordMind != null)
                    WorldMindDiscordMind.Call("WorldMindDiscordMind_SendAdminSummary", message);

                RecordWorldMindEvent("admin_summary_generated", new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["summary"] = message
                });
            }
            catch (Exception ex)
            {
                if (_config.General.Debug)
                    Puts($"WorldMind admin summary failed: {ex.Message}");

                if (reply && admin != null)
                    Reply(admin, local);
            }
        }

        private void AskAdminQuestion(string question, BasePlayer admin)
        {
            if (WorldMindV2 == null)
            {
                Reply(admin, "WorldMindV2 is not loaded.");
                return;
            }

            string prompt =
                "You are WorldMind answering an admin-only question about generic Rust server activity.\n" +
                "Never recommend auto-ban, auto-kick, auto-punish, or irreversible moderation. Only suggest manual review steps.\n" +
                "Do not invent data. If the available admin packet is insufficient, say so.\n" +
                $"Admin question: {question}\n" +
                $"Admin data JSON:\n{JsonConvert.SerializeObject(BuildAdminPacket(), Formatting.Indented)}";

            try
            {
                object result = WorldMindV2.Call("WorldMind_AskText", prompt, "WorldMindAdminMind", "admin_question");
                string message = result == null ? "" : result.ToString();
                Reply(admin, string.IsNullOrWhiteSpace(message) ? "WorldMind returned no answer." : message);
            }
            catch (Exception ex)
            {
                Reply(admin, $"WorldMind admin question failed: {ex.Message}");
            }
        }

        private void RecordWorldMindEvent(string eventType, object payload)
        {
            if (!_config.WorldMindIntegration.RecordEventsToWorldMind || WorldMindV2 == null) return;

            try
            {
                Dictionary<string, object> packet = new Dictionary<string, object>
                {
                    ["plugin"] = "WorldMindAdminMind",
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

        private AdminSummaryPacket BuildAdminPacket()
        {
            List<AdminReport> openReports = _data.Reports.Where(x => x.Status == "open").ToList();

            List<TrackedPlayer> topActivityPlayers = _data.Players.Values
                .OrderByDescending(x => x.ActivityScore)
                .Take(_config.Reporting.TopPlayerLimit)
                .ToList();

            List<AdminActivity> recentActivity = _data.RecentActivity
                .OrderByDescending(x => x.TimestampUtc)
                .Take(_config.Reporting.SummaryActivityLimit)
                .ToList();

            return new AdminSummaryPacket
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                WorldMindLinked = WorldMindV2 != null,
                PlayerBrainLinked = WorldMindPlayerBrain != null,
                ShopBrainLinked = WorldMindShopBrain != null,
                DiscordMindLinked = WorldMindDiscordMind != null,
                TrackedPlayers = _data.Players.Count,
                OpenReportCount = openReports.Count,
                TotalReportCount = _data.Reports.Count,
                RecentActivityCount = _data.RecentActivity.Count,
                ChatSignalCount = _data.ChatSignals.Count,
                OpenReports = openReports.Take(_config.Reporting.SummaryReportLimit).ToList(),
                TopActivityPlayers = topActivityPlayers,
                RecentActivity = recentActivity,
                RecentChatSignals = _data.ChatSignals.OrderByDescending(x => x.TimestampUtc).Take(_config.Reporting.SummaryChatSignalLimit).ToList()
            };
        }

        private string BuildLocalAdminSummary()
        {
            AdminSummaryPacket packet = BuildAdminPacket();

            List<string> lines = new List<string>
            {
                "WorldMindAdminMind summary",
                $"Tracked players: {packet.TrackedPlayers}",
                $"Open reports: {packet.OpenReportCount}",
                $"Recent activities: {packet.RecentActivityCount}",
                $"Chat signals: {packet.ChatSignalCount}",
                "",
                "Linked plugins:",
                $"- WorldMindV2: {(packet.WorldMindLinked ? "yes" : "no")}",
                $"- PlayerBrain: {(packet.PlayerBrainLinked ? "yes" : "no")}",
                $"- ShopBrain: {(packet.ShopBrainLinked ? "yes" : "no")}",
                $"- DiscordMind: {(packet.DiscordMindLinked ? "yes" : "no")}",
                "",
                "Open reports:"
            };

            if (packet.OpenReports.Count == 0)
            {
                lines.Add("- none");
            }
            else
            {
                foreach (AdminReport report in packet.OpenReports)
                    lines.Add($"- {report.ReportId} | target={report.TargetName} | {report.Subject}: {report.Message}");
            }

            lines.Add("");
            lines.Add("Top activity players:");

            if (packet.TopActivityPlayers.Count == 0)
            {
                lines.Add("- none");
            }
            else
            {
                foreach (TrackedPlayer p in packet.TopActivityPlayers)
                    lines.Add($"- {p.PlayerName} ({p.PlayerId}) | score={p.ActivityScore} | last={p.LastSeenUtc}");
            }

            if (!string.IsNullOrWhiteSpace(_data.LastWorldMindSummary))
            {
                lines.Add("");
                lines.Add("Last WorldMind summary:");
                lines.Add(_data.LastWorldMindSummary);
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildReportsText()
        {
            List<AdminReport> reports = _data.Reports
                .OrderByDescending(x => x.TimestampUtc)
                .Take(_config.Reporting.ReportListLimit)
                .ToList();

            if (reports.Count == 0)
                return "No reports recorded.";

            List<string> lines = new List<string> { "Recent reports:" };

            foreach (AdminReport report in reports)
            {
                lines.Add($"- {report.ReportId} | {report.Status} | target={report.TargetName} | reporter={report.ReporterName} | {report.Message}");
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildPlayerAdminText(string query)
        {
            TrackedPlayer tracked = GetTrackedPlayer(query);

            if (tracked == null)
                return $"No tracked player found for: {query}";

            List<string> lines = new List<string>
            {
                $"Admin profile: {tracked.PlayerName} ({tracked.PlayerId})",
                $"First seen: {tracked.FirstSeenUtc}",
                $"Last seen: {tracked.LastSeenUtc}",
                $"Connections: {tracked.ConnectionCount}",
                $"Activity score: {tracked.ActivityScore}",
                "Activity counts:"
            };

            foreach (KeyValuePair<string, int> kvp in tracked.ActivityCounts.OrderByDescending(x => x.Value).Take(12))
                lines.Add($"- {kvp.Key}: {kvp.Value}");

            List<AdminReport> reports = _data.Reports
                .Where(x => string.Equals(x.TargetId, tracked.PlayerId, StringComparison.OrdinalIgnoreCase) || string.Equals(x.TargetName, tracked.PlayerName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.TimestampUtc)
                .Take(5)
                .ToList();

            lines.Add("Recent reports:");
            if (reports.Count == 0)
                lines.Add("- none");
            else
                foreach (AdminReport report in reports)
                    lines.Add($"- {report.ReportId} | {report.Status} | {report.Message}");

            return string.Join("\n", lines.ToArray());
        }

        private string BuildRecentActivityText(int count)
        {
            List<AdminActivity> activities = _data.RecentActivity
                .OrderByDescending(x => x.TimestampUtc)
                .Take(count)
                .ToList();

            if (activities.Count == 0)
                return "No recent activity recorded.";

            List<string> lines = new List<string> { "Recent admin activity:" };

            foreach (AdminActivity activity in activities)
                lines.Add($"- {activity.TimestampUtc} | {activity.ActivityType} | {activity.PlayerName} | {activity.Summary}");

            return string.Join("\n", lines.ToArray());
        }

        private string BuildPluginStatusText()
        {
            return
                "WorldMind plugin links\n" +
                $"- WorldMindV2: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"- WorldMindPlayerBrain: {(WorldMindPlayerBrain != null ? "yes" : "no")}\n" +
                $"- WorldMindShopBrain: {(WorldMindShopBrain != null ? "yes" : "no")}\n" +
                $"- WorldMindDiscordMind: {(WorldMindDiscordMind != null ? "yes" : "no")}";
        }

        private string GetStatusText()
        {
            return
                "WorldMindAdminMind status\n" +
                $"WorldMindV2 linked: {(WorldMindV2 != null ? "yes" : "no")}\n" +
                $"Tracked players: {_data.Players.Count}\n" +
                $"Reports: {_data.Reports.Count}\n" +
                $"Open reports: {_data.Reports.Count(x => x.Status == "open")}\n" +
                $"Recent activity: {_data.RecentActivity.Count}\n" +
                $"Chat signals: {_data.ChatSignals.Count}\n" +
                $"Last WorldMind summary UTC: {(string.IsNullOrWhiteSpace(_data.LastWorldMindSummaryUtc) ? "none" : _data.LastWorldMindSummaryUtc)}";
        }

        private TrackedPlayer GetTrackedPlayer(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            TrackedPlayer direct;
            if (_data.Players.TryGetValue(query, out direct))
                return direct;

            string lower = query.ToLowerInvariant();

            return _data.Players.Values.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(x.PlayerName) && x.PlayerName.ToLowerInvariant().Contains(lower)) ||
                (!string.IsNullOrWhiteSpace(x.PlayerId) && x.PlayerId.Contains(query)));
        }

        #endregion

        #region Helpers

        private string GetString(Dictionary<string, object> packet, string key, string fallback)
        {
            if (packet == null) return fallback;
            object value;
            return packet.TryGetValue(key, out value) && value != null ? value.ToString() : fallback;
        }

        private int GetInt(Dictionary<string, object> packet, string key, int fallback)
        {
            if (packet == null) return fallback;
            object value;
            if (!packet.TryGetValue(key, out value) || value == null) return fallback;

            int parsed;
            return int.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        private void TrimList<T>(List<T> list, int max)
        {
            if (list == null) return;
            max = Math.Max(1, max);

            while (list.Count > max)
                list.RemoveAt(0);
        }

        private string CleanText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string clean = value
                .Replace("@everyone", "@\u200beveryone")
                .Replace("@here", "@\u200bhere")
                .Trim();

            if (maxLength > 0 && clean.Length > maxLength)
                clean = clean.Substring(0, maxLength - 3) + "...";

            return clean;
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return;
            player.ChatMessage($"<color=#d7b46a>[WorldMind AdminMind]</color> {message}");
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

            [JsonProperty("Chat Monitoring - generic signal tracking only")]
            public ChatMonitoringSettings ChatMonitoring = new ChatMonitoringSettings();

            [JsonProperty("Scheduled Reports")]
            public ScheduledReportSettings ScheduledReports = new ScheduledReportSettings();

            [JsonProperty("Discord Integration")]
            public DiscordSettings Discord = new DiscordSettings();

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
                if (ChatMonitoring == null) ChatMonitoring = new ChatMonitoringSettings();
                if (ScheduledReports == null) ScheduledReports = new ScheduledReportSettings();
                if (Discord == null) Discord = new DiscordSettings();
                ChatMonitoring.EnsureDefaults();
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

        private class WorldMindIntegrationSettings
        {
            [JsonProperty("RecordEventsToWorldMind")]
            public bool RecordEventsToWorldMind = true;
        }

        private class ReportingSettings
        {
            [JsonProperty("KeepRecentActivity")]
            public int KeepRecentActivity = 250;

            [JsonProperty("KeepRecentReports")]
            public int KeepRecentReports = 150;

            [JsonProperty("ReportListLimit")]
            public int ReportListLimit = 15;

            [JsonProperty("SummaryActivityLimit")]
            public int SummaryActivityLimit = 25;

            [JsonProperty("SummaryReportLimit")]
            public int SummaryReportLimit = 10;

            [JsonProperty("SummaryChatSignalLimit")]
            public int SummaryChatSignalLimit = 10;

            [JsonProperty("TopPlayerLimit")]
            public int TopPlayerLimit = 10;

            [JsonProperty("ReportActivityWeight")]
            public int ReportActivityWeight = 5;
        }

        private class ChatMonitoringSettings
        {
            [JsonProperty("TrackChatSignals")]
            public bool TrackChatSignals = false;

            [JsonProperty("FlaggedTerms")]
            public List<string> FlaggedTerms = new List<string>();

            [JsonProperty("FlaggedTermWeight")]
            public int FlaggedTermWeight = 3;

            [JsonProperty("KeepRecentChatSignals")]
            public int KeepRecentChatSignals = 100;

            [JsonProperty("MaxStoredChatPreviewLength")]
            public int MaxStoredChatPreviewLength = 180;

            public void EnsureDefaults()
            {
                if (FlaggedTerms == null) FlaggedTerms = new List<string>();

                if (FlaggedTerms.Count == 0)
                {
                    FlaggedTerms.Add("cheat");
                    FlaggedTerms.Add("hacker");
                    FlaggedTerms.Add("esp");
                    FlaggedTerms.Add("aimbot");
                    FlaggedTerms.Add("report");
                }
            }
        }

        private class ScheduledReportSettings
        {
            [JsonProperty("EnablePeriodicAdminSummary")]
            public bool EnablePeriodicAdminSummary = false;

            [JsonProperty("PeriodicAdminSummaryMinutes")]
            public float PeriodicAdminSummaryMinutes = 120f;
        }

        private class DiscordSettings
        {
            [JsonProperty("SendAdminSummariesToDiscord")]
            public bool SendAdminSummariesToDiscord = false;
        }

        private class StoredData
        {
            [JsonProperty("Players")]
            public Dictionary<string, TrackedPlayer> Players = new Dictionary<string, TrackedPlayer>();

            [JsonProperty("Reports")]
            public List<AdminReport> Reports = new List<AdminReport>();

            [JsonProperty("RecentActivity")]
            public List<AdminActivity> RecentActivity = new List<AdminActivity>();

            [JsonProperty("ChatSignals")]
            public List<ChatSignal> ChatSignals = new List<ChatSignal>();

            [JsonProperty("LastWorldMindSummaryUtc")]
            public string LastWorldMindSummaryUtc = "";

            [JsonProperty("LastWorldMindSummary")]
            public string LastWorldMindSummary = "";

            public void EnsureDefaults()
            {
                if (Players == null) Players = new Dictionary<string, TrackedPlayer>();
                if (Reports == null) Reports = new List<AdminReport>();
                if (RecentActivity == null) RecentActivity = new List<AdminActivity>();
                if (ChatSignals == null) ChatSignals = new List<ChatSignal>();
            }
        }

        public class TrackedPlayer
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string FirstSeenUtc = "";
            public string LastSeenUtc = "";
            public int ConnectionCount = 0;
            public int ActivityScore = 0;
            public Dictionary<string, int> ActivityCounts = new Dictionary<string, int>();
        }

        public class AdminReport
        {
            public string ReportId = "";
            public string ReporterId = "";
            public string ReporterName = "";
            public string TargetId = "";
            public string TargetName = "";
            public string Subject = "";
            public string Message = "";
            public string Type = "";
            public string Status = "open";
            public string TimestampUtc = "";
            public string ClosedUtc = "";
            public string ClosedBy = "";
            public string ClosedNote = "";
        }

        public class AdminActivity
        {
            public string ActivityType = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public string Summary = "";
            public int Weight = 1;
            public string TimestampUtc = "";
            public string ExtraJson = "";
        }

        public class ChatSignal
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string MessagePreview = "";
            public string Reason = "";
            public int Weight = 1;
            public string TimestampUtc = "";
        }

        public class AdminSummaryPacket
        {
            public string TimestampUtc = "";
            public bool WorldMindLinked;
            public bool PlayerBrainLinked;
            public bool ShopBrainLinked;
            public bool DiscordMindLinked;
            public int TrackedPlayers;
            public int OpenReportCount;
            public int TotalReportCount;
            public int RecentActivityCount;
            public int ChatSignalCount;
            public List<AdminReport> OpenReports = new List<AdminReport>();
            public List<TrackedPlayer> TopActivityPlayers = new List<TrackedPlayer>();
            public List<AdminActivity> RecentActivity = new List<AdminActivity>();
            public List<ChatSignal> RecentChatSignals = new List<ChatSignal>();
        }

        #endregion
    }
}

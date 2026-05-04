using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindMapBrain", "Devi8d0ne", "1.0.1")]
    [Description("Generic map/grid/location intelligence provider for WorldMindV2.")]
    public class WorldMindMapBrain : RustPlugin
    {
        private const string AdminPerm = "worldmindmapbrain.admin";
        private const string UsePerm = "worldmindmapbrain.use";
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
        private readonly Dictionary<string, string> _namedZones = new Dictionary<string, string>();

        private class PluginConfig
        {
            [JsonProperty("Console Branding")]
            public BrandingConfig Branding = new BrandingConfig();

            [JsonProperty("General Settings")]
            public GeneralConfig General = new GeneralConfig();

            [JsonProperty("Grid Settings")]
            public GridConfig Grid = new GridConfig();

            [JsonProperty("Named Zones - optional owner-defined areas")]
            public List<NamedZoneConfig> NamedZones = new List<NamedZoneConfig>
            {
                new NamedZoneConfig
                {
                    Name = "Example Zone",
                    Enabled = false,
                    CenterX = 0f,
                    CenterZ = 0f,
                    RadiusMeters = 75f,
                    Description = "Optional owner-defined area. Disable, edit, or remove this example."
                }
            };

            [JsonProperty("WorldMind Integration")]
            public WorldMindConfig WorldMind = new WorldMindConfig();

            [JsonProperty("Chat Output")]
            public ChatConfig Chat = new ChatConfig();
        }

        private class BrandingConfig
        {
            public bool PrintAsciiOnLoad = true;
            public bool PrintMadeWithLoveOnLoad = true;
        }

        private class GeneralConfig
        {
            public bool RequirePermissionForPlayerCommands = false;
            public bool AllowPlayersToUseWhereAmI = true;
            public bool RecordMapLookupsToWorldMind = true;
            public bool UseWorldMindForDescriptions = true;
            public int CommandCooldownSeconds = 10;
        }

        private class GridConfig
        {
            public float CellSizeMeters = 150f;
            public bool IncludeCoordinatesInAdminOutput = true;
            public bool IncludeApproximateBiome = false;
            public bool IncludeNearestNamedZone = true;
            public bool UseSimpleGridMath = true;
        }

        private class NamedZoneConfig
        {
            public string Name = "";
            public bool Enabled = false;
            public float CenterX = 0f;
            public float CenterZ = 0f;
            public float RadiusMeters = 75f;
            public string Description = "";
        }

        private class WorldMindConfig
        {
            public int MaxResponseCharacters = 180;
            public string SystemInstruction = "Describe this Rust map location briefly and generically. Do not mention server-specific commands, VIP, Discord, WarMode, custom economy, or custom plugins unless provided as facts by WorldMindV2.";
        }

        private class ChatConfig
        {
            public string Prefix = "<color=#c2a36b>[WorldMind Map]</color>";
            public string NoPermission = "You do not have permission to use this command.";
            public string Cooldown = "MapBrain is cooling down. Try again shortly.";
        }

        private readonly Dictionary<ulong, double> _cooldowns = new Dictionary<ulong, double>();

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config was null");
            }
            catch
            {
                PrintWarning("Config read failed; creating a new default config.");
                LoadDefaultConfig();
                return;
            }

            MergeMissingConfigFields();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void MergeMissingConfigFields()
        {
            var changed = false;

            if (_config.Branding == null) { _config.Branding = new BrandingConfig(); changed = true; }
            if (_config.General == null) { _config.General = new GeneralConfig(); changed = true; }
            if (_config.Grid == null) { _config.Grid = new GridConfig(); changed = true; }
            if (_config.NamedZones == null) { _config.NamedZones = new List<NamedZoneConfig>(); changed = true; }
            if (_config.WorldMind == null) { _config.WorldMind = new WorldMindConfig(); changed = true; }
            if (_config.Chat == null) { _config.Chat = new ChatConfig(); changed = true; }

            if (changed)
                PrintWarning("Config was missing fields; merged defaults without resetting owner values.");
        }

        private void Init()
        {
            permission.RegisterPermission(AdminPerm, this);
            permission.RegisterPermission(UsePerm, this);
            cmd.AddChatCommand("wmmap", this, nameof(CmdMap));
            cmd.AddConsoleCommand("worldmindmap.status", this, nameof(ConsoleStatus));
            cmd.AddConsoleCommand("worldmindmap.reload", this, nameof(ConsoleReload));
        }

        private void OnServerInitialized()
        {
            RebuildNamedZoneCache();

            if (_config.Branding.PrintAsciiOnLoad)
                Puts(Dv8dAscii);

            if (_config.Branding.PrintMadeWithLoveOnLoad)
                Puts($"{MadeWithLoveTag} | Author: Devi8d0ne | Plugin: WorldMindMapBrain");
        }

        private void RebuildNamedZoneCache()
        {
            _namedZones.Clear();

            foreach (var zone in _config.NamedZones)
            {
                if (zone == null || !zone.Enabled || string.IsNullOrWhiteSpace(zone.Name))
                    continue;

                _namedZones[zone.Name] = zone.Description ?? string.Empty;
            }
        }

        private bool HasAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPerm));
        }

        private bool HasUse(BasePlayer player)
        {
            if (player == null)
                return false;

            if (!_config.General.RequirePermissionForPlayerCommands)
                return true;

            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, UsePerm) || permission.UserHasPermission(player.UserIDString, AdminPerm);
        }

        private void CmdMap(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (args == null || args.Length == 0)
            {
                Reply(player, "Usage: /wmmap whereami | describe | status | reload | test");
                return;
            }

            var sub = args[0].ToLowerInvariant();

            if (sub == "status")
            {
                if (!HasAdmin(player)) { Reply(player, _config.Chat.NoPermission); return; }
                Reply(player, BuildStatus());
                return;
            }

            if (sub == "reload")
            {
                if (!HasAdmin(player)) { Reply(player, _config.Chat.NoPermission); return; }
                LoadConfig();
                RebuildNamedZoneCache();
                Reply(player, "WorldMindMapBrain config reloaded.");
                return;
            }

            if (sub == "test")
            {
                if (!HasAdmin(player)) { Reply(player, _config.Chat.NoPermission); return; }
                Reply(player, DescribePosition(player.transform.position, player.displayName, true));
                return;
            }

            if (sub == "whereami" || sub == "where" || sub == "loc")
            {
                if (!_config.General.AllowPlayersToUseWhereAmI || !HasUse(player)) { Reply(player, _config.Chat.NoPermission); return; }
                if (!CheckCooldown(player)) return;
                Reply(player, DescribePosition(player.transform.position, player.displayName, false));
                return;
            }

            if (sub == "describe")
            {
                if (!HasUse(player)) { Reply(player, _config.Chat.NoPermission); return; }
                if (!CheckCooldown(player)) return;
                Reply(player, DescribePosition(player.transform.position, player.displayName, true));
                return;
            }

            Reply(player, "Unknown subcommand. Usage: /wmmap whereami | describe | status | reload | test");
        }

        private void ConsoleStatus(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith(BuildStatus());
        }

        private void ConsoleReload(ConsoleSystem.Arg arg)
        {
            LoadConfig();
            RebuildNamedZoneCache();
            arg.ReplyWith("WorldMindMapBrain config reloaded.");
        }

        private string BuildStatus()
        {
            return $"WorldMindMapBrain v1.0.0 | WorldMindV2: {(WorldMindV2 == null ? "not found" : "found")} | World size: {GetWorldSize():0} | Cell size: {_config.Grid.CellSizeMeters:0}m | Named zones enabled: {_namedZones.Count}";
        }

        private bool CheckCooldown(BasePlayer player)
        {
            if (_config.General.CommandCooldownSeconds <= 0 || HasAdmin(player))
                return true;

            var now = Interface.Oxide.Now;
            double next;
            if (_cooldowns.TryGetValue(player.userID, out next) && now < next)
            {
                Reply(player, _config.Chat.Cooldown);
                return false;
            }

            _cooldowns[player.userID] = now + _config.General.CommandCooldownSeconds;
            return true;
        }

        private void Reply(BasePlayer player, string message)
        {
            player.ChatMessage($"{_config.Chat.Prefix} {message}");
        }

        private float GetWorldSize()
        {
            try
            {
                if (TerrainMeta.Size.x > 0f)
                    return TerrainMeta.Size.x;
            }
            catch { }

            try
            {
                return ConVar.Server.worldsize;
            }
            catch { }

            return 4500f;
        }

        private string GetGrid(Vector3 position)
        {
            var worldSize = GetWorldSize();
            var cellSize = Math.Max(50f, _config.Grid.CellSizeMeters);
            var half = worldSize / 2f;

            var x = Mathf.Clamp(position.x + half, 0f, worldSize - 1f);
            var z = Mathf.Clamp(half - position.z, 0f, worldSize - 1f);

            var col = Mathf.FloorToInt(x / cellSize);
            var row = Mathf.FloorToInt(z / cellSize) + 1;

            return ColumnName(col) + row;
        }

        private string ColumnName(int index)
        {
            index = Math.Max(0, index);
            var result = string.Empty;

            do
            {
                var rem = index % 26;
                result = (char)('A' + rem) + result;
                index = (index / 26) - 1;
            } while (index >= 0);

            return result;
        }

        private string GetNearestNamedZone(Vector3 position)
        {
            if (!_config.Grid.IncludeNearestNamedZone || _config.NamedZones == null)
                return string.Empty;

            NamedZoneConfig best = null;
            var bestDistance = float.MaxValue;

            foreach (var zone in _config.NamedZones)
            {
                if (zone == null || !zone.Enabled || string.IsNullOrWhiteSpace(zone.Name) || zone.RadiusMeters <= 0f)
                    continue;

                var dist = Vector2.Distance(new Vector2(position.x, position.z), new Vector2(zone.CenterX, zone.CenterZ));
                if (dist <= zone.RadiusMeters && dist < bestDistance)
                {
                    best = zone;
                    bestDistance = dist;
                }
            }

            if (best == null)
                return string.Empty;

            return string.IsNullOrWhiteSpace(best.Description) ? best.Name : $"{best.Name}: {best.Description}";
        }

        private string GetApproximateBiome(Vector3 position)
        {
            // BiomeMap API signatures differ across Rust/uMod builds.
            // Keep this safe and compile-stable by defaulting to unknown unless
            // a later build adds a verified adapter for the target server version.
            if (!_config.Grid.IncludeApproximateBiome)
                return "unknown";

            return "unknown";
        }

        private string BuildRawLocationSummary(Vector3 position)
        {
            var grid = GetGrid(position);
            var biome = GetApproximateBiome(position);
            var zone = GetNearestNamedZone(position);
            var coord = _config.Grid.IncludeCoordinatesInAdminOutput ? $" coords ({position.x:0}, {position.y:0}, {position.z:0})" : string.Empty;

            var summary = $"Grid {grid}{coord}";

            if (!string.IsNullOrWhiteSpace(biome) && biome != "unknown")
                summary += $", biome {biome}";

            if (!string.IsNullOrWhiteSpace(zone))
                summary += $", near {zone}";

            return summary + ".";
        }

        private string DescribePosition(Vector3 position, string playerName, bool allowWorldMind)
        {
            var raw = BuildRawLocationSummary(position);

            if (!allowWorldMind || !_config.General.UseWorldMindForDescriptions || WorldMindV2 == null)
            {
                RecordLookup(playerName, position, raw, false);
                return raw;
            }

            var prompt = $"{_config.WorldMind.SystemInstruction}\nPlayer: {playerName}\nLocation facts: {raw}\nReturn one short line under {_config.WorldMind.MaxResponseCharacters} characters.";
            var response = AskWorldMind(prompt);

            if (string.IsNullOrWhiteSpace(response))
            {
                RecordLookup(playerName, position, raw, false);
                return raw;
            }

            response = TrimForChat(response, _config.WorldMind.MaxResponseCharacters);
            RecordLookup(playerName, position, response, true);
            return response;
        }

        private string AskWorldMind(string prompt)
        {
            try
            {
                var result = WorldMindV2?.CallHook("WorldMind_AskText", prompt, "map_location_description", null);
                return result as string;
            }
            catch (Exception ex)
            {
                PrintWarning($"WorldMind_AskText failed: {ex.Message}");
                return null;
            }
        }

        private void RecordLookup(string playerName, Vector3 position, string result, bool ai)
        {
            if (!_config.General.RecordMapLookupsToWorldMind || WorldMindV2 == null)
                return;

            try
            {
                var packet = new Dictionary<string, object>
                {
                    ["plugin"] = Name,
                    ["eventType"] = "map_lookup",
                    ["playerName"] = playerName ?? "unknown",
                    ["grid"] = GetGrid(position),
                    ["x"] = position.x,
                    ["y"] = position.y,
                    ["z"] = position.z,
                    ["result"] = result,
                    ["usedAI"] = ai
                };

                WorldMindV2.CallHook("WorldMind_RecordEvent", packet);
            }
            catch { }
        }

        private string TrimForChat(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (max <= 0 || value.Length <= max)
                return value;

            return value.Substring(0, Math.Max(0, max - 3)).TrimEnd() + "...";
        }

        // Hooks for companion plugins.
        private string WorldMindMapBrain_DescribePosition(Vector3 position)
        {
            return DescribePosition(position, "system", true);
        }

        private string WorldMindMapBrain_DescribePositionRaw(Vector3 position)
        {
            return BuildRawLocationSummary(position);
        }

        private string WorldMindMapBrain_GetGrid(Vector3 position)
        {
            return GetGrid(position);
        }

        private Dictionary<string, object> WorldMindMapBrain_GetLocationPacket(Vector3 position)
        {
            return new Dictionary<string, object>
            {
                ["grid"] = GetGrid(position),
                ["x"] = position.x,
                ["y"] = position.y,
                ["z"] = position.z,
                ["biome"] = GetApproximateBiome(position),
                ["namedZone"] = GetNearestNamedZone(position),
                ["rawSummary"] = BuildRawLocationSummary(position)
            };
        }
    }
}

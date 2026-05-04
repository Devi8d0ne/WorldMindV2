using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WorldMindLocationBrainV2", "Devi8d0ne", "2.0.0")]
    [Description("WorldMind location intelligence layer: grid, biome, monument, terrain, safezone, road/shore context, and shared location APIs.")]
    public class WorldMindLocationBrainV2 : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";
        private const string PermissionAdmin = "worldmindlocationbrainv2.admin";
        private const string DataFolder = "WorldMindLocationBrainV2";
        private const string DV8DAsciiTag = @"
DDDDDDDD      VV        VV     88888888      DDDDDDDD
DDDDDDDDD     VV        VV    8888888888     DDDDDDDDD
DD     DDD    VV        VV    88      88     DD     DDD
DD      DD    VV        VV    88      88     DD      DD
DD      DD     VV      VV      88888888      DD      DD
DD      DD     VV      VV     8888888888     DD      DD
DD      DD      VV    VV      88      88     DD      DD
DD     DDD       VV  VV       88      88     DD     DDD
DDDDDDDDD         VVVV        8888888888     DDDDDDDDD
DDDDDDDD           VV          88888888      DDDDDDDD
";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindSignalBrainV2;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<string, LocationContext> _contextCache = new Dictionary<string, LocationContext>();
        private readonly Dictionary<ulong, string> _lastPlayerGrid = new Dictionary<ulong, string>();
        private readonly List<MonumentRecord> _monuments = new List<MonumentRecord>();
        private double _lastHeartbeat;
        private double _lastMonumentScan;
        private string _lastError = "";
        private string _lastContextSummary = "none";
        private int _contextsBuilt;
        private int _cacheHits;
        private int _apiCalls;
        private bool _registeredWithCore;
        private bool _debug;

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            LoadConfigValues();
            LoadData();
            _debug = _config.Diagnostics.DebugToConsole;
        }

        private void OnServerInitialized()
        {
            ScanMonuments(true);
            RegisterWithWorldMind();
            timer.Every(Math.Max(10f, _config.Diagnostics.HeartbeatSeconds), Heartbeat);
            timer.Every(Math.Max(60f, _config.Monuments.RescanMinutes * 60f), () => ScanMonuments(false));
            if (_config.Tracking.TrackPlayerMovement)
                timer.Every(Math.Max(5f, _config.Tracking.PlayerLocationTickSeconds), TrackPlayers);

            Puts(DV8DAsciiTag);
            Puts("WorldMindLocationBrainV2 loaded. " + MadeWithLoveTag + ".");
            Puts("Purpose: grid/biome/monument/terrain context for the WorldMind suite.");
        }

        private void Unload()
        {
            SaveData();
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
                PrintError("Config read failed. Existing config was NOT overwritten. Runtime defaults are being used. Error: " + ex.Message);
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

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFolder + "/WorldMindLocationData");
                if (_data == null) _data = new StoredData();
            }
            catch (Exception ex)
            {
                PrintError("Location data read failed. Existing data was NOT overwritten. Error: " + ex.Message);
                _data = new StoredData();
            }

            if (_data.KnownMonuments == null) _data.KnownMonuments = new List<MonumentRecord>();
            if (_data.GridStats == null) _data.GridStats = new Dictionary<string, GridStats>();
            if (_data.PlayerLastKnown == null) _data.PlayerLastKnown = new Dictionary<string, PlayerLocationRecord>();
            if (_data.RecentContexts == null) _data.RecentContexts = new List<LocationContext>();
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(DataFolder + "/WorldMindLocationData", _data);
        }

        #endregion

        #region Commands

        [ChatCommand("wmlocation")]
        private void CmdLocation(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player))
            {
                Reply(player, "No permission.");
                return;
            }

            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";

            if (sub == "status")
            {
                Reply(player, BuildStatusText());
                return;
            }

            if (sub == "here")
            {
                LocationContext ctx = BuildLocationContext(player.transform.position, player.UserIDString, player.displayName, true);
                Reply(player, FormatContext(ctx));
                return;
            }

            if (sub == "monuments")
            {
                Reply(player, BuildMonumentText());
                return;
            }

            if (sub == "grid")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmlocation grid <grid>");
                    return;
                }
                Reply(player, BuildGridText(args[1].ToUpperInvariant()));
                return;
            }

            if (sub == "player")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Usage: /wmlocation player <name/id>");
                    return;
                }
                Reply(player, BuildPlayerLocationText(string.Join(" ", args.Skip(1).ToArray())));
                return;
            }

            if (sub == "rescan")
            {
                ScanMonuments(true);
                Reply(player, "Monuments rescanned: " + _monuments.Count);
                return;
            }

            if (sub == "debug")
            {
                if (args.Length < 2)
                {
                    Reply(player, "Debug is " + (_debug ? "on" : "off") + ". Usage: /wmlocation debug on|off");
                    return;
                }
                _debug = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                _config.Diagnostics.DebugToConsole = _debug;
                SaveConfig();
                Reply(player, "Debug set to " + _debug + ".");
                return;
            }

            if (sub == "reload")
            {
                LoadConfigValues();
                LoadData();
                ScanMonuments(true);
                Reply(player, "WorldMindLocationBrainV2 config/data reloaded.");
                return;
            }

            Reply(player, "Usage: /wmlocation status|here|monuments|grid <grid>|player <name/id>|rescan|debug on|off|reload");
        }

        [ConsoleCommand("wmlocation.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.Player() != null && !IsAdmin(arg.Player()))
            {
                arg.ReplyWith("No permission.");
                return;
            }
            arg.ReplyWith(BuildStatusText());
        }

        [ConsoleCommand("wmlocation.rescan")]
        private void CcmdRescan(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.Player() != null && !IsAdmin(arg.Player()))
            {
                arg.ReplyWith("No permission.");
                return;
            }
            ScanMonuments(true);
            arg.ReplyWith("Monuments rescanned: " + _monuments.Count);
        }

        #endregion

        #region Public WorldMind Location API

        private object WorldMindLocation_Describe(Vector3 position)
        {
            _apiCalls++;
            return BuildLocationContext(position, "", "", false);
        }

        private object WorldMindLocation_GetContext(Vector3 position, string playerId = "", string playerName = "")
        {
            _apiCalls++;
            return BuildLocationContext(position, playerId ?? "", playerName ?? "", false);
        }

        private object WorldMindLocation_GetGrid(Vector3 position)
        {
            _apiCalls++;
            return GetGrid(position);
        }

        private object WorldMindLocation_GetBiome(Vector3 position)
        {
            _apiCalls++;
            return DetectBiome(position);
        }

        private object WorldMindLocation_GetNearestMonument(Vector3 position)
        {
            _apiCalls++;
            return FindNearestMonument(position);
        }

        private object WorldMindLocation_IsSafeZone(Vector3 position)
        {
            _apiCalls++;
            return IsSafeZone(position);
        }

        private object WorldMindLocation_GetGridStats(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return null;
            GridStats stats;
            return _data.GridStats.TryGetValue(grid.ToUpperInvariant(), out stats) ? stats : null;
        }

        private object WorldMindLocation_GetRecentContexts(int count = 10)
        {
            count = Mathf.Clamp(count, 1, 50);
            return _data.RecentContexts.Skip(Math.Max(0, _data.RecentContexts.Count - count)).ToList();
        }

        private object WorldMindLocation_GetPlayerLastKnown(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;
            PlayerLocationRecord rec;
            return _data.PlayerLastKnown.TryGetValue(playerId, out rec) ? rec : null;
        }

        private object WorldMindLocation_GetProof()
        {
            return BuildProofPacket();
        }

        #endregion

        #region Location Brain

        private LocationContext BuildLocationContext(Vector3 position, string playerId, string playerName, bool force)
        {
            string grid = GetGrid(position);
            string cacheKey = grid + ":" + Mathf.RoundToInt(position.x / _config.Cache.CacheCellMeters) + ":" + Mathf.RoundToInt(position.z / _config.Cache.CacheCellMeters);
            LocationContext cached;
            if (!force && _config.Cache.Enabled && _contextCache.TryGetValue(cacheKey, out cached))
            {
                double age = Interface.Oxide.Now - cached.CacheTime;
                if (age <= _config.Cache.CacheSeconds)
                {
                    _cacheHits++;
                    return cached;
                }
            }

            MonumentRecord nearest = FindNearestMonument(position);
            string biome = DetectBiome(position);
            List<string> topology = DetectTopology(position);
            bool safezone = IsSafeZone(position);
            bool shoreline = topology.Contains("shore") || topology.Contains("water") || position.y < _config.LocationRules.LowElevationShoreHintY;
            bool road = topology.Contains("road") || topology.Contains("roadside");
            bool underground = position.y < _config.LocationRules.UndergroundYThreshold || topology.Contains("tunnel") || topology.Contains("underground");

            LocationContext ctx = new LocationContext
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                CacheTime = Interface.Oxide.Now,
                PlayerId = playerId ?? "",
                PlayerName = playerName ?? "",
                X = Mathf.Round(position.x * 10f) / 10f,
                Y = Mathf.Round(position.y * 10f) / 10f,
                Z = Mathf.Round(position.z * 10f) / 10f,
                Grid = grid,
                Biome = biome,
                Terrain = BuildTerrainLabel(position, biome, topology),
                Topology = topology,
                NearestMonument = nearest == null ? "" : nearest.Name,
                NearestMonumentDistance = nearest == null ? -1f : Vector3.Distance(position, nearest.Position),
                NearMonument = nearest != null && Vector3.Distance(position, nearest.Position) <= _config.Monuments.NearMonumentMeters,
                IsSafeZone = safezone,
                NearRoad = road,
                NearShore = shoreline,
                Underground = underground,
                ContextLabel = "",
                RiskHint = ""
            };

            ctx.ContextLabel = BuildContextLabel(ctx);
            ctx.RiskHint = BuildRiskHint(ctx);
            _lastContextSummary = ctx.ContextLabel;
            _contextsBuilt++;

            TrackGridContext(ctx);
            RememberRecentContext(ctx);

            if (_config.Cache.Enabled) _contextCache[cacheKey] = ctx;
            if (_debug) Puts("Location context: " + JsonConvert.SerializeObject(ctx));
            return ctx;
        }

        private void TrackGridContext(LocationContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.Grid)) return;
            GridStats stats;
            if (!_data.GridStats.TryGetValue(ctx.Grid, out stats))
            {
                stats = new GridStats { Grid = ctx.Grid };
                _data.GridStats[ctx.Grid] = stats;
            }
            stats.LastSeenUtc = DateTime.UtcNow.ToString("o");
            stats.ContextSamples++;
            stats.LastBiome = ctx.Biome;
            stats.LastTerrain = ctx.Terrain;
            stats.LastMonument = ctx.NearestMonument;
            if (ctx.IsSafeZone) stats.SafeZoneSamples++;
            if (ctx.NearRoad) stats.RoadSamples++;
            if (ctx.NearShore) stats.ShoreSamples++;
            if (ctx.Underground) stats.UndergroundSamples++;
        }

        private void RememberRecentContext(LocationContext ctx)
        {
            if (ctx == null || !_config.Tracking.SaveRecentContexts) return;
            _data.RecentContexts.Add(ctx);
            int max = Math.Max(20, _config.Tracking.MaxRecentContexts);
            while (_data.RecentContexts.Count > max) _data.RecentContexts.RemoveAt(0);
        }

        private string GetGrid(Vector3 position)
        {
            float worldSize = ConVar.Server.worldsize > 0 ? ConVar.Server.worldsize : _config.LocationRules.FallbackWorldSize;
            float half = worldSize / 2f;
            float cell = Math.Max(50f, _config.LocationRules.GridCellMeters);
            int cols = Mathf.Max(1, Mathf.CeilToInt(worldSize / cell));
            int x = Mathf.Clamp(Mathf.FloorToInt((position.x + half) / cell), 0, cols - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt((half - position.z) / cell), 0, cols - 1);
            return ToGridLetters(x) + z.ToString();
        }

        private string ToGridLetters(int index)
        {
            index = Math.Max(0, index);
            string result = "";
            do
            {
                int rem = index % 26;
                result = (char)('A' + rem) + result;
                index = (index / 26) - 1;
            } while (index >= 0);
            return result;
        }

        private string DetectBiome(Vector3 position)
        {
            if (!_config.BiomeDetection.Enabled) return "unknown";
            try
            {
                object biomeMap = GetStaticMember(typeof(TerrainMeta), "BiomeMap");
                if (biomeMap != null)
                {
                    object raw = InvokeBest(biomeMap, "GetBiome", new object[] { position });
                    if (raw != null)
                    {
                        string text = raw.ToString().ToLowerInvariant();
                        if (text.Contains("arid") || text.Contains("desert")) return "desert";
                        if (text.Contains("snow") || text.Contains("tundra")) return "snow";
                        if (text.Contains("forest")) return "forest";
                        if (text.Contains("temperate")) return "forest";
                        if (text.Contains("0")) return GuessBiomeFromElevation(position);
                        return CleanToken(raw.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = "Biome detection failed: " + ex.Message;
            }
            return GuessBiomeFromElevation(position);
        }

        private string GuessBiomeFromElevation(Vector3 position)
        {
            if (position.y >= _config.BiomeDetection.SnowElevationHint) return "snow";
            if (position.y <= _config.BiomeDetection.DesertLowElevationHint && position.z < 0f) return "desert";
            return "forest";
        }

        private List<string> DetectTopology(Vector3 position)
        {
            List<string> result = new List<string>();
            if (!_config.TopologyDetection.Enabled) return result;
            try
            {
                object topologyMap = GetStaticMember(typeof(TerrainMeta), "TopologyMap");
                if (topologyMap != null)
                {
                    object raw = InvokeBest(topologyMap, "GetTopology", new object[] { position });
                    if (raw != null)
                    {
                        int mask;
                        if (int.TryParse(raw.ToString(), out mask))
                        {
                            AddTopologyByMask(result, mask);
                        }
                        else
                        {
                            result.Add(CleanToken(raw.ToString()));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = "Topology detection failed: " + ex.Message;
            }

            if (position.y < _config.LocationRules.UndergroundYThreshold && !result.Contains("underground")) result.Add("underground");
            return result.Distinct().ToList();
        }

        private void AddTopologyByMask(List<string> result, int mask)
        {
            if (mask == 0) return;
            AddIfMask(result, mask, 1 << 0, "field");
            AddIfMask(result, mask, 1 << 1, "cliff");
            AddIfMask(result, mask, 1 << 2, "summit");
            AddIfMask(result, mask, 1 << 3, "beachside");
            AddIfMask(result, mask, 1 << 4, "beach");
            AddIfMask(result, mask, 1 << 5, "forest");
            AddIfMask(result, mask, 1 << 6, "forestside");
            AddIfMask(result, mask, 1 << 7, "ocean");
            AddIfMask(result, mask, 1 << 8, "oceanside");
            AddIfMask(result, mask, 1 << 9, "decor");
            AddIfMask(result, mask, 1 << 10, "monument");
            AddIfMask(result, mask, 1 << 11, "road");
            AddIfMask(result, mask, 1 << 12, "roadside");
            AddIfMask(result, mask, 1 << 13, "swamp");
            AddIfMask(result, mask, 1 << 14, "river");
            AddIfMask(result, mask, 1 << 15, "riverside");
            AddIfMask(result, mask, 1 << 16, "lake");
            AddIfMask(result, mask, 1 << 17, "lakeside");
            AddIfMask(result, mask, 1 << 18, "offshore");
            AddIfMask(result, mask, 1 << 19, "powerline");
            AddIfMask(result, mask, 1 << 20, "powerline_side");
            AddIfMask(result, mask, 1 << 21, "building");
            AddIfMask(result, mask, 1 << 22, "cliffside");
        }

        private void AddIfMask(List<string> list, int mask, int flag, string label)
        {
            if ((mask & flag) != 0 && !list.Contains(label)) list.Add(label);
        }

        private string BuildTerrainLabel(Vector3 position, string biome, List<string> topology)
        {
            if (topology != null)
            {
                if (topology.Contains("road") || topology.Contains("roadside")) return "road corridor";
                if (topology.Contains("monument")) return "monument zone";
                if (topology.Contains("river") || topology.Contains("riverside")) return "river terrain";
                if (topology.Contains("lake") || topology.Contains("lakeside")) return "lake terrain";
                if (topology.Contains("ocean") || topology.Contains("beach") || topology.Contains("beachside")) return "shoreline";
                if (topology.Contains("cliff") || topology.Contains("cliffside") || topology.Contains("summit")) return "high ground";
                if (topology.Contains("forest") || topology.Contains("forestside")) return "forest cover";
                if (topology.Contains("swamp")) return "swamp";
                if (topology.Contains("underground")) return "underground";
            }
            return string.IsNullOrEmpty(biome) ? "open terrain" : biome + " terrain";
        }

        private string BuildContextLabel(LocationContext ctx)
        {
            List<string> parts = new List<string>();
            parts.Add("Grid " + ctx.Grid);
            if (!string.IsNullOrEmpty(ctx.NearestMonument))
            {
                if (ctx.NearMonument) parts.Add("near " + ctx.NearestMonument);
                else parts.Add("closest monument " + ctx.NearestMonument + " (" + Mathf.RoundToInt(ctx.NearestMonumentDistance) + "m)");
            }
            if (!string.IsNullOrEmpty(ctx.Biome)) parts.Add(ctx.Biome);
            if (!string.IsNullOrEmpty(ctx.Terrain)) parts.Add(ctx.Terrain);
            if (ctx.IsSafeZone) parts.Add("safezone");
            if (ctx.Underground) parts.Add("underground");
            return string.Join(" | ", parts.ToArray());
        }

        private string BuildRiskHint(LocationContext ctx)
        {
            if (ctx == null) return "unknown";
            if (ctx.IsSafeZone) return "low: safezone context";
            if (ctx.Underground) return "high: underground/tunnel context";
            if (ctx.NearMonument) return "elevated: monument traffic likely";
            if (ctx.NearRoad) return "elevated: road corridor traffic likely";
            if (ctx.NearShore) return "variable: shoreline movement and boat activity possible";
            return "normal: open island context";
        }

        private bool IsSafeZone(Vector3 position)
        {
            if (!_config.SafeZoneDetection.Enabled) return false;
            foreach (MonumentRecord monument in _monuments)
            {
                if (monument == null) continue;
                if (!monument.SafeZoneLikely) continue;
                if (Vector3.Distance(position, monument.Position) <= _config.SafeZoneDetection.SafeZoneRadiusMeters) return true;
            }
            return false;
        }

        #endregion

        #region Monuments

        private void ScanMonuments(bool force)
        {
            double now = Interface.Oxide.Now;
            if (!force && now - _lastMonumentScan < 60) return;
            _lastMonumentScan = now;
            _monuments.Clear();

            if (!_config.Monuments.Enabled)
            {
                _data.KnownMonuments = new List<MonumentRecord>();
                return;
            }

            try
            {
                MonumentInfo[] found = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
                foreach (MonumentInfo info in found)
                {
                    if (info == null) continue;
                    string rawName = SafeName(info.name);
                    if (string.IsNullOrEmpty(rawName)) continue;
                    MonumentRecord rec = new MonumentRecord
                    {
                        Name = NormalizeMonumentName(rawName),
                        RawName = rawName,
                        X = info.transform.position.x,
                        Y = info.transform.position.y,
                        Z = info.transform.position.z,
                        Grid = GetGrid(info.transform.position),
                        SafeZoneLikely = IsSafeZoneName(rawName),
                        UpdatedUtc = DateTime.UtcNow.ToString("o")
                    };
                    _monuments.Add(rec);
                }
                _monuments.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                _data.KnownMonuments = _monuments.ToList();
                SaveData();
                if (_debug) Puts("Scanned monuments: " + _monuments.Count);
            }
            catch (Exception ex)
            {
                _lastError = "Monument scan failed: " + ex.Message;
                PrintWarning(_lastError);
            }
        }

        private MonumentRecord FindNearestMonument(Vector3 position)
        {
            if (_monuments.Count == 0) ScanMonuments(false);
            MonumentRecord best = null;
            float bestDist = float.MaxValue;
            foreach (MonumentRecord rec in _monuments)
            {
                if (rec == null) continue;
                float dist = Vector3.Distance(position, rec.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = rec;
                }
            }
            return best;
        }

        private string NormalizeMonumentName(string raw)
        {
            string value = raw ?? "";
            value = value.Replace("assets/bundled/prefabs/autospawn/monument/", "");
            value = value.Replace("assets/bundled/prefabs/modding/", "");
            value = value.Replace(".prefab", "");
            value = value.Replace("_", " ").Replace("-", " ");
            value = value.Replace("monument", "");
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            value = value.Trim();
            if (string.IsNullOrEmpty(value)) value = raw;
            return CultureTitle(value);
        }

        private string CultureTitle(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string[] parts = value.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                parts[i] = char.ToUpperInvariant(parts[i][0]) + (parts[i].Length > 1 ? parts[i].Substring(1).ToLowerInvariant() : "");
            }
            return string.Join(" ", parts);
        }

        private bool IsSafeZoneName(string raw)
        {
            string v = (raw ?? "").ToLowerInvariant();
            return v.Contains("compound") || v.Contains("outpost") || v.Contains("bandit") || v.Contains("fishing_village") || v.Contains("stables") || v.Contains("ranch");
        }

        #endregion

        #region Tracking / Core Proof

        private void TrackPlayers()
        {
            if (!_config.Tracking.TrackPlayerMovement) return;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                LocationContext ctx = BuildLocationContext(player.transform.position, player.UserIDString, player.displayName, false);
                string oldGrid;
                if (!_lastPlayerGrid.TryGetValue(player.userID, out oldGrid) || oldGrid != ctx.Grid)
                {
                    _lastPlayerGrid[player.userID] = ctx.Grid;
                    _data.PlayerLastKnown[player.UserIDString] = new PlayerLocationRecord
                    {
                        PlayerId = player.UserIDString,
                        PlayerName = player.displayName,
                        Grid = ctx.Grid,
                        ContextLabel = ctx.ContextLabel,
                        X = ctx.X,
                        Y = ctx.Y,
                        Z = ctx.Z,
                        UpdatedUtc = DateTime.UtcNow.ToString("o")
                    };
                    RecordToCore("player_grid_changed", player.UserIDString, player.displayName, ctx);
                }
            }
            SaveData();
        }

        private void RegisterWithWorldMind()
        {
            Dictionary<string, object> packet = new Dictionary<string, object>
            {
                ["plugin"] = Name,
                ["version"] = Version.ToString(),
                ["purpose"] = "Location intelligence: grid, biome, monument, terrain, safezone, road/shore/underground context.",
                ["commands"] = new[] { "/wmlocation status", "/wmlocation here", "/wmlocation monuments", "/wmlocation grid <grid>", "/wmlocation player <name/id>" },
                ["apis"] = new[] { "WorldMindLocation_GetContext", "WorldMindLocation_GetGrid", "WorldMindLocation_GetBiome", "WorldMindLocation_GetNearestMonument", "WorldMindLocation_IsSafeZone" }
            };
            object result = WorldMindV2?.Call("WorldMind_RegisterPlugin", Name, packet);
            _registeredWithCore = result is bool && (bool)result;
            if (!_registeredWithCore && result != null) _registeredWithCore = true;
        }

        private void Heartbeat()
        {
            _lastHeartbeat = Interface.Oxide.Now;
            Dictionary<string, object> packet = BuildProofPacket();
            object result = WorldMindV2?.Call("WorldMind_Heartbeat", Name, packet);
            if (result == null && _config.Diagnostics.WarnWhenCoreMissing)
                _lastError = "WorldMindV2 heartbeat hook returned null. Core missing or older Core build.";
        }

        private void RecordToCore(string eventType, string playerId, string playerName, LocationContext ctx)
        {
            if (!_config.WorldMindRouting.RecordLocationEventsToCore || ctx == null) return;
            Dictionary<string, object> truth = new Dictionary<string, object>
            {
                ["playerId"] = playerId ?? "",
                ["playerName"] = playerName ?? "",
                ["eventType"] = eventType,
                ["grid"] = ctx.Grid,
                ["biome"] = ctx.Biome,
                ["terrain"] = ctx.Terrain,
                ["nearestMonument"] = ctx.NearestMonument,
                ["nearestMonumentDistance"] = ctx.NearestMonumentDistance,
                ["nearMonument"] = ctx.NearMonument,
                ["safezone"] = ctx.IsSafeZone,
                ["nearRoad"] = ctx.NearRoad,
                ["nearShore"] = ctx.NearShore,
                ["underground"] = ctx.Underground,
                ["contextLabel"] = ctx.ContextLabel,
                ["riskHint"] = ctx.RiskHint,
                ["position"] = ctx.X + ", " + ctx.Y + ", " + ctx.Z
            };
            WorldMindV2?.Call("WorldMind_RecordEvent", Name, eventType, playerId ?? "", truth);
        }

        private Dictionary<string, object> BuildProofPacket()
        {
            return new Dictionary<string, object>
            {
                ["plugin"] = Name,
                ["version"] = Version.ToString(),
                ["registeredWithCore"] = _registeredWithCore,
                ["contextsBuilt"] = _contextsBuilt,
                ["cacheHits"] = _cacheHits,
                ["apiCalls"] = _apiCalls,
                ["monuments"] = _monuments.Count,
                ["gridStats"] = _data.GridStats.Count,
                ["playerLastKnown"] = _data.PlayerLastKnown.Count,
                ["lastContextSummary"] = _lastContextSummary,
                ["lastError"] = _lastError,
                ["lastHeartbeatAgoSeconds"] = _lastHeartbeat <= 0 ? -1 : Mathf.RoundToInt((float)(Interface.Oxide.Now - _lastHeartbeat))
            };
        }

        #endregion

        #region Formatting / Helpers

        private string BuildStatusText()
        {
            return string.Join("\n", new[]
            {
                "WorldMindLocationBrainV2 status:",
                "Registered with Core: " + _registeredWithCore,
                "Monuments known: " + _monuments.Count,
                "Contexts built: " + _contextsBuilt,
                "Cache hits: " + _cacheHits,
                "API calls: " + _apiCalls,
                "Grid records: " + _data.GridStats.Count,
                "Player last-known records: " + _data.PlayerLastKnown.Count,
                "Last context: " + _lastContextSummary,
                "Last error: " + (string.IsNullOrEmpty(_lastError) ? "none" : _lastError)
            });
        }

        private string BuildMonumentText()
        {
            if (_monuments.Count == 0) return "No monuments detected. Try /wmlocation rescan.";
            List<string> lines = new List<string> { "Known monuments:" };
            foreach (MonumentRecord rec in _monuments.Take(_config.Monuments.MaxMonumentsInCommand))
                lines.Add("- " + rec.Name + " | " + rec.Grid + (rec.SafeZoneLikely ? " | safezone-likely" : ""));
            if (_monuments.Count > _config.Monuments.MaxMonumentsInCommand) lines.Add("..." + (_monuments.Count - _config.Monuments.MaxMonumentsInCommand) + " more");
            return string.Join("\n", lines.ToArray());
        }

        private string BuildGridText(string grid)
        {
            GridStats stats;
            if (!_data.GridStats.TryGetValue(grid, out stats)) return "No grid stats yet for " + grid + ".";
            return "Grid " + grid + ": samples=" + stats.ContextSamples + ", biome=" + stats.LastBiome + ", terrain=" + stats.LastTerrain + ", monument=" + stats.LastMonument + ", road=" + stats.RoadSamples + ", shore=" + stats.ShoreSamples + ", underground=" + stats.UndergroundSamples + ", safezone=" + stats.SafeZoneSamples;
        }

        private string BuildPlayerLocationText(string search)
        {
            if (string.IsNullOrEmpty(search)) return "Missing player search.";
            BasePlayer player = FindPlayer(search);
            if (player != null)
            {
                LocationContext ctx = BuildLocationContext(player.transform.position, player.UserIDString, player.displayName, true);
                return player.displayName + ": " + FormatContext(ctx);
            }

            PlayerLocationRecord rec = _data.PlayerLastKnown.Values.FirstOrDefault(x => x.PlayerId == search || (!string.IsNullOrEmpty(x.PlayerName) && x.PlayerName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
            if (rec == null) return "No online or last-known player match for " + search + ".";
            return rec.PlayerName + " last known: " + rec.ContextLabel + " at " + rec.X + ", " + rec.Y + ", " + rec.Z + " UTC " + rec.UpdatedUtc;
        }

        private string FormatContext(LocationContext ctx)
        {
            if (ctx == null) return "No context.";
            return ctx.ContextLabel + " | risk=" + ctx.RiskHint + " | pos=" + ctx.X + ", " + ctx.Y + ", " + ctx.Z;
        }

        private BasePlayer FindPlayer(string search)
        {
            if (string.IsNullOrEmpty(search)) return null;
            foreach (BasePlayer p in BasePlayer.activePlayerList)
            {
                if (p == null) continue;
                if (p.UserIDString == search) return p;
                if (!string.IsNullOrEmpty(p.displayName) && p.displayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return p;
            }
            return null;
        }

        private bool IsAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin));
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null) return;
            SendReply(player, "<color=#b58a55>[WMLocation]</color> " + message);
        }

        private object GetStaticMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            var prop = type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop != null) return prop.GetValue(null, null);
            var field = type.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field != null) return field.GetValue(null);
            return null;
        }

        private object InvokeBest(object target, string method, object[] args)
        {
            if (target == null || string.IsNullOrEmpty(method)) return null;
            var methods = target.GetType().GetMethods().Where(m => m.Name == method).ToArray();
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                try { return m.Invoke(target, args); } catch { }
            }
            return null;
        }

        private string SafeName(string value)
        {
            return (value ?? "").Trim();
        }

        private string CleanToken(string value)
        {
            value = (value ?? "unknown").ToLowerInvariant().Replace("_", " ").Replace("-", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value;
        }

        #endregion

        #region Config / Data Classes

        private class PluginConfig
        {
            [JsonProperty(Order = 1, PropertyName = "Core Routing")]
            public WorldMindRoutingConfig WorldMindRouting = new WorldMindRoutingConfig();

            [JsonProperty(Order = 2, PropertyName = "Location Rules")]
            public LocationRulesConfig LocationRules = new LocationRulesConfig();

            [JsonProperty(Order = 3, PropertyName = "Monument Detection")]
            public MonumentConfig Monuments = new MonumentConfig();

            [JsonProperty(Order = 4, PropertyName = "Biome Detection")]
            public BiomeConfig BiomeDetection = new BiomeConfig();

            [JsonProperty(Order = 5, PropertyName = "Topology Detection")]
            public TopologyConfig TopologyDetection = new TopologyConfig();

            [JsonProperty(Order = 6, PropertyName = "Safe Zone Detection")]
            public SafeZoneConfig SafeZoneDetection = new SafeZoneConfig();

            [JsonProperty(Order = 7, PropertyName = "Tracking")]
            public TrackingConfig Tracking = new TrackingConfig();

            [JsonProperty(Order = 8, PropertyName = "Cache")]
            public CacheConfig Cache = new CacheConfig();

            [JsonProperty(Order = 9, PropertyName = "Diagnostics")]
            public DiagnosticsConfig Diagnostics = new DiagnosticsConfig();

            public static PluginConfig Default()
            {
                return new PluginConfig();
            }

            public void Normalize()
            {
                if (WorldMindRouting == null) WorldMindRouting = new WorldMindRoutingConfig();
                if (LocationRules == null) LocationRules = new LocationRulesConfig();
                if (Monuments == null) Monuments = new MonumentConfig();
                if (BiomeDetection == null) BiomeDetection = new BiomeConfig();
                if (TopologyDetection == null) TopologyDetection = new TopologyConfig();
                if (SafeZoneDetection == null) SafeZoneDetection = new SafeZoneConfig();
                if (Tracking == null) Tracking = new TrackingConfig();
                if (Cache == null) Cache = new CacheConfig();
                if (Diagnostics == null) Diagnostics = new DiagnosticsConfig();
                LocationRules.GridCellMeters = Math.Max(50, LocationRules.GridCellMeters);
                LocationRules.FallbackWorldSize = Math.Max(1000, LocationRules.FallbackWorldSize);
                Monuments.NearMonumentMeters = Math.Max(25, Monuments.NearMonumentMeters);
                Monuments.RescanMinutes = Math.Max(1, Monuments.RescanMinutes);
                Tracking.PlayerLocationTickSeconds = Math.Max(5, Tracking.PlayerLocationTickSeconds);
                Cache.CacheSeconds = Math.Max(1, Cache.CacheSeconds);
                Cache.CacheCellMeters = Math.Max(10, Cache.CacheCellMeters);
            }
        }

        private class WorldMindRoutingConfig
        {
            public bool RegisterWithWorldMindCore = true;
            public bool SendHeartbeatToCore = true;
            public bool RecordLocationEventsToCore = true;
        }

        private class LocationRulesConfig
        {
            public float GridCellMeters = 150f;
            public float FallbackWorldSize = 4500f;
            public float UndergroundYThreshold = -5f;
            public float LowElevationShoreHintY = 2f;
        }

        private class MonumentConfig
        {
            public bool Enabled = true;
            public float NearMonumentMeters = 225f;
            public int RescanMinutes = 15;
            public int MaxMonumentsInCommand = 35;
        }

        private class BiomeConfig
        {
            public bool Enabled = true;
            public float SnowElevationHint = 250f;
            public float DesertLowElevationHint = 65f;
        }

        private class TopologyConfig
        {
            public bool Enabled = true;
        }

        private class SafeZoneConfig
        {
            public bool Enabled = true;
            public float SafeZoneRadiusMeters = 175f;
        }

        private class TrackingConfig
        {
            public bool TrackPlayerMovement = true;
            public int PlayerLocationTickSeconds = 20;
            public bool SaveRecentContexts = true;
            public int MaxRecentContexts = 250;
        }

        private class CacheConfig
        {
            public bool Enabled = true;
            public int CacheSeconds = 45;
            public float CacheCellMeters = 30f;
        }

        private class DiagnosticsConfig
        {
            public bool DebugToConsole = false;
            public bool WarnWhenCoreMissing = true;
            public int HeartbeatSeconds = 30;
        }

        private class StoredData
        {
            public List<MonumentRecord> KnownMonuments = new List<MonumentRecord>();
            public Dictionary<string, GridStats> GridStats = new Dictionary<string, GridStats>();
            public Dictionary<string, PlayerLocationRecord> PlayerLastKnown = new Dictionary<string, PlayerLocationRecord>();
            public List<LocationContext> RecentContexts = new List<LocationContext>();
        }

        public class MonumentRecord
        {
            public string Name = "";
            public string RawName = "";
            public float X;
            public float Y;
            public float Z;
            public string Grid = "";
            public bool SafeZoneLikely;
            public string UpdatedUtc = "";
            [JsonIgnore] public Vector3 Position { get { return new Vector3(X, Y, Z); } }
        }

        public class GridStats
        {
            public string Grid = "";
            public int ContextSamples;
            public int SafeZoneSamples;
            public int RoadSamples;
            public int ShoreSamples;
            public int UndergroundSamples;
            public string LastBiome = "";
            public string LastTerrain = "";
            public string LastMonument = "";
            public string LastSeenUtc = "";
        }

        public class PlayerLocationRecord
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string Grid = "";
            public string ContextLabel = "";
            public float X;
            public float Y;
            public float Z;
            public string UpdatedUtc = "";
        }

        public class LocationContext
        {
            public string TimestampUtc = "";
            [JsonIgnore] public double CacheTime;
            public string PlayerId = "";
            public string PlayerName = "";
            public float X;
            public float Y;
            public float Z;
            public string Grid = "";
            public string Biome = "";
            public string Terrain = "";
            public List<string> Topology = new List<string>();
            public string NearestMonument = "";
            public float NearestMonumentDistance = -1f;
            public bool NearMonument;
            public bool IsSafeZone;
            public bool NearRoad;
            public bool NearShore;
            public bool Underground;
            public string ContextLabel = "";
            public string RiskHint = "";
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using ProtoBuf;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    [Info("WorldMindMapBrainV2", "Devi8d0ne", "2.9.10")]
    [Description("Self-contained WorldMind tactical map intelligence: private map layers, native Map-parent UI, entity intel cards, heat memory, watch pins, NPC layers, and permission-tiered player intel.")]
    public class WorldMindMapBrainV2 : RustPlugin
    {
        private const string MadeWithLoveTag = "Made with love by Deviated Systems";
        private const string DataFile = "WorldMindMapBrainV2/MapBrainData";
        private const string UiName = "WorldMindMapBrainV2.UI";
        private const string MoveUiName = "WorldMindMapBrainV2.MoveUI";
        private const string CardName = "WorldMindMapBrainV2.Card";
        private const string UiUpdaterName = "WorldMindMapBrainV2.UIUpdater";

        private const string PermAdmin = "worldmindmapbrainv2.admin";
        private const string PermAdminUi = "worldmindmapbrainv2.admin.ui";
        private const string PermAdminMoveUi = "worldmindmapbrainv2.admin.ui.move";
        private const string PermAdminIntel = "worldmindmapbrainv2.admin.intel";
        private const string PermAdminPlayers = "worldmindmapbrainv2.admin.players";
        private const string PermAdminSleepers = "worldmindmapbrainv2.admin.sleepers";
        private const string PermAdminTc = "worldmindmapbrainv2.admin.tc";
        private const string PermAdminStashes = "worldmindmapbrainv2.admin.stashes";
        private const string PermAdminBags = "worldmindmapbrainv2.admin.bags";
        private const string PermAdminNpcs = "worldmindmapbrainv2.admin.npcs";
        private const string PermAdminNpcsHostile = "worldmindmapbrainv2.admin.npcs.hostile";
        private const string PermAdminNpcsSafezone = "worldmindmapbrainv2.admin.npcs.safezone";
        private const string PermAdminAnimals = "worldmindmapbrainv2.admin.animals";
        private const string PermAdminScientists = "worldmindmapbrainv2.admin.scientists";
        private const string PermAdminBosses = "worldmindmapbrainv2.admin.bosses";
        private const string PermAdminHeat = "worldmindmapbrainv2.admin.heat";
        private const string PermAdminHeat5 = "worldmindmapbrainv2.admin.heat.5m";
        private const string PermAdminHeat15 = "worldmindmapbrainv2.admin.heat.15m";
        private const string PermAdminHeat60 = "worldmindmapbrainv2.admin.heat.1h";
        private const string PermAdminHeat24 = "worldmindmapbrainv2.admin.heat.24h";
        private const string PermAdminHeatWipe = "worldmindmapbrainv2.admin.heat.wipe";
        private const string PermAdminRaid = "worldmindmapbrainv2.admin.raid";
        private const string PermAdminClusters = "worldmindmapbrainv2.admin.clusters";
        private const string PermAdminWatch = "worldmindmapbrainv2.admin.watch";
        private const string PermAdminPulse = "worldmindmapbrainv2.admin.pulse";
        private const string PermAdminZones = "worldmindmapbrainv2.admin.zones";
        private const string PermAdminHidden = "worldmindmapbrainv2.admin.hidden";

        private const string PermPlayer = "worldmindmapbrainv2.player";
        private const string PermPlayerBasic = "worldmindmapbrainv2.player.basic";
        private const string PermPlayerScout = "worldmindmapbrainv2.player.scout";
        private const string PermPlayerVip = "worldmindmapbrainv2.player.vip";
        private const string PermPlayerEvent = "worldmindmapbrainv2.player.event";
        private const string PermPlayerHeat = "worldmindmapbrainv2.player.heat";
        private const string PermPlayerZones = "worldmindmapbrainv2.player.zones";
        private const string PermPlayerIntel = "worldmindmapbrainv2.player.intel";
        private const string PermPlayerPulse = "worldmindmapbrainv2.player.pulse";
        private const string PermPlayerLootHints = "worldmindmapbrainv2.player.loothints";
        private const string PermPlayerEventZones = "worldmindmapbrainv2.player.eventzones";

        private static readonly string[] AllPermissions =
        {
            PermAdmin, PermAdminUi, PermAdminMoveUi, PermAdminIntel, PermAdminPlayers, PermAdminSleepers, PermAdminTc,
            PermAdminStashes, PermAdminBags, PermAdminNpcs, PermAdminNpcsHostile, PermAdminNpcsSafezone, PermAdminAnimals,
            PermAdminScientists, PermAdminBosses, PermAdminHeat, PermAdminHeat5, PermAdminHeat15, PermAdminHeat60,
            PermAdminHeat24, PermAdminHeatWipe, PermAdminRaid, PermAdminClusters, PermAdminWatch, PermAdminPulse,
            PermAdminZones, PermAdminHidden, PermPlayer, PermPlayerBasic, PermPlayerScout, PermPlayerVip, PermPlayerEvent,
            PermPlayerHeat, PermPlayerZones, PermPlayerIntel, PermPlayerPulse, PermPlayerLootHints, PermPlayerEventZones
        };

        private const string DV8DAsciiTag = @"
DDDDDDDD      VV        VV     88888888      DDDDDDDD
DDDDDDDDD     VV        VV    8888888888     DDDDDDDDD
DD     DDD    VV        VV    88      88     DD     DDD
DD      DD    VV        VV    88      88     DD      DD
DD      DD     VV      VV      88888888      DD      DD
DD      DD     VV      VV     8888888888     DD      DD
DD      DD      VV    VV      88      88     DD      DD
DD     DDD       VV  VV       88      88     DD     DDD
DDDDDDDDD        VVVV        8888888888     DDDDDDDDD
DDDDDDDD          VV          88888888      DDDDDDDD
";

        [PluginReference] private Plugin WorldMindV2;
        [PluginReference] private Plugin WorldMindSignalBrainV2;
        [PluginReference] private Plugin WorldMindLocationBrainV2;

        private PluginConfig _config;
        private StoredData _data;
        private Timer _tickTimer;
        private readonly Dictionary<ulong, PlayerSession> _sessions = new Dictionary<ulong, PlayerSession>();
        private readonly Dictionary<ulong, double> _lastUiRender = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, List<MapNote>> _lastRenderedNotes = new Dictionary<ulong, List<MapNote>>();
        private readonly Dictionary<ulong, Dictionary<ulong, IntelTarget>> _visibleTargets = new Dictionary<ulong, Dictionary<ulong, IntelTarget>>();
        private readonly Dictionary<string, WorldMapSignal> _lastSignalBucket = new Dictionary<string, WorldMapSignal>();
        private double _lastHeartbeat;
        private double _lastPulse;

        #region Lifecycle

        private void Init()
        {
            foreach (string perm in AllPermissions)
                permission.RegisterPermission(perm, this);

            LoadConfigValues();
            LoadData();
            PrintStartup();
        }

        private void OnServerInitialized()
        {
            RegisterWithCore();
            StartTimers();
        }

        private void Unload()
        {
            if (_tickTimer != null) _tickTimer.Destroy();
            foreach (BasePlayer player in BasePlayer.activePlayerList.ToArray())
            {
                CuiHelper.DestroyUi(player, UiName);
                CuiHelper.DestroyUi(player, MoveUiName);
                CuiHelper.DestroyUi(player, CardName);
            }
            DisposeAllCachedNotes();
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            // Prepare the map-parent UI once. Because the parent is native "Map", it is not a normal HUD panel;
            // it becomes visible with the Rust map and hides with the Rust map.
            GetSession(player);
            if (_config != null && _config.Ui != null && _config.Ui.Enabled && _config.Ui.Parent == "Map" && CanUseAnyMap(player))
                timer.Once(1f, () => { if (player != null && player.IsConnected) RenderUi(player); });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiName);
            CuiHelper.DestroyUi(player, MoveUiName);
            CuiHelper.DestroyUi(player, CardName);
            DisposeCachedNotes(player.userID);
            _lastUiRender.Remove(player.userID);
            _visibleTargets.Remove(player.userID);
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
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile);
                if (_data == null) _data = new StoredData();
            }
            catch (Exception ex)
            {
                PrintError("MapBrain data read failed. Existing data JSON was NOT overwritten. Error: " + ex.Message);
                _data = new StoredData();
            }
            _data.Normalize();
        }

        private void SaveData()
        {
            if (_data == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
        }

        private void StartTimers()
        {
            if (_tickTimer != null) _tickTimer.Destroy();
            _tickTimer = timer.Every(Mathf.Max(1f, _config.Refresh.TickSeconds), Tick);
        }

        private void Tick()
        {
            if (!_config.Enabled) return;
            if (Interface.Oxide.Now - _lastHeartbeat > _config.Refresh.HeartbeatSeconds)
                Heartbeat();

            if (Interface.Oxide.Now - _lastPulse > _config.MapMemory.PulseSeconds)
            {
                BuildMapPulse();
                _lastPulse = Interface.Oxide.Now;
            }

            PruneMemory();
            PruneMapBoundUi();
            PrepareMapParentUiForActivePlayers();
        }

        #endregion

        #region Commands

        [ChatCommand("wmmap")]
        private void CmdMap(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!CanUseAnyMap(player)) { Reply(player, "No permission."); return; }

            if (args == null || args.Length == 0)
            {
                RenderUi(player);
                SendMapRefresh(player);
                Reply(player, "WorldMind map UI prepared. Open your Rust map to use it. Use /wmmap ui off to hide it.");
                return;
            }

            string sub = args[0].ToLowerInvariant();
            if (sub == "ui") { HandleUiToggleCommand(player, args); return; }
            if (sub == "close") { CuiHelper.DestroyUi(player, UiName); CuiHelper.DestroyUi(player, MoveUiName); CuiHelper.DestroyUi(player, CardName); return; }
            if (sub == "status") { Reply(player, BuildStatus()); return; }
            if (sub == "perms") { Reply(player, BuildPermHelp()); return; }
            if (sub == "help") { Reply(player, BuildHelp()); return; }

            if (sub == "reload")
            {
                if (!IsAdmin(player)) { Reply(player, "Admin permission required."); return; }
                LoadConfigValues(); LoadData(); StartTimers(); Reply(player, "MapBrain reloaded."); return;
            }

            if (sub == "clear")
            {
                if (!IsAdmin(player)) { Reply(player, "Admin permission required."); return; }
                _data.GridMemory.Clear(); _data.WatchPins.Clear(); _data.MapPulse = new MapPulse(); SaveData(); Reply(player, "Map memory and watch pins cleared."); return;
            }

            if (sub == "admin" && args.Length >= 2) { ToggleAdminLayer(player, args[1].ToLowerInvariant()); return; }
            if (sub == "player" && args.Length >= 2) { TogglePlayerLayer(player, args[1].ToLowerInvariant()); return; }
            if (sub == "npc" && args.Length >= 2) { ToggleNpcLayer(player, args[1].ToLowerInvariant()); return; }
            if (sub == "heat" && args.Length >= 2) { SetHeatWindow(player, args[1].ToLowerInvariant()); return; }
            if (sub == "watch") { HandleWatch(player, args.Skip(1).ToArray()); return; }
            if (sub == "grid" && args.Length >= 2) { ShowGrid(player, args[1].ToUpperInvariant()); return; }
            if (sub == "pulse") { Reply(player, BuildPulseText(IsAdmin(player))); return; }
            if (sub == "zones") { Reply(player, BuildZoneList(IsAdmin(player))); return; }
            if (sub == "addzone") { AddZoneCommand(player, args); return; }
            if (sub == "remove") { RemoveZoneCommand(player, args); return; }

            Reply(player, BuildHelp());
        }

        private void HandleUiToggleCommand(BasePlayer player, string[] args)
        {
            PlayerSession session = GetSession(player);
            string mode = args != null && args.Length >= 2 ? args[1].ToLowerInvariant() : "toggle";

            if (mode == "status")
            {
                Reply(player, "Map UI toggle: " + (session.HideUi ? "OFF" : "ON") + ". Last prepared: " + FormatAge(GetLastUiRender(player.userID)) + ". Use /wmmap ui on to hard-restore it.");
                return;
            }

            if (mode == "reset")
            {
                ResetUi(player);
                session = GetSession(player);
                session.HideUi = false;
                session.LastMapPingAt = Interface.Oxide.Now;
                session.LastMapButtonAt = Interface.Oxide.Now;
                SaveData();
                ForceRestoreUi(player);
                Reply(player, "WorldMind map UI reset and restored. Open the Rust map; the sidebar should be prepared automatically.");
                return;
            }

            bool turnOn = mode == "on" || mode == "enable" || mode == "enabled" || mode == "show" || mode == "restore" || mode == "back";
            bool turnOff = mode == "off" || mode == "disable" || mode == "disabled" || mode == "hide";

            if (turnOn)
                session.HideUi = false;
            else if (turnOff)
                session.HideUi = true;
            else
                session.HideUi = !session.HideUi;

            SaveData();

            if (session.HideUi)
            {
                CuiHelper.DestroyUi(player, UiName);
                CuiHelper.DestroyUi(player, CardName);
                Reply(player, "WorldMind map UI disabled for you. Use /wmmap ui on to re-enable it.");
                return;
            }

            ForceRestoreUi(player);
            Reply(player, "WorldMind map UI enabled and restored. Open the Rust map; if it is already open, close and reopen it once.");
        }

        private double GetLastUiRender(ulong userId)
        {
            double last;
            return _lastUiRender.TryGetValue(userId, out last) ? last : -9999;
        }

        private string FormatAge(double timestamp)
        {
            if (timestamp < 0) return "never";
            double age = Interface.Oxide.Now - timestamp;
            if (age < 1) return "just now";
            return Mathf.RoundToInt((float)age) + "s ago";
        }

        private void ForceRestoreUi(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            PlayerSession session = GetSession(player);
            session.HideUi = false;
            session.MapLikelyOpen = true;
            session.LastMapPingAt = Interface.Oxide.Now;
            session.LastMapButtonAt = Interface.Oxide.Now;
            _lastUiRender.Remove(player.userID);
            CuiHelper.DestroyUi(player, UiName);
            CuiHelper.DestroyUi(player, CardName);
            SendMapRefresh(player);
            timer.Once(0.05f, () => { if (player != null && player.IsConnected) RenderUi(player); });
            timer.Once(0.75f, () => { if (player != null && player.IsConnected && !GetSession(player).HideUi) RenderUi(player); });
        }

        [ConsoleCommand("worldmindmapbrainv2.ui")]
        private void CcmdUi(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg == null ? null : arg.Player();
            if (player == null || !CanUseAnyMap(player)) return;
            if (arg.Args == null || arg.Args.Length == 0) { if (IsMapUiAllowedNow(player)) RenderUi(player); else CuiHelper.DestroyUi(player, UiName); return; }

            string action = arg.GetString(0, "").ToLowerInvariant();
            if (action == "toggle" && arg.Args.Length >= 2)
            {
                string key = arg.GetString(1, "").ToLowerInvariant();
                if (key.StartsWith("npc_")) ToggleNpcLayer(player, key.Substring(4));
                else if (IsAdmin(player)) ToggleAdminLayer(player, key);
                else TogglePlayerLayer(player, key);
                RenderUi(player);
                SendMapRefresh(player);
                return;
            }
            if (action == "panel")
            {
                PlayerSession ps = GetSession(player);
                ps.HideUi = !ps.HideUi;
                SaveData();
                if (ps.HideUi) CuiHelper.DestroyUi(player, UiName);
                else if (IsMapUiAllowedNow(player)) RenderUi(player);
                return;
            }
            if (action == "heat" && arg.Args.Length >= 2) { SetHeatWindow(player, arg.GetString(1, "15m")); RenderUi(player); SendMapRefresh(player); return; }
            if (action == "move" && arg.Args.Length >= 2) { MoveUi(player, arg.GetString(1, "")); RenderUi(player); return; }
            if (action == "lock") { GetSession(player).UiLocked = !GetSession(player).UiLocked; SaveData(); RenderUi(player); return; }
            if (action == "save") { GetSession(player).UiLocked = true; SaveData(); RenderUi(player); return; }
            if (action == "reset") { ResetUi(player); RenderUi(player); return; }
            if (action == "close") { PlayerSession ps = GetSession(player); ps.HideUi = true; SaveData(); CuiHelper.DestroyUi(player, UiName); CuiHelper.DestroyUi(player, MoveUiName); CuiHelper.DestroyUi(player, CardName); Reply(player, "WorldMind map UI hidden. Use /wmmap ui on to bring it back."); return; }
            if (action == "refresh") { if (IsMapUiAllowedNow(player)) RenderUi(player); else CuiHelper.DestroyUi(player, UiName); SendMapRefresh(player); return; }
        }

        [ConsoleCommand("wmmap.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.Player() != null && !IsAdmin(arg.Player())) { arg.ReplyWith("No permission."); return; }
            arg.ReplyWith(BuildStatus());
        }

        #endregion

        #region Public API

        private object WorldMindMap_CreatePlayerVisibleZone(string id, Vector3 position, float radius, string color, string label, string category, string tier)
        {
            return CreateZone(id, position, radius, color, label, category, true, false, tier);
        }

        private object WorldMindMap_CreateAdminZone(string id, Vector3 position, float radius, string color, string label, string category)
        {
            return CreateZone(id, position, radius, color, label, category, false, true, "admin");
        }

        private object WorldMindMap_RemoveZone(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            bool removed = _data.ManualZones.Remove(id);
            SaveData();
            return removed;
        }

        private object WorldMindMap_ClearCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return false;
            List<string> ids = _data.ManualZones.Where(x => x.Value != null && Same(x.Value.Category, category)).Select(x => x.Key).ToList();
            foreach (string id in ids) _data.ManualZones.Remove(id);
            SaveData();
            return ids.Count;
        }

        private object WorldMindMap_GetGridMemory(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return null;
            GridMemoryRecord rec;
            return _data.GridMemory.TryGetValue(grid.ToUpperInvariant(), out rec) ? rec : null;
        }

        private object WorldMindMap_GetPulse()
        {
            return _data.MapPulse;
        }

        private object WorldMindMap_GetProof()
        {
            return _data.Proof;
        }

        private object WorldMindMap_AddWatchPin(string ownerId, string id, string type, string target, string label)
        {
            if (string.IsNullOrEmpty(id)) return false;
            _data.WatchPins[id] = new WatchPin
            {
                Id = id,
                OwnerId = ownerId ?? "server",
                Type = type ?? "generic",
                Target = target ?? "",
                Label = label ?? id,
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                Enabled = true
            };
            SaveData();
            return true;
        }

        #endregion

        #region Native Map Events

        private void OnPlayerPingsSend(BasePlayer player, MapNoteList mapNoteList)
        {
            if (player == null || mapNoteList == null || !_config.Enabled) return;
            if (!CanUseAnyMap(player)) return;

            PlayerSession session = GetSession(player);
            session.LastMapPingAt = Interface.Oxide.Now;
            session.MapLikelyOpen = true;
            if (!session.AnyLayerEnabled())
            {
                if (_config.Ui.Enabled && _config.Ui.ShowOnlyWithMap) ThrottledRenderUi(player);
                return;
            }

            List<MapNote> notes = Facepunch.Pool.Get<List<MapNote>>();
            BuildNotesForPlayer(player, session, notes);

            DisposeCachedNotes(player.userID);
            _lastRenderedNotes[player.userID] = notes;
            mapNoteList.notes.AddRange(notes);

            if (_config.Ui.Enabled && _config.Ui.ShowOnlyWithMap)
                ThrottledRenderUi(player);
        }

        // BUTTON.MAP is not available on all Rust/uMod builds.
        // Map UI rendering is driven by OnPlayerPingsSend and the native Map parent instead.

        private object OnMapMarkerAdd(BasePlayer player, MapNote note)
        {
            if (player == null || note == null || !_config.Intel.Enabled) return null;
            if (!CanUseAnyMap(player)) return null;

            PlayerSession session = GetSession(player);
            if (!session.Intel) return null;
            if (IsAdmin(player) && !Has(player, PermAdminIntel)) return null;
            if (!IsAdmin(player) && !HasAny(player, PermPlayerIntel, PermPlayerScout, PermPlayerVip, PermPlayerEvent)) return null;

            IntelTarget target = FindNearestVisibleTarget(player, note.worldPosition, IsAdmin(player));
            if (target == null)
            {
                ShowToast(player, "No WorldMind intel target close enough.", false);
                return null;
            }

            RenderIntelCard(player, target);
            RecordMapInteraction(player, "intel_card_opened", target);
            return _config.Intel.SuppressManualMapMarkerWhenIntelOpens ? (object)false : null;
        }

        #endregion

        #region Note Building

        private void BuildNotesForPlayer(BasePlayer viewer, PlayerSession session, List<MapNote> notes)
        {
            Dictionary<ulong, IntelTarget> visible = new Dictionary<ulong, IntelTarget>();
            bool admin = IsAdmin(viewer);

            if (admin)
            {
                if (session.Players && Has(player: viewer, perm: PermAdminPlayers)) AddPlayerNotes(viewer, notes, visible, false);
                if (session.Sleepers && Has(viewer, PermAdminSleepers)) AddSleeperNotes(viewer, notes, visible);
                if (session.Tc && Has(viewer, PermAdminTc)) AddTcNotes(notes, visible);
                if (session.Stashes && Has(viewer, PermAdminStashes)) AddStashNotes(notes, visible);
                if (session.Bags && Has(viewer, PermAdminBags)) AddBagNotes(notes, visible);
                if (session.Npcs && Has(viewer, PermAdminNpcs)) AddNpcNotes(viewer, session, notes, visible);
                if (session.Raid && Has(viewer, PermAdminRaid)) AddRaidPressureNotes(notes, visible, false);
                if (session.Clusters && Has(viewer, PermAdminClusters)) AddClusterNotes(notes, visible, false);
                if (session.Watch && Has(viewer, PermAdminWatch)) AddWatchNotes(viewer, notes, visible);
            }

            if (session.Heat && CanSeeHeat(viewer, admin)) AddHeatNotes(viewer, session, notes, visible, admin);
            if (session.Zones && CanSeeZones(viewer, admin)) AddZoneNotes(viewer, notes, visible, admin);
            if (!admin && session.Pulse && HasAny(viewer, PermPlayerPulse, PermPlayerVip, PermPlayerEvent)) AddPulseNote(viewer, notes, visible);

            _visibleTargets[viewer.userID] = visible;
            _data.Proof.LastPrivateNoteBuildUtc = DateTime.UtcNow.ToString("o");
            _data.Proof.LastPrivateNoteCount = notes.Count;
            _data.Proof.LastViewer = viewer.displayName;
        }

        private void AddPlayerNotes(BasePlayer viewer, List<MapNote> notes, Dictionary<ulong, IntelTarget> visible, bool publicSafe)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || player == viewer) continue;
                if (Has(player, PermAdminHidden)) continue;
                MapNote note = CreateNote(_config.Icons.PlayerIcon, _config.Icons.PlayerColor, player.transform.position, player.net.ID, "");
                notes.Add(note);
                AddTarget(visible, note, new IntelTarget("player", player.displayName, player, player.transform.position));
            }
        }

        private void AddSleeperNotes(BasePlayer viewer, List<MapNote> notes, Dictionary<ulong, IntelTarget> visible)
        {
            foreach (BasePlayer player in BasePlayer.sleepingPlayerList)
            {
                if (player == null) continue;
                if (Has(player, PermAdminHidden)) continue;
                MapNote note = CreateNote(_config.Icons.SleeperIcon, _config.Icons.SleeperColor, player.transform.position, player.net.ID, "");
                notes.Add(note);
                AddTarget(visible, note, new IntelTarget("sleeper", player.displayName, player, player.transform.position));
            }
        }

        private void AddTcNotes(List<MapNote> notes, Dictionary<ulong, IntelTarget> visible)
        {
            foreach (BuildingPrivlidge tc in BaseNetworkable.serverEntities.OfType<BuildingPrivlidge>())
            {
                if (tc == null || tc.IsDestroyed) continue;
                MapNote note = CreateNote(_config.Icons.TcIcon, Mathf.Clamp(tc.authorizedPlayers.Count - 1, 0, 5), tc.transform.position, tc.net.ID, "");
                notes.Add(note);
                AddTarget(visible, note, new IntelTarget("tool cupboard", "Tool Cupboard", tc, tc.transform.position));
            }
            foreach (SimplePrivilege priv in BaseNetworkable.serverEntities.OfType<SimplePrivilege>())
            {
                if (priv == null || priv.IsDestroyed) continue;
                MapNote note = CreateNote(_config.Icons.SimplePrivilegeIcon, _config.Icons.TcColor, priv.transform.position, priv.net.ID, "");
                notes.Add(note);
                AddTarget(visible, note, new IntelTarget("privilege", "Privilege", priv, priv.transform.position));
            }
        }

        private void AddStashNotes(List<MapNote> notes, Dictionary<ulong, IntelTarget> visible)
        {
            foreach (StashContainer stash in BaseNetworkable.serverEntities.OfType<StashContainer>())
            {
                if (stash == null || stash.IsDestroyed || stash.inventory == null || stash.inventory.itemList.Count == 0) continue;
                MapNote note = CreateNote(_config.Icons.StashIcon, Mathf.Clamp(stash.inventory.itemList.Count - 1, 0, 5), stash.transform.position, stash.net.ID, "");
                notes.Add(note);
                AddTarget(visible, note, new IntelTarget("stash", "Stash", stash, stash.transform.position));
            }
        }

        private void AddBagNotes(List<MapNote> notes, Dictionary<ulong, IntelTarget> visible)
        {
            foreach (SleepingBag bag in BaseNetworkable.serverEntities.OfType<SleepingBag>())
            {
                if (bag == null || bag.IsDestroyed) continue;
                MapNote note = CreateNote(_config.Icons.BagIcon, _config.Icons.BagColor, bag.transform.position, bag.net.ID, "");
                notes.Add(note);
                AddTarget(visible, note, new IntelTarget("bag", "Sleeping Bag", bag, bag.transform.position));
            }
        }

        private void AddNpcNotes(BasePlayer viewer, PlayerSession session, List<MapNote> notes, Dictionary<ulong, IntelTarget> visible)
        {
            foreach (BaseEntity entity in BaseNetworkable.serverEntities.OfType<BaseEntity>())
            {
                if (entity == null || entity.IsDestroyed) continue;
                NpcKind kind = ClassifyNpc(entity);
                if (kind == NpcKind.None) continue;

                if (kind == NpcKind.SafeZone && (!session.NpcSafezone || !Has(viewer, PermAdminNpcsSafezone))) continue;
                if (kind == NpcKind.Hostile && (!session.NpcHostile || !Has(viewer, PermAdminNpcsHostile))) continue;
                if (kind == NpcKind.Animal && (!session.NpcAnimals || !Has(viewer, PermAdminAnimals))) continue;
                if (kind == NpcKind.Scientist && (!session.NpcScientists || !Has(viewer, PermAdminScientists))) continue;
                if (kind == NpcKind.Boss && (!session.NpcBosses || !Has(viewer, PermAdminBosses))) continue;

                int icon = _config.Icons.NpcIcon;
                int color = _config.Icons.NpcColor;
                string label = "";
                if (kind == NpcKind.SafeZone) { icon = _config.Icons.SafeZoneNpcIcon; color = _config.Icons.SafeZoneNpcColor; }
                else if (kind == NpcKind.Animal) { icon = _config.Icons.AnimalIcon; color = _config.Icons.AnimalColor; }
                else if (kind == NpcKind.Scientist) { icon = _config.Icons.ScientistIcon; color = _config.Icons.ScientistColor; }
                else if (kind == NpcKind.Boss) { icon = _config.Icons.BossIcon; color = _config.Icons.BossColor; }

                MapNote note = CreateNote(icon, color, entity.transform.position, entity.net.ID, label);
                notes.Add(note);
                AddTarget(visible, note, new IntelTarget("npc:" + kind.ToString().ToLowerInvariant(), CleanPrefab(entity), entity, entity.transform.position));
            }
        }

        private void AddHeatNotes(BasePlayer viewer, PlayerSession session, List<MapNote> notes, Dictionary<ulong, IntelTarget> visible, bool admin)
        {
            List<GridMemoryRecord> grids = _data.GridMemory.Values.OrderByDescending(x => GetWindowScore(x, session.HeatWindow)).Take(_config.Heat.MaxVisible).ToList();
            foreach (GridMemoryRecord grid in grids)
            {
                int score = GetWindowScore(grid, session.HeatWindow);
                if (score < (admin ? _config.Heat.AdminMinimumScore : _config.Heat.PlayerMinimumScore)) continue;
                Vector3 pos = GridToWorld(grid.Grid);
                string label = admin ? "" : VagueHeatLabel(grid, score);
                MapNote note = CreateNote(_config.Icons.HeatIcon, HeatColor(score), pos, default(NetworkableId), label);
                notes.Add(note);
                AddTarget(visible, note, IntelTarget.GridHeat(grid, pos, session.HeatWindow));
            }
        }

        private void AddRaidPressureNotes(List<MapNote> notes, Dictionary<ulong, IntelTarget> visible, bool publicSafe)
        {
            foreach (GridMemoryRecord grid in _data.GridMemory.Values.OrderByDescending(x => x.RaidPressure).Take(_config.Raid.MaxVisibleRaidGrids))
            {
                if (grid.RaidPressure < _config.Raid.MinimumRaidPressure) continue;
                Vector3 pos = GridToWorld(grid.Grid);
                MapNote note = CreateNote(_config.Icons.RaidIcon, _config.Icons.RaidColor, pos, default(NetworkableId), "");
                notes.Add(note);
                AddTarget(visible, note, IntelTarget.GridRaid(grid, pos));
            }
        }

        private void AddClusterNotes(List<MapNote> notes, Dictionary<ulong, IntelTarget> visible, bool publicSafe)
        {
            foreach (ClusterRecord cluster in BuildClusters().Take(_config.Clusters.MaxVisibleClusters))
            {
                MapNote note = CreateNote(_config.Icons.ClusterIcon, _config.Icons.ClusterColor, cluster.Position, default(NetworkableId), "");
                notes.Add(note);
                AddTarget(visible, note, IntelTarget.CreateCluster(cluster));
            }
        }

        private void AddZoneNotes(BasePlayer viewer, List<MapNote> notes, Dictionary<ulong, IntelTarget> visible, bool admin)
        {
            foreach (ManualZone zone in _data.ManualZones.Values)
            {
                if (zone == null || !zone.Enabled) continue;
                if (!admin && !zone.PlayerVisible) continue;
                if (!admin && !CanSeeTier(viewer, zone.PlayerTier)) continue;
                Vector3 pos = zone.Position();
                MapNote note = CreateNote(_config.Icons.ZoneIcon, _config.Icons.ZoneColor, pos, default(NetworkableId), _config.Labels.ShowPublicZoneLabels ? Truncate(zone.Label, _config.Labels.MaxPublicLabelChars) : "");
                notes.Add(note);
                AddTarget(visible, note, IntelTarget.CreateZone(zone));
            }
        }

        private void AddWatchNotes(BasePlayer viewer, List<MapNote> notes, Dictionary<ulong, IntelTarget> visible)
        {
            foreach (WatchPin pin in _data.WatchPins.Values)
            {
                if (pin == null || !pin.Enabled) continue;
                if (!string.IsNullOrEmpty(pin.OwnerId) && pin.OwnerId != "server" && pin.OwnerId != viewer.UserIDString) continue;
                Vector3 pos;
                if (!TryResolveWatchPosition(pin, out pos)) continue;
                MapNote note = CreateNote(_config.Icons.WatchIcon, _config.Icons.WatchColor, pos, default(NetworkableId), "");
                notes.Add(note);
                AddTarget(visible, note, IntelTarget.Watch(pin, pos));
            }
        }

        private void AddPulseNote(BasePlayer viewer, List<MapNote> notes, Dictionary<ulong, IntelTarget> visible)
        {
            if (_data.MapPulse == null || string.IsNullOrEmpty(_data.MapPulse.HottestGrid)) return;
            Vector3 pos = GridToWorld(_data.MapPulse.HottestGrid);
            MapNote note = CreateNote(_config.Icons.PulseIcon, _config.Icons.PulseColor, pos, default(NetworkableId), _config.MapPulse.PlayerPulseLabel);
            notes.Add(note);
            AddTarget(visible, note, IntelTarget.CreatePulse(_data.MapPulse, pos));
        }

        private MapNote CreateNote(int icon, int color, Vector3 position, NetworkableId associatedId, string label)
        {
            MapNote note = Facepunch.Pool.Get<MapNote>();
            note.noteType = 1;
            note.isPing = true;
            note.icon = icon;
            note.colourIndex = Mathf.Clamp(color, 0, 5);
            note.worldPosition = position;
            note.associatedId = associatedId;
            note.label = label ?? "";
            note.totalDuration = 0f;
            return note;
        }

        private void AddTarget(Dictionary<ulong, IntelTarget> visible, MapNote note, IntelTarget target)
        {
            if (note == null || target == null) return;
            ulong key = note.associatedId.IsValid ? note.associatedId.Value : HashPosition(target.Position, target.Type, target.Label);
            visible[key] = target;
            target.NoteKey = key;
        }

        #endregion

        #region Intel Cards

        private IntelTarget FindNearestVisibleTarget(BasePlayer viewer, Vector3 mapClick, bool admin)
        {
            Dictionary<ulong, IntelTarget> visible;
            if (!_visibleTargets.TryGetValue(viewer.userID, out visible) || visible == null || visible.Count == 0)
                return null;

            float max = admin ? _config.Intel.AdminInspectDistance : _config.Intel.PlayerInspectDistance;
            IntelTarget best = null;
            float bestDist = float.MaxValue;
            foreach (IntelTarget target in visible.Values)
            {
                if (target == null) continue;
                float d = Vector2.Distance(target.Position.XZ2D(), mapClick.XZ2D());
                if (d < bestDist)
                {
                    best = target;
                    bestDist = d;
                }
            }
            if (best == null || bestDist > max) return null;
            best.DistanceFromClick = bestDist;
            return best;
        }

        private void RenderIntelCard(BasePlayer viewer, IntelTarget target)
        {
            CuiHelper.DestroyUi(viewer, CardName);
            bool admin = IsAdmin(viewer);
            IntelCardData data = BuildIntelCardData(viewer, target, admin);

            CuiElementContainer c = new CuiElementContainer();
            string panel = c.Add(new CuiPanel
            {
                Image = { Color = _config.Ui.CardBackgroundColor },
                RectTransform = { AnchorMin = _config.Ui.CardAnchorMin, AnchorMax = _config.Ui.CardAnchorMax, OffsetMin = _config.Ui.CardOffsetMin, OffsetMax = _config.Ui.CardOffsetMax },
                CursorEnabled = true
            }, "Map", CardName);

            AddText(c, panel, data.Title, 13, TextAnchor.MiddleLeft, "0.04 0.82", "0.88 0.98", _config.Ui.TitleColor, true);
            AddButton(c, panel, "X", "worldmindmapbrainv2.ui close", "0.89 0.84", "0.98 0.98", _config.Ui.DangerButtonColor, 12);

            float y = 0.76f;
            foreach (string line in data.Lines.Take(_config.Intel.MaxCardLines))
            {
                AddText(c, panel, line, 10, TextAnchor.MiddleLeft, "0.04 " + (y - 0.06f).ToString("0.00"), "0.96 " + y.ToString("0.00"), _config.Ui.BodyColor, false);
                y -= 0.065f;
            }

            if (_config.Intel.UseWorldMindSummaries)
            {
                AddText(c, panel, "WorldMind: " + Truncate(data.Summary, _config.Intel.MaxSummaryChars), 10, TextAnchor.UpperLeft, "0.04 0.05", "0.96 0.23", _config.Ui.AccentColor, false);
                TryAskWorldMindForIntel(viewer, target, data);
            }
            else
            {
                AddText(c, panel, Truncate(data.Summary, _config.Intel.MaxSummaryChars), 10, TextAnchor.UpperLeft, "0.04 0.05", "0.96 0.23", _config.Ui.AccentColor, false);
            }

            CuiHelper.AddUi(viewer, c);
        }

        private IntelCardData BuildIntelCardData(BasePlayer viewer, IntelTarget target, bool admin)
        {
            IntelCardData data = new IntelCardData();
            data.Title = (admin ? "WORLD MIND ADMIN INTEL" : "WORLD MIND ISLAND INTEL");
            data.Lines = new List<string>();
            data.Summary = "No model summary yet. Local island facts loaded.";

            string grid = GetGrid(target.Position);
            data.Lines.Add("Target: " + target.Label);
            data.Lines.Add("Type: " + target.Type);
            data.Lines.Add("Grid: " + grid + " | Click distance: " + Mathf.RoundToInt(target.DistanceFromClick) + "m");

            GridMemoryRecord memory = GetGridMemory(grid);
            if (memory != null)
            {
                data.Lines.Add("Grid heat: 5m " + memory.Heat5m + " / 15m " + memory.Heat15m + " / 1h " + memory.Heat1h + " / 24h " + memory.Heat24h);
                if (admin) data.Lines.Add("Grid risk: raid " + memory.RaidPressure + " / combat " + memory.CombatEvents + " / deaths " + memory.DeathEvents);
            }

            if (!admin)
            {
                if (target.Type.StartsWith("heat") || target.Type.StartsWith("zone") || target.Type == "pulse")
                {
                    data.Lines.Add("Read: " + VaguePlayerRead(target, memory));
                    data.Summary = VaguePlayerRead(target, memory);
                }
                return data;
            }

            BaseEntity entity = target.Entity;
            BasePlayer player = entity as BasePlayer;
            if (player != null)
            {
                data.Lines.Add("SteamID: " + player.UserIDString);
                data.Lines.Add("State: " + (player.IsSleeping() ? "sleeping" : player.IsAlive() ? "alive" : "dead") + " | Team: " + player.currentTeam);
                data.Lines.Add("Health: " + Mathf.RoundToInt(player.health) + " | Held: " + HeldItemName(player));
                object signal = CallPlugin(WorldMindSignalBrainV2, "WorldMindSignal_GetPlayerState", player.UserIDString);
                if (signal != null) data.Lines.Add("Signal: " + Truncate(JsonConvert.SerializeObject(signal), 90));
                data.Summary = player.displayName + " is at " + grid + ". Current local facts are loaded for admin review.";
                return data;
            }

            BuildingPrivlidge tc = entity as BuildingPrivlidge;
            if (tc != null)
            {
                data.Lines.Add("Authorized: " + tc.authorizedPlayers.Count);
                data.Lines.Add("Upkeep: " + BuildingPrivlidge.FormatUpkeepMinutes(tc.GetProtectedMinutes()));
                data.Lines.Add("Nearby bags: " + CountEntitiesNear<SleepingBag>(tc.transform.position, _config.Intel.NearbyRadius));
                data.Lines.Add("Nearby stashes: " + CountEntitiesNear<StashContainer>(tc.transform.position, _config.Intel.NearbyRadius));
                data.Summary = "Base control marker at " + grid + ". Ownership and nearby support entities are visible to admin.";
                return data;
            }

            SimplePrivilege simple = entity as SimplePrivilege;
            if (simple != null)
            {
                data.Lines.Add("Authorized: " + simple.authorizedPlayers.Count);
                data.Summary = "Simple privilege marker at " + grid + ".";
                return data;
            }

            StashContainer stash = entity as StashContainer;
            if (stash != null)
            {
                data.Lines.Add("OwnerID: " + stash.OwnerID);
                data.Lines.Add("Items: " + (stash.inventory == null ? 0 : stash.inventory.itemList.Count));
                data.Summary = "Stash signal at " + grid + ". Item count and owner ID visible to admin only.";
                return data;
            }

            SleepingBag bag = entity as SleepingBag;
            if (bag != null)
            {
                data.Lines.Add("Deployer: " + bag.deployerUserID);
                data.Lines.Add("Bag name: " + Safe(bag.niceName));
                data.Summary = "Respawn point at " + grid + ".";
                return data;
            }

            if (entity != null)
            {
                BaseCombatEntity combat = entity as BaseCombatEntity;
                data.Lines.Add("Prefab: " + CleanPrefab(entity));
                if (combat != null) data.Lines.Add("Health: " + Mathf.RoundToInt(combat.health) + " / " + Mathf.RoundToInt(combat.MaxHealth()));
                data.Lines.Add("OwnerID: " + entity.OwnerID);
                data.Summary = "Entity at " + grid + ": " + CleanPrefab(entity);
                return data;
            }

            if (target.GridMemory != null)
            {
                data.Lines.Add("Window: " + target.Window + " | Score: " + GetWindowScore(target.GridMemory, target.Window));
                data.Lines.Add("Combat: " + target.GridMemory.CombatEvents + " | Deaths: " + target.GridMemory.DeathEvents + " | Loot: " + target.GridMemory.LootEvents);
                data.Lines.Add("Raid pressure: " + target.GridMemory.RaidPressure + " | Last: " + Safe(target.GridMemory.LastReason));
                data.Summary = "Grid " + target.GridMemory.Grid + " has live WorldMind map memory.";
            }

            if (target.Zone != null)
            {
                data.Lines.Add("Category: " + target.Zone.Category + " | Tier: " + target.Zone.PlayerTier);
                data.Lines.Add("Public: " + target.Zone.PlayerVisible + " | Radius: " + Mathf.RoundToInt(target.Zone.Radius));
                data.Summary = target.Zone.Label + " is a configured WorldMind map zone.";
            }

            if (target.Cluster != null)
            {
                data.Lines.Add("Cluster type: " + target.Cluster.Type);
                data.Lines.Add("Count: " + target.Cluster.Count + " | Grid: " + target.Cluster.Grid);
                data.Summary = "Cluster detected by MapBrain: " + target.Cluster.Type + " x" + target.Cluster.Count + ".";
            }

            return data;
        }

        private void TryAskWorldMindForIntel(BasePlayer viewer, IntelTarget target, IntelCardData data)
        {
            if (WorldMindV2 == null || target == null) return;
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["Plugin"] = Name;
            request["EventType"] = "map_intel_card";
            request["PlayerId"] = viewer.UserIDString;
            request["PlayerName"] = viewer.displayName;
            request["Tone"] = _config.Intel.WorldMindTone;
            request["Urgency"] = 1;
            request["Truth"] = new Dictionary<string, object>
            {
                ["viewerIsAdmin"] = IsAdmin(viewer),
                ["targetType"] = target.Type,
                ["targetLabel"] = target.Label,
                ["grid"] = GetGrid(target.Position),
                ["safeForPlayers"] = !IsAdmin(viewer),
                ["localLines"] = data.Lines
            };
            try
            {
                _data.Proof.LastWorldMindCallUtc = DateTime.UtcNow.ToString("o");
                WorldMindV2.Call("WorldMind_AskText", request, new Action<string>(text =>
                {
                    if (viewer == null || !viewer.IsConnected || string.IsNullOrEmpty(text)) return;
                    _data.Proof.LastWorldMindSuccessUtc = DateTime.UtcNow.ToString("o");
                    data.Summary = Truncate(text, _config.Intel.MaxSummaryChars);
                    RenderIntelCardNoAsk(viewer, target, data);
                }));
            }
            catch (Exception ex)
            {
                SetError("WorldMind map intel call failed: " + ex.Message);
            }
        }

        private void RenderIntelCardNoAsk(BasePlayer viewer, IntelTarget target, IntelCardData data)
        {
            if (!IsMapUiAllowedNow(viewer))
            {
                CuiHelper.DestroyUi(viewer, CardName);
                return;
            }
            bool old = _config.Intel.UseWorldMindSummaries;
            _config.Intel.UseWorldMindSummaries = false;
            try
            {
                CuiHelper.DestroyUi(viewer, CardName);
                CuiElementContainer c = new CuiElementContainer();
                string panel = c.Add(new CuiPanel
                {
                    Image = { Color = _config.Ui.CardBackgroundColor },
                    RectTransform = { AnchorMin = _config.Ui.CardAnchorMin, AnchorMax = _config.Ui.CardAnchorMax, OffsetMin = _config.Ui.CardOffsetMin, OffsetMax = _config.Ui.CardOffsetMax },
                    CursorEnabled = true
                }, "Map", CardName);
                AddText(c, panel, data.Title, 13, TextAnchor.MiddleLeft, "0.04 0.82", "0.88 0.98", _config.Ui.TitleColor, true);
                AddButton(c, panel, "X", "worldmindmapbrainv2.ui close", "0.89 0.84", "0.98 0.98", _config.Ui.DangerButtonColor, 12);
                float y = 0.76f;
                foreach (string line in data.Lines.Take(_config.Intel.MaxCardLines))
                {
                    AddText(c, panel, line, 10, TextAnchor.MiddleLeft, "0.04 " + (y - 0.06f).ToString("0.00"), "0.96 " + y.ToString("0.00"), _config.Ui.BodyColor, false);
                    y -= 0.065f;
                }
                AddText(c, panel, "WorldMind: " + Truncate(data.Summary, _config.Intel.MaxSummaryChars), 10, TextAnchor.UpperLeft, "0.04 0.05", "0.96 0.23", _config.Ui.AccentColor, false);
                CuiHelper.AddUi(viewer, c);
            }
            finally { _config.Intel.UseWorldMindSummaries = old; }
        }

        #endregion

        #region Map Memory And Signals

        private void BuildMapPulse()
        {
            ImportSignalBrainEvents();
            MapPulse pulse = new MapPulse();
            pulse.GeneratedUtc = DateTime.UtcNow.ToString("o");
            List<GridMemoryRecord> hot = _data.GridMemory.Values.OrderByDescending(x => x.Heat15m).Take(5).ToList();
            if (hot.Count > 0)
            {
                pulse.HottestGrid = hot[0].Grid;
                pulse.HottestScore = hot[0].Heat15m;
                pulse.AdminSummary = "Hottest grid: " + hot[0].Grid + " | Heat: " + hot[0].Heat15m + " | Raid: " + hot[0].RaidPressure + " | Deaths: " + hot[0].DeathEvents;
                pulse.PlayerSummary = VagueHeatLabel(hot[0], hot[0].Heat15m);
            }
            pulse.TopGrids = hot.Select(x => x.Grid + ":" + x.Heat15m).ToList();
            pulse.RaidPressureGrids = _data.GridMemory.Values.Where(x => x.RaidPressure >= _config.Raid.MinimumRaidPressure).OrderByDescending(x => x.RaidPressure).Take(5).Select(x => x.Grid + ":" + x.RaidPressure).ToList();
            _data.MapPulse = pulse;
            _data.Proof.LastPulseUtc = pulse.GeneratedUtc;
            SaveData();
        }

        private void ImportSignalBrainEvents()
        {
            if (WorldMindSignalBrainV2 == null) return;
            object raw = null;
            try { raw = WorldMindSignalBrainV2.Call("WorldMindSignal_GetRecentEvents", _config.MapMemory.SignalEventsToRead); }
            catch (Exception ex) { SetError("SignalBrain event import failed: " + ex.Message); return; }
            if (raw == null) return;
            List<WorldMapSignal> events = ConvertSignals(raw);
            foreach (WorldMapSignal ev in events) RememberSignal(ev);
        }

        private void RememberSignal(WorldMapSignal ev)
        {
            if (ev == null || string.IsNullOrEmpty(ev.Grid) || ev.Grid == "unknown") return;
            string grid = ev.Grid.ToUpperInvariant();
            string bucket = grid + ":" + ev.EventType + ":" + ev.PlayerId + ":" + ev.TargetId;
            WorldMapSignal last;
            if (_lastSignalBucket.TryGetValue(bucket, out last) && Interface.Oxide.Now - last.RuntimeSeen < _config.MapMemory.DedupeSeconds) return;
            ev.RuntimeSeen = Interface.Oxide.Now;
            _lastSignalBucket[bucket] = ev;

            GridMemoryRecord rec = GetOrCreateGridMemory(grid);
            int points = PointsForSignal(ev);
            rec.Heat5m += points;
            rec.Heat15m += points;
            rec.Heat1h += points;
            rec.Heat24h += points;
            rec.HeatWipe += points;
            rec.TotalEvents++;
            rec.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
            rec.LastReason = ev.EventType + " / " + ev.Severity;
            rec.LastCategory = ev.Category;

            string cat = (ev.Category ?? "").ToLowerInvariant();
            string type = (ev.EventType ?? "").ToLowerInvariant();
            if (cat.Contains("combat") || type.Contains("damage") || type.Contains("kill")) rec.CombatEvents++;
            if (cat.Contains("death") || type.Contains("death")) rec.DeathEvents++;
            if (cat.Contains("loot") || type.Contains("loot")) rec.LootEvents++;
            if (cat.Contains("gather") || type.Contains("gather")) rec.GatherEvents++;
            if (cat.Contains("build") || type.Contains("build")) rec.BuildEvents++;
            if (type.Contains("raid") || type.Contains("explosive") || type.Contains("structure") || type.Contains("wall") || type.Contains("door")) rec.RaidPressure += Math.Max(1, points);

            rec.RecentReasons.Add(DateTime.UtcNow.ToString("HH:mm") + " " + rec.LastReason);
            while (rec.RecentReasons.Count > _config.MapMemory.MaxReasonsPerGrid) rec.RecentReasons.RemoveAt(0);
            _data.Proof.EventsRemembered++;
        }

        private List<WorldMapSignal> ConvertSignals(object raw)
        {
            try
            {
                string json = JsonConvert.SerializeObject(raw);
                List<WorldMapSignal> list = JsonConvert.DeserializeObject<List<WorldMapSignal>>(json);
                return list ?? new List<WorldMapSignal>();
            }
            catch { return new List<WorldMapSignal>(); }
        }

        private GridMemoryRecord GetOrCreateGridMemory(string grid)
        {
            GridMemoryRecord rec;
            if (!_data.GridMemory.TryGetValue(grid, out rec) || rec == null)
            {
                rec = new GridMemoryRecord { Grid = grid, CreatedUtc = DateTime.UtcNow.ToString("o") };
                _data.GridMemory[grid] = rec;
            }
            return rec;
        }

        private GridMemoryRecord GetGridMemory(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return null;
            GridMemoryRecord rec;
            return _data.GridMemory.TryGetValue(grid.ToUpperInvariant(), out rec) ? rec : null;
        }

        private void PruneMemory()
        {
            if (_data == null || _data.GridMemory == null) return;
            foreach (GridMemoryRecord rec in _data.GridMemory.Values)
            {
                rec.Heat5m = Mathf.Max(0, rec.Heat5m - _config.MapMemory.Decay5mPerTick);
                rec.Heat15m = Mathf.Max(0, rec.Heat15m - _config.MapMemory.Decay15mPerTick);
                rec.Heat1h = Mathf.Max(0, rec.Heat1h - _config.MapMemory.Decay1hPerTick);
                rec.Heat24h = Mathf.Max(0, rec.Heat24h - _config.MapMemory.Decay24hPerTick);
                rec.RaidPressure = Mathf.Max(0, rec.RaidPressure - _config.MapMemory.RaidDecayPerTick);
            }
            SaveData();
        }

        private int PointsForSignal(WorldMapSignal ev)
        {
            int p = 1;
            string sev = (ev.Severity ?? "").ToLowerInvariant();
            if (sev.Contains("critical")) p += 10;
            else if (sev.Contains("danger")) p += 6;
            else if (sev.Contains("memory")) p += 5;
            else if (sev.Contains("interesting")) p += 3;
            string cat = (ev.Category ?? "").ToLowerInvariant();
            if (cat.Contains("death")) p += 5;
            if (cat.Contains("combat")) p += 4;
            if (cat.Contains("raid")) p += 8;
            if (cat.Contains("loot")) p += 2;
            return Mathf.Clamp(p, 1, 25);
        }

        private bool IsMapUiAllowedNow(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !CanUseAnyMap(player)) return false;
            if (_config == null || _config.Ui == null || !_config.Ui.Enabled) return false;
            PlayerSession session = GetSession(player);
            if (session.HideUi) return false;

            // If the UI is parented to Rust's native Map panel, render it once and let Rust handle visibility.
            // This matches the reliable native-map pattern: the panel is not visible on the HUD, only when Map is open.
            if (string.Equals(_config.Ui.Parent, "Map", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!_config.Ui.ShowOnlyWithMap) return true;

            double now = Interface.Oxide.Now;
            double newestMapSignal = Math.Max(session.LastMapPingAt, session.LastMapButtonAt);
            double age = now - newestMapSignal;
            return age >= 0 && age <= Mathf.Max(0.5f, _config.Ui.MapUiLifetimeAfterPingSeconds);
        }

        private void PruneMapBoundUi()
        {
            if (_config == null || _config.Ui == null || !_config.Ui.ShowOnlyWithMap) return;
            if (string.Equals(_config.Ui.Parent, "Map", StringComparison.OrdinalIgnoreCase)) return;

            double now = Interface.Oxide.Now;
            double lifetime = Mathf.Max(0.5f, _config.Ui.MapUiLifetimeAfterPingSeconds);

            foreach (BasePlayer player in BasePlayer.activePlayerList.ToArray())
            {
                if (player == null || !player.IsConnected) continue;
                PlayerSession session = GetSession(player);
                double newestMapSignal = Math.Max(session.LastMapPingAt, session.LastMapButtonAt);
                if (now - newestMapSignal > lifetime)
                {
                    session.MapLikelyOpen = false;
                    CuiHelper.DestroyUi(player, UiName);
                    CuiHelper.DestroyUi(player, CardName);
                }
            }
        }

        private void PrepareMapParentUiForActivePlayers()
        {
            if (_config == null || _config.Ui == null || !_config.Ui.Enabled) return;
            if (!string.Equals(_config.Ui.Parent, "Map", StringComparison.OrdinalIgnoreCase)) return;

            double now = Interface.Oxide.Now;
            double interval = Mathf.Max(2f, _config.Ui.MapParentPrepareIntervalSeconds);

            foreach (BasePlayer player in BasePlayer.activePlayerList.ToArray())
            {
                if (player == null || !player.IsConnected || !CanUseAnyMap(player)) continue;
                PlayerSession session = GetSession(player);
                if (session.HideUi) continue;

                double last;
                if (_lastUiRender.TryGetValue(player.userID, out last) && now - last < interval) continue;
                _lastUiRender[player.userID] = now;
                RenderUi(player);
            }
        }

        #endregion

        #region UI

        private void ThrottledRenderUi(BasePlayer player)
        {
            double now = Interface.Oxide.Now;
            double last;
            if (_lastUiRender.TryGetValue(player.userID, out last) && now - last < _config.Ui.RenderThrottleSeconds) return;
            _lastUiRender[player.userID] = now;
            RenderUi(player);
        }

        private void RenderUi(BasePlayer player)
        {
            if (player == null || !CanUseAnyMap(player) || !_config.Ui.Enabled) return;
            if (!IsMapUiAllowedNow(player))
            {
                CuiHelper.DestroyUi(player, UiName);
                return;
            }

            PlayerSession s = GetSession(player);
            NormalizeUiFrame(s);
            CuiHelper.DestroyUi(player, UiName);
            CuiHelper.DestroyUi(player, MoveUiName);

            bool admin = IsAdmin(player);
            List<RailButtonSpec> buttons = BuildRailButtons(player, s, admin);

            float buttonSize = Mathf.Clamp(_config.Ui.RailButtonPixelSize, 22f, 40f);
            float gap = Mathf.Clamp(_config.Ui.RailButtonGapPixels, 2f, 14f);
            float titleHeight = 38f;
            float topPad = 10f;
            float bottomPad = 14f;
            float railWidth = Mathf.Max(46f, buttonSize + 20f);
            float railHeight = topPad + titleHeight + (buttons.Count * buttonSize) + (Mathf.Max(0, buttons.Count - 1) * gap) + bottomPad;
            float halfHeight = railHeight * 0.5f;

            // Move the whole WorldMind map rail down slightly so the title and first buttons do not clip off the top.
            float railVerticalOffset = -70f;
            string offsetMin = ShiftOffset("4 " + (-halfHeight + railVerticalOffset).ToString("0"), s.OffsetX, s.OffsetY);
            string offsetMax = ShiftOffset((4f + railWidth).ToString("0") + " " + (halfHeight + railVerticalOffset).ToString("0"), s.OffsetX, s.OffsetY);

            CuiElementContainer c = new CuiElementContainer();
            string parent = string.IsNullOrEmpty(_config.Ui.Parent) ? "Map" : _config.Ui.Parent;
            string rail = c.Add(new CuiPanel
            {
                Image = { Color = _config.Ui.BackgroundColor, Sprite = "assets/content/ui/ui.background.rounded.png", ImageType = Image.Type.Tiled },
                RectTransform = { AnchorMin = _config.Ui.DefaultAnchorMin, AnchorMax = _config.Ui.DefaultAnchorMax, OffsetMin = offsetMin, OffsetMax = offsetMax },
                CursorEnabled = true
            }, parent, UiName);

            AddText(c, rail, "WM", 10, TextAnchor.MiddleCenter, "0 1", "1 1", _config.Ui.TitleColor, true, "0 -" + titleHeight.ToString("0"), "0 0");

            for (int i = 0; i < buttons.Count; i++)
            {
                RailButtonSpec b = buttons[i];
                float centerY = -(topPad + titleHeight + (buttonSize * 0.5f) + (i * (buttonSize + gap)));
                AddMapRailButton(c, rail, b.Label, b.Sprite, b.Active, b.Command, i, centerY);
            }

            AddMapUiUpdater(c, rail);
            CuiHelper.AddUi(player, c);

            if (!s.UiLocked && Has(player, PermAdminMoveUi))
                RenderMoveControls(player, s, halfHeight, railWidth);
        }

        private void RenderMoveControls(BasePlayer player, PlayerSession s, float railHalfHeight, float railWidth)
        {
            if (player == null || !player.IsConnected || s == null) return;

            string parent = string.IsNullOrEmpty(_config.Ui.Parent) ? "Map" : _config.Ui.Parent;
            CuiHelper.DestroyUi(player, MoveUiName);

            // Centered move panel. It is intentionally independent from the rail offset so movement
            // controls always open in the middle of the map screen.
            CuiElementContainer c = new CuiElementContainer();
            string panel = c.Add(new CuiPanel
            {
                Image = { Color = _config.Ui.BackgroundColor, Sprite = "assets/content/ui/ui.background.rounded.png", ImageType = Image.Type.Tiled },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-90 -78", OffsetMax = "90 78" },
                CursorEnabled = true
            }, parent, MoveUiName);

            AddText(c, panel, "MOVE MAP RAIL", 10, TextAnchor.MiddleCenter, "0.05 0.80", "0.95 0.98", _config.Ui.TitleColor, true);

            AddButton(c, panel, "UP", "worldmindmapbrainv2.ui move up", "0.36 0.59", "0.64 0.77", _config.Ui.ButtonColor, 9);
            AddButton(c, panel, "LFT", "worldmindmapbrainv2.ui move left", "0.08 0.39", "0.36 0.57", _config.Ui.ButtonColor, 9);
            AddButton(c, panel, "RGT", "worldmindmapbrainv2.ui move right", "0.64 0.39", "0.92 0.57", _config.Ui.ButtonColor, 9);
            AddButton(c, panel, "DWN", "worldmindmapbrainv2.ui move down", "0.36 0.19", "0.64 0.37", _config.Ui.ButtonColor, 9);

            AddButton(c, panel, "SAV", "worldmindmapbrainv2.ui save", "0.08 0.05", "0.34 0.16", _config.Ui.OnButtonColor, 8);
            AddButton(c, panel, "RST", "worldmindmapbrainv2.ui reset", "0.37 0.05", "0.63 0.16", _config.Ui.OffButtonColor, 8);
            AddButton(c, panel, "LCK", "worldmindmapbrainv2.ui lock", "0.66 0.05", "0.92 0.16", _config.Ui.ButtonColor, 8);

            CuiHelper.AddUi(player, c);
        }

        private class RailButtonSpec
        {
            public string Label;
            public string Sprite;
            public bool Active;
            public string Command;

            public RailButtonSpec(string label, string sprite, bool active, string command)
            {
                Label = label;
                Sprite = sprite;
                Active = active;
                Command = command;
            }
        }

        private List<RailButtonSpec> BuildRailButtons(BasePlayer player, PlayerSession s, bool admin)
        {
            List<RailButtonSpec> buttons = new List<RailButtonSpec>();

            if (admin)
            {
                buttons.Add(new RailButtonSpec("PLY", "assets/content/ui/map/icon-map_pin.png", s.Players, "worldmindmapbrainv2.ui toggle players"));
                buttons.Add(new RailButtonSpec("SLP", "assets/content/ui/map/icon-map_sleep.png", s.Sleepers, "worldmindmapbrainv2.ui toggle sleepers"));
                buttons.Add(new RailButtonSpec("TCS", "assets/content/ui/map/icon-map_home.png", s.Tc, "worldmindmapbrainv2.ui toggle tc"));
                buttons.Add(new RailButtonSpec("STS", "assets/prefabs/deployable/small stash/small_stash.png", s.Stashes, "worldmindmapbrainv2.ui toggle stashes"));
                buttons.Add(new RailButtonSpec("BAG", "assets/prefabs/deployable/sleeping bag/sleepingbag.png", s.Bags, "worldmindmapbrainv2.ui toggle bags"));
                buttons.Add(new RailButtonSpec("NPC", "", s.Npcs, "worldmindmapbrainv2.ui toggle npcs"));
                buttons.Add(new RailButtonSpec("SAF", "", s.NpcSafezone, "worldmindmapbrainv2.ui toggle npc_safezone"));
                buttons.Add(new RailButtonSpec("HST", "", s.NpcHostile, "worldmindmapbrainv2.ui toggle npc_hostile"));
                buttons.Add(new RailButtonSpec("ANI", "", s.NpcAnimals, "worldmindmapbrainv2.ui toggle npc_animals"));
                buttons.Add(new RailButtonSpec("SCI", "", s.NpcScientists, "worldmindmapbrainv2.ui toggle npc_scientists"));
                buttons.Add(new RailButtonSpec("RAD", "", s.Raid, "worldmindmapbrainv2.ui toggle raid"));
                buttons.Add(new RailButtonSpec("CLS", "", s.Clusters, "worldmindmapbrainv2.ui toggle clusters"));
                buttons.Add(new RailButtonSpec("WCH", "assets/content/ui/map/icon-map_pin.png", s.Watch, "worldmindmapbrainv2.ui toggle watch"));
            }

            buttons.Add(new RailButtonSpec("HEA", "", s.Heat, "worldmindmapbrainv2.ui toggle heat"));
            buttons.Add(new RailButtonSpec("ZON", "assets/content/ui/map/icon-map_pin.png", s.Zones, "worldmindmapbrainv2.ui toggle zones"));
            buttons.Add(new RailButtonSpec("INT", "", s.Intel, "worldmindmapbrainv2.ui toggle intel"));
            buttons.Add(new RailButtonSpec("PUL", "", s.Pulse, "worldmindmapbrainv2.ui toggle pulse"));
            buttons.Add(new RailButtonSpec(s.UiLocked ? "LCK" : "MOV", "", !s.UiLocked, "worldmindmapbrainv2.ui lock"));

            // Movement controls are no longer stacked into the main rail.
            // Unlocking the rail opens a separate movement panel instead.
            buttons.Add(new RailButtonSpec("REF", "", false, "worldmindmapbrainv2.ui refresh"));
            buttons.Add(new RailButtonSpec("OFF", "", false, "worldmindmapbrainv2.ui close"));
            return buttons;
        }

        private void AddMapRailButton(CuiElementContainer c, string parent, string label, string sprite, bool active, string command, int row, float centerY)
        {
            float pixelSize = Mathf.Clamp(_config.Ui.RailButtonPixelSize, 22f, 40f);
            float half = pixelSize * 0.5f;

            string buttonName = parent + ".Btn." + row;
            string bg = active ? _config.Ui.OnButtonColor : _config.Ui.OffButtonColor;
            string fg = active ? _config.Ui.AccentColor : _config.Ui.BodyColor;
            string code = ThreeLetterCode(label);
            string hostedUrl = GetHostedButtonImage(code);
            bool useHostedImage = _config.Ui.UseHostedButtonImages && !string.IsNullOrEmpty(hostedUrl);
            bool useSprite = !useHostedImage && _config.Ui.UseNativeButtonSprites && !string.IsNullOrEmpty(sprite);
            bool showText = !useHostedImage && !useSprite;

            c.Add(new CuiButton
            {
                Button =
                {
                    Command = command,
                    Color = bg,
                    Sprite = "assets/icons/circle_closed.png",
                    Material = "assets/icons/iconmaterial.mat"
                },
                Text =
                {
                    Text = showText ? code : "",
                    FontSize = _config.Ui.RailCodeFontSize,
                    Align = TextAnchor.MiddleCenter,
                    Color = _config.Ui.ButtonTextColor,
                    Font = "robotocondensed-bold.ttf"
                },
                RectTransform =
                {
                    AnchorMin = "0.5 1",
                    AnchorMax = "0.5 1",
                    OffsetMin = "-" + half.ToString("0.#") + " " + (centerY - half).ToString("0.#"),
                    OffsetMax = half.ToString("0.#") + " " + (centerY + half).ToString("0.#")
                }
            }, parent, buttonName);

            if (useHostedImage)
            {
                c.Add(new CuiElement
                {
                    Parent = buttonName,
                    Components =
                    {
                        new CuiRawImageComponent { Url = hostedUrl, Color = fg },
                        new CuiRectTransformComponent { AnchorMin = "0.18 0.18", AnchorMax = "0.82 0.82" }
                    }
                });
            }
            else if (useSprite)
            {
                c.Add(new CuiElement
                {
                    Parent = buttonName,
                    Components =
                    {
                        new CuiImageComponent { Sprite = sprite, Color = fg, Material = "assets/icons/iconmaterial.mat" },
                        new CuiRectTransformComponent { AnchorMin = "0.22 0.22", AnchorMax = "0.78 0.78" }
                    }
                });
            }
        }

        private string GetHostedButtonImage(string code)
        {
            if (_config == null || _config.Ui == null || _config.Ui.HostedButtonImages == null) return "";
            string value;
            return _config.Ui.HostedButtonImages.TryGetValue(code, out value) ? (value ?? "").Trim() : "";
        }

        private string ThreeLetterCode(string value)
        {
            value = (value ?? "").Trim().ToUpperInvariant();
            if (value.Length == 0) return "???";
            if (value.Length == 1) return value + value + value;
            if (value.Length == 2) return value + value.Substring(1, 1);
            return value.Substring(0, 3);
        }

        private void AddMapUiUpdater(CuiElementContainer c, string parent)
        {
            if (_config == null || _config.Ui == null || !_config.Ui.UseSelfRefresh) return;
            c.Add(new CuiElement
            {
                Parent = parent,
                Name = UiUpdaterName,
                Components =
                {
                    new CuiCountdownComponent { Command = "worldmindmapbrainv2.ui refresh", EndTime = Mathf.Max(1f, _config.Ui.SelfRefreshSeconds), DestroyIfDone = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0" }
                }
            });
        }

        private void AddToggle(CuiElementContainer c, string parent, string label, bool on, string key, float y)
        {
            AddText(c, parent, label, 8, TextAnchor.MiddleLeft, "0.05 " + (y - 0.025f).ToString("0.00"), "0.62 " + (y + 0.035f).ToString("0.00"), _config.Ui.BodyColor, false);
            AddButton(c, parent, on ? "ON" : "OFF", "worldmindmapbrainv2.ui toggle " + key, "0.66 " + (y - 0.025f).ToString("0.00"), "0.96 " + (y + 0.035f).ToString("0.00"), on ? _config.Ui.OnButtonColor : _config.Ui.OffButtonColor, 8);
        }

        private void AddMiniButton(CuiElementContainer c, string parent, string label, string command, float x, float y, bool selected)
        {
            AddButton(c, parent, label, command, x.ToString("0.00") + " " + y.ToString("0.00"), (x + 0.14f).ToString("0.00") + " " + (y + 0.05f).ToString("0.00"), selected ? _config.Ui.OnButtonColor : _config.Ui.ButtonColor, 8);
        }

        private void AddText(CuiElementContainer c, string parent, string text, int size, TextAnchor anchor, string min, string max, string color, bool bold, string offsetMin, string offsetMax)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Align = anchor, Color = color, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf" },
                RectTransform = { AnchorMin = min, AnchorMax = max, OffsetMin = offsetMin, OffsetMax = offsetMax }
            }, parent);
        }

        private void AddText(CuiElementContainer c, string parent, string text, int size, TextAnchor anchor, string min, string max, string color, bool bold)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Align = anchor, Color = color, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private void AddButton(CuiElementContainer c, string parent, string text, string command, string min, string max, string color, int size)
        {
            c.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                Text = { Text = text ?? "", FontSize = size, Align = TextAnchor.MiddleCenter, Color = _config.Ui.ButtonTextColor, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        #endregion

        #region Layer Toggles

        private void ToggleAdminLayer(BasePlayer player, string key)
        {
            if (!IsAdmin(player)) { Reply(player, "Admin permission required."); return; }
            PlayerSession s = GetSession(player);
            if (key == "all")
            {
                s.Players = s.Sleepers = s.Tc = s.Stashes = s.Bags = s.Npcs = s.NpcSafezone = s.NpcHostile = s.NpcAnimals = s.NpcScientists = s.NpcBosses = s.Heat = s.Zones = s.Intel = s.Raid = s.Clusters = s.Watch = s.Pulse = true;
            }
            else if (key == "off") s.DisableAll();
            else if (key == "players") s.Players = !s.Players;
            else if (key == "sleepers") s.Sleepers = !s.Sleepers;
            else if (key == "tc") s.Tc = !s.Tc;
            else if (key == "stashes") s.Stashes = !s.Stashes;
            else if (key == "bags") s.Bags = !s.Bags;
            else if (key == "npcs") s.Npcs = !s.Npcs;
            else if (key == "heat") s.Heat = !s.Heat;
            else if (key == "zones") s.Zones = !s.Zones;
            else if (key == "intel") s.Intel = !s.Intel;
            else if (key == "raid") s.Raid = !s.Raid;
            else if (key == "clusters") s.Clusters = !s.Clusters;
            else if (key == "watch") s.Watch = !s.Watch;
            else if (key == "pulse") s.Pulse = !s.Pulse;
            else { Reply(player, "Unknown admin layer."); return; }
            SaveData(); SendMapRefresh(player); Reply(player, "Admin map layer toggled: " + key);
        }

        private void TogglePlayerLayer(BasePlayer player, string key)
        {
            if (!CanUsePlayerMap(player) && !IsAdmin(player)) { Reply(player, "Player map permission required."); return; }
            PlayerSession s = GetSession(player);
            if (key == "all") { s.Heat = s.Zones = s.Intel = s.Pulse = true; }
            else if (key == "off") s.DisablePlayerSafe();
            else if (key == "heat") s.Heat = !s.Heat;
            else if (key == "zones") s.Zones = !s.Zones;
            else if (key == "intel") s.Intel = !s.Intel;
            else if (key == "pulse") s.Pulse = !s.Pulse;
            else { Reply(player, "Unknown player layer."); return; }
            SaveData(); SendMapRefresh(player); Reply(player, "Player map layer toggled: " + key);
        }

        private void ToggleNpcLayer(BasePlayer player, string key)
        {
            if (!IsAdmin(player)) { Reply(player, "Admin permission required."); return; }
            PlayerSession s = GetSession(player);
            s.Npcs = true;
            if (key == "safezone" || key == "safe") s.NpcSafezone = !s.NpcSafezone;
            else if (key == "hostile") s.NpcHostile = !s.NpcHostile;
            else if (key == "animals") s.NpcAnimals = !s.NpcAnimals;
            else if (key == "scientists" || key == "sci") s.NpcScientists = !s.NpcScientists;
            else if (key == "bosses" || key == "boss") s.NpcBosses = !s.NpcBosses;
            else if (key == "all") s.NpcSafezone = s.NpcHostile = s.NpcAnimals = s.NpcScientists = s.NpcBosses = true;
            else if (key == "off") s.NpcSafezone = s.NpcHostile = s.NpcAnimals = s.NpcScientists = s.NpcBosses = false;
            else { Reply(player, "Unknown NPC layer."); return; }
            SaveData(); SendMapRefresh(player); Reply(player, "NPC map layer toggled: " + key);
        }

        private void SetHeatWindow(BasePlayer player, string window)
        {
            if (window == "5") window = "5m";
            if (window == "15") window = "15m";
            if (window == "60") window = "1h";
            if (window == "24") window = "24h";
            if (window != "5m" && window != "15m" && window != "1h" && window != "24h" && window != "wipe") window = "15m";
            PlayerSession s = GetSession(player);
            if (!CanSeeHeatWindow(player, window)) { Reply(player, "No permission for heat window: " + window); return; }
            s.HeatWindow = window;
            s.Heat = true;
            SaveData(); SendMapRefresh(player); Reply(player, "Heat window: " + window);
        }

        #endregion

        #region Watch And Zones

        private void HandleWatch(BasePlayer player, string[] args)
        {
            if (!IsAdmin(player) || !Has(player, PermAdminWatch)) { Reply(player, "Watch permission required."); return; }
            if (args == null || args.Length == 0) { Reply(player, "Usage: /wmmap watch add grid <grid> <label> | player <name/id> | remove <id> | list"); return; }
            string sub = args[0].ToLowerInvariant();
            if (sub == "list") { Reply(player, BuildWatchList(player)); return; }
            if (sub == "remove" && args.Length >= 2) { _data.WatchPins.Remove(args[1]); SaveData(); Reply(player, "Watch pin removed: " + args[1]); return; }
            if (sub == "add" && args.Length >= 3)
            {
                string type = args[1].ToLowerInvariant();
                string target = args[2];
                string id = "watch_" + player.userID + "_" + type + "_" + target.Replace(" ", "_");
                string label = args.Length > 3 ? string.Join(" ", args.Skip(3).ToArray()) : target;
                _data.WatchPins[id] = new WatchPin { Id = id, OwnerId = player.UserIDString, Type = type, Target = target, Label = label, Enabled = true, CreatedUtc = DateTime.UtcNow.ToString("o") };
                SaveData(); Reply(player, "Watch pin added: " + id); return;
            }
            Reply(player, "Usage: /wmmap watch add grid <grid> <label> | /wmmap watch add player <name/id> | /wmmap watch remove <id> | /wmmap watch list");
        }

        private void AddZoneCommand(BasePlayer player, string[] args)
        {
            if (!IsAdmin(player) || !Has(player, PermAdminZones)) { Reply(player, "Zone permission required."); return; }
            if (args.Length < 7) { Reply(player, "Usage: /wmmap addzone <id> <grid> <radius> <admin|public> <tier> <label>"); return; }
            string id = args[1];
            string grid = args[2].ToUpperInvariant();
            float radius;
            if (!float.TryParse(args[3], out radius)) radius = 100f;
            bool playerVisible = args[4].ToLowerInvariant() == "public";
            string tier = args[5].ToLowerInvariant();
            string label = string.Join(" ", args.Skip(6).ToArray());
            Vector3 pos = GridToWorld(grid);
            CreateZone(id, pos, radius, _config.ZoneDefaults.DefaultColor, label, "manual", playerVisible, true, tier);
            Reply(player, "Zone added: " + id + " public=" + playerVisible + " tier=" + tier);
        }

        private void RemoveZoneCommand(BasePlayer player, string[] args)
        {
            if (!IsAdmin(player) || !Has(player, PermAdminZones)) { Reply(player, "Zone permission required."); return; }
            if (args.Length < 2) { Reply(player, "Usage: /wmmap remove <id>"); return; }
            bool removed = _data.ManualZones.Remove(args[1]);
            SaveData(); Reply(player, removed ? "Zone removed." : "Zone not found.");
        }

        private bool CreateZone(string id, Vector3 pos, float radius, string color, string label, string category, bool playerVisible, bool adminVisible, string tier)
        {
            if (string.IsNullOrEmpty(id)) return false;
            _data.ManualZones[id] = new ManualZone
            {
                Id = id,
                Label = label ?? id,
                Category = category ?? "manual",
                Grid = GetGrid(pos),
                X = pos.x, Y = pos.y, Z = pos.z,
                Radius = Mathf.Clamp(radius, 20f, 1000f),
                Color = color ?? _config.ZoneDefaults.DefaultColor,
                PlayerVisible = playerVisible,
                AdminVisible = adminVisible,
                PlayerTier = string.IsNullOrEmpty(tier) ? "basic" : tier,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };
            SaveData(); return true;
        }

        #endregion

        #region Helpers

        private bool IsAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || Has(player, PermAdmin));
        }

        private bool CanUsePlayerMap(BasePlayer player)
        {
            return HasAny(player, PermPlayer, PermPlayerBasic, PermPlayerScout, PermPlayerVip, PermPlayerEvent);
        }

        private bool CanUseAnyMap(BasePlayer player)
        {
            return IsAdmin(player) || CanUsePlayerMap(player);
        }

        private bool Has(BasePlayer player, string perm)
        {
            if (player == null || string.IsNullOrEmpty(perm)) return false;
            if (player.IsAdmin && perm.StartsWith("worldmindmapbrainv2.admin")) return true;
            return permission.UserHasPermission(player.UserIDString, perm);
        }

        private bool HasAny(BasePlayer player, params string[] perms)
        {
            foreach (string p in perms) if (Has(player, p)) return true;
            return false;
        }

        private bool CanSeeHeat(BasePlayer p, bool admin)
        {
            return admin ? Has(p, PermAdminHeat) : HasAny(p, PermPlayerHeat, PermPlayerScout, PermPlayerVip, PermPlayerEvent);
        }

        private bool CanSeeZones(BasePlayer p, bool admin)
        {
            return admin ? Has(p, PermAdminZones) : HasAny(p, PermPlayerZones, PermPlayerBasic, PermPlayerScout, PermPlayerVip, PermPlayerEvent);
        }

        private bool CanSeeTier(BasePlayer p, string tier)
        {
            tier = (tier ?? "basic").ToLowerInvariant();
            if (tier == "event") return HasAny(p, PermPlayerEvent, PermAdmin);
            if (tier == "vip") return HasAny(p, PermPlayerVip, PermPlayerEvent, PermAdmin);
            if (tier == "scout") return HasAny(p, PermPlayerScout, PermPlayerVip, PermPlayerEvent, PermAdmin);
            return HasAny(p, PermPlayerBasic, PermPlayer, PermPlayerScout, PermPlayerVip, PermPlayerEvent, PermAdmin);
        }

        private bool CanSeeHeatWindow(BasePlayer p, string window)
        {
            if (!IsAdmin(p)) return window == "5m" || window == "15m" || HasAny(p, PermPlayerScout, PermPlayerVip, PermPlayerEvent);
            if (window == "5m") return Has(p, PermAdminHeat5) || Has(p, PermAdminHeat);
            if (window == "15m") return Has(p, PermAdminHeat15) || Has(p, PermAdminHeat);
            if (window == "1h") return Has(p, PermAdminHeat60) || Has(p, PermAdminHeat);
            if (window == "24h") return Has(p, PermAdminHeat24) || Has(p, PermAdminHeat);
            if (window == "wipe") return Has(p, PermAdminHeatWipe) || Has(p, PermAdminHeat);
            return true;
        }

        private PlayerSession GetSession(BasePlayer player)
        {
            PlayerSession session;
            if (_data.PlayerSessions.TryGetValue(player.UserIDString, out session) && session != null)
            {
                session.Normalize(); return session;
            }
            session = PlayerSession.Default(_config.Ui);
            _data.PlayerSessions[player.UserIDString] = session;
            return session;
        }

        private void MoveUi(BasePlayer player, string dir)
        {
            if (!Has(player, PermAdminMoveUi)) return;
            PlayerSession s = GetSession(player);
            if (s.UiLocked) return;
            float step = _config.Ui.MoveStepPixels;
            float x = 0f, y = 0f;
            if (dir == "up") y = step;
            if (dir == "down") y = -step;
            if (dir == "left") x = -step;
            if (dir == "right") x = step;
            s.OffsetX += x; s.OffsetY += y;
            ApplyUiOffsets(s);
            SaveData();
        }

        private void ResetUi(BasePlayer player)
        {
            PlayerSession s = GetSession(player);
            s.UiAnchorMin = _config.Ui.DefaultAnchorMin;
            s.UiAnchorMax = _config.Ui.DefaultAnchorMax;
            s.OffsetX = 0; s.OffsetY = 0;
            s.UiOffsetMin = _config.Ui.DefaultOffsetMin;
            s.UiOffsetMax = _config.Ui.DefaultOffsetMax;
            NormalizeUiFrame(s);
            s.UiLocked = true;
            SaveData();
        }

        private void NormalizeUiFrame(PlayerSession s)
        {
            if (s == null || _config == null || _config.Ui == null) return;
            if (!_config.Ui.ForceFixedRailSize) return;

            s.UiAnchorMin = _config.Ui.DefaultAnchorMin;
            s.UiAnchorMax = _config.Ui.DefaultAnchorMax;
            ApplyUiOffsets(s);
        }

        private void ApplyUiOffsets(PlayerSession s)
        {
            if (s == null || _config == null || _config.Ui == null) return;
            s.UiOffsetMin = ShiftOffset(_config.Ui.DefaultOffsetMin, s.OffsetX, s.OffsetY);
            s.UiOffsetMax = ShiftOffset(_config.Ui.DefaultOffsetMax, s.OffsetX, s.OffsetY);
        }

        private string ShiftOffset(string offset, float x, float y)
        {
            string[] parts = (offset ?? "0 0").Split(' ');
            float ox = 0f, oy = 0f;
            if (parts.Length > 0) float.TryParse(parts[0], out ox);
            if (parts.Length > 1) float.TryParse(parts[1], out oy);
            return (ox + x).ToString("0") + " " + (oy + y).ToString("0");
        }

        private void SendMapRefresh(BasePlayer player)
        {
            try { player.SendPingsToClient(); } catch { }
        }

        private void DisposeCachedNotes(ulong userId)
        {
            List<MapNote> notes;
            if (!_lastRenderedNotes.TryGetValue(userId, out notes) || notes == null) return;
            foreach (MapNote note in notes) if (note != null) note.Dispose();
            notes.Clear();
            _lastRenderedNotes.Remove(userId);
        }

        private void DisposeAllCachedNotes()
        {
            foreach (ulong id in _lastRenderedNotes.Keys.ToList()) DisposeCachedNotes(id);
        }

        private Vector3 GridToWorld(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return Vector3.zero;
            grid = grid.ToUpperInvariant();
            char c = grid[0];
            int number = 0;
            int.TryParse(grid.Substring(1), out number);
            float x = ((c - 'A') * 150f) - 4500f + 75f;
            float z = 4500f - (number * 150f) - 75f;
            float y = TerrainMeta.HeightMap == null ? 0f : TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));
            return new Vector3(x, y + 1f, z);
        }

        private string GetGrid(Vector3 position)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((position.x + 4500f) / 150f), 0, 25);
            int z = Mathf.Clamp(Mathf.FloorToInt((4500f - position.z) / 150f), 0, 99);
            char letter = (char)('A' + x);
            return letter + z.ToString();
        }

        private int GetWindowScore(GridMemoryRecord rec, string window)
        {
            if (rec == null) return 0;
            if (window == "5m") return rec.Heat5m;
            if (window == "1h") return rec.Heat1h;
            if (window == "24h") return rec.Heat24h;
            if (window == "wipe") return rec.HeatWipe;
            return rec.Heat15m;
        }

        private int HeatColor(int score)
        {
            if (score >= _config.Heat.CriticalScore) return _config.Icons.CriticalHeatColor;
            if (score >= _config.Heat.DangerScore) return _config.Icons.DangerHeatColor;
            return _config.Icons.HeatColor;
        }

        private string VagueHeatLabel(GridMemoryRecord rec, int score)
        {
            if (rec == null) return _config.MapPulse.QuietText;
            if (score >= _config.Heat.CriticalScore) return "Something ugly is happening near " + rec.Grid + ".";
            if (score >= _config.Heat.DangerScore) return "Trouble is building around " + rec.Grid + ".";
            return "Movement reported near " + rec.Grid + ".";
        }

        private string VaguePlayerRead(IntelTarget target, GridMemoryRecord rec)
        {
            if (target == null) return "The island has nothing clean to say yet.";
            if (target.Zone != null) return "Public zone: " + target.Zone.Label + ".";
            if (rec != null) return VagueHeatLabel(rec, Math.Max(rec.Heat5m, rec.Heat15m));
            return "A public WorldMind marker is active here.";
        }

        private NpcKind ClassifyNpc(BaseEntity e)
        {
            if (e == null) return NpcKind.None;
            string n = CleanPrefab(e).ToLowerInvariant();
            string t = e.GetType().Name.ToLowerInvariant();
            if (n.Contains("bradley") || n.Contains("patrolhelicopter") || n.Contains("ch47")) return NpcKind.Boss;
            if (n.Contains("peacekeeper") || n.Contains("bandit_guard") || n.Contains("outpost") || n.Contains("compound") || n.Contains("safezone")) return NpcKind.SafeZone;
            if (n.Contains("scientist") || t.Contains("scientist")) return NpcKind.Scientist;
            if (n.Contains("bear") || n.Contains("boar") || n.Contains("wolf") || n.Contains("stag") || n.Contains("chicken") || n.Contains("horse") || n.Contains("shark") || n.Contains("crocodile") || t.Contains("animal")) return NpcKind.Animal;
            if (t.Contains("npc") || n.Contains("npc")) return NpcKind.Hostile;
            return NpcKind.None;
        }

        private List<ClusterRecord> BuildClusters()
        {
            List<ClusterRecord> clusters = new List<ClusterRecord>();
            AddEntityCluster<BuildingPrivlidge>(clusters, "base_cluster", _config.Clusters.BaseClusterRadius, _config.Clusters.MinBaseClusterCount);
            AddEntityCluster<SleepingBag>(clusters, "bag_cluster", _config.Clusters.BagClusterRadius, _config.Clusters.MinBagClusterCount);
            AddEntityCluster<StashContainer>(clusters, "stash_cluster", _config.Clusters.StashClusterRadius, _config.Clusters.MinStashClusterCount);
            return clusters.OrderByDescending(x => x.Count).ToList();
        }

        private void AddEntityCluster<T>(List<ClusterRecord> clusters, string type, float radius, int min) where T : BaseEntity
        {
            List<T> entities = BaseNetworkable.serverEntities.OfType<T>().Where(x => x != null && !x.IsDestroyed).ToList();
            HashSet<T> used = new HashSet<T>();
            foreach (T e in entities)
            {
                if (used.Contains(e)) continue;
                List<T> near = entities.Where(x => Vector3.Distance(x.transform.position, e.transform.position) <= radius).ToList();
                if (near.Count < min) continue;
                foreach (T n in near) used.Add(n);
                Vector3 avg = Vector3.zero;
                foreach (T n in near) avg += n.transform.position;
                avg /= near.Count;
                clusters.Add(new ClusterRecord { Type = type, Count = near.Count, Position = avg, Grid = GetGrid(avg), Radius = radius });
            }
        }

        private bool TryResolveWatchPosition(WatchPin pin, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (pin == null) return false;
            if (pin.Type == "grid") { pos = GridToWorld(pin.Target); return true; }
            if (pin.Type == "player")
            {
                BasePlayer p = FindPlayer(pin.Target);
                if (p == null) return false;
                pos = p.transform.position;
                return true;
            }
            return false;
        }

        private BasePlayer FindPlayer(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId)) return null;
            foreach (BasePlayer p in BasePlayer.allPlayerList)
            {
                if (p == null) continue;
                if (p.UserIDString == nameOrId) return p;
                if ((p.displayName ?? "").IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0) return p;
            }
            return null;
        }

        private int CountEntitiesNear<T>(Vector3 pos, float radius) where T : BaseEntity
        {
            return BaseNetworkable.serverEntities.OfType<T>().Count(x => x != null && !x.IsDestroyed && Vector3.Distance(x.transform.position, pos) <= radius);
        }

        private string HeldItemName(BasePlayer p)
        {
            Item item = p == null ? null : p.GetActiveItem();
            return item == null ? "none" : item.info == null ? item.ToString() : item.info.shortname;
        }

        private string CleanPrefab(BaseEntity e)
        {
            if (e == null) return "unknown";
            string s = e.ShortPrefabName;
            if (string.IsNullOrEmpty(s)) s = e.PrefabName;
            if (string.IsNullOrEmpty(s)) s = e.GetType().Name;
            return s;
        }

        private ulong HashPosition(Vector3 pos, string type, string label)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(pos.x);
                h = h * 31 + Mathf.RoundToInt(pos.z);
                h = h * 31 + (type ?? "").GetHashCode();
                h = h * 31 + (label ?? "").GetHashCode();
                return (ulong)(uint)h;
            }
        }

        private object CallPlugin(Plugin plugin, string hook, params object[] args)
        {
            try { return plugin == null ? null : plugin.Call(hook, args); }
            catch { return null; }
        }

        private void RegisterWithCore()
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["plugin"] = Name,
                ["version"] = Version.ToString(),
                ["purpose"] = "Self-contained tactical map intelligence, private map notes, player-safe public map intel, NPC layers, heat memory, and WorldMind entity cards."
            };
            try { if (WorldMindV2 != null) WorldMindV2.Call("WorldMind_RegisterPlugin", Name, payload); }
            catch (Exception ex) { SetError("Core registration failed: " + ex.Message); }
        }

        private void Heartbeat()
        {
            _lastHeartbeat = Interface.Oxide.Now;
            _data.Proof.LastHeartbeatUtc = DateTime.UtcNow.ToString("o");
            _data.Proof.WorldMindConnected = WorldMindV2 != null;
            try { if (WorldMindV2 != null) WorldMindV2.Call("WorldMind_Heartbeat", Name, _data.Proof); }
            catch (Exception ex) { SetError("Core heartbeat failed: " + ex.Message); }
        }

        private void RecordMapInteraction(BasePlayer player, string eventType, IntelTarget target)
        {
            _data.Proof.MapInteractions++;
            try
            {
                if (WorldMindV2 != null)
                {
                    Dictionary<string, object> truth = new Dictionary<string, object>
                    {
                        ["viewer"] = player.displayName,
                        ["viewerId"] = player.UserIDString,
                        ["viewerIsAdmin"] = IsAdmin(player),
                        ["eventType"] = eventType,
                        ["targetType"] = target.Type,
                        ["targetLabel"] = target.Label,
                        ["grid"] = GetGrid(target.Position)
                    };
                    WorldMindV2.Call("WorldMind_RecordEvent", Name, eventType, player.UserIDString, truth);
                }
            }
            catch { }
            SaveData();
        }

        private void SetError(string error)
        {
            if (_data == null || _data.Proof == null) return;
            _data.Proof.LastError = error ?? "";
            _data.Proof.LastErrorUtc = DateTime.UtcNow.ToString("o");
        }

        private void Reply(BasePlayer player, string message)
        {
            SendReply(player, "<color=#00F0FF>[WorldMindMap]</color> " + message);
        }

        private void ShowToast(BasePlayer player, string message, bool ok)
        {
            try { player.ShowToast(ok ? GameTip.Styles.Blue_Normal : GameTip.Styles.Red_Normal, message); }
            catch { Reply(player, message); }
        }

        private bool Same(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }

        private string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= max ? value : value.Substring(0, max).Trim() + "...";
        }

        private void PrintStartup()
        {
            Puts(DV8DAsciiTag);
            Puts("WorldMindMapBrainV2 loaded. " + MadeWithLoveTag + ".");
            Puts("Self-contained map intelligence: private layers, native Map-parent UI, safe player intel, admin entity cards, heat memory, NPC toggles, and no teleport.");
        }

        #endregion

        #region Text Builders

        private string BuildStatus()
        {
            return string.Join("\n", new[]
            {
                "WorldMindMapBrainV2 status:",
                "Enabled: " + _config.Enabled,
                "WorldMind connected: " + (WorldMindV2 != null),
                "SignalBrain connected: " + (WorldMindSignalBrainV2 != null),
                "Grid memory records: " + _data.GridMemory.Count,
                "Manual zones: " + _data.ManualZones.Count,
                "Watch pins: " + _data.WatchPins.Count,
                "Last pulse: " + Safe(_data.Proof.LastPulseUtc),
                "Last notes built: " + _data.Proof.LastPrivateNoteCount + " for " + Safe(_data.Proof.LastViewer),
                "Last error: " + Safe(_data.Proof.LastError)
            });
        }

        private string BuildHelp()
        {
            return "Commands: /wmmap ui <on|off|status>, /wmmap admin <layer>, /wmmap player <layer>, /wmmap npc <safezone|hostile|animals|scientists|bosses|all|off>, /wmmap heat <5m|15m|1h|24h|wipe>, /wmmap watch, /wmmap grid <grid>, /wmmap pulse, /wmmap perms";
        }

        private string BuildPermHelp()
        {
            return string.Join("\n", new[]
            {
                "Core grants:",
                "oxide.grant group admin worldmindmapbrainv2.admin",
                "oxide.grant group admin worldmindmapbrainv2.admin.ui.move",
                "oxide.grant group default worldmindmapbrainv2.player.basic",
                "Admin layer perms include: players, sleepers, tc, stashes, bags, npcs, npcs.safezone, npcs.hostile, animals, scientists, bosses, heat, raid, clusters, watch, zones, intel, pulse.",
                "Player tiers: player.basic, player.scout, player.vip, player.event. Optional: player.heat, player.zones, player.intel, player.pulse, player.loothints, player.eventzones."
            });
        }

        private string BuildPulseText(bool admin)
        {
            MapPulse p = _data.MapPulse ?? new MapPulse();
            if (admin) return Safe(p.AdminSummary) + "\nTop: " + string.Join(", ", p.TopGrids.ToArray()) + "\nRaid: " + string.Join(", ", p.RaidPressureGrids.ToArray());
            return Safe(p.PlayerSummary);
        }

        private string BuildZoneList(bool admin)
        {
            if (_data.ManualZones.Count == 0) return "No manual zones.";
            return string.Join("\n", _data.ManualZones.Values.Where(z => admin || z.PlayerVisible).Select(z => z.Id + " | " + z.Grid + " | " + z.Label + " | tier=" + z.PlayerTier).Take(20).ToArray());
        }

        private string BuildWatchList(BasePlayer player)
        {
            if (_data == null || _data.WatchPins == null || player == null)
            {
                return "No watch pins.";
            }

            string output = "";
            int count = 0;

            foreach (WatchPin pin in _data.WatchPins.Values)
            {
                if (pin == null) continue;
                if (pin.OwnerId != player.UserIDString && pin.OwnerId != "server") continue;

                if (count > 0) output += "\n";
                output += pin.Id + " | " + pin.Type + " | " + pin.Target + " | " + pin.Label;
                count++;
            }

            if (count <= 0)
            {
                return "No watch pins.";
            }

            return output;
        }

        private void ShowGrid(BasePlayer player, string grid)
        {
            GridMemoryRecord rec = GetGridMemory(grid);
            if (rec == null) { Reply(player, "No grid memory for " + grid + "."); return; }
            Reply(player, JsonConvert.SerializeObject(rec, Formatting.Indented));
        }

        #endregion

        #region Config And Data

        private class PluginConfig
        {
            public bool Enabled = true;
            public RefreshConfig Refresh = new RefreshConfig();
            public UiConfig Ui = new UiConfig();
            public IconConfig Icons = new IconConfig();
            public LabelConfig Labels = new LabelConfig();
            public IntelConfig Intel = new IntelConfig();
            public HeatConfig Heat = new HeatConfig();
            public RaidConfig Raid = new RaidConfig();
            public ClusterConfig Clusters = new ClusterConfig();
            public MapMemoryConfig MapMemory = new MapMemoryConfig();
            public ZoneDefaultsConfig ZoneDefaults = new ZoneDefaultsConfig();
            public MapPulseConfig MapPulse = new MapPulseConfig();

            public static PluginConfig Default() { PluginConfig c = new PluginConfig(); c.Normalize(); return c; }
            public void Normalize()
            {
                if (Refresh == null) Refresh = new RefreshConfig();
                if (Ui == null) Ui = new UiConfig();
                Ui.Normalize();
                if (Icons == null) Icons = new IconConfig();
                if (Labels == null) Labels = new LabelConfig();
                if (Intel == null) Intel = new IntelConfig();
                if (Heat == null) Heat = new HeatConfig();
                if (Raid == null) Raid = new RaidConfig();
                if (Clusters == null) Clusters = new ClusterConfig();
                if (MapMemory == null) MapMemory = new MapMemoryConfig();
                if (ZoneDefaults == null) ZoneDefaults = new ZoneDefaultsConfig();
                if (MapPulse == null) MapPulse = new MapPulseConfig();
            }
        }

        private class RefreshConfig
        {
            public float TickSeconds = 10f;
            public float HeartbeatSeconds = 30f;
        }

        private class UiConfig
        {
            public bool Enabled = true;
            public bool ShowOnlyWithMap = true;
            public string Parent = "Map";
            public float RenderThrottleSeconds = 1f;
            public float MapUiLifetimeAfterPingSeconds = 4.0f;
            public float MapParentPrepareIntervalSeconds = 3.0f;
            public bool RenderOnMapButtonInput = false;
            public float MapButtonRenderDelaySeconds = 0.08f;
            public bool AutoPrepareForMap = true;
            public bool UseSelfRefresh = true;
            public float SelfRefreshSeconds = 2.0f;
            public bool UseNativeButtonSprites = false;
            public bool UseHostedButtonImages = true;
            public bool ShowThreeLetterFallbackWhenNoImage = true;
            public bool ShowThreeLetterOverlayOnImages = false;
            public string HostedButtonImageRule = "Paste hosted image URLs into HostedButtonImages by 3-letter code. Empty values fall back to centered 3-letter text. No tiny overlay letters are rendered on images.";
            public List<string> HostedButtonIconCatalog = DefaultHostedButtonIconCatalog();
            public Dictionary<string, string> HostedButtonImages = DefaultHostedButtonImages();
            public int RailCodeFontSize = 7;
            public float RailButtonPixelSize = 26f;
            public float RailButtonGapPixels = 3f;
            public float RailHeightPixels = 0f;
            public bool ForceFixedRailSize = true;
            public string DefaultAnchorMin = "0 0.5";
            public string DefaultAnchorMax = "0 0.5";
            public string DefaultOffsetMin = "4 -620";
            public string DefaultOffsetMax = "54 620";
            public string BackgroundColor = "0.03 0.025 0.02 0.86";
            public string CardBackgroundColor = "0.03 0.025 0.02 0.92";
            public string TitleColor = "0.85 0.72 0.45 1";
            public string BodyColor = "0.82 0.82 0.76 1";
            public string AccentColor = "0.40 0.85 0.80 1";
            public string ButtonTextColor = "0.95 0.95 0.9 1";
            public string ButtonColor = "0.25 0.31 0.18 0.95";
            public string OnButtonColor = "0.24 0.46 0.18 0.95";
            public string OffButtonColor = "0.20 0.20 0.19 0.95";
            public string WarningButtonColor = "0.60 0.42 0.13 0.95";
            public string DangerButtonColor = "0.55 0.16 0.13 0.95";
            public float MoveStepPixels = 12f;
            public string CardAnchorMin = "0.5 0.5";
            public string CardAnchorMax = "0.5 0.5";
            public string CardOffsetMin = "-240 -150";
            public string CardOffsetMax = "240 130";

            public void Normalize()
            {
                ShowThreeLetterOverlayOnImages = false;
                if (HostedButtonIconCatalog == null || HostedButtonIconCatalog.Count == 0) HostedButtonIconCatalog = DefaultHostedButtonIconCatalog();
                if (HostedButtonImages == null) HostedButtonImages = DefaultHostedButtonImages();
                foreach (KeyValuePair<string, string> pair in DefaultHostedButtonImages())
                    if (!HostedButtonImages.ContainsKey(pair.Key)) HostedButtonImages[pair.Key] = pair.Value;
            }

            public static List<string> DefaultHostedButtonIconCatalog()
            {
                return new List<string>
                {
                    "PLY = admin players layer",
                    "SLP = sleepers layer",
                    "TCS = tool cupboards / privilege layer",
                    "STS = stashes layer",
                    "BAG = sleeping bags layer",
                    "NPC = all NPC layer",
                    "SAF = safe-zone NPC layer",
                    "HST = hostile NPC layer",
                    "ANI = animals layer",
                    "SCI = scientists layer",
                    "BOS = bosses / Bradley / patrol heli layer",
                    "RAD = raid pressure layer",
                    "CLS = clusters layer",
                    "WCH = watch pins layer",
                    "HEA = heat layer",
                    "ZON = public/manual zones layer",
                    "INT = click-to-reveal WorldMind intel layer",
                    "PUL = island pulse layer",
                    "LCK = locked UI state",
                    "MOV = move/unlocked UI state",
                    "UP = move UI up",
                    "LFT = move UI left",
                    "RGT = move UI right",
                    "DWN = move UI down",
                    "SAV = save UI position",
                    "RST = reset UI position",
                    "REF = refresh map notes/UI",
                    "OFF = hide this player's map UI"
                };
            }

            public static Dictionary<string, string> DefaultHostedButtonImages()
            {
                Dictionary<string, string> map = new Dictionary<string, string>();
                map.Add("PLY", "");
                map.Add("SLP", "");
                map.Add("TCS", "");
                map.Add("STS", "");
                map.Add("BAG", "");
                map.Add("NPC", "");
                map.Add("SAF", "");
                map.Add("HST", "");
                map.Add("ANI", "");
                map.Add("SCI", "");
                map.Add("BOS", "");
                map.Add("RAD", "");
                map.Add("CLS", "");
                map.Add("WCH", "");
                map.Add("HEA", "");
                map.Add("ZON", "");
                map.Add("INT", "");
                map.Add("PUL", "");
                map.Add("LCK", "");
                map.Add("MOV", "");
                map.Add("UP", "");
                map.Add("LFT", "");
                map.Add("RGT", "");
                map.Add("DWN", "");
                map.Add("SAV", "");
                map.Add("RST", "");
                map.Add("REF", "");
                map.Add("OFF", "");
                return map;
            }
        }

        private class IconConfig
        {
            public int PlayerIcon = 6, PlayerColor = 2;
            public int SleeperIcon = 6, SleeperColor = 3;
            public int TcIcon = 2, TcColor = 4;
            public int SimplePrivilegeIcon = 5;
            public int StashIcon = 11, StashColor = 4;
            public int BagIcon = 7, BagColor = 3;
            public int HeatIcon = 10, HeatColor = 4, DangerHeatColor = 1, CriticalHeatColor = 5;
            public int RaidIcon = 10, RaidColor = 1;
            public int ZoneIcon = 4, ZoneColor = 5;
            public int WatchIcon = 4, WatchColor = 5;
            public int PulseIcon = 10, PulseColor = 4;
            public int ClusterIcon = 2, ClusterColor = 5;
            public int NpcIcon = 6, NpcColor = 1;
            public int SafeZoneNpcIcon = 6, SafeZoneNpcColor = 2;
            public int AnimalIcon = 6, AnimalColor = 3;
            public int ScientistIcon = 6, ScientistColor = 1;
            public int BossIcon = 10, BossColor = 1;
        }

        private class LabelConfig
        {
            public bool HideEntityLabelsByDefault = true;
            public bool ShowPublicZoneLabels = false;
            public int MaxPublicLabelChars = 12;
        }

        private class IntelConfig
        {
            public bool Enabled = true;
            public bool SuppressManualMapMarkerWhenIntelOpens = true;
            public float AdminInspectDistance = 85f;
            public float PlayerInspectDistance = 140f;
            public bool UseWorldMindSummaries = true;
            public string WorldMindTone = "tactical, concise, Rust-aware map intelligence";
            public int MaxSummaryChars = 180;
            public int MaxCardLines = 9;
            public float NearbyRadius = 80f;
        }

        private class HeatConfig
        {
            public int MaxVisible = 25;
            public int AdminMinimumScore = 4;
            public int PlayerMinimumScore = 8;
            public int DangerScore = 18;
            public int CriticalScore = 35;
        }

        private class RaidConfig
        {
            public int MinimumRaidPressure = 10;
            public int MaxVisibleRaidGrids = 12;
        }

        private class ClusterConfig
        {
            public int MaxVisibleClusters = 20;
            public float BaseClusterRadius = 120f;
            public int MinBaseClusterCount = 3;
            public float BagClusterRadius = 90f;
            public int MinBagClusterCount = 4;
            public float StashClusterRadius = 80f;
            public int MinStashClusterCount = 3;
        }

        private class MapMemoryConfig
        {
            public int SignalEventsToRead = 250;
            public float PulseSeconds = 300f;
            public float DedupeSeconds = 12f;
            public int MaxReasonsPerGrid = 8;
            public int Decay5mPerTick = 2;
            public int Decay15mPerTick = 1;
            public int Decay1hPerTick = 0;
            public int Decay24hPerTick = 0;
            public int RaidDecayPerTick = 1;
        }

        private class ZoneDefaultsConfig
        {
            public string DefaultColor = "#ffcc66";
            public float DefaultRadius = 100f;
        }

        private class MapPulseConfig
        {
            public string PlayerPulseLabel = "Island pulse";
            public string QuietText = "The island is quiet here.";
        }

        private class StoredData
        {
            public Dictionary<string, PlayerSession> PlayerSessions = new Dictionary<string, PlayerSession>();
            public Dictionary<string, GridMemoryRecord> GridMemory = new Dictionary<string, GridMemoryRecord>();
            public Dictionary<string, ManualZone> ManualZones = new Dictionary<string, ManualZone>();
            public Dictionary<string, WatchPin> WatchPins = new Dictionary<string, WatchPin>();
            public MapPulse MapPulse = new MapPulse();
            public ProofRecord Proof = new ProofRecord();
            public void Normalize()
            {
                if (PlayerSessions == null) PlayerSessions = new Dictionary<string, PlayerSession>();
                if (GridMemory == null) GridMemory = new Dictionary<string, GridMemoryRecord>();
                if (ManualZones == null) ManualZones = new Dictionary<string, ManualZone>();
                if (WatchPins == null) WatchPins = new Dictionary<string, WatchPin>();
                if (MapPulse == null) MapPulse = new MapPulse();
                if (Proof == null) Proof = new ProofRecord();
                foreach (PlayerSession s in PlayerSessions.Values) if (s != null) s.Normalize();
            }
        }

        private class PlayerSession
        {
            public bool Players, Sleepers, Tc, Stashes, Bags, Npcs, NpcSafezone, NpcHostile, NpcAnimals, NpcScientists, NpcBosses, Heat, Zones, Intel, Raid, Clusters, Watch, Pulse;
            public bool HideUi = false;
            public string HeatWindow = "15m";
            public bool UiLocked = true;
            public double LastMapPingAt = -9999;
            public double LastMapButtonAt = -9999;
            public bool MapLikelyOpen;
            public string UiAnchorMin = "0 0.5", UiAnchorMax = "0 0.5", UiOffsetMin = "4 -235", UiOffsetMax = "44 235";
            public float OffsetX, OffsetY;
            public static PlayerSession Default(UiConfig ui)
            {
                return new PlayerSession { Heat = true, Zones = true, Intel = true, Pulse = false, UiAnchorMin = ui.DefaultAnchorMin, UiAnchorMax = ui.DefaultAnchorMax, UiOffsetMin = ui.DefaultOffsetMin, UiOffsetMax = ui.DefaultOffsetMax };
            }
            public void Normalize()
            {
                if (string.IsNullOrEmpty(HeatWindow)) HeatWindow = "15m";
                if (string.IsNullOrEmpty(UiAnchorMin)) UiAnchorMin = "0 0.5";
                if (string.IsNullOrEmpty(UiAnchorMax)) UiAnchorMax = "0 0.5";
                if (string.IsNullOrEmpty(UiOffsetMin)) UiOffsetMin = "4 -235";
                if (string.IsNullOrEmpty(UiOffsetMax)) UiOffsetMax = "44 235";
            }
            public bool AnyLayerEnabled() { return Players || Sleepers || Tc || Stashes || Bags || Npcs || Heat || Zones || Intel || Raid || Clusters || Watch || Pulse; }
            public void DisableAll() { Players = Sleepers = Tc = Stashes = Bags = Npcs = NpcSafezone = NpcHostile = NpcAnimals = NpcScientists = NpcBosses = Heat = Zones = Intel = Raid = Clusters = Watch = Pulse = false; }
            public void DisablePlayerSafe() { Heat = Zones = Intel = Pulse = false; }
        }

        private class GridMemoryRecord
        {
            public string Grid = "";
            public string CreatedUtc = "";
            public string LastUpdatedUtc = "";
            public string LastReason = "";
            public string LastCategory = "";
            public int Heat5m, Heat15m, Heat1h, Heat24h, HeatWipe, RaidPressure, TotalEvents, CombatEvents, DeathEvents, LootEvents, GatherEvents, BuildEvents;
            public List<string> RecentReasons = new List<string>();
        }

        private class ManualZone
        {
            public string Id = "";
            public string Label = "";
            public string Category = "manual";
            public string Grid = "";
            public float X, Y, Z, Radius = 100f;
            public string Color = "#ffcc66";
            public bool PlayerVisible;
            public bool AdminVisible = true;
            public string PlayerTier = "basic";
            public bool Enabled = true;
            public string CreatedUtc = "";
            public Vector3 Position() { return new Vector3(X, Y, Z); }
        }

        private class WatchPin
        {
            public string Id = "";
            public string OwnerId = "";
            public string Type = "grid";
            public string Target = "";
            public string Label = "";
            public bool Enabled = true;
            public string CreatedUtc = "";
        }

        private class MapPulse
        {
            public string GeneratedUtc = "";
            public string HottestGrid = "";
            public int HottestScore;
            public string AdminSummary = "No map pulse yet.";
            public string PlayerSummary = "The island is quiet.";
            public List<string> TopGrids = new List<string>();
            public List<string> RaidPressureGrids = new List<string>();
        }

        private class ProofRecord
        {
            public bool WorldMindConnected;
            public string LastHeartbeatUtc = "";
            public string LastPulseUtc = "";
            public string LastPrivateNoteBuildUtc = "";
            public int LastPrivateNoteCount;
            public string LastViewer = "";
            public int EventsRemembered;
            public int MapInteractions;
            public string LastWorldMindCallUtc = "";
            public string LastWorldMindSuccessUtc = "";
            public string LastError = "";
            public string LastErrorUtc = "";
        }

        private class WorldMapSignal
        {
            public string Grid = "";
            public string Category = "";
            public string Severity = "";
            public string EventType = "";
            public string PlayerId = "";
            public string TargetId = "";
            [JsonIgnore] public double RuntimeSeen;
        }

        private class ClusterRecord
        {
            public string Type = "";
            public int Count;
            public Vector3 Position;
            public string Grid = "";
            public float Radius;
        }

        private class IntelTarget
        {
            public string Type;
            public string Label;
            public BaseEntity Entity;
            public Vector3 Position;
            public ulong NoteKey;
            public float DistanceFromClick;
            public GridMemoryRecord GridMemory;
            public ManualZone Zone;
            public WatchPin WatchPin;
            public ClusterRecord Cluster;
            public MapPulse Pulse;
            public string Window;
            public IntelTarget(string type, string label, BaseEntity entity, Vector3 pos) { Type = type; Label = label; Entity = entity; Position = pos; }
            public static IntelTarget GridHeat(GridMemoryRecord rec, Vector3 pos, string window) { return new IntelTarget("heat", "Heat " + rec.Grid, null, pos) { GridMemory = rec, Window = window }; }
            public static IntelTarget GridRaid(GridMemoryRecord rec, Vector3 pos) { return new IntelTarget("raid_pressure", "Raid " + rec.Grid, null, pos) { GridMemory = rec, Window = "15m" }; }
            public static IntelTarget CreateZone(ManualZone zone) { return new IntelTarget("zone", zone.Label, null, zone.Position()) { Zone = zone }; }
            public static IntelTarget Watch(WatchPin pin, Vector3 pos) { return new IntelTarget("watch", pin.Label, null, pos) { WatchPin = pin }; }
            public static IntelTarget CreateCluster(ClusterRecord c) { return new IntelTarget("cluster", c.Type, null, c.Position) { Cluster = c }; }
            public static IntelTarget CreatePulse(MapPulse p, Vector3 pos) { return new IntelTarget("pulse", "Island Pulse", null, pos) { Pulse = p }; }
        }

        private class IntelCardData
        {
            public string Title = "";
            public List<string> Lines = new List<string>();
            public string Summary = "";
        }

        private enum NpcKind { None, SafeZone, Hostile, Animal, Scientist, Boss }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Pathfinding;
using ProjectM.Tiles;
using ProjectM.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using V2 = System.Numerics.Vector2;

[assembly: AssemblyVersion("1.2.0.0")]
[assembly: TargetFramework(".NETCoreApp,Version=v6.0")]
namespace VRisingAutoPath;

[BepInPlugin(Guid, "VRising Auto Path", "1.2.0")]
public sealed class AutoPathPlugin : BasePlugin
{
    public const string Guid = "local.vrising.autopath";
    internal static AutoPathPlugin Instance;
    internal ConfigEntry<bool> Enabled;
    internal ConfigEntry<bool> LogProgress;
    private Harmony _harmony;
    public override void Load()
    {
        Instance = this;
        Enabled = Config.Bind("General", "Enabled", true, "Shift + map click: walk along native roads; bat flight moves straight to the target. Client only.");
        LogProgress = Config.Bind("Debug", "LogProgress", true, "Log one position sample per second while following a route.");
        _harmony = new Harmony(Guid);
        try { _harmony.PatchAll(Assembly.GetExecutingAssembly()); }
        catch { _harmony.UnpatchSelf(); Instance = null; throw; }
        AddComponent<AutoPathHud>();
        Log.LogInfo("[AUTOPATH] loaded v3; bat straight flight / native walking route; Shift+map click starts; movement/Esc/F8 cancels.");
    }
    public override bool Unload()
    {
        AutoPathService.ResetWorld();
        _harmony?.UnpatchSelf();
        Instance = null;
        return true;
    }
}

internal static class AutoPathService
{
    internal static readonly PathFollower Follower = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _pending;
    private static bool _awaitClickRelease;
    private static MapMenuMapper _mapToClose;
    private static V2 _goal;
    private static double _requestedAt;
    private static double _lastTick;
    private static double _lastSample;
    private static double _lastPathDiagnostic;
    private static double _lastPathAttempt;
    private static double _lastHealthCheck;
    private static double _planningAt;
    private static LocalRoute _localRoute;
    private static bool _queryMissing;
    private static bool _markerMatched;
    private static bool _modeChosen, _flightMode, _flightStarted;
    private static int _batPrefabHash;
    private static double _postFlightCheckAt;
    private static V2 _postFlightGoal;
    private static readonly FlightMotion FlightMotion = new();
    private static float _lastFlightInput;
    private static V2 _lastPosition;
    private static Entity _character;
    private static World _world;
    private static EntityQuery _guidance;
    private static EntityQuery _tiles;
    private static bool _hasQuery;
    private static bool _announcedWorld;
    private static EntityManager _manager;
    private static Entity _writtenCharacter;
    private static bool _ownsInput;
    internal static bool Busy => _pending || Follower.Active;
    internal static string Status { get; private set; } = "";
    internal static double StatusUntil { get; private set; }
    internal static bool ShowStatus => Busy || Clock.Elapsed.TotalSeconds < StatusUntil;

    internal static void Heartbeat()
    {
        if (_world != null && !_world.IsCreated) { ResetWorld(); return; }
        if (!Busy) return;
        var plugin = AutoPathPlugin.Instance;
        if (plugin == null) return;
        if (!plugin.Enabled.Value) { Cancel("disabled"); return; }
        if (!Application.isFocused) { Cancel("focus-lost"); return; }
        var keyboard = Keyboard.current;
        if (keyboard != null && (keyboard.escapeKey.isPressed || keyboard.f8Key.isPressed))
        { Cancel("cancel-key"); return; }
        double now = Clock.Elapsed.TotalSeconds;
        try
        {
            if (_world != null && _character != Entity.Null && now-_lastHealthCheck > 0.1)
            {
                _lastHealthCheck = now;
                _manager.CompleteAllTrackedJobs();
                if (!_manager.Exists(_character)) { Cancel("character-gone"); return; }
                if (_manager.HasComponent<Health>(_character) && _manager.GetComponentData<Health>(_character).IsDead)
                { Cancel("dead"); return; }
            }
            // GameplayInputSystem may stop updating while dead, disconnected, or in a menu.
            if (now-_lastTick > 0.5) Cancel("no-gameplay-update");
        }
        catch (Exception e)
        {
            Cancel("world-unavailable");
            plugin.Log.LogWarning("[AUTOPATH] lifecycle cleanup: "+e.Message);
        }
    }

    internal static void Begin(float3 goal, MapMenuMapper map)
    {
        Cancel("new-target");
        _goal = new V2(goal.x, goal.z);
        _pending = true;
        _awaitClickRelease = true;
        _mapToClose = map;
        _requestedAt = Clock.Elapsed.TotalSeconds;
        _postFlightCheckAt = 0;
        _queryMissing = false;
        _markerMatched = false;
        _lastTick = _requestedAt;
        _lastPathAttempt = _requestedAt - 1;
        Status = "正在等待游戏生成路线…  F8 取消";
        AutoPathPlugin.Instance.Log.LogInfo($"[AUTOPATH] target=({_goal.X:F2},{_goal.Y:F2}); waiting for native marker path.");
    }

    internal static void ObserveInput(InputState input)
    {
        if (!Busy) return;
        if (Math.Abs(input.GetAnalogValue(AnalogInputAction.MoveX)) > 0.15f ||
            Math.Abs(input.GetAnalogValue(AnalogInputAction.MoveY)) > 0.15f)
        { Cancel("manual-movement"); return; }
        foreach (var button in CancelButtons)
        {
            if (_awaitClickRelease && (button == ButtonInputAction.Primary || button == ButtonInputAction.TravelSkill || button == ButtonInputAction.Shapeshift_Modifier)) continue;
            if (input.IsInputDown(button) || input.IsInputPressed(button))
            { Cancel("manual-" + button); return; }
        }
    }
    private static readonly ButtonInputAction[] CancelButtons = {
        ButtonInputAction.Primary, ButtonInputAction.TravelSkill, ButtonInputAction.WeaponSkill1,
        ButtonInputAction.WeaponSkill2, ButtonInputAction.OffensiveSpell, ButtonInputAction.DefensiveSpell,
        ButtonInputAction.UltimateSpell, ButtonInputAction.Interact, ButtonInputAction.Interrupt,
        ButtonInputAction.AutoMove, ButtonInputAction.Chat, ButtonInputAction.CharacterMenu,
        ButtonInputAction.BuildMenu, ButtonInputAction.MapMenu, ButtonInputAction.SpellBookMenu,
        ButtonInputAction.ToggleActionWheel, ButtonInputAction.VampirePowersMenu,
        ButtonInputAction.Shapeshift_Modifier, ButtonInputAction.Shapeshift_1, ButtonInputAction.Shapeshift_2,
        ButtonInputAction.Shapeshift_3, ButtonInputAction.Shapeshift_4, ButtonInputAction.Shapeshift_5,
        ButtonInputAction.Shapeshift_6, ButtonInputAction.Shapeshift_7, ButtonInputAction.Shapeshift_8,
        ButtonInputAction.Shapeshift_9, ButtonInputAction.Shapeshift_10, ButtonInputAction.Shapeshift_11,
        ButtonInputAction.Shapeshift_12
    };

    internal static void Tick(GameplayInputSystem system)
    {
        var plugin = AutoPathPlugin.Instance;
        if (plugin == null) return;
        try
        {
            var common = system._CommonClientDataSystem;
            if (common == null) return;
            var local = common._SingletonData.LocalUser; // Value copy; avoid generated by-reference property.
            if (local.HasCharacter && !_announcedWorld)
            {
                plugin.Log.LogInfo($"[AUTOPATH] client-ready world={system.World.Name} character={local.CharacterEntity.Index}");
                _announcedWorld = true;
            }
            if (!Busy)
            {
                if(_postFlightCheckAt>0 && Clock.Elapsed.TotalSeconds>=_postFlightCheckAt && local.HasCharacter && _world!=null && _world.IsCreated)
                {
                    _postFlightCheckAt=0;
                    _manager.CompleteAllTrackedJobs();
                    if(_manager.Exists(local.CharacterEntity))
                    {
                        var state=BatFlightState.Read(_world,_manager,local.CharacterEntity,ref _batPrefabHash);
                        plugin.Log.LogInfo($"[AUTOPATH] post-flight-stop position={local.CharacterPosition} remaining={V2.Distance(new V2(local.CharacterPosition.x,local.CharacterPosition.z),_postFlightGoal):F2} flying={state.Flying} bat={state.Bat}");
                    }
                }
                return;
            }
            if (!plugin.Enabled.Value) { Cancel("disabled"); return; }
            if (!Application.isFocused) { Cancel("focus-lost"); return; }
            var keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.escapeKey.isPressed || keyboard.f8Key.isPressed))
            { Cancel("cancel-key"); return; }
            if (!local.HasCharacter || local.ControlledEntity != local.CharacterEntity)
            { Cancel("no-walking-character"); return; }
            var world = system.World;
            if (_world != world)
            {
                DisposeQuery();
                _world = world;
                _batPrefabHash = 0;
                _manager = world.EntityManager;
                var builder = new EntityQueryBuilder(Allocator.Temp);
                try
                {
                    builder = builder.AddAll(new ComponentType(Il2CppType.Of<MarkerGuidanceSingleton>(), ComponentType.AccessMode.ReadOnly))
                        .WithOptions(EntityQueryOptions.IncludeSystems | EntityQueryOptions.IncludeDisabledEntities);
                    _guidance = _manager.CreateEntityQuery(ref builder);
                    var tileBuilder = new EntityQueryBuilder(Allocator.Temp);
                    try
                    {
                        tileBuilder = tileBuilder.AddAll(new ComponentType(Il2CppType.Of<StaticWalkableTileData>(), ComponentType.AccessMode.ReadOnly))
                            .WithOptions(EntityQueryOptions.IncludeSystems);
                        _tiles = _manager.CreateEntityQuery(ref tileBuilder);
                    }
                    finally { tileBuilder.Dispose(); }
                    _hasQuery = true;
                }
                finally { builder.Dispose(); }
            }
            var em = world.EntityManager;
            em.CompleteAllTrackedJobs();
            var character = local.CharacterEntity;
            if (!em.Exists(character) || !em.HasComponent<EntityInput>(character) || !em.HasComponent<Movement>(character))
            { Cancel("missing-movement"); return; }
            if (em.HasComponent<Health>(character) && em.GetComponentData<Health>(character).IsDead)
            { Cancel("dead"); return; }
            if (_character != Entity.Null && _character != character)
            { Cancel("character-changed"); return; }
            _character = character;
            var position = new V2(local.CharacterPosition.x, local.CharacterPosition.z);
            _lastPosition = position;
            double now = Clock.Elapsed.TotalSeconds;
            float dt = (float)(now - _lastTick);
            _lastTick = now;
            if (dt <= 0) return;
            var flight=BatFlightState.Read(world,em,character,ref _batPrefabHash);
            if(!_modeChosen)
            {
                _modeChosen=true; _flightMode=flight.Bat;
                plugin.Log.LogInfo($"[AUTOPATH] mode={(_flightMode ? "bat" : "walk")} bat={flight.Bat} flying={flight.Flying} can-fly={flight.CanFly} impaired={flight.Blocked} form-speed={flight.Speed:F2}");
                if(flight.Flying && !flight.Bat) { Cancel("unsupported-flight"); return; }
            }
            if(_flightMode)
            {
                if(!flight.Bat) { Cancel("bat-form-ended"); return; }
                if(!flight.Ready)
                {
                    if(_flightStarted) { Cancel("flight-state-changed"); return; }
                    if(now-_requestedAt>8) {Cancel("flight-not-ready");return;}
                    Status="正在等待蝙蝠升空完成…  F8 取消";
                    return;
                }
                if(!_flightStarted)
                {
                    if(!Follower.Start(new[]{position,_goal},position,_goal)) {Cancel("invalid-flight-target");return;}
                    _flightStarted=true; _pending=false;
                    float fallback=flight.Speed>0 ? flight.Speed : em.GetComponentData<Movement>(character).Speed.Value;
                    FlightMotion.Reset(position,now,fallback); _lastFlightInput=0;
                    plugin.Log.LogInfo($"[AUTOPATH] straight-flight accepted start=({position.X:F2},{position.Y:F2}) goal=({_goal.X:F2},{_goal.Y:F2}) distance={V2.Distance(position,_goal):F2} initial-speed={FlightMotion.Speed:F2} native-form-speed={flight.Speed:F2} y={local.CharacterPosition.y:F2}");
                }
            }
            else if(flight.Bat || flight.Flying) {Cancel("navigation-mode-changed");return;}
            if (_pending && !_flightMode)
            {
                if (_localRoute == null && now - _requestedAt > 6)
                { Cancel(_queryMissing ? "route-data-unavailable" : _markerMatched ? "native-route-empty" : "marker-not-updated"); return; }
                if (_localRoute == null && now - _lastPathAttempt < 0.25) return;
                _lastPathAttempt = now;
                if (_localRoute == null)
                {
                    int count = _hasQuery ? _guidance.CalculateEntityCount() : 0;
                    _queryMissing = count != 1;
                    if (count != 1)
                    {
                        if (now - _lastPathDiagnostic > 1)
                        {
                            _lastPathDiagnostic = now;
                            plugin.Log.LogInfo("[AUTOPATH] path-query count=" + count);
                        }
                        return;
                    }
                    var entities = _guidance.ToEntityArray(Allocator.Temp);
                    try
                    {
                        var guidance = em.GetComponentData<MarkerGuidanceSingleton>(entities[0]);
                        _markerMatched = V2.Distance(new V2(guidance.MarkerPosition.x, guidance.MarkerPosition.z), _goal) < 2f;
                        if (now - _lastPathDiagnostic > 1)
                        {
                            _lastPathDiagnostic = now;
                            int length = guidance.Path.IsCreated ? guidance.Path.Length : 0;
                            plugin.Log.LogInfo($"[AUTOPATH] native-path count={length} marker={guidance.MarkerPosition} matched={_markerMatched}");
                        }
                        if (_markerMatched && guidance.Path.IsCreated && guidance.Path.Length > 0)
                        {
                            var points = new List<V2>(guidance.Path.Length);
                            for (int i = 0; i < guidance.Path.Length; i++)
                            { var p = guidance.Path[i]; points.Add(new V2(p.x,p.y)); }
                            if (!CreateLocalRoute(points,position,character,em)) return;
                            _planningAt = now;
                        }
                    }
                    finally { entities.Dispose(); }
                }
                if (_localRoute != null)
                {
                    if (now-_planningAt > 10) { Cancel("connector-timeout"); return; }
                    Status = _localRoute.Stage == "start" ? "正在寻找接入道路的路线…  F8 取消" : "正在补齐目标附近的路线…  F8 取消";
                    _localRoute.Advance();
                    if (!_localRoute.Done) return;
                    if (_localRoute.Reason != "found") { Cancel(_localRoute.Reason); return; }
                    int completedPoints = _localRoute.Points.Count;
                    if (!Follower.Start(_localRoute.Points,position,_goal)) { Cancel("invalid-completed-route"); return; }
                    plugin.Log.LogInfo($"[AUTOPATH] connectors start-points={_localRoute.StartConnectorPoints} end-points={_localRoute.EndConnectorPoints} trimmed-start={_localRoute.TrimmedStartPoints} trimmed-end={_localRoute.TrimmedEndPoints}");
                    _localRoute = null;
                    _pending = false;
                    plugin.Log.LogInfo($"[AUTOPATH] native-path accepted points={Follower.Count}; completed={completedPoints}; start=({position.X:F2},{position.Y:F2}) goal=({_goal.X:F2},{_goal.Y:F2})");
                }
                if (_pending) return;
            }
            // The activation click must not become an attack or an immediate manual interruption.
            if (_awaitClickRelease)
            {
                if ((Mouse.current != null && Mouse.current.leftButton.isPressed) || (keyboard != null && keyboard.shiftKey.isPressed)) return;
                _awaitClickRelease = false;
                _lastTick = now;
                _mapToClose?.HandleCloseMenu();
                _mapToClose = null;
                return;
            }
            if(_flightMode) FlightMotion.Sample(position,now,_lastFlightInput);
            var direction = Follower.Tick(position, dt, _flightMode ? FlightMotion.Speed : em.GetComponentData<Movement>(character).Speed.Value);
            if (!Follower.Active) { Cancel(Follower.Reason); return; }
            if(_flightMode)
            {
                direction=FlightMotion.LimitNearGoal(direction,V2.Distance(position,_goal),dt);
                _lastFlightInput=direction.Length();
            }
            var input = em.GetComponentData<EntityInput>(character);
            input.Movement = new float2(direction.X, direction.Y);
            em.SetComponentData(character, input);
            _manager = em;
            _writtenCharacter = character;
            _ownsInput = true;
            Status = $"{(_flightMode ? "蝙蝠直线飞行" : "自动寻路")}  {V2.Distance(position,_goal):F0} 米  ·  F8 / 移动取消";
            if (plugin.LogProgress.Value && now - _lastSample >= 1)
            {
                _lastSample = now;
                plugin.Log.LogInfo($"[AUTOPATH] step={Follower.Index}/{Follower.Count} position=({position.X:F2},{position.Y:F2}) remaining={V2.Distance(position,_goal):F2} input=({direction.X:F2},{direction.Y:F2}) dt={dt:F3} mode={(_flightMode ? "bat" : "walk")} y={local.CharacterPosition.y:F2} flight-speed={(_flightMode ? FlightMotion.Speed : 0):F2}");
            }
        }
        catch (Exception error)
        {
            Cancel("runtime-error");
            plugin.Log.LogError("[AUTOPATH] " + error);
        }
    }

    private static unsafe bool CreateLocalRoute(List<V2> points, V2 position, Entity character, EntityManager em)
    {
        if (_tiles.CalculateEntityCount() != 1 || !em.HasComponent<MapCollision>(character))
        { Cancel("terrain-data-unavailable"); return false; }
        var entities = _tiles.ToEntityArray(Allocator.Temp);
        try
        {
            var data = em.GetComponentData<StaticWalkableTileData>(entities[0]);
            var pointer = (StaticWalkableTile*)data.Blob.m_data.m_Ptr;
            if (pointer == null) { Cancel("terrain-data-unavailable"); return false; }
            var radius = em.GetComponentData<MapCollision>(character).Radius;
            AutoPathPlugin.Instance.Log.LogInfo($"[AUTOPATH] source-centre-walkable={IsStaticWalkable(pointer,position)} source-footprint-clear={WalkableLine.CanTraverse(position,position,radius,p=>IsStaticWalkable(pointer,p))} radius={radius:F2}");
            _localRoute = new LocalRoute(points,position,_goal,radius,p=>IsStaticWalkable(pointer,p));
            return true;
        }
        finally { entities.Dispose(); }
    }

    private static unsafe bool IsStaticWalkable(StaticWalkableTile* data, V2 point)
    {
        var tile = PathfindingUtility.WorldToPathfindingTile(new float2(point.X,point.Y),0);
        int x = tile.x-StaticWalkableTile.TILE_OFFSET_X;
        int y = tile.y-StaticWalkableTile.TILE_OFFSET_Y;
        if (x < 0 || y < 0 || x >= data->MapSizeInTilesX || y >= data->MapSizeInTilesY) return false;
        int index = x/8 + y/8*StaticWalkableTile.MAP_SIZE_IN_SUB_CELLS_X;
        if (index < 0 || index >= data->SubCells.Length) return false;
        return data->IsBitSet(index,(byte)(tile.x%8 + 8*(tile.y%8)));
    }
    internal static void Cancel(string reason)
    {
        if(reason=="arrived" && _flightMode)
        {
            reason="flight-arrived";
            if(AutoPathPlugin.Instance?.LogProgress.Value==true)
            { _postFlightGoal=_goal; _postFlightCheckAt=Clock.Elapsed.TotalSeconds+1; }
        }
        bool wasBusy = Busy || _ownsInput || _awaitClickRelease || _character != Entity.Null;
        _pending = false;
        _modeChosen=false; _flightMode=false; _flightStarted=false;
        _lastFlightInput=0;
        _localRoute = null;
        _awaitClickRelease = false;
        _mapToClose = null;
        Follower.Stop(reason);
        _character = Entity.Null;
        if (_ownsInput)
        {
            _ownsInput = false;
            try
            {
                if (_world != null && _world.IsCreated && _manager.Exists(_writtenCharacter) && _manager.HasComponent<EntityInput>(_writtenCharacter))
                {
                    _manager.CompleteAllTrackedJobs();
                    var input = _manager.GetComponentData<EntityInput>(_writtenCharacter);
                    input.Movement = default;
                    _manager.SetComponentData(_writtenCharacter, input);
                }
            }
            catch (Exception e) { AutoPathPlugin.Instance?.Log.LogWarning("[AUTOPATH] input cleanup: " + e.Message); }
        }
        if (wasBusy) AutoPathPlugin.Instance?.Log.LogInfo($"[AUTOPATH] stopped reason={reason} position=({_lastPosition.X:F2},{_lastPosition.Y:F2}) remaining={V2.Distance(_lastPosition,_goal):F2}");
        if (wasBusy)
        {
            Status = StopMessage(reason);
            StatusUntil = Clock.Elapsed.TotalSeconds + 8;
        }
    }

    private static string StopMessage(string reason)
    {
        if(reason=="arrived") return "已到达目标附近";
        if(reason=="flight-arrived") return "已到达目标上空，水平移动已停止；请手动落地";
        if(reason=="bat-form-ended" || reason=="flight-state-changed") return "蝙蝠飞行状态已改变，自动飞行停止";
        if(reason=="flight-not-ready") return "蝙蝠尚未进入可控飞行，请完成升空后重新选点";
        if(reason=="unsupported-flight") return "无法确认蝙蝠形态，自动导航停止";
        if(reason=="navigation-mode-changed") return "移动形态已改变，请重新在地图选点";
        if(reason=="stuck") return "自动寻路停止：前方受阻";
        if(reason=="marker-not-updated") return "尚未读到当前目标的游戏路线，请重新点选";
        if(reason=="native-route-empty") return "游戏未生成主路线，请换近一点的道路位置";
        if(reason=="route-data-unavailable" || reason=="terrain-data-unavailable") return "无法读取地图通行数据，请重新进服";
        if(reason.EndsWith("too-far")) return "目标或人物离道路超过 128 米，请分段选点";
        if(reason=="end-target-blocked") return "目标位置空间不足或不可通行，请点旁边空地";
        if(reason=="start-source-blocked") return "脚下空间不足，请走到道路或空地再选点";
        if(reason.EndsWith("source-blocked") || reason.EndsWith("target-blocked")) return "道路连接点空间不足，请换一个目标位置";
        if(reason=="start-unreachable") return "无法接入游戏主路线，请先走到附近道路";
        if(reason=="end-unreachable") return "主路线终点无法绕行到目标，请换旁边空地";
        if(reason.EndsWith("search-limit") || reason=="connector-timeout") return "局部绕路搜索超限，请选择较近的目标";
        if(reason.Contains("invalid")) return "游戏路线数据无效，请重新点选";
        if(reason=="runtime-error" || reason=="map-error") return "插件运行异常，请查看日志";
        return "自动寻路已停止";
    }

    internal static void ResetWorld()
    {
        Cancel("world-stopped");
        DisposeQuery();
        _world = null;
        _batPrefabHash=0; _postFlightCheckAt=0;
        _announcedWorld = false;
    }
    private static void DisposeQuery()
    {
        if (!_hasQuery) return;
        _hasQuery = false;
        if (_world != null && _world.IsCreated) { _guidance.Dispose(); _tiles.Dispose(); }
    }
}

public sealed class AutoPathHud : MonoBehaviour
{
    private GUIStyle _style;
    private Font _font;
    public AutoPathHud(IntPtr pointer) : base(pointer) { }
    public void Update() => AutoPathService.Heartbeat();
    public void OnGUI()
    {
        if (AutoPathPlugin.Instance == null || !AutoPathService.ShowStatus) return;
        if (_style == null)
        {
            _style = new GUIStyle();
            _style.font = GUI.skin.font;
            // The string CreateDynamicFontFromOSFont overload is stripped in this build. This native icall exists.
            try
            {
                _font = new Font();
                Font.Internal_CreateDynamicFont(_font, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(new[]{"Microsoft YaHei"}), 18);
                _style.font = _font;
            }
            catch (Exception e) { AutoPathPlugin.Instance.Log.LogWarning("[AUTOPATH] HUD font fallback: " + e.Message); }
            _style.fontSize = 18;
            _style.normal.textColor = Color.white;
        }
        GUI.Box(new Rect(20, Screen.height - 90, 540, 40), "");
        GUI.Label(new Rect(30, Screen.height - 84, 520, 30), AutoPathService.Status, _style);
    }
}

[HarmonyPatch(typeof(MapMenuMapper), nameof(MapMenuMapper.OnMapClicked))]
internal static class MapClickPatch
{
    private static bool _forwarding;
    [HarmonyPrefix]
    private static bool Prefix(MapMenuMapper __instance, PointerEventData eventData)
    {
        if (_forwarding || AutoPathPlugin.Instance == null || !AutoPathPlugin.Instance.Enabled.Value) return true;
        var keyboard = Keyboard.current;
        bool shortcut = eventData.button == PointerEventData.InputButton.Left && keyboard != null && keyboard.shiftKey.isPressed && !keyboard.ctrlKey.isPressed;
        if (!shortcut) { AutoPathService.Cancel("map-click"); return true; }
        try
        {
            if ((int)__instance._MenuMode != 0 || __instance.IsDead() || __instance._MapMenu == null) return true;
            var rect = __instance._MapMenu.MapTexture.rectTransform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, eventData.pressEventCamera, out var anchored)) return false;
            if (!MapMenuMapper.IsMarkerWithinMap(anchored, rect)) return false;
            var matrix = __instance._WorldToAnchoredSpace;
            var goal = MapUtils.GetWorldPositionFromAnchoredPosition(ref matrix, new float2(anchored.x, anchored.y));
            AutoPathService.Begin(goal, __instance);
            var original = eventData.button;
            _forwarding = true;
            try
            {
                eventData.button = PointerEventData.InputButton.Right;
                // Ask the existing map handler to create the normal synchronized marker and its route.
                __instance.OnMapClicked(eventData);
            }
            finally { eventData.button = original; _forwarding = false; }
            return false;
        }
        catch (Exception e)
        {
            _forwarding = false;
            AutoPathService.Cancel("map-error");
            AutoPathPlugin.Instance.Log.LogError("[AUTOPATH] map selection: " + e);
            return false;
        }
    }
}

[HarmonyPatch(typeof(GameplayInputSystem), nameof(GameplayInputSystem.HandleInput))]
internal static class ManualInputPatch
{
    [HarmonyPrefix]
    private static void Prefix(InputState inputState) => AutoPathService.ObserveInput(inputState);
}

[HarmonyPatch(typeof(GameplayInputSystem), nameof(GameplayInputSystem.OnUpdate))]
internal static class MovementPatch
{
    [HarmonyPostfix]
    private static void Postfix(GameplayInputSystem __instance) => AutoPathService.Tick(__instance);
}





using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

[assembly: AssemblyVersion("6.0.0.0")]
[assembly: AssemblyFileVersion("6.0.0.0")]
[assembly: TargetFramework(".NETCoreApp,Version=v6.0")]

namespace VRisingMoveSpeed;

[BepInPlugin(PluginGuid, "VRising MoveSpeed", "6.0.0")]
public sealed class MoveSpeedPlugin : BasePlugin
{
    public const string PluginGuid = "dsh.vrising.movespeed";
    internal static MoveSpeedPlugin Instance;
    internal ConfigEntry<bool> Enabled;
    internal ConfigEntry<float> Multiplier;
    internal ConfigEntry<bool> ScaleMountAcceleration;
    internal ConfigEntry<bool> LogMovement;
    internal ManualLogSource Logger => Log;
    private Harmony _harmony;

    public override void Load()
    {
        Instance = this;
        Enabled = Config.Bind("1. General", "Enabled", true, "人物与骑乘移速总开关；关闭后撤销本插件修改。");
        Multiplier = Config.Bind("1. General", "SpeedMultiplier", 1.5f,
            new ConfigDescription("人物和坐骑使用相同倍率；1 为原速。", new AcceptableValueRange<float>(0.1f, 10f)));
        ScaleMountAcceleration = Config.Bind("2. Advanced", "ScaleMountAcceleration", true,
            "骑乘加速度和减速度也按倍率缩放，以尽量保持达到最高速度和刹停所需时间；转向速度不变。");
        LogMovement = Config.Bind("3. Debug", "LogMovement", false,
            "记录每个在线角色的水平位移速度；排除明显传送，分别标记步行与骑乘。默认关闭。");
        _harmony = new Harmony(PluginGuid);
        SpeedService.Start(this);
        try { _harmony.PatchAll(Assembly.GetExecutingAssembly()); }
        catch
        {
            SpeedService.Stop();
            _harmony.UnpatchSelf();
            Instance = null;
            throw;
        }
        Logger.LogInfo("[MoveSpeed v6] 已加载：人物和坐骑同倍率；变身速度从角色控制器读取；按玩家独立管理。");
    }

    public override bool Unload()
    {
        // Block all future ticks before clearing registrations; no background coroutine exists.
        SpeedService.Stop();
        _harmony?.UnpatchSelf();
        Instance = null;
        return true;
    }
}

internal sealed class OwnedModification
{
    internal Entity Source;
    internal ModificationId Id;
    internal bool Active;
    internal float Factor;
}

internal sealed class PlayerState
{
    internal readonly Entity Source;
    internal readonly OwnedModification Walking;
    internal readonly OwnedModification FormSpeed;
    internal readonly OwnedModification Gallop;
    internal readonly OwnedModification Acceleration;
    internal readonly RawScaleState MountWalk = new();
    internal readonly RawScaleState MountReverse = new();
    internal readonly RawScaleState MountDeceleration = new();
    internal Entity Mount;
    internal Entity FormTarget;
    internal bool HasSample;
    internal float LastX;
    internal float LastZ;
    internal double LastSampleSeconds;
    internal bool LastSampleMounted;
    internal bool LastSampleShapeshifted;
    internal long RetryAfterMs;

    internal PlayerState(Entity source)
    {
        Source = source;
        Walking = new OwnedModification { Source = source };
        FormSpeed = new OwnedModification { Source = source };
        Gallop = new OwnedModification { Source = source };
        Acceleration = new OwnedModification { Source = source };
    }
}

internal sealed partial class ServerContext
{
    internal readonly World World;
    internal EntityManager Em;
    internal readonly EntityQuery Players;
    internal readonly ModificationSystem Modifications;
    internal readonly Dictionary<Entity, PlayerState> States = new();
    private readonly HashSet<Entity> _seen = new();
    private readonly List<Entity> _stale = new();
    private readonly MoveSpeedPlugin _plugin;
    private bool _closed;

    internal ServerContext(World world, MoveSpeedPlugin plugin)
    {
        World = world;
        _plugin = plugin;
        Em = world.EntityManager;
        Modifications = world.GetExistingSystemManaged<ModificationSystem>();
        if (Modifications == null) throw new InvalidOperationException("服务器的 ModificationSystem 尚未创建。");
        var builder = new EntityQueryBuilder(Allocator.Temp);
        try
        {
            builder = builder.AddAll(new ComponentType(Il2CppType.Of<PlayerCharacter>(), ComponentType.AccessMode.ReadOnly))
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            Players = Em.CreateEntityQuery(ref builder);
        }
        finally { builder.Dispose(); }
        var sourceBuilder = new EntityQueryBuilder(Allocator.Temp);
        try
        {
            sourceBuilder = sourceBuilder.AddAll(new ComponentType(Il2CppType.Of<EntityOwner>(), ComponentType.AccessMode.ReadOnly))
                .AddAll(new ComponentType(Il2CppType.Of<EntityCreator>(), ComponentType.AccessMode.ReadOnly))
                .AddAll(new ComponentType(Il2CppType.Of<ModifyUnitStatBuff_DOTS>(), ComponentType.AccessMode.ReadOnly))
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            OwnedSources = Em.CreateEntityQuery(ref sourceBuilder);
        }
        finally { sourceBuilder.Dispose(); }
        _plugin.Logger.LogInfo("[MoveSpeed v6] 服务器世界已接入；不使用固定基速，也不修改永久坐骑属性。");
    }

    private static bool HasOwn<T>(ref ModificationsRegistry registry, Entity entity,
        ref T component, ref ModifiableFloat field, OwnedModification state) where T : unmanaged
    {
        if (!state.Active) return false;
        // The generated by-ref enumerator Current getter is also unsafe here. Source references return plain values.
        var references = registry.GetSourceModificationReferences(state.Source, Allocator.Temp);
        try
        {
            for (int index = 0; index < references.Length; index++)
                if (references[index].Id.Id == state.Id.Id) return true;
        }
        finally { references.Dispose(); }
        return false;
    }

    private static void Remove<T>(ref ModificationsRegistry registry, Entity entity,
        ref T component, ref ModifiableFloat field, OwnedModification state) where T : unmanaged
    {
        if (HasOwn(ref registry, entity, ref component, ref field, state))
            RegistryInterop.Remove(ref registry, entity, ref component, ref field, state.Id);
        state.Active = false;
    }

    internal void Scale<T>(ref ModificationsRegistry registry, Entity entity,
        ref T component, ref ModifiableFloat field, OwnedModification state, float multiplier) where T : unmanaged
    {
        SpeedMath.Target(0, multiplier); // Validate the factor without reading a previously scaled speed as a baseline.
        if (state.Active && !HasOwn(ref registry, entity, ref component, ref field, state)) state.Active = false;
        if (SpeedMath.Near(multiplier, 1))
        {
            Remove(ref registry, entity, ref component, ref field, state);
            return;
        }
        if (state.Active)
        {
            // Skill curves can change on every frame. They must never trigger replacement of our registration.
            if (SpeedMath.Near(state.Factor, multiplier)) return;
            RegistryInterop.Update(ref registry, entity, ref component, ref field, state.Id, multiplier);
        }
        else
        {
            // Apply once after the game's ordinary skill and equipment modifiers.
            RegistryInterop.Add(ref registry, entity, ref component, ref field,
                ModificationType.Multiply, multiplier, int.MaxValue, state.Source, out state.Id);
            state.Active = true;
        }
        state.Factor = multiplier;
    }

    private bool Online(Entity entity)
    {
        if (!Em.Exists(entity)) return false;
        var userEntity = Em.GetComponentData<PlayerCharacter>(entity).UserEntity;
        return Em.Exists(userEntity) && Em.HasComponent<User>(userEntity) && Em.GetComponentData<User>(userEntity).IsConnected;
    }

    internal void Update(long nowMs, float multiplier)
    {
        if (_closed) return;
        var registry = Modifications._Registry;
        ReadPersistedSources();
        _seen.Clear();
        var players = Players.ToEntityArray(Allocator.Temp);
        try
        {
            for (int index = 0; index < players.Length; index++)
            {
                Entity player = players[index];
                bool online = Online(player);
                bool active = online && !SpeedMath.Near(multiplier, 1);
                if (!States.TryGetValue(player, out var state))
                {
                    state = AcquirePlayer(ref registry, player, active);
                    if (state == null) continue;
                    States.Add(player, state);
                }
                _seen.Add(player);
                if (nowMs < state.RetryAfterMs) continue;
                try
                {
                    UpdatePlayer(ref registry, player, state, active ? multiplier : 1);
                    if (!active)
                    {
                        ReleasePlayer(ref registry, player, state);
                        States.Remove(player);
                        continue;
                    }
                    if (_plugin.LogMovement.Value) Sample(player, state, nowMs);
                    else state.HasSample = false;
                }
                catch (Exception error)
                {
                    try { RestorePlayer(ref registry, player, state); }
                    catch (Exception cleanup) { _plugin.Logger.LogError("[MoveSpeed v6] 玩家修改回退失败: " + cleanup.Message); }
                    state.RetryAfterMs = nowMs + 5000;
                    _plugin.Logger.LogError("[MoveSpeed v6] 玩家实体 " + player.Index + " 修改失败；5 秒后重试: " + error.Message);
                }
            }
            _stale.Clear();
            foreach (var pair in States) if (!_seen.Contains(pair.Key)) _stale.Add(pair.Key);
            foreach (var player in _stale)
            {
                ReleasePlayer(ref registry, player, States[player]);
                States.Remove(player);
            }
        }
        finally
        {
            // Registry is a value-type interop wrapper; write its updated bookkeeping back.
            Modifications._Registry = registry;
            players.Dispose();
        }
    }

    internal void UpdatePlayer(ref ModificationsRegistry registry, Entity player, PlayerState state, float multiplier)
    {
        PersistentOwnership.Mark(Em, state.Source, player);
        ReconcilePlayer(ref registry, player, state);
        bool hasMounter = Em.HasComponent<Mounter>(player);
        bool mounted = hasMounter && Em.Exists(Em.GetComponentData<Mounter>(player).MountEntity);
        var formTarget = state.FormTarget;
        if (Em.Exists(formTarget) && Em.HasComponent<Shapeshift>(formTarget))
        {
            var form = Em.GetComponentData<Shapeshift>(formTarget);
            try
            {
                if (!mounted && form.IsShapeshifted._Value)
                    Scale(ref registry, formTarget, ref form, ref form.Speed, state.FormSpeed, multiplier);
                else Remove(ref registry, formTarget, ref form, ref form.Speed, state.FormSpeed);
            }
            finally { Em.SetComponentData(formTarget, form); }
        }
        if (Em.HasComponent<Movement>(player))
        {
            var movement = Em.GetComponentData<Movement>(player);
            try
            {
                if (mounted) Remove(ref registry, player, ref movement, ref movement.Speed, state.Walking);
                else Scale(ref registry, player, ref movement, ref movement.Speed, state.Walking, multiplier);
            }
            finally { Em.SetComponentData(player, movement); }
        }
        if (!mounted)
        {
            if (hasMounter)
            {
                var inactive = Em.GetComponentData<Mounter>(player);
                try
                {
                    Remove(ref registry, player, ref inactive, ref inactive.GallopMaxSpeed, state.Gallop);
                    Remove(ref registry, player, ref inactive, ref inactive.Acceleration, state.Acceleration);
                    if (inactive.MountEntity.Equals(state.Mount) || inactive.MountEntity.Equals(Entity.Null))
                    {
                        inactive.WalkMaxSpeed = state.MountWalk.Restore(inactive.WalkMaxSpeed);
                        inactive.MaxSpeedReverse = state.MountReverse.Restore(inactive.MaxSpeedReverse);
                        inactive.Deceleration = state.MountDeceleration.Restore(inactive.Deceleration);
                    }
                }
                finally { Em.SetComponentData(player, inactive); }
            }
            state.Mount = Entity.Null;
            // Keep the IDs until a recreated component can be checked or this player's source is released.
            state.MountWalk.HasWrite = state.MountReverse.HasWrite = state.MountDeceleration.HasWrite = false;
            PersistentOwnership.Save(Em, state);
            return;
        }

        var mount = Em.GetComponentData<Mounter>(player);
        try
        {
            if (!mount.MountEntity.Equals(state.Mount))
            {
                // A recreated Mounter may still retain registry entries; remove only known owned IDs.
                Remove(ref registry, player, ref mount, ref mount.GallopMaxSpeed, state.Gallop);
                Remove(ref registry, player, ref mount, ref mount.Acceleration, state.Acceleration);
                state.MountWalk.HasWrite = state.MountReverse.HasWrite = state.MountDeceleration.HasWrite = false;
                state.Mount = mount.MountEntity;
            }
            Scale(ref registry, player, ref mount, ref mount.GallopMaxSpeed, state.Gallop, multiplier);
            // The game's existing GallopMaxSpeedMultiplier is left intact: it is applied once, alongside our scaled speed.
            mount.WalkMaxSpeed = state.MountWalk.Apply(mount.WalkMaxSpeed, multiplier);
            mount.MaxSpeedReverse = state.MountReverse.Apply(mount.MaxSpeedReverse, multiplier);
            if (_plugin.ScaleMountAcceleration.Value)
            {
                Scale(ref registry, player, ref mount, ref mount.Acceleration, state.Acceleration, multiplier);
                mount.Deceleration = state.MountDeceleration.Apply(mount.Deceleration, multiplier);
            }
            else
            {
                Remove(ref registry, player, ref mount, ref mount.Acceleration, state.Acceleration);
                mount.Deceleration = state.MountDeceleration.Restore(mount.Deceleration);
            }
        }
        finally { Em.SetComponentData(player, mount); }
        PersistentOwnership.Save(Em, state);
    }

    internal void RestorePlayer(ref ModificationsRegistry registry, Entity player, PlayerState state)
    {
        if (!Em.Exists(player)) return;
        var formTarget = state.FormTarget;
        if (Em.Exists(formTarget) && Em.HasComponent<Shapeshift>(formTarget))
        {
            var form = Em.GetComponentData<Shapeshift>(formTarget);
            try { Remove(ref registry, formTarget, ref form, ref form.Speed, state.FormSpeed); }
            finally { Em.SetComponentData(formTarget, form); }
        }
        if (Em.HasComponent<Movement>(player))
        {
            var movement = Em.GetComponentData<Movement>(player);
            try { Remove(ref registry, player, ref movement, ref movement.Speed, state.Walking); }
            finally { Em.SetComponentData(player, movement); }
        }
        if (Em.HasComponent<Mounter>(player))
        {
            var mount = Em.GetComponentData<Mounter>(player);
            try
            {
                Remove(ref registry, player, ref mount, ref mount.GallopMaxSpeed, state.Gallop);
                Remove(ref registry, player, ref mount, ref mount.Acceleration, state.Acceleration);
                if (mount.MountEntity.Equals(state.Mount) || mount.MountEntity.Equals(Entity.Null))
                {
                    mount.WalkMaxSpeed = state.MountWalk.Restore(mount.WalkMaxSpeed);
                    mount.MaxSpeedReverse = state.MountReverse.Restore(mount.MaxSpeedReverse);
                    mount.Deceleration = state.MountDeceleration.Restore(mount.Deceleration);
                }
            }
            finally { Em.SetComponentData(player, mount); }
        }
    }

    private void ReleasePlayer(ref ModificationsRegistry registry, Entity player, PlayerState state)
    {
        try { RestorePlayer(ref registry, player, state); }
        finally
        {
            if (Em.Exists(state.Source))
            {
                registry.ClearLooseSourceModifications(state.Source, ref Em);
                Em.DestroyEntity(state.Source);
            }
        }
    }

    private void Sample(Entity player, PlayerState state, long nowMs)
    {
        bool mounted = Em.HasComponent<Mounter>(player) && Em.Exists(Em.GetComponentData<Mounter>(player).MountEntity);
        var formTarget = GetFormTarget(player);
        bool shapeshifted = !mounted && Em.Exists(formTarget)
            && Em.GetComponentData<Shapeshift>(formTarget).IsShapeshifted._Value;
        double now = nowMs / 1000.0;
        if (now - state.LastSampleSeconds < 1) return;
        bool hasPosition = Em.HasComponent<LocalTransform>(player);
        var position = hasPosition ? Em.GetComponentData<LocalTransform>(player).Position : default;
        string displacement = "unavailable";
        if (hasPosition && state.HasSample && mounted == state.LastSampleMounted && shapeshifted == state.LastSampleShapeshifted)
        {
            double dt = now - state.LastSampleSeconds;
            double dx = position.x - state.LastX, dz = position.z - state.LastZ;
            double speed = Math.Sqrt(dx * dx + dz * dz) / dt;
            displacement = dt <= 2.5 && speed < 100 ? speed.ToString("F3") : "filtered";
        }
        float statSpeed = mounted ? Em.GetComponentData<Mounter>(player).GallopMaxSpeed._Value
            : shapeshifted ? Em.GetComponentData<Shapeshift>(formTarget).Speed._Value
            : Em.HasComponent<Movement>(player) ? Em.GetComponentData<Movement>(player).Speed._Value : float.NaN;
        _plugin.Logger.LogInfo("[MoveSpeed v6][sample] entity=" + player.Index + " mode=" + (mounted ? "mounted" : shapeshifted ? "shapeshift" : "walking")
            + " factor=" + _plugin.Multiplier.Value + " statSpeed=" + statSpeed.ToString("F3")
            + " ownId=" + (mounted ? state.Gallop.Id.Id : shapeshifted ? state.FormSpeed.Id.Id : state.Walking.Id.Id) + " horizontalSpeed=" + displacement
            + DescribeForm(player, state));
        state.HasSample = hasPosition;
        state.LastX = position.x;
        state.LastZ = position.z;
        state.LastSampleSeconds = now;
        state.LastSampleMounted = mounted;
        state.LastSampleShapeshifted = shapeshifted;
    }

    internal void Clear()
    {
        if (_closed) return;
        if (World.IsCreated)
        {
            var registry = Modifications._Registry;
            try
            {
                foreach (var pair in States)
                {
                    try { ReleasePlayer(ref registry, pair.Key, pair.Value); }
                    catch (Exception error) { _plugin.Logger.LogError("[MoveSpeed v6] 清理玩家失败: " + error.Message); }
                }
            }
            finally { Modifications._Registry = registry; }
        }
        States.Clear();
    }

    internal void Close()
    {
        if (_closed) return;
        try { Clear(); }
        finally
        {
            _closed = true;
            if (World.IsCreated)
            {
                Players.Dispose();
                OwnedSources.Dispose();
            }
        }
    }
}

internal static class SpeedService
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static MoveSpeedPlugin _plugin;
    private static ServerContext _context;
    private static long _lastTickMs;
    private static long _lastPollMs;
    private static long _retryAfterMs;
    private static DateTime _lastConfigWrite;
    private static bool _running;
    private static bool _wasEnabled;
#if VALIDATION
    private static bool _validated;
#endif

    internal static void Start(MoveSpeedPlugin plugin)
    {
        _plugin = plugin;
        _running = true;
        _wasEnabled = false;
#if VALIDATION
        _validated = false;
#endif
        _lastTickMs = _lastPollMs = _retryAfterMs = 0;
        _lastConfigWrite = File.Exists(plugin.Config.ConfigFilePath) ? File.GetLastWriteTimeUtc(plugin.Config.ConfigFilePath) : DateTime.MinValue;
    }

    internal static void Tick(World world)
    {
        if (!_running || world == null || !world.IsCreated || world.Name != "Server") return;
        long now = Clock.ElapsedMilliseconds;
        if (now - _lastTickMs < 100 || now < _retryAfterMs) return;
        _lastTickMs = now;
        try
        {
            if (_context != null && (!_context.World.IsCreated || _context.World.SequenceNumber != world.SequenceNumber))
            {
                _context.Close();
                _context = null;
            }
            if (now - _lastPollMs >= 250)
            {
                _lastPollMs = now;
                string path = _plugin.Config.ConfigFilePath;
                if (File.Exists(path))
                {
                    DateTime write = File.GetLastWriteTimeUtc(path);
                    if (write != _lastConfigWrite)
                    {
                        _plugin.Config.Reload();
                        _lastConfigWrite = File.GetLastWriteTimeUtc(path);
                        _plugin.Logger.LogInfo("[MoveSpeed v6] 配置已重新读取。");
                    }
                }
            }
            if (!float.IsFinite(_plugin.Multiplier.Value)) throw new InvalidOperationException("倍率不是有限数值。");
            var modifications = world.GetExistingSystemManaged<ModificationSystem>();
            if (modifications == null || !modifications._Registry._SourceReferences.IsCreated) return;
            var em = world.EntityManager;
            em.CompleteAllTrackedJobs();
            if (_context == null)
            {
                _context = new ServerContext(world, _plugin);
            }
#if VALIDATION
            if (!_validated && _context.Players.CalculateEntityCount() > 0)
            {
                NativeValidation.Run(_context, _plugin.Logger);
                _validated = true;
            }
#endif
            _wasEnabled = _plugin.Enabled.Value;
            _context.Update(now, _plugin.Enabled.Value ? _plugin.Multiplier.Value : 1);
#if VALIDATION
            PersistenceValidation.Tick(_context, _plugin.Logger);
#endif
        }
        catch (Exception error)
        {
            _retryAfterMs = now + 5000;
            _plugin.Logger.LogError("[MoveSpeed v6] 维护失败，5 秒后重试: " + error);
        }
    }

    internal static void WorldEnding(World world)
    {
        if (_context == null || world == null || !_context.World.IsCreated || _context.World.SequenceNumber != world.SequenceNumber) return;
        try { _context.Close(); }
        catch (Exception error) { _plugin?.Logger.LogError("[MoveSpeed v6] 世界关闭清理失败: " + error.Message); }
        finally { _context = null; _wasEnabled = false; }
    }

    internal static void Stop()
    {
        _running = false;
        try { _context?.Close(); }
        catch (Exception error) { _plugin?.Logger.LogError("[MoveSpeed v6] 卸载清理失败: " + error.Message); }
        finally { _context = null; _plugin = null; _wasEnabled = false; }
    }
}

[HarmonyPatch(typeof(ComponentSystemGroup), "OnUpdate")]
internal static class ServerTickPatch
{
    [HarmonyPostfix]
    private static void Postfix(ComponentSystemGroup __instance)
    {
        if (__instance.GetIl2CppType().FullName == "Unity.Entities.SimulationSystemGroup")
            SpeedService.Tick(__instance.World);
    }
}

[HarmonyPatch(typeof(World), "Dispose")]
internal static class ServerWorldDestroyPatch
{
    [HarmonyPrefix]
    private static void Prefix(World __instance) => SpeedService.WorldEnding(__instance);
}




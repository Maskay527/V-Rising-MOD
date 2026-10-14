using System;
using System.Collections.Generic;
using ProjectM;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;

namespace VRisingMoveSpeed;

internal sealed partial class ServerContext
{
    internal readonly EntityQuery OwnedSources;
    private readonly Dictionary<Entity, Entity> _persistedSources = new();
    private readonly HashSet<Entity> _scheduledBuffs = new();
    private readonly HashSet<Entity> _obsoleteSources = new();

    private void ReadPersistedSources()
    {
        _persistedSources.Clear();
        var sources = OwnedSources.ToEntityArray(Allocator.Temp);
        try
        {
            for (int index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                if (PersistentOwnership.IsMarked(Em, source))
                {
                    var owner = Em.GetComponentData<EntityOwner>(source).Owner;
                    _persistedSources.TryAdd(owner, source);
                }
            }
        }
        finally { sources.Dispose(); }
    }

    private bool LegacySource(Entity source)
    {
        if (!Em.Exists(source)) return false;
        var types = Em.GetComponentTypes(source, Allocator.Temp);
        try { return types.Length == 1 && types[0].GetManagedType().FullName == "Unity.Entities.Simulate"; }
        finally { types.Dispose(); }
    }

    private bool LegacyRegistration(ModificationData<float> item) =>
        (item.ModType == ModificationType.Add && item.Priority == int.MaxValue - 1024
            || item.ModType == ModificationType.Multiply && item.Priority == int.MaxValue) && LegacySource(item.Source);

    private bool LegacyBuff(Entity source)
    {
        if (!Em.Exists(source) || !Em.HasComponent<PrefabGUID>(source)
            || Em.GetComponentData<PrefabGUID>(source).GuidHash != 740689171
            || !Em.HasComponent<ModifyUnitStatBuff_DOTS>(source)) return false;
        var buffer = Em.GetBuffer<ModifyUnitStatBuff_DOTS>(source);
        return buffer.Length == 1 && buffer[0].StatType == (UnitStatType)5
            && buffer[0].AttributeCapType == (AttributeCapType)(-1) && buffer[0].Modifier == 1 && buffer[0].Priority == 0;
    }

    internal PlayerState AcquirePlayer(ref ModificationsRegistry registry, Entity player, bool active)
    {
        bool hasLegacy = false;
        Entity source = Entity.Null;
        float legacyMountFactor = 1;
        float legacyDecelerationFactor = 1;
        _persistedSources.TryGetValue(player, out source);
        void Find(List<ModificationData<float>> entries, bool mountField, bool accelerationField = false)
        {
            foreach (var item in entries)
            {
                if (PersistentOwnership.IsOwned(Em, item.Source, player) && !PersistentOwnership.IsOwned(Em, source, player))
                    source = item.Source;
                if (LegacyBuff(item.Source)) hasLegacy = true;
                if (!LegacyRegistration(item)) continue;
                hasLegacy = true;
                if (item.ModType == ModificationType.Multiply)
                {
                    if (source.Equals(Entity.Null)) source = item.Source;
                }
            }
        }
        if (Em.HasComponent<Movement>(player))
        {
            var movement = Em.GetComponentData<Movement>(player);
            Find(RegistryInterop.Snapshot(ref registry, player, ref movement, ref movement.Speed), false);
        }
        var formTarget = GetFormTarget(player);
        if (Em.Exists(formTarget))
        {
            var form = Em.GetComponentData<Shapeshift>(formTarget);
            Find(RegistryInterop.Snapshot(ref registry, formTarget, ref form, ref form.Speed), false);
        }
        if (Em.HasComponent<Mounter>(player))
        {
            var mount = Em.GetComponentData<Mounter>(player);
            Find(RegistryInterop.Snapshot(ref registry, player, ref mount, ref mount.GallopMaxSpeed), true);
            Find(RegistryInterop.Snapshot(ref registry, player, ref mount, ref mount.Acceleration), false, true);
        }
        bool persisted = PersistentOwnership.IsOwned(Em, source, player);
        if (!active && !persisted && !hasLegacy) return null;
        if (!persisted && Em.HasComponent<Mounter>(player))
        {
            var mount = Em.GetComponentData<Mounter>(player);
            try
            {
                legacyMountFactor = RemoveLegacyMountField(ref registry, player, ref mount, ref mount.GallopMaxSpeed);
                legacyDecelerationFactor = RemoveLegacyMountField(ref registry, player, ref mount, ref mount.Acceleration);
            }
            finally { Em.SetComponentData(player, mount); }
        }
        if (source.Equals(Entity.Null)) source = Em.CreateEntity();
        var state = new PlayerState(source);
        if (persisted) PersistentOwnership.Load(Em, state);
        else
        {
            PersistentOwnership.Mark(Em, source, player);
            if (Em.HasComponent<Mounter>(player) && float.IsFinite(legacyMountFactor)
                && legacyMountFactor > 0 && !SpeedMath.Near(legacyMountFactor, 1))
            {
                // Recover the effective legacy factor from the native before/after values.
                // This includes additive v2 registrations and repeated v3 registrations.
                var mount = Em.GetComponentData<Mounter>(player);
                state.Mount = mount.MountEntity;
                RecoverRaw(state.MountWalk, mount.WalkMaxSpeed, legacyMountFactor);
                RecoverRaw(state.MountReverse, mount.MaxSpeedReverse, legacyMountFactor);
                if (float.IsFinite(legacyDecelerationFactor) && legacyDecelerationFactor > 0 && !SpeedMath.Near(legacyDecelerationFactor, 1))
                    RecoverRaw(state.MountDeceleration, mount.Deceleration, legacyDecelerationFactor);
            }
            PersistentOwnership.Save(Em, state);
        }
        _plugin.Logger.LogInfo("[MoveSpeed v6][adopt] player=" + player.Index + " persisted=" + persisted + " legacy=" + hasLegacy);
        return state;
    }

    private float RemoveLegacyMountField(ref ModificationsRegistry registry, Entity player, ref Mounter component, ref ModifiableFloat field)
    {
        float before = field._Value;
        bool removed = false;
        foreach (var item in RegistryInterop.Snapshot(ref registry, player, ref component, ref field))
        {
            if (!LegacyRegistration(item)) continue;
            RegistryInterop.Remove(ref registry, player, ref component, ref field, item.Id);
            _obsoleteSources.Add(item.Source);
            removed = true;
            _plugin.Logger.LogInfo("[MoveSpeed v6][migrate] remove legacy mount id=" + item.Id.Id + " value=" + item.ModValue);
        }
        if (!removed) return 1;
        float factor = before / field._Value;
        if (!float.IsFinite(factor) || factor <= 0) throw new InvalidOperationException("旧坐骑加速项无法还原有效倍率。");
        return factor;
    }

    private static void RecoverRaw(RawScaleState raw, float value, float factor)
    {
        raw.Baseline = value / factor; raw.LastWritten = value; raw.HasWrite = true;
    }

    private void ReconcileField<T>(ref ModificationsRegistry registry, Entity player, ref T component,
        ref ModifiableFloat field, OwnedModification owned, Entity owner) where T : unmanaged
    {
        owned.Active = false;
        foreach (var item in RegistryInterop.Snapshot(ref registry, player, ref component, ref field))
        {
            if (LegacyBuff(item.Source) && _scheduledBuffs.Add(item.Source))
            {
                ProjectM.Shared.DestroyUtility.Destroy(Em, item.Source, (DestroyDebugReason)13);
                _plugin.Logger.LogInfo("[MoveSpeed v6][migrate] remove legacy speed buff=" + item.Source.Index);
            }
            if (!PersistentOwnership.IsOwned(Em, item.Source, owner) && !LegacyRegistration(item)) continue;
            if (item.Source.Equals(owned.Source) && item.ModType == ModificationType.Multiply
                && item.Priority == int.MaxValue && !owned.Active)
            {
                owned.Id = item.Id; owned.Factor = item.ModValue; owned.Active = true;
                continue;
            }
            RegistryInterop.Remove(ref registry, player, ref component, ref field, item.Id);
            if (!item.Source.Equals(owned.Source)) _obsoleteSources.Add(item.Source);
            _plugin.Logger.LogInfo("[MoveSpeed v6][migrate] remove duplicate/legacy id=" + item.Id.Id
                + " type=" + item.ModType + " value=" + item.ModValue);
        }
    }

    internal void ReconcilePlayer(ref ModificationsRegistry registry, Entity player, PlayerState state)
    {
        var formTarget = GetFormTarget(player);
        if (!state.FormTarget.Equals(formTarget) && Em.Exists(state.FormTarget)
            && Em.HasComponent<Shapeshift>(state.FormTarget))
        {
            var oldForm = Em.GetComponentData<Shapeshift>(state.FormTarget);
            try { Remove(ref registry, state.FormTarget, ref oldForm, ref oldForm.Speed, state.FormSpeed); }
            finally { Em.SetComponentData(state.FormTarget, oldForm); }
        }
        state.FormTarget = formTarget;
        if (Em.Exists(formTarget))
        {
            var form = Em.GetComponentData<Shapeshift>(formTarget);
            try { ReconcileField(ref registry, formTarget, ref form, ref form.Speed, state.FormSpeed, player); }
            finally { Em.SetComponentData(formTarget, form); }
        }
        if (Em.HasComponent<Movement>(player))
        {
            var movement = Em.GetComponentData<Movement>(player);
            try { ReconcileField(ref registry, player, ref movement, ref movement.Speed, state.Walking, player); }
            finally { Em.SetComponentData(player, movement); }
        }
        if (Em.HasComponent<Mounter>(player))
        {
            var mount = Em.GetComponentData<Mounter>(player);
            try
            {
                ReconcileField(ref registry, player, ref mount, ref mount.GallopMaxSpeed, state.Gallop, player);
                ReconcileField(ref registry, player, ref mount, ref mount.Acceleration, state.Acceleration, player);
            }
            finally { Em.SetComponentData(player, mount); }
        }
        foreach (var obsolete in _obsoleteSources)
        {
            if (obsolete.Equals(state.Source)) continue;
            var references = registry.GetSourceModificationReferences(obsolete, Allocator.Temp);
            try { if (references.Length == 0 && Em.Exists(obsolete)) Em.DestroyEntity(obsolete); }
            finally { references.Dispose(); }
        }
        _obsoleteSources.Clear();
    }
}


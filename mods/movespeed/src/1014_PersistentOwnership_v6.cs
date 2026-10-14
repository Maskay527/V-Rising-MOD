using ProjectM;
using Unity.Entities;

namespace VRisingMoveSpeed;

// Use existing serializable native components, with no Buff, PrefabGUID or SpawnTag.
// The buffer is metadata only: no gameplay buff system sees it as an active buff.
internal static class PersistentOwnership
{
    private const float Signature = 121004f;
    private const float Signature2 = 270104f;
    internal static bool IsMarked(EntityManager em, Entity source)
    {
        if (!em.Exists(source) || !em.HasComponent<EntityOwner>(source)
            || !em.HasComponent<EntityCreator>(source) || !em.HasComponent<ModifyUnitStatBuff_DOTS>(source)
            || em.HasComponent<Buff>(source) || em.HasComponent<Stunlock.Core.PrefabGUID>(source)) return false;
        var buffer = em.GetBuffer<ModifyUnitStatBuff_DOTS>(source);
        return buffer.Length == 4 && buffer[0].Value == Signature && buffer[0].SoftCapValue == Signature2
            && buffer[0].Modifier == 4 && buffer[0].Priority == int.MaxValue
            && buffer[0].StatType == (UnitStatType)5;
    }

    internal static bool IsOwned(EntityManager em, Entity source, Entity player) =>
        IsMarked(em, source) && em.GetComponentData<EntityOwner>(source).Owner.Equals(player);

    internal static void Mark(EntityManager em, Entity source, Entity player)
    {
        if (IsMarked(em, source)) return;
        em.AddComponentData(source, new EntityOwner { Owner = player });
        em.AddComponentData(source, new EntityCreator { Creator = new NetworkedEntity { _Entity = Entity.Null } });
        var buffer = em.AddBuffer<ModifyUnitStatBuff_DOTS>(source);
        buffer.Add(new ModifyUnitStatBuff_DOTS { StatType = (UnitStatType)5, Value = Signature,
            SoftCapValue = Signature2, Modifier = 4, Priority = int.MaxValue, AttributeCapType = (AttributeCapType)(-1) });
        for (int index = 1; index <= 3; index++) buffer.Add(default);
    }

    internal static void Load(EntityManager em, PlayerState state)
    {
        state.Mount = em.GetComponentData<EntityCreator>(state.Source).Creator._Entity;
        var buffer = em.GetBuffer<ModifyUnitStatBuff_DOTS>(state.Source);
        Read(buffer[1], state.MountWalk); Read(buffer[2], state.MountReverse); Read(buffer[3], state.MountDeceleration);
    }

    internal static void Save(EntityManager em, PlayerState state)
    {
        if (!IsMarked(em, state.Source)) return;
        em.SetComponentData(state.Source, new EntityCreator { Creator = new NetworkedEntity { _Entity = state.Mount } });
        var buffer = em.GetBuffer<ModifyUnitStatBuff_DOTS>(state.Source);
        buffer[1] = Write(state.MountWalk); buffer[2] = Write(state.MountReverse); buffer[3] = Write(state.MountDeceleration);
    }

    private static void Read(ModifyUnitStatBuff_DOTS row, RawScaleState state)
    {
        state.Baseline = row.Value; state.LastWritten = row.Modifier; state.HasWrite = row.IncreaseByStacks;
    }
    private static ModifyUnitStatBuff_DOTS Write(RawScaleState state) => new()
    {
        Value = state.Baseline, Modifier = state.LastWritten, IncreaseByStacks = state.HasWrite
    };
}


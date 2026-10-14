using ProjectM;
using Stunlock.Core;
using Unity.Entities;

namespace VRisingAutoPath;

internal readonly struct BatFlightState
{
    internal readonly bool Bat, Flying, CanFly, Blocked, Shapeshifted;
    internal readonly float Speed;
    internal bool Ready => Bat && Flying && CanFly && !Blocked;
    internal BatFlightState(bool bat,bool flying,bool canFly,bool blocked,bool shapeshifted,float speed)
    { Bat=bat; Flying=flying; CanFly=canFly; Blocked=blocked; Shapeshifted=shapeshifted; Speed=speed; }

    internal static BatFlightState Read(World world,EntityManager em,Entity character,ref int batPrefabHash)
    {
        if(batPrefabHash==0)
        {
            var prefabs=world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if(prefabs!=null && prefabs._PrefabLookupMap.TryGetPrefabGuidWithName("AB_Shapeshift_Bat_TakeFlight_Buff",out PrefabGUID guid))
                batPrefabHash=guid.GuidHash;
        }
        bool bat=false;
        if(batPrefabHash!=0 && em.HasComponent<BuffBuffer>(character))
        {
            var buffs=em.GetBuffer<BuffBuffer>(character,true);
            for(int i=0;i<buffs.Length;i++)
                if(buffs[i].PrefabGuid.GuidHash==batPrefabHash && em.Exists(buffs[i].Entity)) {bat=true;break;}
        }
        bool flying=false,blocked=false;
        if(em.HasComponent<BuffableFlagState>(character))
        {
            var flags=em.GetComponentData<BuffableFlagState>(character);
            flying=flags.IsSet(BuffModificationTypes.IsFlying);
            blocked=flags.IsSet(BuffModificationTypes.MovementImpair);
        }
        bool shifted=false; float speed=0;
        if(em.HasComponent<Shapeshift>(character))
        {
            var form=em.GetComponentData<Shapeshift>(character);
            shifted=form.IsShapeshifted._Value; speed=form.Speed._Value;
        }
        return new BatFlightState(bat,flying,em.HasComponent<CanFly>(character),blocked,shifted,speed);
    }
}

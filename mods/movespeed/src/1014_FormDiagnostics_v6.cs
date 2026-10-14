using System;
using System.Globalization;
using System.Text;
using ProjectM;
using ProjectM.Gameplay.Scripting;
using ProjectM.Network;
using Unity.Entities;

namespace VRisingMoveSpeed;

internal sealed partial class ServerContext
{
    internal Entity GetFormTarget(Entity player)
    {
        if (Em.HasComponent<ControlledBy>(player))
        {
            var controller = Em.GetComponentData<ControlledBy>(player).Controller;
            if (Em.Exists(controller) && Em.HasComponent<Shapeshift>(controller)) return controller;
        }
        if (Em.HasComponent<PlayerCharacter>(player))
        {
            var user = Em.GetComponentData<PlayerCharacter>(player).UserEntity;
            if (Em.Exists(user) && Em.HasComponent<Shapeshift>(user)) return user;
        }
        // Supports pre-existing controlled fixtures and custom characters with a local component.
        return Em.HasComponent<Shapeshift>(player) ? player : Entity.Null;
    }

    // Read-only diagnostics. Do not infer which field drives movement from a buff name.
    internal string DescribeForm(Entity player, PlayerState state)
    {
        try
        {
            string name = "unknown";
            Entity controlled = Entity.Null;
            if (Em.HasComponent<PlayerCharacter>(player))
            {
                var userEntity = Em.GetComponentData<PlayerCharacter>(player).UserEntity;
                if (Em.Exists(userEntity) && Em.HasComponent<User>(userEntity))
                {
                    var user = Em.GetComponentData<User>(userEntity);
                    name = user.CharacterName.ToString();
                    controlled = user.LocalCharacter._Entity;
                }
            }
            var formTarget = GetFormTarget(player);
            bool hasShape = Em.Exists(formTarget);
            var shape = hasShape ? Em.GetComponentData<Shapeshift>(formTarget) : default;
            var text = new StringBuilder(" utc=").Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(" name=\"").Append(Clean(name)).Append('"')
                .Append(" controlledEntity=").Append(controlled.Index)
                .Append(" shapeEntity=").Append(formTarget.Index)
                .Append(" playerHasShape=").Append(Em.HasComponent<Shapeshift>(player))
                .Append(" hasShape=").Append(hasShape)
                .Append(" shapeFlag=").Append(hasShape ? shape.IsShapeshifted._Value.ToString() : "missing")
                .Append(" shapeSpeed=").Append(hasShape ? shape.Speed._Value.ToString("F3", CultureInfo.InvariantCulture) : "missing")
                .Append(" formOwnActive=").Append(state.FormSpeed.Active)
                .Append(" formOwnId=").Append(state.FormSpeed.Id.Id);
            AppendFormBuffs(text, player);
            return text.ToString();
        }
        catch (Exception error)
        {
            // Logging failures must not cause Update() to withdraw working movement modifiers.
            return " formDiagnosticError=\"" + Clean(error.GetType().Name + ": " + error.Message) + "\"";
        }
    }

    private void AppendFormBuffs(StringBuilder text, Entity player)
    {
        text.Append(" formBuffs=[");
        if (!Em.HasComponent<BuffBuffer>(player))
        {
            text.Append("no-buffer]");
            return;
        }
        var prefabs = World.GetExistingSystemManaged<PrefabCollectionSystem>();
        var buffs = Em.GetBuffer<BuffBuffer>(player);
        bool any = false;
        for (int index = 0; index < buffs.Length; index++)
        {
            var row = buffs[index];
            if (!Em.Exists(row.Entity)) continue;
            bool hasScript = Em.HasComponent<Script_Buff_Shapeshift_DataShared>(row.Entity);
            string buffName = prefabs == null ? row.PrefabGuid.GuidHash.ToString(CultureInfo.InvariantCulture)
                : prefabs._PrefabLookupMap.GetName(row.PrefabGuid);
            if (!hasScript && !buffName.Contains("Shapeshift", StringComparison.OrdinalIgnoreCase)
                && !buffName.Contains("BatForm", StringComparison.OrdinalIgnoreCase)
                && !buffName.Contains("TakeFlight", StringComparison.OrdinalIgnoreCase)) continue;
            if (any) text.Append(';');
            any = true;
            text.Append(Clean(buffName)).Append('#').Append(row.Entity.Index);
            if (hasScript) text.Append("(scriptSpeed=").Append(Em.GetComponentData<Script_Buff_Shapeshift_DataShared>(row.Entity)
                .MovementSpeed.ToString("F3", CultureInfo.InvariantCulture)).Append(')');
        }
        if (!any) text.Append("none");
        text.Append(']');
    }

    private static string Clean(string value) => (value ?? "unknown").Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
}

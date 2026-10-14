using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using ProjectM;
using Unity.Entities;

namespace VRisingMoveSpeed;

// The game's generated generic ref-TComponent wrappers marshal blittable structs as object refs.
// Reuse their closed native MethodInfo, but pin and pass the actual struct storage.
internal static unsafe class RegistryInterop
{
    private static class Methods<T> where T : unmanaged
    {
        internal static readonly IntPtr Add = Resolve("AddModification", "_ModifiableFloat_ModificationType_", typeof(T));
        internal static readonly IntPtr Update = Resolve("UpdateModification", "_ModifiableFloat_ModificationId_Single_", typeof(T));
        internal static readonly IntPtr Remove = Resolve("RemoveModification", "_ModifiableFloat_ModificationId_", typeof(T));
        internal static readonly IntPtr Entry = Resolve("TryGetEntry", "_ModifiableFloat_byref_Readonly_Single_", typeof(T));
    }

    private static IntPtr Resolve(string operation, string discriminator, Type component)
    {
        var store = typeof(ModificationsRegistry).GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name.StartsWith("MethodInfoStoreGeneric_" + operation + "_", StringComparison.Ordinal)
                && type.Name.Contains(discriminator, StringComparison.Ordinal));
        var pointer = store.MakeGenericType(component).GetField("Pointer", BindingFlags.Static | BindingFlags.NonPublic);
        if (pointer == null) throw new MissingFieldException("当前游戏互操作程序集缺少属性方法句柄。");
        return (IntPtr)pointer.GetValue(null);
    }

    internal static void Add<T>(ref ModificationsRegistry registry, Entity entity, ref T component,
        ref ModifiableFloat field, ModificationType type, float value, int priority, Entity source, out ModificationId id) where T : unmanaged
    {
        id = default;
        fixed (ModificationsRegistry* registryPtr = &registry)
        fixed (T* componentPtr = &component)
        fixed (ModifiableFloat* fieldPtr = &field)
        fixed (ModificationId* idPtr = &id)
        {
            void** args = stackalloc void*[8];
            args[0] = &entity; args[1] = componentPtr; args[2] = fieldPtr; args[3] = &type;
            args[4] = &value; args[5] = &priority; args[6] = &source; args[7] = idPtr;
            IntPtr error = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(Methods<T>.Add, (IntPtr)registryPtr, args, ref error);
            Il2CppException.RaiseExceptionIfNecessary(error);
        }
    }

    internal static void Update<T>(ref ModificationsRegistry registry, Entity entity, ref T component,
        ref ModifiableFloat field, ModificationId id, float value) where T : unmanaged
    {
        fixed (ModificationsRegistry* registryPtr = &registry)
        fixed (T* componentPtr = &component)
        fixed (ModifiableFloat* fieldPtr = &field)
        {
            void** args = stackalloc void*[5];
            args[0] = &entity; args[1] = componentPtr; args[2] = fieldPtr; args[3] = &id; args[4] = &value;
            IntPtr error = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(Methods<T>.Update, (IntPtr)registryPtr, args, ref error);
            Il2CppException.RaiseExceptionIfNecessary(error);
        }
    }

    internal static void Remove<T>(ref ModificationsRegistry registry, Entity entity, ref T component,
        ref ModifiableFloat field, ModificationId id) where T : unmanaged
    {
        fixed (ModificationsRegistry* registryPtr = &registry)
        fixed (T* componentPtr = &component)
        fixed (ModifiableFloat* fieldPtr = &field)
        {
            void** args = stackalloc void*[4];
            args[0] = &entity; args[1] = componentPtr; args[2] = fieldPtr; args[3] = &id;
            IntPtr error = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(Methods<T>.Remove, (IntPtr)registryPtr, args, ref error);
            Il2CppException.RaiseExceptionIfNecessary(error);
        }
    }

    private static readonly IntPtr CurrentValueMethod = (IntPtr)typeof(ModificationEnumerator<float>)
        .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(field => field.Name.StartsWith("NativeMethodInfoPtr_System_Collections_Generic_IEnumerator_", StringComparison.Ordinal)
            && field.Name.Contains("get_Current", StringComparison.Ordinal)).GetValue(null);

    internal static bool Entry<T>(ref ModificationsRegistry registry, Entity entity, ref T component,
        ref ModifiableFloat field, out ModifiableEntry<float>.Readonly entry) where T : unmanaged
    {
        entry = default;
        fixed (ModificationsRegistry* r = &registry) fixed (T* c = &component)
        fixed (ModifiableFloat* f = &field) fixed (ModifiableEntry<float>.Readonly* e = &entry)
        {
            void** args = stackalloc void*[4]; args[0] = &entity; args[1] = c; args[2] = f; args[3] = e;
            IntPtr error = IntPtr.Zero;
            var result = IL2CPP.il2cpp_runtime_invoke(Methods<T>.Entry, (IntPtr)r, args, ref error);
            Il2CppException.RaiseExceptionIfNecessary(error);
            return *(bool*)IL2CPP.il2cpp_object_unbox(result);
        }
    }

    private static ModificationData<float> Current(ref ModificationEnumerator<float> iterator)
    {
        fixed (ModificationEnumerator<float>* p = &iterator)
        {
            IntPtr error = IntPtr.Zero;
            var result = IL2CPP.il2cpp_runtime_invoke(CurrentValueMethod, (IntPtr)p, null, ref error);
            Il2CppException.RaiseExceptionIfNecessary(error);
            return *(ModificationData<float>*)IL2CPP.il2cpp_object_unbox(result);
        }
    }

    internal static System.Collections.Generic.List<ModificationData<float>> Snapshot<T>(ref ModificationsRegistry registry,
        Entity entity, ref T component, ref ModifiableFloat field) where T : unmanaged
    {
        var result = new System.Collections.Generic.List<ModificationData<float>>();
        if (!Entry(ref registry, entity, ref component, ref field, out var entry)) return result;
        var iterator = entry.Modifications;
        while (iterator.MoveNext())
        {
            if (result.Count >= 4096) throw new InvalidOperationException("移速修改列表超出合理范围。");
            result.Add(Current(ref iterator));
        }
        return result;
    }
}




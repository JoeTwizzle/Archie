﻿using System;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Archie.Helpers;

namespace Archie
{
    public sealed class World
    {
        /// <summary>
        /// Stores in which architype an entity is
        /// </summary>
        readonly Dictionary<EntityId, ComponentIndexRecord> EntityIndex;
        /// <summary>
        /// Archetype storing all entities with no components
        /// </summary>
        readonly Dictionary<int, Archetype> AllArchetypes;
        /// <summary>
        /// Used to find the archetypes containing a component
        /// </summary>
        readonly Dictionary<Type, Dictionary<ArchitypeId, TypeIndexRecord>> ComponentIndex;

        public World()
        {
            EntityIndex = new();
            ComponentIndex = new();
            AllArchetypes = new();
        }

        #region Helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Archetype GetArchetype(in EntityId entity)
        {
            return GetComponentIndexRecord(entity).Archetype;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref ComponentIndexRecord GetComponentIndexRecord(in EntityId entity)
        {
            return ref CollectionsMarshal.GetValueRefOrNullRef(EntityIndex, entity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Dictionary<ArchitypeId, TypeIndexRecord> GetContainingArchetypes(Type componentType)
        {
            return ComponentIndex[componentType];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] GetComponentPool<T>(Archetype archetype) where T : struct, IComponent<T>
        {
            return (T[])archetype.ComponentPools[GetTypeIndexRecord<T>(archetype).ComponentTypeIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TypeIndexRecord GetTypeIndexRecord<T>(Archetype archetype) where T : struct, IComponent<T>
        {
            var archetypes = GetContainingArchetypes(typeof(T));
            return ref CollectionsMarshal.GetValueRefOrNullRef(archetypes, archetype.Id);
        }

        public static int GetComponentHash(Span<Type> componentTypes)
        {
            componentTypes.Sort();
            int hash = 0;
            for (int i = 0; i < componentTypes.Length; i++)
            {
                hash = HashCode.Combine(hash, componentTypes[i].GetHashCode());
            }
            return hash;
        }

        #endregion

        #region Component Operations
        public void AddComponentImmediate<T>(in EntityId entity) where T : struct, IComponent<T>
        {
            var arch = GetArchetype(entity);

            var newArch = GetOrCreateArchetypeVariantAdd<T>(arch);

            var i = GetTypeIndexRecord<T>(newArch).ComponentTypeIndex;
            //Move entity to new archetype
            //Will want to delay this in future
            var index = MoveEntityImmediate(arch, newArch, entity);
            T.Init(ref ((T[])newArch.ComponentPools[i])[index]);
        }

        public void RemoveComponentImmediate<T>(in EntityId entity) where T : struct, IComponent<T>
        {
            var arch = GetArchetype(entity);
            var i = GetTypeIndexRecord<T>(arch).ComponentTypeIndex;
            var index = GetComponentIndexRecord(entity).ComponentIndex;
            T.Del(ref ((T[])arch.ComponentPools[i])[index]);
            var newArch = GetOrCreateArchetypeVariantRemove<T>(arch);
            //Move entity to new archetype
            //Will want to delay this in future
            MoveEntityImmediate(arch, newArch, entity);
        }

        public bool HasComponent<T>(in EntityId entity) where T : struct, IComponent<T>
        {
            return HasComponent(entity, typeof(T));
        }

        public bool HasComponent(EntityId entity, Type component)
        {
            ref ComponentIndexRecord record = ref GetComponentIndexRecord(entity);
            Archetype archetype = record.Archetype;
            Dictionary<ArchitypeId, TypeIndexRecord> archetypes = ComponentIndex[component];
            return archetypes.ContainsKey(archetype.Id);
        }

        public ref T GetComponent<T>(EntityId entity) where T : struct, IComponent<T>
        {
            // First check if archetype has component
            ref ComponentIndexRecord record = ref GetComponentIndexRecord(entity);
            if (Unsafe.IsNullRef(ref record))
            {
                ThrowHelper.ThrowNullRefrenceException($"Entity Id:{entity.Id} does not have a component of type {typeof(T).Name} attached");
            }
            //Get the pool of components
            var pool = GetComponentPool<T>(record.Archetype).AsSpan();
            return ref pool[(int)record.ComponentIndex];
        }

        #endregion

        #region Archetype Operations
        public Archetype? GetArchetype(Span<Type> types)
        {
            int length = types.Length;
            var pool = ArrayPool<Type>.Shared.Rent(length);
            types.CopyTo(pool);
            int hash = GetComponentHash(pool.AsSpan(0, length));
            ArrayPool<Type>.Shared.Return(pool);
            return AllArchetypes.GetValueOrDefault(hash);
        }

        private Archetype? FindSiblingRemove<T>(Archetype source) where T : struct, IComponent<T>
        {
            if (source.Siblings.TryGetValue(typeof(T), out var siblings))
            {
                return siblings.Remove;
            }
            //Find hash
            int length = source.Types.Length - 1;
            var pool = ArrayPool<Type>.Shared.Rent(length);
            int index = 0;
            for (int i = 0; i < source.Types.Length; i++)
            {
                var compType = source.Types[i];
                if (compType != typeof(T))
                {
                    pool[index++] = compType;
                }
            }
            int hash = GetComponentHash(pool.AsSpan(0, length));
            ArrayPool<Type>.Shared.Return(pool);
            return AllArchetypes.GetValueOrDefault(hash);
        }

        public Archetype? FindSibling<T>(Archetype source, bool add) where T : struct, IComponent<T>
        {
            return add ? FindSiblingAdd<T>(source) : FindSiblingRemove<T>(source);
        }

        public Archetype GetOrCreateArchetypeVariant<T>(Archetype source, bool add) where T : struct, IComponent<T>
        {
            return add ? GetOrCreateArchetypeVariantAdd<T>(source) : GetOrCreateArchetypeVariantRemove<T>(source);
        }

        private Archetype GetOrCreateArchetypeVariantRemove<T>(Archetype source) where T : struct, IComponent<T>
        {
            var arch = FindSiblingRemove<T>(source);
            if (arch != null)
            {
                return arch;
            }
            Type[] pools = new Type[source.Types.Length - 1];
            int index = 0;
            for (int i = 0; i < source.Types.Length; i++)
            {
                var compPool = source.Types[i];
                if (compPool != typeof(T))
                {
                    pools[index++] = compPool;
                }
            }
            var archetype = CreateArchetype(pools);
            return archetype;
        }

        private Archetype? FindSiblingAdd<T>(Archetype source) where T : struct, IComponent<T>
        {
            if (source.Siblings.TryGetValue(typeof(T), out var siblings))
            {
                return siblings.Add;
            }
            //Find hash
            int length = source.Types.Length + 1;
            var pool = ArrayPool<Type>.Shared.Rent(length);
            for (int i = 0; i < source.Types.Length; i++)
            {
                pool[i] = source.Types[i];
            }
            pool[length - 1] = typeof(T);
            int hash = GetComponentHash(pool.AsSpan(0, length));
            ArrayPool<Type>.Shared.Return(pool);
            return AllArchetypes.GetValueOrDefault(hash);
        }

        private Archetype GetOrCreateArchetypeVariantAdd<T>(Archetype source) where T : struct, IComponent<T>
        {
            var arch = FindSiblingAdd<T>(source);
            if (arch != null)
            {
                return arch;
            }
            Type[] pools = new Type[source.Types.Length + 1];
            for (int i = 0; i < source.Types.Length; i++)
            {
                pools[i] = source.Types[i];
            }
            pools[pools.Length - 1] = typeof(T);
            var archetype = CreateArchetype(pools);
            //TODO: Store in index
            return archetype;
        }

        internal Archetype CreateArchetype(Type[] types)
        {
            // Create
            var archetype = new Archetype(types);
            // Store in index
            for (uint i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (!ComponentIndex.TryGetValue(type, out var dict))
                {
                    dict = new();
                    ComponentIndex.Add(type, dict);
                }
                dict.Add(archetype.Id, new TypeIndexRecord(i));
            }
            // Calculate hash
            int length = types.Length;
            var pool = ArrayPool<Type>.Shared.Rent(length);
            for (int i = 0; i < length; i++)
            {
                pool[i] = types[i];
            }
            int hash = GetComponentHash(pool.AsSpan(0, length));
            ArrayPool<Type>.Shared.Return(pool);
            // Store in all archetypes
            AllArchetypes.Add(hash, archetype);
            return archetype;
        }
        #endregion

        #region Entity Operations


        public EntityId CreateEntityImmediate()
        {
            var entity = GetNextEntity();
            var ts = Array.Empty<Type>();
            var archetype = GetArchetype(ts);
            archetype ??= CreateArchetype(ts);
            AddEntityImmediate(archetype, entity);
            return entity;
        }

        internal EntityId GetNextEntity()
        {
            return new EntityId();
        }

        internal uint MoveEntityImmediate(Archetype? src, Archetype dest, in EntityId entity)
        {
            Debug.Assert(src != dest);

            if (src != null)
            {
                ref ComponentIndexRecord compIndexRecord = ref GetComponentIndexRecord(entity);
                uint oldIndex = compIndexRecord.ComponentIndex;
                //Add to new Archetype
                dest.GrowIfNeeded(1);
                uint newIndex = dest.entityCount++;
                //Copy data to new Arrays
                for (int i = 0; i < dest.Types.Length; i++)
                {
                    if (ComponentIndex[dest.Types[i]].TryGetValue(src.Id, out var typeIndexRecord))
                    {
                        Array.Copy(src.ComponentPools[typeIndexRecord.ComponentTypeIndex], oldIndex, dest.ComponentPools[i], newIndex, 1);
                    }
                }
                //Compact old Arrays
                for (int i = 0; i < src.ComponentPools.Length; i++)
                {
                    var pool = src.ComponentPools[i];
                    Array.Copy(pool, oldIndex + 1, pool, oldIndex, src.entityCount - (oldIndex - 1));
                }
                //Remove from old Archetype
                src.entityCount--;
                compIndexRecord.ComponentIndex = newIndex;
                compIndexRecord.Archetype = dest;
                return newIndex;
            }
            return AddEntityImmediate(dest, entity);
        }

        internal uint AddEntityImmediate(Archetype dest, in EntityId entity)
        {
            if (!EntityIndex.ContainsKey(entity))
            {
                dest.GrowIfNeeded(1);
                EntityIndex.Add(entity, new ComponentIndexRecord() { Archetype = dest, ComponentIndex = dest.entityCount });
                return dest.entityCount++;
            }
            ref ComponentIndexRecord compIndexRecord = ref GetComponentIndexRecord(entity);
            compIndexRecord.Archetype = dest;
            dest.GrowIfNeeded(1);
            return compIndexRecord.ComponentIndex = dest.entityCount++;
        }

        internal uint RemoveEntityImmediate(Archetype src, in EntityId entity)
        {
            ref ComponentIndexRecord compIndexRecord = ref GetComponentIndexRecord(entity);

            uint index = compIndexRecord.ComponentIndex;
            for (int i = 0; i < src.ComponentPools.Length; i++)
            {
                var pool = src.ComponentPools[i];
                Array.Copy(pool, index + 1, pool, index, src.entityCount - (index - 1));
            }
            src.entityCount--;
            return index;
        }

        internal void DestroyEntityImmediate(Archetype src, in EntityId entity)
        {
            RemoveEntityImmediate(src, entity);
            Debug.Assert(EntityIndex.Remove(entity));
        }

        #endregion
    }
}

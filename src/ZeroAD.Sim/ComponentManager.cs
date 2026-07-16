using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim
{
    /// <summary>
    /// Message broadcast to all components on an entity (or all entities).
    /// Mirrors <c>MT_*</c> messages from <c>TypeList.h</c>.
    /// </summary>
    public interface IMessage
    {
        int TypeId { get; }
    }

    public sealed class ComponentManager
    {
        private readonly EntityManager _entityManager = new();
        private readonly Rand48 _rng;
        private readonly ComponentRegistry _registry;
        private readonly Dictionary<EntityId, Dictionary<InterfaceId, IComponent>> _componentsByEntity = new();
        private readonly List<EntityId> _allEntities = new();

        public EntityManager Entities => _entityManager;
        public Rand48 RNG => _rng;
        public ComponentRegistry Registry => _registry;
        public IReadOnlyList<EntityId> AllEntities => _allEntities;

        public ComponentManager(uint rngSeed, ComponentRegistry? registry = null)
        {
            _rng = new Rand48(rngSeed);
            _registry = registry ?? new ComponentRegistry();
        }

        public EntityId CreateEntity()
        {
            var id = _entityManager.AllocateEntity();
            _componentsByEntity[id] = new Dictionary<InterfaceId, IComponent>();
            _allEntities.Add(id);
            return id;
        }

        public void AddComponent(EntityId entity, ComponentTypeId cid)
        {
            var component = _registry.CreateComponent(cid);
            component.SetEntity(entity);
            var iid = _registry.GetInterfaceForComponent(cid);
            _componentsByEntity[entity][iid] = component;
            ((IComponent)component).Init();
        }

        public void AddComponent<T>(EntityId entity, T component) where T : ComponentBase
        {
            component.SetEntity(entity);
            var iid = _registry.GetInterfaceIdForType<T>();
            if (!iid.IsValid)
            {
                var attr = (ComponentAttribute?)Attribute.GetCustomAttribute(
                    typeof(T), typeof(ComponentAttribute));
                iid = attr != null
                    ? _registry.RegisterInterface(attr.InterfaceName)
                    : _registry.RegisterInterface(typeof(T).Name);
                _registry.CacheTypeMapping<T>(iid);
            }
            _componentsByEntity[entity][iid] = component;
            ((IComponent)component).Init();
        }

        public T? QueryInterface<T>(EntityId entity) where T : class, IComponent
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return null;

            var iid = _registry.GetInterfaceIdForType<T>();
            if (iid.IsValid && components.TryGetValue(iid, out var compDirect))
                return compDirect as T;

            foreach (var comp in components.Values)
                if (comp is T typed)
                    return typed;
            return null;
        }

        public IComponent? QueryInterface(EntityId entity, InterfaceId iid)
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return null;
            return components.TryGetValue(iid, out var comp) ? comp : null;
        }

        public void PostMessage(EntityId entity, IMessage message)
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return;
            foreach (var kvp in components)
                if (kvp.Value is IComponentMessageHandler handler)
                    handler.HandleMessage(message);
        }

        public void BroadcastMessage(IMessage message)
        {
            foreach (var kvp in _componentsByEntity)
                foreach (var comp in kvp.Value.Values)
                    if (comp is IComponentMessageHandler handler)
                        handler.HandleMessage(message);
        }

        public void DestroyEntity(EntityId entity)
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return;
            foreach (var comp in components.Values)
                comp.Deinit();
            _componentsByEntity.Remove(entity);
            _allEntities.Remove(entity);
        }

        public void ResetState()
        {
            foreach (var components in _componentsByEntity.Values)
                foreach (var comp in components.Values)
                    comp.Deinit();
            _componentsByEntity.Clear();
            _allEntities.Clear();
            _entityManager.Reset();
        }

        public byte[] ComputeStateHash()
        {
            var serializer = new Serialization.HashSerializer();
            serializer.StringASCII("rng", _rng.Serialize());
            serializer.NumberU32("next entity id", _entityManager.NextEntityId);

            foreach (var kvp in _componentsByEntity)
            {
                if (kvp.Key.IsLocal)
                    continue;
                serializer.NumberU32("entity", kvp.Key.Value);
                foreach (var comp in kvp.Value.Values)
                    comp.Serialize(serializer);
            }

            return serializer.ComputeHash();
        }
    }

    public interface IComponentMessageHandler
    {
        void HandleMessage(IMessage message);
    }
}

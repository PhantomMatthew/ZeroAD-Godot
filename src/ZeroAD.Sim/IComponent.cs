using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim
{
    /// <summary>
    /// Base interface for all simulation components.
    /// Components are attached to entities and respond to messages.
    /// </summary>
    public interface IComponent
    {
        EntityId Entity { get; }
        void Init();
        void Deinit();
        void Serialize(ISerializer serializer);
        void Deserialize(IDeserializer deserializer);
    }

    public abstract class ComponentBase : IComponent
    {
        public EntityId Entity { get; private set; }

        void IComponent.Init() => OnInit();
        void IComponent.Deinit() => OnDeinit();

        protected virtual void OnInit() { }
        protected virtual void OnDeinit() { }

        public abstract void Serialize(ISerializer serializer);
        public abstract void Deserialize(IDeserializer deserializer);

        internal void SetEntity(EntityId entity) => Entity = entity;
    }
}

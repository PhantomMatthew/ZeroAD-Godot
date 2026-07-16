using System;
using System.Collections.Generic;

namespace ZeroAD.Sim
{
    /// <summary>
    /// Identifies a component interface type. Dynamic (not enum) to support mod-loaded components.
    /// Mirrors the <c>IID_*</c> concept from <c>TypeList.h</c> without compile-time coupling.
    /// </summary>
    public readonly struct InterfaceId : IEquatable<InterfaceId>
    {
        public readonly int Value;
        public readonly string Name;

        public static readonly InterfaceId Invalid = new(0, "Invalid");

        public InterfaceId(int value, string name)
        {
            Value = value;
            Name = name;
        }

        public bool IsValid => Value != 0;
        public bool Equals(InterfaceId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is InterfaceId i && Equals(i);
        public override int GetHashCode() => Value;
        public override string ToString() => Name;
        public static bool operator ==(InterfaceId a, InterfaceId b) => a.Value == b.Value;
        public static bool operator !=(InterfaceId a, InterfaceId b) => a.Value != b.Value;
    }

    /// <summary>Identifies a component implementation type.</summary>
    public readonly struct ComponentTypeId : IEquatable<ComponentTypeId>
    {
        public readonly int Value;
        public readonly string Name;

        public static readonly ComponentTypeId Invalid = new(0, "Invalid");

        public ComponentTypeId(int value, string name)
        {
            Value = value;
            Name = name;
        }

        public bool IsValid => Value != 0;
        public bool Equals(ComponentTypeId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is ComponentTypeId c && Equals(c);
        public override int GetHashCode() => Value;
        public override string ToString() => Name;
    }

    /// <summary>Attribute marking a component class with its interface and name.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ComponentAttribute : Attribute
    {
        public string Name { get; }
        public string InterfaceName { get; }

        public ComponentAttribute(string name, string interfaceName)
        {
            Name = name;
            InterfaceName = interfaceName;
        }
    }

    public sealed class ComponentRegistry
    {
        private int _nextIid = 1;
        private int _nextCid = 1;
        private readonly Dictionary<string, InterfaceId> _iidsByName = new();
        private readonly Dictionary<string, ComponentTypeId> _cidsByName = new();
        private readonly Dictionary<ComponentTypeId, InterfaceId> _cidToIid = new();
        private readonly Dictionary<ComponentTypeId, Func<ComponentBase>> _factories = new();
        private readonly Dictionary<InterfaceId, ComponentTypeId> _defaultImpl = new();
        private readonly Dictionary<Type, InterfaceId> _typeCache = new();

        public InterfaceId GetInterfaceIdForType<T>() where T : class =>
            _typeCache.TryGetValue(typeof(T), out var iid) ? iid : InterfaceId.Invalid;

        public void CacheTypeMapping<T>(InterfaceId iid) => _typeCache[typeof(T)] = iid;

        public InterfaceId RegisterInterface(string name)
        {
            if (_iidsByName.TryGetValue(name, out var existing))
                return existing;
            var iid = new InterfaceId(_nextIid++, name);
            _iidsByName[name] = iid;
            return iid;
        }

        public InterfaceId GetInterface(string name) =>
            _iidsByName.TryGetValue(name, out var iid) ? iid : InterfaceId.Invalid;

        public ComponentTypeId RegisterComponent<T>(string name, string interfaceName)
            where T : ComponentBase, new()
        {
            if (_cidsByName.TryGetValue(name, out var existing))
                return existing;

            var iid = RegisterInterface(interfaceName);
            var cid = new ComponentTypeId(_nextCid++, name);

            _cidsByName[name] = cid;
            _cidToIid[cid] = iid;
            _factories[cid] = () => new T();

            if (!_defaultImpl.ContainsKey(iid))
                _defaultImpl[iid] = cid;

            return cid;
        }

        public ComponentTypeId GetComponentType(string name) =>
            _cidsByName.TryGetValue(name, out var cid) ? cid : ComponentTypeId.Invalid;

        public InterfaceId GetInterfaceForComponent(ComponentTypeId cid) =>
            _cidToIid.TryGetValue(cid, out var iid) ? iid : InterfaceId.Invalid;

        public ComponentBase CreateComponent(ComponentTypeId cid) =>
            _factories.TryGetValue(cid, out var factory)
                ? factory()
                : throw new InvalidOperationException($"Unknown component type: {cid}");

        public ComponentTypeId? GetDefaultImplementation(InterfaceId iid) =>
            _defaultImpl.TryGetValue(iid, out var cid) ? cid : null;

        /// <summary>Auto-register all types in an assembly marked with <see cref="ComponentAttribute"/>.</summary>
        public void AutoRegister(System.Reflection.Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = (ComponentAttribute?)Attribute.GetCustomAttribute(type, typeof(ComponentAttribute));
                if (attr == null)
                    continue;
                if (!typeof(ComponentBase).IsAssignableFrom(type))
                    continue;

                var method = typeof(ComponentRegistry).GetMethod(nameof(RegisterComponent))!
                    .MakeGenericMethod(type);
                method.Invoke(this, new object[] { attr.Name, attr.InterfaceName });
            }
        }

        public string GenerateSchema()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<Components>");
            foreach (var kvp in _cidsByName)
            {
                var iid = _cidToIid[kvp.Value];
                sb.AppendLine($"  <Component name=\"{kvp.Key}\" interface=\"{iid.Name}\"/>");
            }
            sb.AppendLine("</Components>");
            return sb.ToString();
        }
    }
}

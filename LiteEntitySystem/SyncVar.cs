using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Possible flags for how a SyncVar behaves (e.g., interpolated, rollback, etc.).
    /// </summary>
    [Flags]
    public enum SyncFlags : byte
    {
        None                = 0,
        Interpolated        = 1,
        LagCompensated      = 1 << 1,
        OnlyForOtherPlayers = 1 << 2,
        OnlyForOwner        = 1 << 3,
        AlwaysRollback      = 1 << 4,
        NeverRollBack       = 1 << 5
    }

    /// <summary>
    /// An attribute you can place on fields or classes to indicate special SyncVar flags or execution order.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class)]
    public class SyncVarFlags : Attribute
    {
        public readonly SyncFlags Flags;
        public readonly OnSyncExecutionOrder OnSyncExecutionOrder;
        public readonly string OnChangeCallback;
        
        public SyncVarFlags(SyncFlags flags)
        {
            Flags = flags;
        }
        
        public SyncVarFlags(string onChangeCallback)
        {
            OnChangeCallback = onChangeCallback;
        }
        
        public SyncVarFlags(SyncFlags flags, string onChangeCallback)
        {
            Flags = flags;
            OnChangeCallback = onChangeCallback;
        }
        
        public SyncVarFlags(OnSyncExecutionOrder executionOrder)
        {
            OnSyncExecutionOrder = executionOrder;
        }
        
        public SyncVarFlags(SyncFlags flags, OnSyncExecutionOrder executionOrder)
        {
            Flags = flags;
            OnSyncExecutionOrder = executionOrder;
        }
        
        public SyncVarFlags(SyncFlags flags, OnSyncExecutionOrder executionOrder, string onChangeCallback)
        {
            Flags = flags;
            OnSyncExecutionOrder = executionOrder;
            OnChangeCallback = onChangeCallback;
        }
    }

    /// <summary>
    /// Standard event arguments for SyncVar changes, containing old and new values.
    /// </summary>
    public class SyncVarChangedEventArgs<T> : EventArgs
    {
        public T OldValue { get; }
        public T NewValue { get; }

        public SyncVarChangedEventArgs(T oldValue, T newValue)
        {
            OldValue = oldValue;
            NewValue = newValue;
        }
    }

    /// <summary>
    /// Provides a standard interface for receiving SyncVar change notifications.
    /// </summary>
    public interface INotifySyncVarChanged<T>
    {
        event EventHandler<SyncVarChangedEventArgs<T>> ValueChanged;
    }

    /// <summary>
    /// A network-synced variable stored as a struct but able to raise events when changed. 
    /// The event handlers are stored in a static dictionary keyed by (InternalEntity, fieldId).
    /// This avoids losing subscriptions when the struct is copied.
    /// 
    /// NOTE: Make sure to remove subscriptions for destroyed entities to prevent memory leaks!
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SyncVar<T> : INotifySyncVarChanged<T>, IEquatable<T>, IEquatable<SyncVar<T>>
        where T : unmanaged
    {
        /// <summary>
        /// The actual backing field for the data.
        /// </summary>
        private T _value;

        /// <summary>
        /// The unique identifier for this SyncVar within the Container.
        /// Typically set by reflection or other initialization code.
        /// </summary>
        internal ushort FieldId;

        /// <summary>
        /// The entity (owner) that contains this SyncVar. 
        /// Used for dictionary lookups and sending network change notifications.
        /// </summary>
        internal InternalEntity Container;

        /// <summary>
        /// A static dictionary of (Container, FieldId) => Combined event delegates.
        /// We store all subscriptions here to avoid losing them on struct copies.
        /// </summary>
        private static readonly Dictionary<(InternalEntity entity, ushort fieldId), 
            EventHandler<SyncVarChangedEventArgs<T>>> _eventRegistry = new();

        /// <summary>
        /// A lock to ensure thread-safe modifications to the static _eventRegistry.
        /// Remove this if you are certain your code is single-threaded.
        /// </summary>
        private static readonly object _registryLock = new();

        /// <summary>
        /// Fired when the Value changes. 
        /// Under the hood, this adds/removes the handler from _eventRegistry keyed by (Container, FieldId).
        /// </summary>
        public event EventHandler<SyncVarChangedEventArgs<T>> ValueChanged
        {
            add
            {
                if (Container == null)
                {
                    throw new InvalidOperationException(
                        "SyncVar<T> ValueChanged must be registered after base.RegisterRPC");
                }

                lock (_registryLock)
                {
                    var key = (Container, FieldId);
                    if (_eventRegistry.TryGetValue(key, out var existing))
                    {
                        _eventRegistry[key] = existing + value;
                    }
                    else
                    {
                        _eventRegistry[key] = value;
                    }
                }
            }
            remove
            {
                lock (_registryLock)
                {
                    var key = (Container, FieldId);
                    if (_eventRegistry.TryGetValue(key, out var existing))
                    {
                        var newDelegate = existing - value;
                        if (newDelegate == null)
                            _eventRegistry.Remove(key);
                        else
                            _eventRegistry[key] = newDelegate;
                    }
                }
            }
        }

        /// <summary>
        /// Get or set the underlying value. Setting will trigger an event if it actually changes.
        /// </summary>
        public T Value
        {
            get => _value;
            set
            {
                if (!Utils.FastEquals(ref value, ref _value))
                {
                    T oldVal = _value;

                    // Notify the entity manager for networking or other logic.
                    Container?.EntityManager.EntityFieldChanged(Container, FieldId, ref value);

                    _value = value;

                    // Look up the event handler in the registry and invoke it.
                    lock (_registryLock)
                    {
                        var key = (Container, FieldId);
                        if (_eventRegistry.TryGetValue(key, out var handler))
                        {
                            var args = new SyncVarChangedEventArgs<T>(oldVal, value);
                            // We pass Container as the 'sender' (could also pass 'this' if desired).
                            handler.Invoke(Container, args);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Internal direct set, bypassing event logic (for initializations).
        /// </summary>
        internal void SetDirect(T value) => _value = value;

        /// <summary>
        /// Called to initialize this SyncVar with its owning entity and field ID.
        /// Typically invoked after reflection or manual setup.
        /// </summary>
        internal void Init(InternalEntity container, ushort fieldId)
        {
            Container = container;
            FieldId = fieldId;
            // Immediately notify the manager that this field exists. 
            // You could pass ref _value if needed for some initial sync.
            Container?.EntityManager.EntityFieldChanged(Container, FieldId, ref _value);
        }

        /// <summary>
        /// Sets Value from raw network data. If the value changes, returns true.
        /// Also swaps the old value out (for some network reconciliation logic).
        /// </summary>
        internal unsafe bool SetFromAndSync(byte* data)
        {
            if (!Utils.FastEquals(ref _value, data))
            {
                // If there's a change, swap and return true
                var temp = _value;
                _value = *(T*)data;
                *(T*)data = temp; // put the old value into data
                return true;
            }
            // If no change, just update from data, return false
            _value = *(T*)data;
            return false;
        }

        /// <summary>
        /// Allows implicit casting of SyncVar{T} to T.
        /// </summary>
        public static implicit operator T(SyncVar<T> sv) => sv._value;

        /// <inheritdoc />
        public override string ToString() => _value.ToString();

        /// <inheritdoc />
        public override int GetHashCode() => _value.GetHashCode();

        /// <inheritdoc />
        public override bool Equals(object o) 
            => o is SyncVar<T> sv && Utils.FastEquals(ref sv._value, ref _value);

        /// <summary>
        /// Equality operator compares underlying values by your Utils.FastEquals logic.
        /// </summary>
        public static bool operator ==(SyncVar<T> a, SyncVar<T> b) 
            => Utils.FastEquals(ref a._value, ref b._value);

        public static bool operator !=(SyncVar<T> a, SyncVar<T> b)
            => !Utils.FastEquals(ref a._value, ref b._value);

        public static bool operator ==(T a, SyncVar<T> b)
            => Utils.FastEquals(ref a, ref b._value);

        public static bool operator !=(T a, SyncVar<T> b)
            => !Utils.FastEquals(ref a, ref b._value);

        public static bool operator ==(SyncVar<T> a, T b)
            => Utils.FastEquals(ref a._value, ref b);

        public static bool operator !=(SyncVar<T> a, T b)
            => !Utils.FastEquals(ref a._value, ref b);

        public bool Equals(T other) => Utils.FastEquals(ref _value, ref other);
        
        public bool Equals(SyncVar<T> other) => Utils.FastEquals(ref _value, ref other._value);
    }
}

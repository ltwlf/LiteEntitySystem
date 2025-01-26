using System;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Base class for user-defined sync fields that replicate data from server to client.
    /// Provides a "OnValueChanged" event on the client and enforces that 
    /// the subclass calls RaiseClientValueChanged() when applying new server data.
    /// </summary>
    public abstract class SyncableField : InternalBaseClass
    {
        // Fields used by LiteEntitySystem for hooking up server <-> client sync
        internal InternalEntity ParentEntityInternal;
        internal ExecuteFlags Flags;
        internal ushort RPCOffset;

        /// <summary>
        /// Event fired on the client whenever new data from the server
        /// actually changes this field's value.
        /// </summary>
        public event Action<SyncableField> OnValueChanged;

        /// <summary>
        /// True if we are on the server side (owning instance).
        /// </summary>
        protected internal bool IsServer => ParentEntityInternal != null && ParentEntityInternal.IsServer;

        /// <summary>
        /// True if we are on the client side.
        /// </summary>
        protected internal bool IsClient => !IsServer;

        /// <summary>
        /// By default, we assume no rollback (e.g. for client prediction).
        /// Subclasses can override if they store enough data to rollback.
        /// </summary>
        public virtual bool IsRollbackSupported => false;

        // A private flag to detect if we have called "RaiseClientValueChanged()" 
        // during this RPC read cycle
        private bool _didRaiseThisRPC;

        // ----------------------------------------------------------------
        //  Overriding Hooks from InternalBaseClass
        // ----------------------------------------------------------------

        /// <summary>
        /// Called before reading an incoming RPC from the server. 
        /// We reset our detection flag here.
        /// </summary>
        protected internal virtual void BeforeReadRPC()
        {
            _didRaiseThisRPC = false;
        }

        /// <summary>
        /// Called after reading an incoming RPC from the server. 
        /// If the subclass never called "RaiseClientValueChanged()"
        /// but the field was updated on the client, 
        /// we throw an exception to enforce usage.
        /// </summary>
        protected internal virtual void AfterReadRPC()
        {
            if (IsClient && !_didRaiseThisRPC)
            {
                throw new InvalidOperationException(
                    $"SyncableField subclass '{GetType().Name}' did not call RaiseClientValueChanged() " +
                    "after applying new server data. Be sure to call RaiseClientValueChanged() whenever " +
                    "the field's value changes on the client."
                );
            }
        }

        /// <summary>
        /// Called if we do a rollback (for client prediction).
        /// Subclasses can override to revert to previous state if needed.
        /// </summary>
        protected internal virtual void OnRollback()
        {
        }

        /// <summary>
        /// Called once to let you register your client actions 
        /// (like "SetValue" or "Init" methods) with SyncableRPCRegistrator.
        /// </summary>
        protected internal virtual void RegisterRPC(ref SyncableRPCRegistrator r)
        {
        }

        // ----------------------------------------------------------------
        //   RaiseClientValueChanged logic
        // ----------------------------------------------------------------

        /// <summary>
        /// Subclasses call this after applying new data from the server 
        /// if the field's value has changed. 
        /// This fires the OnValueChanged event on the client.
        /// </summary>
        protected void RaiseClientValueChanged()
        {
            if (IsClient)
            {
                _didRaiseThisRPC = true;
                OnValueChanged?.Invoke(this);
            }
        }

        // ----------------------------------------------------------------
        //   ExecuteRPC methods for server -> client replication
        // ----------------------------------------------------------------

        /// <summary>
        /// Executes a cached RemoteCall with no data.
        /// Only does something if we are on the server (server -> client).
        /// </summary>
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            if (IsServer)
            {
                ParentEntityInternal.ServerManager.AddRemoteCall(
                    ParentEntityInternal,
                    (ushort)(rpc.Id + RPCOffset),
                    rpc.Flags
                );
            }
        }

        /// <summary>
        /// Executes a cached RemoteCall with a single unmanaged value "T".
        /// Only does something if we are on the server (server -> client).
        /// </summary>
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            if (IsServer)
            {
                unsafe
                {
                    ParentEntityInternal.ServerManager.AddRemoteCall(
                        ParentEntityInternal,
                        new ReadOnlySpan<T>(&value, 1),
                        (ushort)(rpc.Id + RPCOffset),
                        Flags
                    );
                }
            }
        }

        /// <summary>
        /// Executes a cached RemoteCall with a span of unmanaged values "T".
        /// Only does something if we are on the server (server -> client).
        /// </summary>
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            if (IsServer)
            {
                ParentEntityInternal.ServerManager.AddRemoteCall(
                    ParentEntityInternal,
                    value,
                    (ushort)(rpc.Id + RPCOffset),
                    Flags
                );
            }
        }

        /// <summary>
        /// Executes a cached RemoteCallSerializable with "T" that implements ISpanSerializable.
        /// Only does something if we are on the server (server -> client).
        /// </summary>
        protected void ExecuteRPC<T>(in RemoteCallSerializable<T> rpc, T value) where T : struct, ISpanSerializable
        {
            if (IsServer)
            {
                var writer = new SpanWriter(stackalloc byte[value.MaxSize]);
                value.Serialize(ref writer);
                ParentEntityInternal.ServerManager.AddRemoteCall<byte>(
                    ParentEntityInternal,
                    writer.RawData.Slice(0, writer.Position),
                    (ushort)(rpc.Id + RPCOffset),
                    Flags
                );
            }
        }
    }
}

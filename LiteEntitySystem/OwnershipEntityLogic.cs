using System;
using LiteNetLib.Utils;
using LiteEntitySystem;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Example subclass that adds easy methods for requesting
    /// and releasing ownership from client or server.
    /// </summary>
    public abstract class OwnershipEntityLogic : EntityLogic
    {
        // We'll define a server action. The client will call it to request/release ownership.
        private static RemoteCallSpan<byte> _ownershipRequestAction;

        protected OwnershipEntityLogic(EntityParams entityParams) : base(entityParams)
        {
        }

        protected override void RegisterRPC(ref RPCRegistrator r)
        {
            base.RegisterRPC(ref r);

            // Create a 'server action' so the client can call into the server
            // The server side method is OnOwnershipRequest
            r.CreateRPCAction(this, OnOwnershipRequest, ref _ownershipRequestAction, ExecuteFlags.ExecuteOnServer);
        }

        /// <summary>
        /// Called by client or server to request new ownership.
        /// The newOwnerId is optional.
        /// If called on the client, it sets the owner by default to the local player.
        /// If called on the server, the owner is set to the server.
        /// </summary>
        /// <param name="newOwnerId">0 to release to server, otherwise a player id</param>
        public virtual void RequestOwnership(byte? newOwnerId = null)
        {
            // If we are the server, we can do it immediately
            if (IsServer)
            {
                SetOwner(this, newOwnerId ?? EntityManager.ServerPlayerId);
                return;
            }

            // If we are a client, we call the server action
            if (IsClient)
            {
                var writer = new NetDataWriter();
                // Put newOwnerId
                writer.Put(newOwnerId ?? ((ClientEntityManager)EntityManager).LocalPlayer.Id);

                // Execute the server action, i.e. the OnOwnershipRequest method on server
                ExecuteRPC(_ownershipRequestAction, writer.CopyData());
            }
        }

        /// <summary>
        /// Called by client or server to release ownership
        /// which means setting ownership to the server (playerId=0).
        /// </summary>
        public virtual void ReleaseOwnership()
        {
            RequestOwnership(EntityManager.ServerPlayerId);
        }

        /// <summary>
        /// Server side method that handles the client request
        /// for new ownership.
        /// </summary>
        /// <param name="data">Serialized data from the client request</param>
        private void OnOwnershipRequest(ReadOnlySpan<byte> data)
        {
            // This method is called ONLY on the server side.
            if (!IsServer)
                return;

            var reader = new NetDataReader();
            reader.SetSource(data.ToArray());

            byte requestedOwnerId = reader.GetByte();

            // Possibly do checks if the client is allowed to do this.
            // If valid, set owner
            SetOwner(this, requestedOwnerId);
        }
    }
}
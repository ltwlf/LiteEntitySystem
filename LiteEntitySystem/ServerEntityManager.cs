using System;
using System.Collections.Generic;
using K4os.Compression.LZ4;
using LiteEntitySystem.Collections;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;

namespace LiteEntitySystem
{
    public enum ServerSendRate : byte
    {
        EqualToFPS = 1,
        HalfOfFPS = 2,
        ThirdOfFPS = 3
    }

    internal readonly struct PendingClientPackets
    {
        public readonly NetPlayer Player;
        public readonly byte PacketType;
        public readonly byte[] Data;

        public PendingClientPackets(NetPlayer player, byte packetType, byte[] data)
        {
            Player = player;
            PacketType = packetType;
            Data = data;
        }
    }

    /// <summary>
    /// Server entity manager
    /// </summary>
    public sealed class ServerEntityManager : EntityManager
    {
        public const int MaxStoredInputs = 30;

        private readonly IdGeneratorUShort _entityIdQueue = new(1, MaxSyncedEntityCount);
        private readonly IdGeneratorByte _playerIdQueue = new(1, MaxPlayers);

        // Single queue for both ClientRequests and ClientRPCs:
        private readonly Queue<PendingClientPackets> _pendingClientMessages = new();

        // Queue of RPC packets that the server will send to clients
        private readonly Queue<RemoteCallPacket> _rpcPool = new();

        private byte[] _packetBuffer =
            new byte[(MaxParts + 1) * NetConstants.MaxPacketSize + StateSerializer.MaxStateSize];

        private readonly SparseMap<NetPlayer> _netPlayers = new(MaxPlayers + 1);
        private readonly StateSerializer[] _stateSerializers = new StateSerializer[MaxSyncedEntityCount];
        private readonly byte[] _inputDecodeBuffer = new byte[NetConstants.MaxUnreliableDataSize];
        private readonly NetDataReader _requestsReader = new();

        // Use an AVLTree for changed entities (id+version+creationTime ordering)
        private readonly AVLTree<InternalEntity> _changedEntities = new();

        private byte[] _compressionBuffer = new byte[4096];

        /// <summary>
        /// Network players count
        /// </summary>
        public int PlayersCount => _netPlayers.Count;

        /// <summary>
        /// Rate at which server will make and send packets
        /// </summary>
        public readonly ServerSendRate SendRate;

        /// <summary>
        /// Add try catch to entity updates
        /// </summary>
        public bool SafeEntityUpdate = false;

        private ushort _minimalTick;
        private int _nextOrderNum;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="packetHeader">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        /// <param name="sendRate">Send rate of server (depends on fps)</param>
        /// <param name="maxHistorySize">Maximum size of lag compensation history in ticks</param>
        public ServerEntityManager(
            EntityTypesMap typesMap,
            byte packetHeader,
            byte framesPerSecond,
            ServerSendRate sendRate,
            MaxHistorySize maxHistorySize = MaxHistorySize.Size32)
            : base(typesMap, NetworkMode.Server, packetHeader, maxHistorySize)
        {
            InternalPlayerId = ServerPlayerId;
            _packetBuffer[0] = packetHeader;
            SendRate = sendRate;
            SetTickrate(framesPerSecond);
        }

        public override void Reset()
        {
            base.Reset();
            _nextOrderNum = 0;
            _changedEntities.Clear();
        }

        #region Player Management

        /// <summary>
        /// Create and add new player
        /// </summary>
        /// <param name="peer">AbstractPeer to use</param>
        /// <returns>Newly created player, null if players count is maximum</returns>
        public NetPlayer AddPlayer(AbstractNetPeer peer)
        {
            if (_netPlayers.Count == MaxPlayers)
                return null;
            if (peer.AssignedPlayer != null)
            {
                Logger.LogWarning("Peer already has an assigned player");
                return peer.AssignedPlayer;
            }

            if (_netPlayers.Count == 0)
                _changedEntities.Clear();

            var player = new NetPlayer(peer, _playerIdQueue.GetNewId())
            {
                State = NetPlayerState.RequestBaseline,
                AvailableInput = new SequenceBinaryHeap<InputInfo>(MaxStoredInputs)
            };
            _netPlayers.Set(player.Id, player);
            peer.AssignedPlayer = player;
            return player;
        }

        /// <summary>
        /// Get player by owner id
        /// </summary>
        public NetPlayer GetPlayer(byte ownerId) =>
            _netPlayers.TryGetValue(ownerId, out var p) ? p : null;

        /// <summary>
        /// Remove player using NetPeer.Tag
        /// </summary>
        public bool RemovePlayer(AbstractNetPeer peer) =>
            RemovePlayer(peer.AssignedPlayer);

        /// <summary>
        /// Remove player and owned entities
        /// </summary>
        public bool RemovePlayer(NetPlayer player)
        {
            if (player == null || !_netPlayers.Contains(player.Id))
                return false;

            GetPlayerController(player)?.DestroyWithControlledEntity();

            bool result = _netPlayers.Remove(player.Id);
            _playerIdQueue.ReuseId(player.Id);
            return result;
        }

        #endregion

        #region Controller Helpers

        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        public HumanControllerLogic GetPlayerController(AbstractNetPeer player) =>
            GetPlayerController(player.AssignedPlayer);

        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        public HumanControllerLogic GetPlayerController(byte playerId) =>
            GetPlayerController(_netPlayers.TryGetValue(playerId, out var p) ? p : null);

        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        public HumanControllerLogic GetPlayerController(NetPlayer player)
        {
            if (player == null || !_netPlayers.Contains(player.Id))
                return null;

            foreach (var controller in GetEntities<HumanControllerLogic>())
            {
                if (controller.InternalOwnerId.Value == player.Id)
                    return controller;
            }

            return null;
        }

        /// <summary>
        /// Add new player controller entity
        /// </summary>
        public T AddController<T>(NetPlayer owner, Action<T> initMethod = null)
            where T : HumanControllerLogic =>
            Add<T>(ent =>
            {
                ent.InternalOwnerId.Value = owner.Id;
                initMethod?.Invoke(ent);
            });

        /// <summary>
        /// Add new player controller entity and start controlling entityToControl
        /// </summary>
        public T AddController<T>(NetPlayer owner, PawnLogic entityToControl, Action<T> initMethod = null)
            where T : HumanControllerLogic =>
            Add<T>(ent =>
            {
                ent.InternalOwnerId.Value = owner.Id;
                ent.StartControl(entityToControl);
                initMethod?.Invoke(ent);
            });

        /// <summary>
        /// Add new AI controller entity
        /// </summary>
        public T AddAIController<T>(Action<T> initMethod = null)
            where T : AiControllerLogic =>
            Add(initMethod);

        /// <summary>
        /// Add new entity (singleton)
        /// </summary>
        public T AddSingleton<T>(Action<T> initMethod = null)
            where T : SingletonEntityLogic =>
            Add(initMethod);

        /// <summary>
        /// Add new entity
        /// </summary>
        public T AddEntity<T>(Action<T> initMethod = null)
            where T : EntityLogic =>
            Add(initMethod);

        /// <summary>
        /// Add new entity and set parent
        /// </summary>
        public T AddEntity<T>(EntityLogic parent, Action<T> initMethod = null)
            where T : EntityLogic =>
            Add<T>(e =>
            {
                e.SetParent(parent);
                initMethod?.Invoke(e);
            });

        /// <summary>
        /// Generic typed add with external EntityTypesMap
        /// </summary>
        public TBase AddEntity<TBase, TEntityType>(
            TEntityType type,
            EntityTypesMap<TEntityType> typesMap,
            Action<TBase> initMethod = null // optional initialization
        )
            where TBase : InternalEntity
            where TEntityType : unmanaged, Enum
        {
            // 1) Lookup the RegisteredTypeInfo from your entity map.
            if (!typesMap.TryGetRegisteredInfo(type, out var regInfo))
            {
                throw new Exception($"No entity type registered for enum={type}");
            }

            ref var classData = ref ClassDataDict[regInfo.ClassId];
            ushort entityId = _entityIdQueue.GetNewId();

            ref var stateSerializer = ref _stateSerializers[entityId];
            byte[] ioBuffer = classData.AllocateDataCache();
            stateSerializer.AllocateMemory(ref classData, ioBuffer);

            var entity = AddEntity<TBase>(new EntityParams(
                new EntityDataHeader(
                    entityId,
                    classData.ClassId,
                    stateSerializer.NextVersion,
                    ++_nextOrderNum),
                this,
                ioBuffer));

            stateSerializer.Init(entity, _tick);
            initMethod?.Invoke(entity);

            ConstructEntity(entity);
            _changedEntities.Add(entity);

            return entity;
        }

        private T Add<T>(Action<T> initMethod) where T : InternalEntity
        {
            if (EntityClassInfo<T>.ClassId == 0)
                throw new Exception($"Unregistered entity type: {typeof(T)}");

            ref var classData = ref ClassDataDict[EntityClassInfo<T>.ClassId];

            if (_entityIdQueue.AvailableIds == 0)
            {
                Logger.Log($"Cannot add entity. Max entity count reached: {MaxSyncedEntityCount}");
                return null;
            }

            ushort entityId = _entityIdQueue.GetNewId();
            ref var stateSerializer = ref _stateSerializers[entityId];

            byte[] ioBuffer = classData.AllocateDataCache();
            stateSerializer.AllocateMemory(ref classData, ioBuffer);

            var entity = AddEntity<T>(new EntityParams(
                new EntityDataHeader(
                    entityId,
                    classData.ClassId,
                    stateSerializer.NextVersion,
                    ++_nextOrderNum),
                this,
                ioBuffer));

            stateSerializer.Init(entity, _tick);
            initMethod?.Invoke(entity);
            ConstructEntity(entity);
            _changedEntities.Add(entity);

            return entity;
        }

        #endregion

        #region Reading Client Packets (Inputs / Requests / RPCs)

        /// <summary>
        /// Read data for a player (from NetPeer)
        /// </summary>
        public DeserializeResult Deserialize(AbstractNetPeer peer, ReadOnlySpan<byte> inData) =>
            Deserialize(peer.AssignedPlayer, inData);

        /// <summary>
        /// Read data from NetPlayer
        /// </summary>
        public unsafe DeserializeResult Deserialize(NetPlayer player, ReadOnlySpan<byte> inData)
        {
            if (inData[0] != HeaderByte)
                return DeserializeResult.HeaderCheckFailed;
            inData = inData.Slice(1);

            if (inData.Length < 3)
            {
                Logger.LogWarning($"Invalid data received. Length < 3: {inData.Length}");
                return DeserializeResult.Error;
            }

            byte packetType = inData[0];
            inData = inData.Slice(1);

            switch (packetType)
            {
                case InternalPackets.ClientRequest:
                {
                    // Minimal check
                    if (inData.Length < HumanControllerLogic.MinRequestSize)
                    {
                        Logger.LogError("size less than MinRequestSize for ClientRequest");
                        return DeserializeResult.Error;
                    }

                    // Enqueue request
                    _pendingClientMessages.Enqueue(
                        new PendingClientPackets(player, InternalPackets.ClientRequest, inData.ToArray()));
                    return DeserializeResult.Done;
                }

                case InternalPackets.ClientRPC:
                {
                    // Enqueue RPC
                    _pendingClientMessages.Enqueue(
                        new PendingClientPackets(player, InternalPackets.ClientRPC, inData.ToArray()));
                    return DeserializeResult.Done;
                }

                case InternalPackets.ClientInput:
                {
                    // Handle input right away (since it’s time-sensitive).
                    int minInputSize = 0;
                    int minDeltaSize = 0;

                    // If you have many controllers, consider caching this per-player to avoid repeated scanning
                    foreach (var humanControllerLogic in GetEntities<HumanControllerLogic>())
                    {
                        if (humanControllerLogic.OwnerId != player.Id)
                            continue;

                        minInputSize += humanControllerLogic.InputSize;
                        minDeltaSize += humanControllerLogic.MinInputDeltaSize;
                    }

                    ushort clientTick = BitConverter.ToUInt16(inData);
                    inData = inData.Slice(2);
                    bool isFirstInput = true;

                    while (inData.Length >= InputPacketHeader.Size)
                    {
                        var inputInfo = new InputInfo { Tick = clientTick };
                        fixed (byte* rawData = inData)
                            inputInfo.Header = *(InputPacketHeader*)rawData;

                        inData = inData.Slice(InputPacketHeader.Size);

                        bool correctInput = (player.State == NetPlayerState.WaitingForFirstInput ||
                                             Utils.SequenceDiff(inputInfo.Tick, player.LastReceivedTick) > 0);

                        // If buffer is full, remove oldest
                        int removedTick = (player.AvailableInput.Count == MaxStoredInputs && correctInput)
                            ? player.AvailableInput.ExtractMin().Tick
                            : -1;

                        // Possibly empty but with header
                        if (isFirstInput && inData.Length < minInputSize)
                        {
                            Logger.LogError($"Bad input from Player {player.Id}, data too small for first input.");
                            return DeserializeResult.Error;
                        }

                        if (!isFirstInput && inData.Length < minDeltaSize)
                        {
                            Logger.LogError($"Bad input from Player {player.Id}, data too small for delta input.");
                            return DeserializeResult.Error;
                        }

                        if (Utils.SequenceDiff(inputInfo.Header.StateA, Tick) > 0 ||
                            Utils.SequenceDiff(inputInfo.Header.StateB, Tick) > 0)
                        {
                            Logger.LogError($"Bad input from Player {player.Id}, invalid sequence.");
                            return DeserializeResult.Error;
                        }

                        inputInfo.Header.LerpMsec = Math.Clamp(inputInfo.Header.LerpMsec, 0f, 1f);
                        if (Utils.SequenceDiff(inputInfo.Header.StateB, player.CurrentServerTick) > 0)
                            player.CurrentServerTick = inputInfo.Header.StateB;

                        clientTick++;

                        // Decode each controller's input
                        foreach (var controller in GetEntities<HumanControllerLogic>())
                        {
                            if (controller.OwnerId != player.Id)
                                continue;

                            if (removedTick >= 0)
                                controller.RemoveIncomingInput((ushort)removedTick);

                            ReadOnlySpan<byte> actualData;

                            if (!isFirstInput)
                            {
                                // Delta
                                var decodedData = new Span<byte>(_inputDecodeBuffer, 0, controller.InputSize);
                                decodedData.Clear();
                                int readBytes = controller.DeltaDecode(inData, decodedData);
                                actualData = decodedData;
                                inData = inData.Slice(readBytes);
                            }
                            else
                            {
                                // Full input
                                isFirstInput = false;
                                actualData = inData.Slice(0, controller.InputSize);
                                controller.DeltaDecodeInit(actualData);
                                inData = inData.Slice(controller.InputSize);
                            }

                            if (correctInput)
                                controller.AddIncomingInput(inputInfo.Tick, actualData);
                        }

                        if (correctInput)
                        {
                            player.AvailableInput.Add(inputInfo, inputInfo.Tick);
                            player.LastReceivedTick = inputInfo.Tick;
                        }
                    }

                    if (player.State == NetPlayerState.WaitingForFirstInput)
                        player.State = NetPlayerState.WaitingForFirstInputProcess;

                    return DeserializeResult.Done;
                }

                default:
                {
                    Logger.LogWarning($"[SEM] Unknown packet type: {packetType}");
                    return DeserializeResult.Error;
                }
            }
        }

        #endregion

        #region Main Logic Tick

        protected override unsafe void OnLogicTick()
        {
            // 1) Process all pending client messages
            ProcessPendingClientMessages();

            // 2) Process inputs -> move controllers forward
            int playersCount = _netPlayers.Count;
            for (int pidx = 0; pidx < playersCount; pidx++)
            {
                var player = _netPlayers.GetByIndex(pidx);

                if (player.State == NetPlayerState.RequestBaseline)
                    continue;
                if (player.AvailableInput.Count == 0)
                    continue;

                var inputFrame = player.AvailableInput.ExtractMin();
                ref var inputData = ref inputFrame.Header;

                player.LastProcessedTick = inputFrame.Tick;
                player.StateATick = inputData.StateA;
                player.StateBTick = inputData.StateB;
                player.LerpTime = inputData.LerpMsec;

                if (player.State == NetPlayerState.WaitingForFirstInputProcess)
                    player.State = NetPlayerState.Active;

                // Apply input to each relevant controller
                foreach (var controller in GetEntities<HumanControllerLogic>())
                {
                    if (controller.InternalOwnerId.Value != player.Id)
                        continue;
                    controller.ApplyIncomingInput(inputFrame.Tick);
                }
            }

            // 3) Update all entities
            if (SafeEntityUpdate)
            {
                foreach (var aliveEntity in AliveEntities)
                    aliveEntity.SafeUpdate();
            }
            else
            {
                foreach (var aliveEntity in AliveEntities)
                    aliveEntity.Update();
            }

            // 4) Write lag compensation state
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                ClassDataDict[lagCompensatedEntity.ClassId].WriteHistory(lagCompensatedEntity, _tick);

            // 5) Send state updates to all clients at configured rate
            if (playersCount == 0 || _tick % (int)SendRate != 0)
                return;

            // Calculate minimalTick among all players
            _minimalTick = _tick;
            int maxBaseline = 0;
            for (int pidx = 0; pidx < playersCount; pidx++)
            {
                var player = _netPlayers.GetByIndex(pidx);
                if (player.State != NetPlayerState.RequestBaseline)
                {
                    _minimalTick = Utils.SequenceDiff(player.StateATick, _minimalTick) < 0
                        ? player.StateATick
                        : _minimalTick;
                }
                else if (maxBaseline == 0)
                {
                    // We might need a big buffer for baseline
                    maxBaseline = sizeof(BaselineDataHeader);
                    foreach (var e in GetEntities<InternalEntity>())
                        maxBaseline += _stateSerializers[e.Id].GetMaximumSize(_tick);

                    if (_packetBuffer.Length < maxBaseline)
                        _packetBuffer = new byte[maxBaseline];

                    int maxCompressedSize =
                        LZ4Codec.MaximumOutputSize(_packetBuffer.Length) + sizeof(BaselineDataHeader);
                    if (_compressionBuffer.Length < maxCompressedSize)
                        _compressionBuffer = new byte[maxCompressedSize];
                }
            }

            // Prepare partial or baseline updates
            fixed (byte* packetBufferPtr = _packetBuffer, compressionBufferPtr = _compressionBuffer)
            {
                for (int pidx = 0; pidx < playersCount; pidx++)
                {
                    var player = _netPlayers.GetByIndex(pidx);

                    // Baseline for new player
                    if (player.State == NetPlayerState.RequestBaseline)
                    {
                        int originalLength = 0;
                        foreach (var e in GetEntities<InternalEntity>())
                        {
                            _stateSerializers[e.Id].MakeBaseline(player.Id, _tick, packetBufferPtr, ref originalLength);
                        }

                        // Fill header
                        var baselineHeader = (BaselineDataHeader*)compressionBufferPtr;
                        baselineHeader->UserHeader = HeaderByte;
                        baselineHeader->PacketType = InternalPackets.BaselineSync;
                        baselineHeader->OriginalLength = originalLength;
                        baselineHeader->Tick = _tick;
                        baselineHeader->PlayerId = player.Id;
                        baselineHeader->SendRate = (byte)SendRate;
                        baselineHeader->Tickrate = Tickrate;

                        // Compress
                        int encodedLength = LZ4Codec.Encode(
                            _packetBuffer, // source bytes
                            0, // source offset
                            originalLength, // source length
                            _compressionBuffer, // target array
                            sizeof(BaselineDataHeader), // target offset
                            _compressionBuffer.Length - sizeof(BaselineDataHeader), // target length
                            LZ4Level.L00_FAST // compression level
                        );

                        // Send
                        player.Peer.SendReliableOrdered(new ReadOnlySpan<byte>(
                            _compressionBuffer, 0,
                            sizeof(BaselineDataHeader) + encodedLength));

                        player.StateATick = _tick;
                        player.CurrentServerTick = _tick;
                        player.State = NetPlayerState.WaitingForFirstInput;

                        Logger.Log(
                            $"[SEM] SendWorld to player {player.Id}. orig: {originalLength}, compressed: {encodedLength}, Tick: {_tick}"
                        );
                        continue;
                    }

                    // Skip if not active
                    if (player.State != NetPlayerState.Active)
                        continue;

                    // Partial diff sync
                    var header = (DiffPartHeader*)packetBufferPtr;
                    header->UserHeader = HeaderByte;
                    header->Part = 0;
                    header->Tick = _tick;
                    int writePosition = sizeof(DiffPartHeader);

                    // Calculate max safe size
                    ushort maxPartSize = (ushort)(player.Peer.GetMaxUnreliablePacketSize() - sizeof(LastPartData));

                    // We iterate over changed entities to generate diffs
                    foreach (var entity in _changedEntities)
                    {
                        ref var stateSerializer = ref _stateSerializers[entity.Id];

                        // If all players have actual state, remove from changed list
                        if (Utils.SequenceDiff(stateSerializer.LastChangedTick, _minimalTick) <= 0)
                        {
                            _changedEntities.Remove(entity);

                            // free destroyed entity
                            if (entity.IsDestroyed)
                            {
                                if (entity.UpdateOrderNum == _nextOrderNum)
                                {
                                    if (GetEntities<InternalEntity>().TryGetMax(out var highestEntity))
                                        _nextOrderNum = highestEntity.UpdateOrderNum;
                                    else
                                        _nextOrderNum = 0;
                                }

                                _entityIdQueue.ReuseId(entity.Id);
                                stateSerializer.Free();
                                RemoveEntity(entity);
                            }

                            continue;
                        }

                        // If the player already has the latest for that entity, skip
                        if (Utils.SequenceDiff(stateSerializer.LastChangedTick, player.StateATick) <= 0)
                            continue;

                        var playerController = GetPlayerController(player);

                        // Write partial diff for this entity
                        if (stateSerializer.MakeDiff(
                                player.Id,
                                _tick,
                                _minimalTick,
                                player.CurrentServerTick,
                                packetBufferPtr,
                                ref writePosition,
                                playerController))
                        {
                            // Check for overflow
                            int overflow = writePosition - maxPartSize;
                            while (overflow > 0)
                            {
                                if (header->Part == MaxParts - 1)
                                {
                                    Logger.Log($"Player:{pidx} forcing baseline at Tick {_tick}");
                                    player.State = NetPlayerState.RequestBaseline;
                                    break;
                                }

                                header->PacketType = InternalPackets.DiffSync;
                                player.Peer.SendUnreliable(
                                    new ReadOnlySpan<byte>(_packetBuffer, 0, maxPartSize));

                                header->Part++;

                                // Move leftover data to the front
                                RefMagic.CopyBlock(packetBufferPtr + sizeof(DiffPartHeader),
                                    packetBufferPtr + maxPartSize,
                                    (uint)overflow);

                                writePosition = sizeof(DiffPartHeader) + overflow;
                                overflow = writePosition - maxPartSize;
                            }

                            if (player.State == NetPlayerState.RequestBaseline)
                                break; // go to next player
                        }
                    }

                    if (player.State == NetPlayerState.RequestBaseline)
                        continue;

                    // Finalize the last diff packet
                    header->PacketType = InternalPackets.DiffSyncLast;
                    var lastPartData = (LastPartData*)(packetBufferPtr + writePosition);
                    lastPartData->LastProcessedTick = player.LastProcessedTick;
                    lastPartData->LastReceivedTick = player.LastReceivedTick;
                    lastPartData->Mtu = maxPartSize;
                    lastPartData->BufferedInputsCount = (byte)player.AvailableInput.Count;
                    writePosition += sizeof(LastPartData);

                    player.Peer.SendUnreliable(new ReadOnlySpan<byte>(_packetBuffer, 0, writePosition));
                }
            }

            // Trigger sending all accumulated packets
            if (playersCount > 0)
                _netPlayers.GetByIndex(0).Peer.TriggerSend();
        }

        #endregion

        #region Processing Pending Client Messages

        /// <summary>
        /// Handle all messages queued during <see cref="Deserialize"/>
        /// </summary>
        private void ProcessPendingClientMessages()
        {
            while (_pendingClientMessages.Count > 0)
            {
                var msg = _pendingClientMessages.Dequeue();

                switch (msg.PacketType)
                {
                    case InternalPackets.ClientRequest:
                    {
                        // Example: parse the request
                        _requestsReader.SetSource(msg.Data);
                        if (_requestsReader.AvailableBytes < HumanControllerLogic.MinRequestSize)
                            break;

                        ushort controllerId = _requestsReader.GetUShort();
                        byte controllerVersion = _requestsReader.GetByte();
                        if (TryGetEntityById<HumanControllerLogic>(
                                new EntitySharedReference(controllerId, controllerVersion),
                                out var controller))
                        {
                            controller.ReadClientRequest(_requestsReader);
                        }

                        break;
                    }

                    case InternalPackets.ClientRPC:
                    {
                        // Parse the RPC
                        var reader = new NetDataReader(msg.Data);
                        ushort entityId = reader.GetUShort();
                        ushort rpcId = reader.GetUShort();

                        // Remaining payload
                        ReadOnlySpan<byte> payload = reader.GetRemainingBytesSpan();

                        var entity = EntitiesDict[entityId];
                        if (entity == null)
                            break;

                        ref var classData = ref ClassDataDict[entity.ClassId];
                        if (rpcId >= classData.RemoteCallsClient.Length)
                            break;

                        var rpcInfo = classData.RemoteCallsClient[rpcId];
                        rpcInfo.Method(entity, payload);
                        break;
                    }
                }
            }
        }

        #endregion

        #region Internal Calls (Mark Changes / RPC to Clients)

        internal override void EntityFieldChanged<T>(InternalEntity entity, ushort fieldId, ref T newValue)
        {
            // If entity is already destroyed and the memory freed, skip
            if (entity.IsDestroyed && _stateSerializers[entity.Id].Entity != entity)
                return;

            _changedEntities.Add(entity);
            _stateSerializers[entity.Id].MarkFieldChanged(fieldId, _tick, ref newValue);
        }

        internal void ForceEntitySync(InternalEntity entity)
        {
            _changedEntities.Add(entity);
            _stateSerializers[entity.Id].ForceFullSync(_tick);
        }

        internal void PoolRpc(RemoteCallPacket rpcNode) =>
            _rpcPool.Enqueue(rpcNode);

        internal void AddRemoteCall(InternalEntity entity, ushort rpcId, ExecuteFlags flags)
        {
            if (PlayersCount == 0)
                return;

            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, 0, rpcId, flags);

            _stateSerializers[entity.Id].AddRpcPacket(rpc);
            _changedEntities.Add(entity);
        }

        internal unsafe void AddRemoteCall<T>(
            InternalEntity entity,
            ReadOnlySpan<T> value,
            ushort rpcId,
            ExecuteFlags flags) where T : unmanaged
        {
            if (PlayersCount == 0)
                return;

            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            int dataSize = sizeof(T) * value.Length;
            if (dataSize > ushort.MaxValue)
            {
                Logger.LogError($"DataSize on RPC {rpcId} for entity={entity} exceeds {ushort.MaxValue} bytes!");
                return;
            }

            rpc.Init(_tick, (ushort)dataSize, rpcId, flags);

            if (value.Length > 0)
            {
                fixed (void* src = value, dst = rpc.Data)
                    RefMagic.CopyBlock(dst, src, (uint)dataSize);
            }

            _stateSerializers[entity.Id].AddRpcPacket(rpc);
            _changedEntities.Add(entity);
        }

        #endregion
    }
}
﻿using Obsidian.Chat;
using Obsidian.ChunkData;
using Obsidian.Commands;
using Obsidian.Entities;
using Obsidian.Events.EventArgs;
using Obsidian.Logging;
using Obsidian.Net;
using Obsidian.Net.Packets;
using Obsidian.Net.Packets.Play;
using Obsidian.PlayerData;
using Obsidian.PlayerData.Info;
using Obsidian.Util;
using Obsidian.World;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Obsidian
{
    public class Client
    {
        private readonly bool Compressed = false;

        public MinecraftStream MinecraftStream { get; set; }

        public ClientState State { get; private set; }

        public CancellationTokenSource Cancellation { get; private set; }

        public bool IsPlaying => this.State == ClientState.Play && this.Player != null; //HACK: Suggest better property name lol -Craftplacer

        private byte[] Token { get; set; }

        public Server OriginServer;
        public TcpClient Tcp;
        public Player Player;
        public Config Config;
        public ClientSettings ClientSettings;

        public int Ping;
        public int PlayerId;

        public byte[] SharedKey = null;

        public bool EncryptionEnabled = false;

        private ConcurrentQueue<Packet> _outgoingPackets = new ConcurrentQueue<Packet>();

        public Client(TcpClient tcp, Config config, int playerId, Server originServer)
        {
            this.Tcp = tcp;
            this.Config = config;
            this.PlayerId = playerId;

            this.OriginServer = originServer;
            this.Cancellation = new CancellationTokenSource();
            this.State = ClientState.Handshaking;

            this.MinecraftStream = new MinecraftStream(tcp.GetStream());
        }

        public Logger Logger => this.OriginServer.Logger;

        #region Packet Sending Methods

        internal async Task DisconnectAsync(ChatMessage reason) => await this.SendPacketAsync(new Disconnect(reason, this.State), true);

        internal async Task ProcessKeepAlive(long id)
        {
            this.Ping = (int)(DateTime.Now.Millisecond - id);
            await this.SendPacketAsync(new KeepAlive(id), true);

            /////Sending ping change in background
            ///await Task.Run(async delegate ()
            ///{
            ///    foreach (Client client in OriginServer.Clients.Where(c => c.IsPlaying))
            ///    {
            ///        await PacketHandler.CreateAsync(new PlayerInfo(2, new List<PlayerInfoAction>()
            ///        {
            ///            new PlayerInfoUpdatePingAction()
            ///            {
            ///                Ping = this.Ping
            ///            }
            ///        }), this.MinecraftStream);
            ///    }
            ///}).ConfigureAwait(false);
        }

        internal async Task SendPlayerLookPositionAsync(Transform poslook, PositionFlags posflags, int tpid = 0) => await this.SendPacketAsync(new PlayerPositionLook(poslook, posflags, tpid), true);

        internal async Task SendBlockChangeAsync(BlockChange b)
        {
            this.Logger.LogMessage($"Sending block change to {Player.Username}");
            await this.SendPacketAsync(b, true);
            this.Logger.LogMessage($"Block change sent to {Player.Username}");
        }

        internal async Task SendSpawnMobAsync(int id, Guid uuid, int type, Transform transform, byte headPitch, Velocity velocity, Entity entity)
        {
            await this.SendPacketAsync(new SpawnMob(id, uuid, type, transform, headPitch, velocity, entity), true);

            this.Logger.LogDebug($"Spawned entity with id {id} for player {this.Player.Username}");
        }

        internal async Task SendEntity(EntityPacket packet)
        {
            await this.SendPacketAsync(packet, true);
            this.Logger.LogDebug($"Sent entity with id {packet.Id} for player {this.Player.Username}");
        }

        internal async Task SendDeclareCommandsAsync()
        {
            var packet = new DeclareCommands();

            var node = new CommandNode()
            {
                Type = CommandNodeType.Root
            };
            foreach (Qmmands.Command command in this.OriginServer.Commands.GetAllCommands())
            {
                var commandNode = new CommandNode()
                {
                    Name = command.Name,
                    Type = CommandNodeType.Literal
                };

                foreach (Qmmands.Parameter parameter in command.Parameters)
                {
                    var parameterNode = new CommandNode()
                    {
                        Name = parameter.Name,
                        Type = CommandNodeType.Argument,
                    };

                    Type type = parameter.Type;

                    if (type == typeof(string)) parameterNode.Parser = new StringCommandParser(parameter.IsRemainder ? StringType.GreedyPhrase : StringType.QuotablePhrase);
                    else if (type == typeof(double)) parameterNode.Parser = new EmptyFlagsCommandParser("brigadier:double");
                    else if (type == typeof(float)) parameterNode.Parser = new EmptyFlagsCommandParser("brigadier:float");
                    else if (type == typeof(int)) parameterNode.Parser = new EmptyFlagsCommandParser("brigadier:integer");
                    else if (type == typeof(bool)) parameterNode.Parser = new CommandParser("brigadier:bool");
                    else continue;

                    commandNode.Children.Add(parameterNode);
                }

                if (commandNode.Children.Count > 0)
                {
                    commandNode.Children[0].Type |= CommandNodeType.IsExecutabe;
                }
                else
                {
                    commandNode.Type |= CommandNodeType.IsExecutabe;
                }

                node.Children.Add(commandNode);

                packet.AddNode(node);
            }

            await this.SendPacketAsync(packet, true);
            this.Logger.LogDebug("Sent Declare Commands packet.");
        }

        internal async Task SendPlayerInfoAsync()
        {
            var list = new List<PlayerInfoAction>();

            foreach (Client client in this.OriginServer.Clients)
            {
                if (!client.IsPlaying)
                {
                    continue;
                }

                Player player = client.Player;

                list.Add(new PlayerInfoAddAction()
                {
                    Name = player.Username,
                    Uuid = player.Uuid,
                    Ping = client.Ping,
                    Gamemode = (int)Player.Gamemode,
                    DisplayName = ChatMessage.Simple(player.Username)
                });
            }

            await this.SendPacketAsync(new PlayerInfo(0, list), true);
            this.Logger.LogDebug($"Sent Player Info packet. from {this.Player.Username}");
        }

        internal async Task SendPlayerAsync(int id, Guid uuid, Transform pos)
        {
            await this.SendPacketAsync(new SpawnPlayer
            {
                Id = id,

                Uuid = uuid,

                Tranform = pos,

                Player = this.Player
            }, true);

            this.Logger.LogDebug("New player spawned!");
        }

        internal async Task SendPlayerAsync(int id, string uuid, Transform pos)
        {
            await this.SendPacketAsync(new SpawnPlayer
            {
                Id = id,

                Uuid3 = uuid,

                Tranform = pos,

                Player = this.Player
            }, true);
            this.Logger.LogDebug("New player spawned!");
        }

        internal async Task SendPlayerListHeaderFooterAsync(ChatMessage header, ChatMessage footer)
        {
            await this.SendPacketAsync(new PlayerListHeaderFooter(header, footer), true);
            this.Logger.LogDebug("Sent Player List Footer Header packet.");
        }

        #endregion Packet Sending Methods

        private async Task<CompressedPacket> GetNextCompressedPacketAsync()
        {
            return await CompressedPacket.ReadFromStreamAsync(this.MinecraftStream);
        }

        private async Task<Packet> GetNextPacketAsync()
        {
            return await PacketSerializer.ReadFromStreamAsync(this.MinecraftStream);
        }

        public async Task StartConnectionAsync()
        {
            while (!Cancellation.IsCancellationRequested && this.Tcp.Connected)
            {
                Packet packet = this.Compressed ? await this.GetNextCompressedPacketAsync() : await this.GetNextPacketAsync();
                Packet returnPacket;

                if (packet.Empty)
                    continue; //Ignoring that packet

                if (this.State == ClientState.Play && packet.PacketData.Length < 1)
                    this.Disconnect();

                Logger.LogDebug($"Got Packet: S:{this.State} I:{packet.PacketId}");

                switch (this.State)
                {
                    case ClientState.Status: //server ping/list
                        switch (packet.PacketId)
                        {
                            case 0x00:
                                var status = new ServerStatus(OriginServer);
                                await this.SendPacketAsync(new RequestResponse(status), true);
                                break;

                            case 0x01:
                                await this.SendPacketAsync(new PingPong(packet.PacketData));
                                this.Disconnect();
                                break;
                        }
                        break;

                    case ClientState.Handshaking:
                        if (packet.PacketId == 0x00)
                        {
                            if (packet == null)
                                throw new InvalidOperationException();

                            var handshake = await PacketSerializer.DeserializeAsync(new Handshake(packet.PacketData));

                            var nextState = handshake.NextState;

                            if (nextState != ClientState.Status && nextState != ClientState.Login)
                            {
                                this.Logger.LogDebug($"Client sent unexpected state ({nextState}), forcing it to disconnect");
                                await this.DisconnectAsync(ChatMessage.Simple("you seem suspicious"));
                            }

                            this.State = nextState;
                            this.Logger.LogMessage($"Handshaking with client (protocol: {handshake.Version}, server: {handshake.ServerAddress}:{handshake.ServerPort})");
                        }
                        else
                        {
                            //Handle legacy ping stuff
                        }
                        break;

                    case ClientState.Login:
                        switch (packet.PacketId)
                        {
                            default:
                                this.Logger.LogError("Client in state Login tried to send an unimplemented packet. Forcing it to disconnect.");
                                await this.DisconnectAsync(ChatMessage.Simple("Unknown Packet Id."));
                                break;

                            case 0x00:
                                var loginStart = new LoginStart(packet.PacketData);

                                string username = loginStart.Username;

                                if (Config.MulitplayerDebugMode)
                                {
                                    username = $"Player{new Random().Next(1, 999)}";
                                    this.Logger.LogDebug($"Overriding username from {loginStart.Username} to {username}");
                                }

                                this.Logger.LogDebug($"Received login request from user {loginStart.Username}");

                                if (this.OriginServer.CheckPlayerOnline(username))
                                    await this.OriginServer.Clients.FirstOrDefault(c => c.Player.Username == username).DisconnectAsync(ChatMessage.Simple("Logged in from another location"));

                                if (this.Config.OnlineMode)
                                {
                                    var users = await MinecraftAPI.GetUsersAsync(new string[] { loginStart.Username });
                                    var uid = users.FirstOrDefault();

                                    var uuid = Guid.Parse(uid.Id);
                                    this.Player = new Player(uuid, loginStart.Username, this);

                                    PacketCryptography.GenerateKeyPair();

                                    var pubKey = PacketCryptography.PublicKeyToAsn();

                                    this.Token = PacketCryptography.GetRandomToken();

                                    await this.SendPacketAsync(new EncryptionRequest(pubKey, this.Token), true);

                                    break;
                                }

                                this.Player = new Player(Guid.NewGuid(), username, this);
                                await ConnectAsync(this.Player.Uuid);

                                break;

                            case 0x01:
                                var encryptionResponse = await PacketSerializer.DeserializeAsync(new EncryptionResponse(packet.PacketData));

                                JoinedResponse response;

                                this.SharedKey = PacketCryptography.Decrypt(encryptionResponse.SharedSecret);

                                var dec2 = PacketCryptography.Decrypt(encryptionResponse.VerifyToken);

                                var dec2Base64 = Convert.ToBase64String(dec2);

                                var tokenBase64 = Convert.ToBase64String(this.Token);

                                if (!dec2Base64.Equals(tokenBase64))
                                {
                                    await this.DisconnectAsync(ChatMessage.Simple("Invalid token.."));
                                    break;
                                }

                                var encodedKey = PacketCryptography.PublicKeyToAsn();

                                var serverId = PacketCryptography.MinecraftShaDigest(SharedKey.Concat(encodedKey).ToArray());

                                response = await MinecraftAPI.HasJoined(this.Player.Username, serverId);

                                if (response is null)
                                {
                                    this.Logger.LogWarning($"Failed to auth {this.Player.Username}");
                                    await this.DisconnectAsync(ChatMessage.Simple("Unable to authenticate.."));
                                    break;
                                }
                                this.EncryptionEnabled = true;
                                this.MinecraftStream = new AesStream(this.Tcp.GetStream(), this.SharedKey);

                                await ConnectAsync(new Guid(response.Id));
                                break;

                            case 0x02:
                                // Login Plugin Response
                                break;
                        }
                        break;

                    case ClientState.Play:

                        ///this.Logger.LogDebugAsync($"Received Play packet with Packet ID 0x{packet.PacketId.ToString("X")}");

                        await PacketHandler.HandlePlayPackets(packet, this);
                        break;
                }

                if (_outgoingPackets.Count > 0)
                    if (_outgoingPackets.TryDequeue(out Packet outgoingPacket))
                    {
                        await this.SendPacketAsync(outgoingPacket);
                    }
                    else
                    {
                        this.Logger.LogError($"Packet {outgoingPacket.GetType().Name} failed to send.");
                    }
            }

            Logger.LogMessage($"Disconnected client");

            if (this.IsPlaying)
                await this.OriginServer.Events.InvokePlayerLeave(new PlayerLeaveEventArgs(this));

            this.OriginServer.Broadcast(string.Format(this.Config.LeaveMessage, this.Player.Username));

            this.Player = null;

            if (Tcp.Connected)
                this.Tcp.Close();
        }

        private async Task ConnectAsync(Guid uuid)
        {
            this.Logger.LogDebug($"Sent Login success to user {this.Player.Username} {this.Player.Uuid.ToString()}");

            this.QueuePacket(new LoginSuccess(uuid, this.Player.Username));

            this.State = ClientState.Play;
            this.Player.Gamemode = Gamemode.Creative;

            this.QueuePacket(new JoinGame((int)(EntityId.Player | (EntityId)this.PlayerId), Gamemode.Creative, 0, 0, "default", true));
            this.Logger.LogDebug("Sent Join Game packet.");

            this.QueuePacket(new SpawnPosition(new Position(0, 100, 0)));
            this.Logger.LogDebug("Sent Spawn Position packet.");

            this.QueuePacket(new PlayerPositionLook(new Transform(0, 105, 0), PositionFlags.NONE, 0));
            this.Logger.LogDebug("Sent Position packet.");

            using (var stream = new MinecraftStream())
            {
                await stream.WriteStringAsync("obsidian");
                this.QueuePacket(new PluginMessage("minecraft:brand", stream.ToArray()));
            }
            this.Logger.LogDebug("Sent server brand.");

            this.OriginServer.Broadcast(string.Format(this.Config.JoinMessage, this.Player.Username));
            await this.OriginServer.Events.InvokePlayerJoin(new PlayerJoinEventArgs(this, DateTimeOffset.Now));

            await this.SendDeclareCommandsAsync();
            await this.SendPlayerInfoAsync();

            await this.SendPlayerListHeaderFooterAsync(string.IsNullOrWhiteSpace(OriginServer.Config.Header) ? null : ChatMessage.Simple(OriginServer.Config.Header),
                                                       string.IsNullOrWhiteSpace(OriginServer.Config.Footer) ? null : ChatMessage.Simple(OriginServer.Config.Footer));
            this.Logger.LogDebug("Sent player list decoration");

            await this.SendChunkAsync(OriginServer.WorldGenerator.GenerateChunk(new Chunk(0, 0)));
            await this.SendChunkAsync(OriginServer.WorldGenerator.GenerateChunk(new Chunk(-1, 0)));
            await this.SendChunkAsync(OriginServer.WorldGenerator.GenerateChunk(new Chunk(0, -1)));
            await this.SendChunkAsync(OriginServer.WorldGenerator.GenerateChunk(new Chunk(-1, -1)));

            //await OriginServer.world.resendBaseChunksAsync(10, 0, 0, 0, 0, this);

            this.Logger.LogDebug("Sent chunk");

            /*if (this.OriginServer.Config.OnlineMode)
            {
                await this.OriginServer.SendNewPlayer(this.PlayerId, this.Player.Uuid, new Transform
                {
                    X = 0,

                    Y = 105,

                    Z = 0,

                    Pitch = 1,

                    Yaw = 1
                });
            }
            else
            {
                await this.OriginServer.SendNewPlayer(this.PlayerId, this.Player.Uuid3, new Transform
                {
                    X = 0,

                    Y = 105,

                    Z = 0,

                    Pitch = 1,

                    Yaw = 1
                });
            }*/
        }

        public async Task SendChunkAsync(Chunk chunk)
        {
            var chunkData = new ChunkDataPacket(chunk.X, chunk.Z);

            for (int i = 0; i < 16; i++)
            {
                chunkData.Data.Add(new ChunkSection().FilledWithLight());
            }

            for (int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 16; y++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        var block = chunk.Blocks[x, y, z];

                        chunkData.Data[6].BlockStateContainer.Set(x, y, z, block);
                    }
                }
            }

            for (int i = 0; i < 16 * 16; i++)
            {
                chunkData.Biomes.Add(29); //TODO: Add proper biomes
            }

            await this.SendPacketAsync(chunkData, true);
        }

        public async Task SendPacketAsync(Packet packet, bool serialize = false)
        {
            if (serialize)
            {
                if (packet is ChunkDataPacket)
                {
                    byte[] data = await packet.SerializeAsync();

                    using (var stream = new MinecraftStream(data))
                        await packet.WriteToStreamAsync(stream, this.MinecraftStream);

                    goto skip;
                }

                using (var stream = new MinecraftStream())
                {
                    await PacketSerializer.SerializeAsync(packet, stream);

                    await packet.WriteToStreamAsync(stream, this.MinecraftStream);
                }

            skip:
                return;
            }
        }

        public void QueuePacket(Packet packet)
        {
            this._outgoingPackets.Enqueue(packet);
        }

        internal void Disconnect() => this.Cancellation.Cancel();
    }
}
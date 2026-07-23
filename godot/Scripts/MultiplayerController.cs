using Godot;
using System.Collections.Generic;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>
/// Pure transport for the host-authoritative lockstep. Owns no game logic:
/// clients ship per-turn command batches to the host (RpcId 1), the host
/// broadcasts aggregated turn bundles, state hashes go to the host for
/// arbitration, and OOS is broadcast back so every peer dumps its state.
/// Godot peer ids (ENet connection ids) and game player ids are separate
/// namespaces; the host assigns the mapping in GameStart.
/// </summary>
public sealed partial class MultiplayerController : Node
{
    private ENetMultiplayerPeer? _peer;
    private NetTurnManager? _netTurn;
    private bool _isHost;
    private uint _localPlayerId = 1;
    private uint _seed;
    private readonly Dictionary<int, uint> _peerToPlayer = new();

    public NetTurnManager? NetTurn => _netTurn;
    public uint LocalPlayerId => _localPlayerId;
    public uint Seed => _seed;
    public bool IsHost => _isHost;
    public new bool IsConnected =>
        _peer != null && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>Raised on every peer once the host has assigned player ids and fixed
    /// the shared seed. (seed, localPlayerId).</summary>
    public event System.Action<uint, uint>? OnGameStart;
    public event System.Action<string>? OnOOS;

    public void StartHost(int port, uint seed)
    {
        _isHost = true;
        _localPlayerId = 1;
        _seed = seed;
        _peer = new ENetMultiplayerPeer();
        _peer.CreateServer(port, 4);
        Multiplayer.MultiplayerPeer = _peer;
        _peerToPlayer[1] = 1; // host's own ENet id is always 1
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        GD.Print($"Hosting on port {port}, seed={seed}, player=1");
    }

    public void StartClient(string address, int port)
    {
        _isHost = false;
        _peer = new ENetMultiplayerPeer();
        _peer.CreateClient(address, port);
        Multiplayer.MultiplayerPeer = _peer;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        GD.Print($"Connecting to {address}:{port}");
    }

    /// <summary>
    /// Wire a freshly created NetTurnManager to the transport. Called by Main once the
    /// sim exists (host: right after world init; client: after GameStart arrives).
    /// </summary>
    public void AttachTurnManager(NetTurnManager tm)
    {
        _netTurn = tm;
        tm.OnTurnBundleReady += (turn, cmds) =>
            Rpc(nameof(ReceiveBundle), turn, NetCommand.SerializeBatch(cmds));
        tm.OnHashComputed += hash =>
            RpcId(1, nameof(SubmitHashToHost), (int)tm.CurrentTurn, hash);
        tm.OnBatchDue += (turn, cmds) =>
        {
            // The host self-ingests its own batch inside AdvanceTurn; only clients ship.
            if (!_isHost)
                RpcId(1, nameof(SubmitBatchToHost), (int)turn, NetCommand.SerializeBatch(cmds));
        };
        tm.OnOOSDetected += (turn, msg) =>
        {
            // Host arbitrates; broadcast so every peer dumps exactly once.
            if (_isHost)
                Rpc(nameof(ReceiveOOS), turn, msg);
        };
        if (_isHost)
            tm.HostBootstrap();
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"Peer connected: {id}");
        if (!_isHost || _netTurn != null) return; // 2-player scope: start on first client
        uint playerId = (uint)(_peerToPlayer.Count + 1);
        _peerToPlayer[(int)id] = playerId;

        var peers = new List<int>();
        var players = new List<int>();
        foreach (var kvp in _peerToPlayer)
        {
            peers.Add(kvp.Key);
            players.Add((int)kvp.Value);
        }
        Rpc(nameof(ReceiveGameStart), _seed, peers.ToArray(), players.ToArray());
        OnGameStart?.Invoke(_seed, _localPlayerId); // host starts its own game
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"Peer disconnected: {id}");
        _peerToPlayer.Remove((int)id);
        // Reconnection/host migration: out of scope (design doc §9).
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveGameStart(uint seed, int[] peers, int[] players)
    {
        _seed = seed;
        long myPeer = Multiplayer.GetUniqueId();
        for (int i = 0; i < peers.Length; i++)
        {
            _peerToPlayer[peers[i]] = (uint)players[i];
            if (peers[i] == myPeer)
                _localPlayerId = (uint)players[i];
        }
        GD.Print($"Game starting: seed={seed}, localPlayer={_localPlayerId}");
        OnGameStart?.Invoke(seed, _localPlayerId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitBatchToHost(int turn, byte[] batch)
    {
        if (!_isHost || _netTurn == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_peerToPlayer.TryGetValue((int)sender, out uint player)) return;
        _netTurn.HostIngestBatch(player, (uint)turn, NetCommand.DeserializeBatch(batch));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveBundle(int turn, byte[] bundle)
    {
        _netTurn?.ReceiveTurnBundle((uint)turn, NetCommand.DeserializeBatch(bundle));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitHashToHost(int turn, byte[] hash)
    {
        if (!_isHost || _netTurn == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_peerToPlayer.TryGetValue((int)sender, out uint player)) return;
        _netTurn.HostReceiveRemoteHash((uint)turn, player, hash);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveOOS(int turn, string msg)
    {
        GD.PrintErr($"OOS at turn {turn}: {msg}");
        OnOOS?.Invoke(msg);
    }

    public void Shutdown()
    {
        if (_peer != null)
        {
            _peer.Close();
            _peer = null;
        }
    }
}

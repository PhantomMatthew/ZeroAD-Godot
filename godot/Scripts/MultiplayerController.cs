using Godot;
using System.Collections.Generic;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

public sealed partial class MultiplayerController : Node
{
    private ENetMultiplayerPeer _peer = null!;
    private NetTurnManager _netTurn = null!;
    private readonly HashSet<uint> _allPlayers = new();
    private uint _localPlayerId = 1;
    private bool _isHost;

    public NetTurnManager NetTurn => _netTurn;
    public bool IsConnected => _peer != null && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    public event System.Action? OnGameStart;
    public event System.Action<string>? OnOOS;

    public void StartHost(int port, uint seed)
    {
        _isHost = true;
        _localPlayerId = 1;
        _peer = new ENetMultiplayerPeer();
        _peer.CreateServer(port, 4);
        Multiplayer.MultiplayerPeer = _peer;
        _allPlayers.Add(1);

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        GD.Print($"Hosting on port {port}, seed={seed}, player=1");
    }

    public void StartClient(string address, int port)
    {
        _isHost = false;
        _localPlayerId = 2;
        _peer = new ENetMultiplayerPeer();
        _peer.CreateClient(address, port);
        Multiplayer.MultiplayerPeer = _peer;
        _allPlayers.Add(2);

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        GD.Print($"Connecting to {address}:{port}, player=2");
    }

    public void InitTurnManager(ZeroAD.Sim.ComponentManager cm, int delay, uint playerId)
    {
        _netTurn = new NetTurnManager(cm, delay, playerId);
        _netTurn.OnCommandsReady += (_, _) => { };
        _netTurn.OnHashComputed += hash =>
        {
            if (IsConnected)
                Rpc("RemoteHash", _netTurn.CurrentTurn, hash);
        };
        _netTurn.OnOOSDetected += (turn, msg) =>
        {
            GD.PrintErr($"OOS: {msg}");
            OnOOS?.Invoke(msg);
        };
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"Peer connected: {id}");
        _allPlayers.Add((uint)id);

        if (_isHost && _allPlayers.Count >= 2)
        {
            Rpc("RemoteGameStart", _allPlayers.Count);
            OnGameStart?.Invoke();
        }
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"Peer disconnected: {id}");
        _allPlayers.Remove((uint)id);
    }

    public void SubmitCommand(NetCommand cmd)
    {
        _netTurn?.SubmitLocalCommand(cmd);

        if (IsConnected)
        {
            byte[] data = cmd.Serialize();
            Rpc("RemoteCommand", _netTurn!.CurrentTurn + 2, (int)cmd.Player, data);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RemoteCommand(int turn, int player, byte[] data)
    {
        if (_netTurn == null) return;
        var cmd = NetCommand.Deserialize(data);
        _netTurn.ReceiveRemoteCommands((uint)player, (uint)turn, new[] { cmd });
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RemoteGameStart(int playerCount)
    {
        GD.Print($"Game starting with {playerCount} players");
        OnGameStart?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RemoteHash(int turn, byte[] hash)
    {
        _netTurn?.ReceiveRemoteHash((uint)turn, hash);
    }

    public bool TryAdvanceTurn()
    {
        if (_netTurn == null) return false;
        if (!_netTurn.IsTurnReady(_allPlayers)) return false;
        _netTurn.AdvanceTurn(_allPlayers);
        return true;
    }

    public void Shutdown()
    {
        if (_peer != null)
        {
            _peer.Close();
            _peer = null!;
        }
    }
}

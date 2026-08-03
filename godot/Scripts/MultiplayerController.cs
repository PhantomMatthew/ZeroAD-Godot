using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>
/// Transport + lobby state machine for the host-authoritative lockstep. Owns no game logic.
/// Two phases:
///  - <b>Lobby</b> (<c>_lobbyActive</c>): peers join, the host assigns each slot
///    <see cref="PlayerSlotKind"/> (Human/AI/Closed) + civ + team, broadcasting the slot table
///    via <see cref="ReceiveLobbyState"/>. No sim runs.
///  - <b>Game</b>: the host clicks Start → <see cref="HostStartGame"/> freezes the slot table and
///    broadcasts it via <see cref="ReceiveGameStart"/>; every peer raises
///    <see cref="OnGameStart"/> and builds the world deterministically from the shared table.
/// Per-turn transport (command batches, turn bundles, OOS hashes) is unchanged: clients ship
/// batches to the host, the host broadcasts bundles, hashes go to the host for arbitration.
/// Godot peer ids (ENet connection ids) and game player ids are separate namespaces; the host
/// owns the mapping. AI slots never enter <c>_expectedPlayers</c> — their commands ride the local
/// <c>_aiBundles</c> channel, so they need no network slot.
/// </summary>
public sealed partial class MultiplayerController : Node
{
    private ENetMultiplayerPeer? _peer;
    private NetTurnManager? _netTurn;
    private bool _isHost;
    private uint _localPlayerId = 1;
    private uint _seed;
    private readonly Dictionary<int, uint> _peerToPlayer = new();

    /// <summary>The host-authoritative lobby slot table. Defaults: slot 1 = host (Human),
    /// slot 2 = open Human (so the first client to join claims a 1v1), slots 3/4 Closed.
    /// The host edits this in the lobby; it is frozen at Start and broadcast to all peers.</summary>
    private List<PlayerSlotSetup> _slots = new()
    {
        new() { PlayerId = 1, Kind = PlayerSlotKind.Human,  Civ = "athen", Team = -1 },
        new() { PlayerId = 2, Kind = PlayerSlotKind.Human,  Civ = "athen", Team = -1 },
        new() { PlayerId = 3, Kind = PlayerSlotKind.Closed },
        new() { PlayerId = 4, Kind = PlayerSlotKind.Closed },
    };
    private bool _lobbyActive;

    public NetTurnManager? NetTurn => _netTurn;
    public uint LocalPlayerId => _localPlayerId;
    public uint Seed => _seed;
    public bool IsHost => _isHost;
    public IReadOnlyList<PlayerSlotSetup> Slots => _slots;
    public new bool IsConnected =>
        _peer != null && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>Raised on every peer once the host has frozen the slot table and fixed the shared
    /// seed. (seed, localPlayerId, frozen slot table). Carries the slot table so each peer can
    /// build an identical world.</summary>
    public event System.Action<uint, uint, IReadOnlyList<PlayerSlotSetup>>? OnGameStart;
    /// <summary>Raised whenever the lobby slot table changes (peer join/leave, host slot edit).
    /// Host raises it locally after broadcasting; clients raise it from
    /// <see cref="ReceiveLobbyState"/>. Main refreshes the lobby UI from it.</summary>
    public event System.Action<IReadOnlyList<PlayerSlotSetup>>? OnLobbyStateChanged;
    public event System.Action<string>? OnOOS;
    /// <summary>收到聊天消息（playerId, text）。MP 时由 ReceiveChat RPC 触发；SP 不经此（直接 raise SimEventBus）。</summary>
    public event System.Action<int, string>? OnChatReceived;

    public void StartHost(int port, uint seed)
    {
        _isHost = true;
        _localPlayerId = 1;
        _seed = seed;
        _lobbyActive = true;
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
        _lobbyActive = true;
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
        if (!_isHost) return;
        // Lobby phase only: claim a Human slot for the new peer, then broadcast the lobby
        // state. The host does NOT start the game here — that waits for HostStartGame so the
        // host can configure AI slots first. Host owns slot 1; clients fill slots 2..K in
        // connect order. AI/Closed slots are host-assigned, never connect-assigned.
        if (!_peerToPlayer.ContainsKey((int)id))
        {
            int claimedSlot = _slots
                .Where(s => s.Kind == PlayerSlotKind.Human && s.PlayerId > 1
                            && !_peerToPlayer.ContainsValue((uint)s.PlayerId))
                .Select(s => s.PlayerId)
                .OrderBy(p => p)
                .DefaultIfEmpty(-1)
                .First();
            if (claimedSlot == -1)
            {
                GD.PrintErr($"No free Human slot for peer {id} (lobby full)");
                return;
            }
            _peerToPlayer[(int)id] = (uint)claimedSlot;
        }
        BroadcastLobbyState();
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"Peer disconnected: {id}");
        _peerToPlayer.Remove((int)id);
        if (_lobbyActive && _isHost) BroadcastLobbyState();
        // Mid-game leave: out of scope (no reconnection/host migration — design doc §9).
    }

    /// <summary>Host → all: broadcast the current peer map + slot table. Clients store it and
    /// refresh their lobby UI; the host refreshes its own UI via the local event raise.</summary>
    private void BroadcastLobbyState()
    {
        var (kinds, civs, teams) = PlayerSlotSetupCodec.Pack(_slots);
        var peers = _peerToPlayer.Keys.ToArray();
        var players = _peerToPlayer.Values.Select(v => (int)v).ToArray();
        Rpc(nameof(ReceiveLobbyState), peers, players, kinds, civs, teams);
        OnLobbyStateChanged?.Invoke(_slots);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveLobbyState(int[] peers, int[] players, int[] kinds, string[] civs, int[] teams)
    {
        _peerToPlayer.Clear();
        for (int i = 0; i < peers.Length; i++)
            _peerToPlayer[peers[i]] = (uint)players[i];
        long myPeer = Multiplayer.GetUniqueId();
        _localPlayerId = _peerToPlayer.TryGetValue((int)myPeer, out var pid) ? pid : 1;
        _slots = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);
        OnLobbyStateChanged?.Invoke(_slots);
    }

    /// <summary>Host-only: edit one slot's kind/civ/team. Slot 1 is locked Human (the host).
    /// Emits a lobby broadcast so clients see the change.</summary>
    public void HostSetSlot(int playerId, PlayerSlotKind kind, string civ, int team)
    {
        if (!_isHost || !_lobbyActive) return;
        int idx = _slots.FindIndex(s => s.PlayerId == playerId);
        if (idx < 0 || playerId == 1) return;
        _slots[idx] = _slots[idx] with { Kind = kind, Civ = civ, Team = team };
        BroadcastLobbyState();
    }

    /// <summary>Host-only: freeze the slot table and start the game. Requires every Human slot
    /// to be claimed by a connected peer (unclaimed Human slots would deadlock the host, since
    /// expectedPlayers = Human slots and the host waits for each to submit a batch).</summary>
    public void HostStartGame()
    {
        if (!_isHost || !_lobbyActive) return;
        int humanSlots = _slots.Count(s => s.Kind == PlayerSlotKind.Human);
        if (humanSlots != _peerToPlayer.Count)
        {
            GD.PrintErr($"Cannot start: {_peerToPlayer.Count} connected peer(s) but {humanSlots} Human slot(s). "
                        + "Each Human slot must be claimed (adjust slots to match).");
            return;
        }
        _lobbyActive = false;
        var (kinds, civs, teams) = PlayerSlotSetupCodec.Pack(_slots);
        Rpc(nameof(ReceiveGameStart), _seed, kinds, civs, teams);
        OnGameStart?.Invoke(_seed, _localPlayerId, _slots); // host starts its own game
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveGameStart(uint seed, int[] kinds, string[] civs, int[] teams)
    {
        _seed = seed;
        _lobbyActive = false;
        _slots = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);
        long myPeer = Multiplayer.GetUniqueId();
        _localPlayerId = _peerToPlayer.TryGetValue((int)myPeer, out var pid) ? pid : 1;
        GD.Print($"Game starting: seed={seed}, localPlayer={_localPlayerId}, slots={_slots.Count}");
        OnGameStart?.Invoke(seed, _localPlayerId, _slots);
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

    // ── 聊天（直接网络消息，不进锁步；匹配原版 NMT_CHAT）──

    /// <summary>客户端 → host：提交聊天。host 解析发送者后广播给所有人。</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitChatToHost(int playerId, string text)
    {
        // host 用 _peerToPlayer 校验发送者身份（防伪造 playerId）。
        int sender = Multiplayer.GetRemoteSenderId();
        if (_peerToPlayer.TryGetValue(sender, out var resolved))
            playerId = (int)resolved;
        // 广播给所有人（含自己，CallLocal=true）。
        Rpc(nameof(ReceiveChat), playerId, text);
    }

    /// <summary>host → 所有人：广播聊天。</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveChat(int playerId, string text)
    {
        OnChatReceived?.Invoke(playerId, text);
    }

    /// <summary>公开发送方法：SP 直接回显；MP 经 host 广播。</summary>
    public void SendChat(int playerId, string text)
    {
        if (_peer == null)
            OnChatReceived?.Invoke(playerId, text);  // SP：本地回显
        else if (_isHost)
            Rpc(nameof(ReceiveChat), playerId, text);  // host 直接广播
        else
            RpcId(1, nameof(SubmitChatToHost), playerId, text);  // client → host
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

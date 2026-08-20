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
    /// slot 2 = AI opponent (matching the original gamesetup default, so a solo host can
    /// Start immediately), slots 3/4 Closed. A joining client claims the first unclaimed
    /// Human slot, else bumps an AI slot back to Human (original behaviour: joiners
    /// replace AI). The host edits this in the lobby; it is frozen at Start and broadcast.</summary>
    private List<PlayerSlotSetup> _slots = new()
    {
        new() { PlayerId = 1, Kind = PlayerSlotKind.Human,  Civ = "athen", Team = -1 },
        new() { PlayerId = 2, Kind = PlayerSlotKind.AI,     Civ = "gaul",  Team = -1 },
        new() { PlayerId = 3, Kind = PlayerSlotKind.Closed },
        new() { PlayerId = 4, Kind = PlayerSlotKind.Closed },
    };
    private bool _lobbyActive;
    /// <summary>Host 选定的大厅地图("" = 默认 arcadia 回退链)。随 lobby 广播 + GameStart
    /// 冻结下发,双端 SetupTerrain 同图——选图已进协议,不再是 SP 独占。</summary>
    private string _mapPath = "";

    /// <summary>gamesetup_mp 的可配置选项（host 大厅可改并广播;Start 时冻结,
    /// 双端各自写进 GameLaunchConfig 后由 ApplyMatchOptions 落地,保证双端一致）。
    /// 字段语义与 SP gamesetup 一致;VictoryConditions 以 "," 连接传输。</summary>
    public sealed record MpLobbyOptions
    {
        public int MapSize = 256;
        public string BiomeId = "";
        public string PlayerPlacement = "circle";
        public int StartingResources = 300;
        public int PopulationCap = 300;
        public int PopulationCapTypeIdx = 0;
        public float GameSpeed = 1f;
        public int CeasefireMinutes;
        public bool Nomad;
        public bool Treasures = true;
        public bool ExploredMap;
        public bool RevealedMap;
        public bool AlliedView = true;
        public bool LockedTeams;
        public bool Cheats;
        public bool Spies;
        public bool LastManStanding;
        public List<string> VictoryConditions = new() { "conquest" };

        public int[] PackInts() => new[]
        {
            MapSize, StartingResources, PopulationCap, PopulationCapTypeIdx, CeasefireMinutes,
            (int)(GameSpeed * 100),
            (Nomad ? 1 : 0) | (Treasures ? 2 : 0) | (ExploredMap ? 4 : 0) | (RevealedMap ? 8 : 0)
                | (AlliedView ? 16 : 0) | (LockedTeams ? 32 : 0) | (Cheats ? 64 : 0)
                | (Spies ? 128 : 0) | (LastManStanding ? 256 : 0),
        };
        public string[] PackStrings() => new[]
        {
            BiomeId, PlayerPlacement, string.Join(',', VictoryConditions),
        };
        public static MpLobbyOptions Unpack(int[] ints, string[] strings)
        {
            var o = new MpLobbyOptions
            {
                MapSize = ints[0],
                StartingResources = ints[1],
                PopulationCap = ints[2],
                PopulationCapTypeIdx = ints[3],
                CeasefireMinutes = ints[4],
                GameSpeed = ints[5] / 100f,
                BiomeId = strings[0],
                PlayerPlacement = strings[1],
            };
            int f = ints[6];
            o.Nomad = (f & 1) != 0;
            o.Treasures = (f & 2) != 0;
            o.ExploredMap = (f & 4) != 0;
            o.RevealedMap = (f & 8) != 0;
            o.AlliedView = (f & 16) != 0;
            o.LockedTeams = (f & 32) != 0;
            o.Cheats = (f & 64) != 0;
            o.Spies = (f & 128) != 0;
            o.LastManStanding = (f & 256) != 0;
            o.VictoryConditions = strings[2].Length > 0
                ? strings[2].Split(',').ToList()
                : new List<string> { "conquest" };
            return o;
        }
    }

    /// <summary>大厅当前选项(host 可改;客户端只读,随广播刷新)。</summary>
    public MpLobbyOptions LobbyOptions { get; private set; } = new();

    /// <summary>大厅选项变更(host 改 / 客户端收到广播)——LobbyUI 刷新显示。</summary>
    public event System.Action<MpLobbyOptions>? OnLobbyOptionsChanged;

    public NetTurnManager? NetTurn => _netTurn;
    public uint LocalPlayerId => _localPlayerId;
    public uint Seed => _seed;
    public bool IsHost => _isHost;
    public IReadOnlyList<PlayerSlotSetup> Slots => _slots;
    public new bool IsConnected =>
        _peer != null && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>Raised on every peer once the host has frozen the slot table and fixed the shared
    /// seed. (seed, localPlayerId, frozen slot table, mapPath). Carries the slot table + map so
    /// each peer can build an identical world. mapPath "" = 默认 arcadia 回退链。</summary>
    public event System.Action<uint, uint, IReadOnlyList<PlayerSlotSetup>, string>? OnGameStart;
    /// <summary>Raised whenever the lobby slot table changes (peer join/leave, host slot edit).
    /// Host raises it locally after broadcasting; clients raise it from
    /// <see cref="ReceiveLobbyState"/>. Main refreshes the lobby UI from it.</summary>
    public event System.Action<IReadOnlyList<PlayerSlotSetup>>? OnLobbyStateChanged;
    /// <summary>大厅地图变更(host 改选 / 客户端收到广播)。rel 路径或 "random/name",
    /// "" = 默认。客户端用它刷新只读地图行。</summary>
    public event System.Action<string>? OnMapChanged;
    public event System.Action<string>? OnOOS;
    /// <summary>Host clicked Start but the slot table can't start (unclaimed Human slots).
    /// Carries a user-readable reason — the lobby shows it in its status line (previously
    /// the refusal was console-only, looking like a dead Start button).</summary>
    public event System.Action<string>? OnStartRefused;
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
        ZeroAD.Sim.Diag.Log("MP", $"Hosting on port {port}, seed={seed}, player=1");
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
        ZeroAD.Sim.Diag.Log("MP", $"Connecting to {address}:{port}");
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
        ZeroAD.Sim.Diag.Log("MP", $"Peer connected: {id}");
        if (!_isHost) return;
        // Lobby phase only: claim a Human slot for the new peer, then broadcast the lobby
        // state. The host does NOT start the game here — that waits for HostStartGame so the
        // host can configure AI slots first. Host owns slot 1; clients fill slots 2..K in
        // connect order. AI/Closed slots are host-assigned, never connect-assigned.
        if (!_peerToPlayer.ContainsKey((int)id))
        {
            // 先占未被认领的 Human 槽;没有则挤掉第一个 AI 槽(原版行为:加入者顶替 AI,
            // 否则默认 AI 槽的房间里客户端永远 "lobby full")。
            int claimedSlot = _slots
                .Where(s => s.Kind == PlayerSlotKind.Human && s.PlayerId > 1
                            && !_peerToPlayer.ContainsValue((uint)s.PlayerId))
                .Select(s => s.PlayerId)
                .OrderBy(p => p)
                .DefaultIfEmpty(-1)
                .First();
            if (claimedSlot == -1)
            {
                claimedSlot = _slots
                    .Where(s => s.Kind == PlayerSlotKind.AI && s.PlayerId > 1)
                    .Select(s => s.PlayerId)
                    .OrderBy(p => p)
                    .DefaultIfEmpty(-1)
                    .First();
                if (claimedSlot != -1)
                {
                    int idx = _slots.FindIndex(s => s.PlayerId == claimedSlot);
                    _slots[idx] = _slots[idx] with { Kind = PlayerSlotKind.Human };
                    ZeroAD.Sim.Diag.Log("MP", $"Peer {id} bumped AI in slot {claimedSlot}");
                }
            }
            if (claimedSlot == -1)
            {
                ZeroAD.Sim.Diag.Err("MP", $"No free slot for peer {id} (lobby full)");
                return;
            }
            _peerToPlayer[(int)id] = (uint)claimedSlot;
        }
        BroadcastLobbyState();
    }

    private void OnPeerDisconnected(long id)
    {
        ZeroAD.Sim.Diag.Log("MP", $"Peer disconnected: {id}");
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
        Rpc(nameof(ReceiveLobbyState), peers, players, kinds, civs, teams, _mapPath,
            LobbyOptions.PackInts(), LobbyOptions.PackStrings());
        OnLobbyStateChanged?.Invoke(_slots);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveLobbyState(int[] peers, int[] players, int[] kinds, string[] civs, int[] teams, string mapPath,
        int[] optionInts, string[] optionStrings)
    {
        _peerToPlayer.Clear();
        for (int i = 0; i < peers.Length; i++)
            _peerToPlayer[peers[i]] = (uint)players[i];
        long myPeer = Multiplayer.GetUniqueId();
        _localPlayerId = _peerToPlayer.TryGetValue((int)myPeer, out var pid) ? pid : 1;
        _slots = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);
        if (_mapPath != mapPath)
        {
            _mapPath = mapPath;
            OnMapChanged?.Invoke(mapPath);
        }
        LobbyOptions = MpLobbyOptions.Unpack(optionInts, optionStrings);
        OnLobbyOptionsChanged?.Invoke(LobbyOptions);
        OnLobbyStateChanged?.Invoke(_slots);
    }

    /// <summary>Host-only: change the lobby map (rel pmp path / "random/name" / "" = 默认).
    /// 广播进 lobby 状态,Start 时冻结进 GameStart——双端凭同一路径建同一张图。</summary>
    public void HostSetMap(string mapPath)
    {
        if (!_isHost || !_lobbyActive) return;
        if (_mapPath == mapPath) return;
        _mapPath = mapPath;
        BroadcastLobbyState();
        OnMapChanged?.Invoke(mapPath);
    }

    /// <summary>该 Human 槽是否已被某个连接的 peer 认领（显示 "Peer N" 与锁定用）。</summary>
    public bool IsSlotClaimedByPeer(int playerId) => _peerToPlayer.ContainsValue((uint)playerId);

    /// <summary>认领该槽的 peer id（显示用;未认领 → null）。</summary>
    public int? PeerIdOfSlot(int playerId)
    {
        foreach (var kv in _peerToPlayer)
            if (kv.Value == (uint)playerId) return kv.Key;
        return null;
    }

    /// <summary>Host-only: replace the lobby options (gamesetup_mp 的可改设置)并广播。
    /// Start 时冻结——双端各自以此配置建局。</summary>
    public void HostSetOptions(MpLobbyOptions options)
    {
        if (!_isHost || !_lobbyActive) return;
        LobbyOptions = options;
        OnLobbyOptionsChanged?.Invoke(options);
        BroadcastLobbyState();
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
            string reason = $"Cannot start: {humanSlots} Human slot(s) but only " +
                            $"{_peerToPlayer.Count} player(s) connected. " +
                            "Set unclaimed Human slots to AI or Closed.";
            ZeroAD.Sim.Diag.Err("MP", reason);
            OnStartRefused?.Invoke(reason);
            return;
        }
        _lobbyActive = false;
        var (kinds, civs, teams) = PlayerSlotSetupCodec.Pack(_slots);
        Rpc(nameof(ReceiveGameStart), _seed, kinds, civs, teams, _mapPath);
        OnGameStart?.Invoke(_seed, _localPlayerId, _slots, _mapPath); // host starts its own game
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveGameStart(uint seed, int[] kinds, string[] civs, int[] teams, string mapPath)
    {
        _seed = seed;
        _lobbyActive = false;
        _slots = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);
        _mapPath = mapPath;
        long myPeer = Multiplayer.GetUniqueId();
        _localPlayerId = _peerToPlayer.TryGetValue((int)myPeer, out var pid) ? pid : 1;
        ZeroAD.Sim.Diag.Log("MP", $"Game starting: seed={seed}, localPlayer={_localPlayerId}, slots={_slots.Count}, map={mapPath}");
        OnGameStart?.Invoke(seed, _localPlayerId, _slots, mapPath);
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
        ZeroAD.Sim.Diag.Err("MP", $"OOS at turn {turn}: {msg}");
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

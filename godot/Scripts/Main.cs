using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

public sealed partial class Main : Node3D
{
	private RTSCamera _camera = null!;
	private SimBridge _sim = null!;
	private Node3D _units = null!;
	private HUD _hud = null!;
	private LobbyUI _lobby = null!;
	private MultiplayerController _mp = null!;

	private readonly HashSet<EntityId> _selectedEntities = new();
	private bool _dragSelecting;
	private Vector2 _dragStart;
	private bool _isDragging;
	private bool _placeBuildingMode;
	private string _buildTemplate = "";
	private PetraAI _ai = null!;
	private bool _gameStarted;
	private bool _isTutorial;
	private TutorialPanel _tutorialPanel = null!;
	private LoadingOverlay? _loadingOverlay;

	public IReadOnlySet<EntityId> SelectedEntities => _selectedEntities;
	public bool IsTutorial => _isTutorial;
	public SimBridge Sim => _sim;
	public void SetCameraFocus(Vector3 pos) => _camera.SetFocus(pos);
	public Vector3? GetCameraFocus() => _camera?.Focus;
	public float GetCameraYaw() => _camera?.Yaw ?? 0f;

	public override void _Ready()
	{
		_camera = new RTSCamera();
		AddChild(_camera);

		var light = new DirectionalLight3D();
		light.Rotation = new Vector3(-0.7f, 0.5f, 0);
		light.LightEnergy = 1.2f;
		AddChild(light);

		var sky = new WorldEnvironment();
		var env = new global::Godot.Environment();
		env.BackgroundColor = new Color(0.45f, 0.65f, 0.9f);
		env.FogEnabled = true;
		env.FogLightColor = new Color(0.5f, 0.7f, 0.95f);
		env.FogDensity = 0.001f;
		sky.Environment = env;
		AddChild(sky);

		_units = new Node3D { Name = "Units" };
		AddChild(_units);

		_sim = new SimBridge { UnitContainer = _units };
		AddChild(_sim);

		_mp = new MultiplayerController { Name = "Multiplayer" };
		AddChild(_mp);

		_ai = new PetraAI { Name = "PetraAI" };
		AddChild(_ai);

		_lobby = new LobbyUI();
		AddChild(_lobby);

		_lobby.OnHostStart += (port, seed) => StartGame(true, port, seed);
		_lobby.OnClientConnect += (addr, port) => StartGame(false, addr, port);
		_lobby.OnSinglePlayer += seed => StartSinglePlayer(seed);
		_lobby.OnTutorialStart += () => StartTutorial();

		_camera.SetFocus(new Vector3(128, 0, 128));

		if (OS.GetEnvironment("ZEROAD_AUTOSTART") == "1")
			CallDeferred(nameof(AutoStart));
		if (OS.GetEnvironment("ZEROAD_TUTORIAL") == "1")
			CallDeferred(nameof(AutoTutorial));
	}

	private void AutoStart() => StartSinglePlayer(42);
	private void AutoTutorial() => StartTutorial();

	private void StartTutorial()
	{
		// Show a loading overlay BEFORE the heavy synchronous work (template parse +
		// terrain load + scenario spawn all happen in BeginGameplay). Godot's frame
		// loop runs _Process BEFORE rendering, so checking a flag in _Process and
		// immediately blocking would never let the overlay draw. Instead we use a
		// one-shot Timer (0.15s = ~9 frames at 60fps) to guarantee the overlay has
		// rendered several times before the blocking scenario setup starts.
		_loadingOverlay = new LoadingOverlay("Loading Introductory Tutorial...");
		AddChild(_loadingOverlay);

		var timer = new Timer { WaitTime = 0.15, OneShot = true, Autostart = true };
		AddChild(timer);
		timer.Timeout += () =>
		{
			BeginGameplay(42, 1, tutorial: true);
			_loadingOverlay?.QueueFree();
			_loadingOverlay = null;
			timer.QueueFree();
		};
	}

	private void StartSinglePlayer(uint seed)
	{
		BeginGameplay(seed, 1);
	}

	private void StartGame(bool isHost, int param1, uint seed)
	{
		// Host selects the seed; the client learns it from the host's GameStart message.
		// Both peers defer world creation until GameStart fires so they share one seed.
		if (isHost)
		{
			_mp.StartHost(param1, seed);
			_mp.OnGameStart += (s, pid) => BeginGameplay(s, pid, isMultiplayer: true, isHost: true);
		}
		else
		{
			_mp.StartClient("127.0.0.1", param1);
			_mp.OnGameStart += (s, pid) => BeginGameplay(s, pid, isMultiplayer: true, isHost: false);
		}

		_lobby.SetStatus("Waiting for players...");
	}

	private void StartGame(bool isHost, string addr, int port)
	{
		_ = isHost;
		_mp.StartClient(addr, port);
		_mp.OnGameStart += (s, pid) => BeginGameplay(s, pid, isMultiplayer: true, isHost: false);
		_lobby.SetStatus("Connecting...");
	}

	private void BeginGameplay(uint seed, uint playerId, bool tutorial = false,
		bool isMultiplayer = false, bool isHost = false)
	{
		if (_gameStarted) return;
		_gameStarted = true;
		_isTutorial = tutorial;
		_lobby.Hide();
		GD.Print($"[Tutorial] BeginGameplay start: tutorial={tutorial}");

		try
		{
			string? templatesPath = FindTemplatesPath();
			GD.Print($"[Tutorial] templatesPath={templatesPath ?? "null"}");

			// One InitWorld path for SP/MP/tutorial: seed + player slots + role all flow in
			// here. In MP the host assigned the seed and player ids over GameStart, so every
			// peer constructs the same world and the same NetTurnManager.
			var role = isMultiplayer
				? (isHost ? ZeroAD.Sim.Net.NetRole.Host : ZeroAD.Sim.Net.NetRole.Client)
				: ZeroAD.Sim.Net.NetRole.Standalone;
			int playerCount = isMultiplayer ? 2 : 1;
			_sim.InitWorld(templatesPath, seed, playerId, role, playerCount);
			GD.Print("[Tutorial] InitWorld done");

			if (isMultiplayer)
			{
				// Wire the transport to the freshly built NetTurnManager. The host bootstraps
				// its empty leading turns so play can start immediately.
				_mp.AttachTurnManager(_sim.NetTurn);
				_mp.OnOOS += OnOOSDetected;
				GD.Print("[MP] AttachTurnManager done");
			}

			if (_hud == null)
			{
				_hud = new HUD(_sim, this);
				AddChild(_hud);
				// Game-over overlay: subscribes to the sim's win/loss events and shows the
				// Victory/Defeat panel when the match ends.
				var gameOver = new GameOverOverlay(_sim, localPlayerId: (int)playerId);
				AddChild(gameOver);
			}

			if (_isTutorial)
			{
				_tutorialPanel = new TutorialPanel();
				AddChild(_tutorialPanel);
				_tutorialPanel.OnReadyPressed += () => _sim.Tutorial?.OnReadyPressed();
				_tutorialPanel.OnQuitPressed += QuitTutorial;
				_sim.Events.TutorialMessage += OnTutorialMessage;
			}

			// Fog-of-war: a selected mirage swaps back to the real entity when it returns
			// to sight (MT_EntityRenamed semantics), so orders/GUI keep targeting the real one.
			_sim.Events.MirageSwapBack += e =>
			{
				if (e.Player == (int)_sim.LocalPlayerId && _selectedEntities.Remove(e.Mirage))
					_selectedEntities.Add(e.Parent);
			};

			if (_isTutorial)
			{
				GD.Print("[Tutorial] calling SetupTutorialWorld...");
				try
				{
					SetupTutorialWorld();
				}
				catch (System.Exception ex)
				{
					GD.PrintErr($"[Tutorial] SetupTutorialWorld FAILED: {ex}");
					GD.PrintErr($"[Tutorial] Stack: {ex.StackTrace}");
					// Don't rethrow — let the game continue without the tutorial scenario rather
					// than crash. The player can still see terrain and the panel.
				}
				GD.Print("[Tutorial] SetupTutorialWorld done");
			}
			else
				SetupGameWorld(playerId);

			GD.Print(_isTutorial
				? "[Tutorial] Introductory Tutorial started"
				: $"[Tutorial] MS6 Game started: player={playerId}, seed={seed}");
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[Tutorial] EXCEPTION in BeginGameplay: {e}");
			GD.PrintErr($"[Tutorial] Stack: {e.StackTrace}");
			throw;
		}
	}

	private void OnTutorialMessage(TutorialNotification notification)
	{
		_tutorialPanel.UpdateTutorial(
			notification.Instructions,
			notification.Warning,
			notification.ReadyButton,
			notification.Leave);
	}

	private void QuitTutorial()
	{
		_gameStarted = false;
		_isTutorial = false;
		_lobby.Show();
		_tutorialPanel?.QueueFree();
		_hud?.QueueFree();
		_hud = null!;
		GetTree().ReloadCurrentScene();
	}

	private string? FindTemplatesPath()
	{
		string projRoot = ProjectSettings.GlobalizePath("res://");
		var candidates = new[]
		{
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries", "data", "mods", "public", "simulation", "templates")),
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries", "data", "mods", "public", "simulation", "templates")),
		};
		foreach (string dir in candidates)
		{
			if (System.IO.Directory.Exists(dir))
			{
				GD.Print($"Found templates at: {dir}");
				return dir;
			}
		}
		GD.PrintErr("FindTemplatesPath: templates dir not found under binaries/data/mods/public/simulation/templates");
		return null;
	}

	private void SetupTerrain(string? pmpRelPath = null)
	{
		string? pmpPath = pmpRelPath != null ? FindDataPath(pmpRelPath) : null;
		pmpPath ??= FindDataPath("maps/scenarios/arcadia.pmp")
			?? FindDataPath("maps/scenarios/laconia_01.pmp");

		if (pmpPath != null)
		{
			try
			{
				var pmp = PmpMap.Load(pmpPath);
				var terrainNode = TerrainRenderer.CreateFromHeightmap(pmp);
				AddChild(terrainNode);
				_sim.FogWorld.Attach(terrainNode, pmp.MapSizeMeters);
				TerrainHeightService.Set(pmp.GetHeightWorld);
				float h = pmp.GetHeightWorld(130, 122);
				_camera.SetFocus(new Vector3(130, h, 122));
				GD.Print($"Loaded PMP terrain: {pmpPath} ({pmp.PatchesPerSide} patches, {pmp.MapSizeMeters}m, height at spawn: {h:F1}m)");

				string? xmlPath = pmpPath.Replace(".pmp", ".xml");
				var water = WaterRenderer.LoadWaterFromXml(xmlPath);
				float waterHeight = water?.height ?? -999f;
				if (water != null)
				{
					var waterMesh = WaterRenderer.CreateWaterPlane(water.Value.height, water.Value.color, pmp.MapSizeMeters);
					AddChild(waterMesh);
					GD.Print($"Water: height={water.Value.height:F1}m color={water.Value.color}");
				}

				// Record the authoritative sim-side water height (matches CCmpWaterManager).
				// The passability grid below is still baked from it for now; a future pass will
				// derive tiles dynamically from (terrainHeight, waterHeight).
				if (water != null)
					_sim.Sim.Water.SetWaterLevel(ZeroAD.Sim.Maths.Fixed.FromFloat(waterHeight));

				// Fill the sim-side passability grid from the heightmap: any tile whose terrain
				// height is at/below the water level is Water, everything else is Land. This drives
				// BuildRestrictions (can't build on water) and Footprint spawn placement.
				FillPassabilityFromPmp(pmp, waterHeight);

				return;
			}
			catch (System.Exception e)
			{
				GD.PrintErr($"PMP load failed: {e.Message}, falling back to generated terrain");
			}
		}

		var map = MapGenerator.GenerateContinents(8, 42);
		// No fog attach here: the generated mesh emits no UVs and uses vertex-color albedo,
		// which the fog shader can't sample — fog stays a PMP-terrain feature for now.
		AddChild(MapGenerator.CreateMeshFromGenerated(map));
		TerrainHeightService.Set((x, z) =>
		{
			int gx = (int)(x / map.TileSize);
			int gz = (int)(z / map.TileSize);
			return map.GetHeight(gx, gz);
		});
		_camera.SetFocus(new Vector3(130, 0, 122));
		// Generated terrain has no water by default; mark everything land so placement still works.
		FillPassabilityAllLand();
		GD.Print("Using generated terrain (no PMP found)");
	}

	/// <summary>Build a [MapSize,MapSize] passability grid from the PMP heightmap + water level and
	/// hand it to the sim-side TerrainComponent. Tiles at/below water are Water, the rest Land.
	/// Also reconfigures TerrainComponent + ObstructionManager bounds to the real map size — they
	/// default to 256m (64 tiles) but real maps are larger (tutorial = 768m), and without this the
	/// placement checks wrongly flag everything in-bounds as FailOutOfBounds.</summary>
	private void FillPassabilityFromPmp(PmpMap pmp, float waterHeight)
	{
		var terrain = _sim.Terrain;
		if (terrain == null) return;

		// Reconfigure terrain dimensions to the actual map, then size the grid to match.
		int tilesPerSide = pmp.TilesPerSide;
		terrain.Configure(tilesPerSide, PmpMap.TileSize);
		var grid = new ZeroAD.Sim.Components.TerrainClass[tilesPerSide, tilesPerSide];
		for (int tz = 0; tz < tilesPerSide; tz++)
			for (int tx = 0; tx < tilesPerSide; tx++)
			{
				float wx = (tx + 0.5f) * terrain.TileSize;
				float wz = (tz + 0.5f) * terrain.TileSize;
				float groundH = pmp.GetHeightWorld(wx, wz);
				grid[tx, tz] = groundH <= waterHeight
					? ZeroAD.Sim.Components.TerrainClass.Water
					: ZeroAD.Sim.Components.TerrainClass.Land;
			}
		terrain.SetPassabilityGrid(grid);

		// Match the obstruction + range spatial-index world bounds to the real map so queries
		// don't clamp to the old 256m limit. SetBounds re-indexes existing shapes.
			float worldM = pmp.MapSizeMeters;
			var f0 = ZeroAD.Sim.Maths.Fixed.Zero;
			var f1 = ZeroAD.Sim.Maths.Fixed.FromFloat(worldM);
			_sim.Obstructions.SetBounds(f0, f0, f1, f1);
			// The fog-of-war vertex grid must cover the real map too (same bounds as the
			// spatial index — one LosGrid vertex per 4m).
			_sim.Range.SetBounds(f1);

			// Build the M3 pathfinding pipeline (passability grid → hierarchical connectivity →
			// A*) now that terrain + obstructions reflect the real map.
			_sim.Pathfinder.RebuildGrid();
		}

	private void FillPassabilityAllLand()
	{
		var terrain = _sim.Terrain;
		if (terrain == null) return;
		int n = terrain.MapSize;
		var grid = new ZeroAD.Sim.Components.TerrainClass[n, n];
		// Default Land (0) is already the zero value, so no need to fill explicitly.
		terrain.SetPassabilityGrid(grid);

		// Match the obstruction bounds to the generated map, then build the pathfinding grid
		// (the PMP path does the same in FillPassabilityFromPmp). Without this, the pathfinder's
		// grid stays null and ComputePath returns empty paths — units would only ever move in
		// straight lines, ignoring terrain and obstructions.
		float worldM = n * terrain.TileSize;
		var f0 = ZeroAD.Sim.Maths.Fixed.Zero;
		var f1 = ZeroAD.Sim.Maths.Fixed.FromFloat(worldM);
		_sim.Obstructions.SetBounds(f0, f0, f1, f1);
		_sim.Range.SetBounds(f1);
		_sim.Pathfinder.RebuildGrid();
	}

	private string? FindDataPath(string relPath)
	{
		string projRoot = ProjectSettings.GlobalizePath("res://");
		var candidates = new[]
		{
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries", "data", "mods", "public", relPath)),
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries", "data", "mods", "public", relPath)),
		};
		foreach (var p in candidates)
			if (System.IO.File.Exists(p))
				return p;
		return null;
	}

	private string? FindDataRoot()
	{
		string projRoot = ProjectSettings.GlobalizePath("res://");
		var candidates = new[]
		{
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries", "data", "mods", "public")),
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries", "data", "mods", "public")),
		};
		foreach (var p in candidates)
			if (System.IO.Directory.Exists(p))
				return p;
		return null;
	}

	private void SetupTutorialWorld()
	{
		GD.Print("[Tutorial] SetupTutorialWorld: loading terrain...");
		SetupTerrain("maps/tutorials/introductory_tutorial.pmp");
		GD.Print("[Tutorial] terrain loaded");

		string? dataRoot = FindDataRoot();
		GD.Print($"[Tutorial] dataRoot={dataRoot ?? "null"}");
		if (dataRoot != null)
		{
			GD.Print("[Tutorial] loading scenario...");
			var scenario = _sim.LoadTutorialScenario(dataRoot);
			if (scenario != null)
			{
				GD.Print($"[Tutorial] scenario loaded: {scenario.Entities.Count} entities, camera=({scenario.CameraX},{scenario.CameraZ})");
				// The scenario's <Camera> position is the Atlas editor's last pose — restore
				// it on launch (matches 0 A.D.'s "start where the designer left off"). The
				// look-at (focus) is the player's (P1) civic centre so the base is centered;
				// if no CC is found we fall back to focusing the scenario's camera position.
				float focusX = scenario.CameraX, focusZ = scenario.CameraZ;
				bool foundCc = false;
				foreach (var ent in scenario.Entities)
				{
					if (ent.Player != 1 || !ent.IsSimulationEntity) continue;
					if (ent.Template.Contains("civil_centre") || ent.Template.Contains("civic_centre"))
					{
						focusX = ent.X; focusZ = ent.Z;
						GD.Print($"[Tutorial] focusing P1 civic centre at ({focusX},{focusZ})");
						foundCc = true;
						break;
					}
				}
				float h = TerrainHeightService.Sample(focusX, focusZ);
				_camera.SetFocus(new Vector3(focusX, h, focusZ));
				// Restore the designer's camera pose (yaw/pitch/distance derived from the
				// scenario Camera → focus vector). Skip when the focus fell back to the
				// camera position itself (no CC) — PlaceFromScenarioCamera needs a non-zero
				// delta to derive a meaningful orbit.
				if (foundCc)
				{
					var camPos = new Vector3(scenario.CameraX, scenario.CameraY, scenario.CameraZ);
					_camera.PlaceFromScenarioCamera(camPos);
					GD.Print($"[Tutorial] restored scenario camera pose from {camPos} toward focus ({focusX},{focusZ})");
				}
			}
			else
			{
				GD.PrintErr("[Tutorial] LoadTutorialScenario returned null!");
			}
		}
		else
		{
			GD.PrintErr("[Tutorial] FindDataRoot returned null — scenario cannot load");
		}

		GD.Print("[Tutorial] StartTutorial...");
		_sim.StartTutorial();
		GD.Print("[Tutorial] showing panel...");
		_tutorialPanel.ShowTutorial();
		GD.Print("[Tutorial] SetupTutorialWorld complete");
	}

	private void SetupGameWorld(uint playerId)
	{
		SetupTerrain();

		bool useRealTemplates = _sim.Templates != null;
		string civ = "athen";

		if (useRealTemplates)
		{
			GD.Print($"Spawning {civ} civilization units from real templates");
			_sim.SpawnFromTemplate($"structures/{civ}/civil_centre", 120, 120);
			_sim.SpawnFromTemplate($"units/{civ}/support_female_citizen", 132, 118);
			_sim.SpawnFromTemplate($"units/{civ}/support_female_citizen", 136, 118);
			_sim.SpawnFromTemplate($"units/{civ}/support_female_citizen", 140, 118);
			_sim.SpawnFromTemplate($"units/{civ}/infantry_spearman_b", 132, 124);
			_sim.SpawnFromTemplate($"units/{civ}/infantry_spearman_b", 136, 124);
			_sim.SpawnFromTemplate($"units/{civ}/cavalry_swordsman_b", 140, 124);
		}
		else
		{
			_sim.SpawnBuilding(120, 120, "Town Center");
			for (int i = 0; i < 5; i++)
				_sim.SpawnUnit(130 + i * 4, 120, isVillager: true);
			for (int i = 0; i < 3; i++)
				_sim.SpawnUnit(130 + i * 4, 130, isSoldier: true);
		}

		for (int i = 0; i < 30; i++)
		{
			float angle = i * 0.4f;
			float dist = 30 + (i % 3) * 8;
			if (useRealTemplates)
				_sim.SpawnFromTemplate("gaia/tree/oak", 120 + Mathf.Cos(angle) * dist, 120 + Mathf.Sin(angle) * dist);
			else
				_sim.SpawnTree(120 + Mathf.Cos(angle) * dist, 120 + Mathf.Sin(angle) * dist);
		}

		var aiPlayer = _sim.Sim.CreateEntity();
		_sim.Sim.AddComponent(aiPlayer, new PlayerComponent { Wood = 200, Food = 200 });

		var aiTownCenter = _sim.SpawnBuilding(200, 200, "AI Town Center");
		for (int i = 0; i < 3; i++)
		{
			var u = _sim.SpawnUnit(210 + i * 4, 200, isVillager: true);
			_ai.RegisterUnit(u);
		}
		for (int i = 0; i < 2; i++)
		{
			var u = _sim.SpawnUnit(210 + i * 4, 210, isSoldier: true);
			_ai.RegisterUnit(u);
		}
		_ai.RegisterBuilding(aiTownCenter);
		_ai.Init(_sim, aiPlayer);

		_sim.SpawnUnit(80, 80, isSoldier: true);
		_sim.SpawnUnit(85, 85, isSoldier: true);

		// Initial buildings/units were spawned AFTER the map-load RebuildGrid, so their static
		// obstructions aren't in the navcell grid yet. Rebuild once more so pathing accounts for
		// the town centres and any scenario buildings.
		_sim.Pathfinder.RebuildGrid();

		// The sandbox world spawns owner-less entities (no seers) — reveal the map so the
		// dev world isn't shrouded. Scenario/tutorial paths keep real fog.
		_sim.Range.SetLosRevealAll(1, true);

		// Frame the player's starting town centre so the game opens on the player's base, not on
		// the camera's stale default focus. Matches what SetupTutorialWorld does after scenario
		// load. Without this, the camera stays wherever _Ready left it and the player can't see
		// (or click) their own TC without panning first.
		_camera.SetFocus(new Vector3(120, 0, 120));
	}

	private readonly List<Node3D> _selectionMarkers = new();

	public override void _Process(double delta)
	{
		if (!_gameStarted) return;

		UpdateSelectionMarkers();

		// Turn advancement is driven by SimBridge._Process, which honours the lockstep
		// barrier (it only advances when the next turn's bundle has arrived). Nothing to
		// force here.

		TryDebugCapture();
	}

	// --- Debug capture (ZEROAD_CAPTURE=1|gather): screenshot + per-entity diagnostics ---
	private int _captureFrames;
	private bool _captureDone;
	private Camera3D? _debugCam;
	private void TryDebugCapture()
	{
		string mode = System.Environment.GetEnvironmentVariable("ZEROAD_CAPTURE") ?? "";
		if (string.IsNullOrEmpty(mode) || _captureDone) return;
		bool gather = mode == "gather";
		bool wide = mode == "wide"; // RTS default camera view — for terrain comparisons
		bool train = mode == "train"; // train a spearman at the CC, verify trained-unit visuals
		_captureFrames++;

		// gather mode: frame 60 orders the first civilian to chop the nearest tree,
		// so the capture lands inside GATHERING (axe prop + chop animation visible).
		if (gather && _captureFrames == 60)
			_sim.DebugOrderFirstCivilianGatherNearest();

		// train mode: frame 60 queues a spearman + a civilian at the first visible
		// civil centre through the real command path.
		if (train && _captureFrames == 60)
		{
			foreach (var kvp in _sim.EntityNodes)
			{
				var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
				if (ident?.TemplateName?.Contains("civil_centre") != true) continue;
				int lp = (int)_sim.LocalPlayerId;
				if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
				_sim.CommandTrainSoldier(kvp.Key);
				_sim.CommandTrain(kvp.Key);
				break;
			}
		}

		// Mode "1": fixed frames (camera at 175, capture at 180). Mode "wide": RTS
		// camera as-is, capture at 185. Mode "gather": wait until any civilian
		// actually reaches GATHERING (walk time varies with tree distance), spawn
		// the camera that frame, capture the next; hard cap at frame 3000.
		// Mode "train": wait until a trained spearman exists and is visible
		// (training takes ~15s sim), then frame it like the gather camera.
		bool spawnCam;
		bool captureNow;
		if (!gather && !train)
		{
			spawnCam = !wide && _captureFrames == 175 && _debugCam == null;
			captureNow = _captureFrames == (wide ? 600 : 180);
		}
		else
		{
			spawnCam = false;
			if (_debugCam == null && _captureFrames >= (train ? 600 : 900))
			{
				bool ready = false;
				foreach (var kvp in _sim.EntityNodes)
				{
					if (train)
					{
						var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
						if (ident?.TemplateName?.Contains("infantry_spearman") != true) continue;
						int lp = (int)_sim.LocalPlayerId;
						if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
						ready = true; break;
					}
					var g = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.UnitAIComponent>(kvp.Key)?.FsmStateName ?? "";
					// Trigger on APPROACHING (mid-walk) OR GATHERING so we can capture both the
					// walk cycle and the gather cycle from a single capture session.
					if (g.Contains("GATHER.APPROACHING") || g.Contains("GATHER.GATHERING")) { ready = true; break; }
				}
				spawnCam = ready || _captureFrames >= 3000;
			}
			captureNow = _debugCam != null; // the frame after the camera spawned
		}

		// Camera spawn: dedicated debug Camera3D on a visible civilian (RTSCamera._Process
		// fights manual position sets, so we add a separate current camera we control).
		// "wide" instead mounts a high overview above the player's civil centre
		// (RTS camera focus is unreliable in captures) for terrain comparisons.
		if (wide && _debugCam == null && _captureFrames == 175)
		{
			foreach (var kvp in _sim.EntityNodes)
			{
				var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
				if (ident?.TemplateName?.Contains("civil_centre") != true) continue;
				int lp = (int)_sim.LocalPlayerId;
				if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
				var p = kvp.Value.GlobalPosition;
				_debugCam = new Camera3D();
				AddChild(_debugCam);
				_debugCam.GlobalPosition = p + new Vector3(80f, 160f, 140f);
				_debugCam.LookAt(p, Vector3.Up);
				_debugCam.Current = true;
				break;
			}
		}
		if (spawnCam && _debugCam == null)
		{
			Node3D? firstCiv = null;
			foreach (var kvp in _sim.EntityNodes)
			{
				var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
				// train mode frames the trained spearman; other modes frame civilians
				// (ZEROAD_CAPTURE_TARGET overrides the template substring).
				string want = train ? "infantry_spearman"
					: System.Environment.GetEnvironmentVariable("ZEROAD_CAPTURE_TARGET") ?? "support_civilian";
				if (ident?.TemplateName?.Contains(want) != true) continue;
				int lp = (int)_sim.LocalPlayerId;
				if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
				firstCiv ??= kvp.Value;
				// In gather mode prefer the civilian that is actually gathering.
				var fsm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.UnitAIComponent>(kvp.Key)?.FsmStateName ?? "";
				if (gather && !fsm.Contains("GATHER")) continue;
				var p = kvp.Value.GlobalPosition;
				_debugCam = new Camera3D();
				AddChild(_debugCam);
				_debugCam.GlobalPosition = new Vector3(p.X + 4f, p.Y + 3.5f, p.Z + 4f);
				_debugCam.LookAt(p + new Vector3(0, 1f, 0), Vector3.Up);
				_debugCam.Current = true;
				break;
			}
		}

		if (!captureNow) return;
		_captureDone = true;

		string dir = "/tmp/zeroad_debug";
		System.IO.Directory.CreateDirectory(dir);
		// Headless (RasterizerSceneDummy) has no real viewport texture — skip the PNG.
		if (DisplayServer.GetName() != "headless")
			GetViewport().GetTexture().GetImage().SavePng($"{dir}/frame.png");

		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"frame={_captureFrames} entities={_sim.EntityNodes.Count} turn={_sim.NetTurn.CurrentTurn}");
		sb.AppendLine($"camera_pos={_camera.GlobalPosition:F1} camera_focus={_camera.Focus:F1}");
		sb.AppendLine($"debugcam={(_debugCam != null ? _debugCam.GlobalPosition.ToString("F1") : "null")} current={GetViewport().GetCamera3D()?.Name ?? "none"}");
		foreach (var kvp in _sim.EntityNodes)
		{
			var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
			string tmpl = ident?.TemplateName ?? ident?.Name ?? "?";
			var fsm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.UnitAIComponent>(kvp.Key)?.FsmStateName ?? "";
			var gatherer = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.ResourceGatherer>(kvp.Key);
			string gtarget = gatherer?.TargetSupply is EntityId gs ? $" gtarget={gs.Value}" : "";
			var node = kvp.Value;
			var anim = ModelLibrary.FindManualAnimator(node);
			var props = ZeroAD.Godot.Actors.Composition.StatePropSwitcher.Find(node);
			var mesh = _findFirstMesh(node);
			int lp = (int)_sim.LocalPlayerId;
			var vis = _sim.Range.GetLosVisibility(kvp.Key, lp);
			sb.AppendLine($"eid={kvp.Key.Value} tmpl={tmpl} fsm={fsm} pos={node.GlobalPosition:F1}{gtarget} " +
				$"vis={vis} mesh={(mesh != null ? mesh.Name : "none")} " +
				$"anim={(anim != null ? anim.Summary : "none")} clips={(anim != null ? anim.StatesCsv : "")} " +
				$"props={(props != null ? props.Summary : "-")}");
		}
		System.IO.File.WriteAllText($"{dir}/entities.txt", sb.ToString());
		GD.Print($"DEBUG_CAPTURE wrote {dir}/frame.png + entities.txt");
	}

	private static MeshInstance3D? _findFirstMesh(Node n)
	{
		if (n is MeshInstance3D m) return m;
		foreach (var c in n.GetChildren())
		{
			var r = _findFirstMesh(c);
			if (r != null) return r;
		}
		return null;
	}

	/// <summary>
	/// OOS handler: write a binary + text state dump so the two peers' dumps can be
	/// diffed to locate the divergence. Triggered via the host's broadcast once it
	/// detects a state-hash mismatch.
	/// </summary>
	private void OnOOSDetected(string msg)
	{
		string dir = ProjectSettings.GlobalizePath("user://oos");
		var (bin, txt) = ZeroAD.Sim.Serialization.StateDump.WriteAll(
			_sim.Sim, dir, _sim.NetTurn.CurrentTurn, _sim.LocalPlayerId);
		GD.PrintErr($"OOS: {msg}\nState dumped:\n  {txt}\n  {bin}");
	}

	private void UpdateSelectionMarkers()
	{
		foreach (var m in _selectionMarkers)
			m.QueueFree();
		_selectionMarkers.Clear();

		foreach (var eid in _selectedEntities)
		{
			var node = _sim.EntityNodes.GetValueOrDefault(eid);
			if (node == null) continue;
			// Read identity/owner/health through the GuiInterface facade.
			var st = _sim.Gui.GetEntityState(eid);
			bool isBuilding = st?.IsBuilding ?? false;
			int ownerPlayerId = st?.OwnerPlayerId ?? -1;
			int healthMax = st?.HealthMax ?? 0;
			float healthFraction = st?.HealthFraction ?? 0f;
			float ringRadius = isBuilding ? 10f : 2f;

			Color friendlyColor = ownerPlayerId == 1
				? new Color(0.08f, 0.22f, 0.58f)
				: new Color(0.72f, 0.06f, 0.06f);
			Color enemyColor = new Color(0.72f, 0.06f, 0.06f);

			var ring = SelectionRing.Create(ringRadius, friendlyColor, enemyColor,
				isBuilding ? SelectionRing.Shape.Square : SelectionRing.Shape.Circle);
			ring.Position = new Vector3(0, 0.1f, 0);
			node.AddChild(ring);
			_selectionMarkers.Add(ring);

			if (healthMax > 0)
			{
				var bar = SelectionRing.CreateHealthBar(healthFraction);
				bar.Position = new Vector3(0, isBuilding ? 6f : 2.5f, 0);
				node.AddChild(bar);
				_selectionMarkers.Add(bar);
			}
		}
	}

	// _UnhandledInput (not _Input) so that clicks absorbed by the HUD's Control nodes —
	// e.g. pressing a training button — don't also fall through to HandleLeftClick and wipe
	// the current selection. GUI-consumed events never reach here; only raw 3D-scene clicks do.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_gameStarted) return;

		if (@event is InputEventKey key && key.Pressed)
		{
			if (key.Keycode == Key.H && _isTutorial) _tutorialPanel.Toggle();
			if (key.Keycode == Key.B) EnterBuildMode("House");
			if (key.Keycode == Key.T) TrainVillager(Input.IsKeyPressed(Key.Shift));
			if (key.Keycode == Key.S) TrainSoldier(Input.IsKeyPressed(Key.Shift));
			if (key.Keycode == Key.Escape) { _placeBuildingMode = false; _selectedEntities.Clear(); }
		}

		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				if (_placeBuildingMode) { PlaceBuilding(mb.Position); return; }
				_dragStart = mb.Position;
				_dragSelecting = true;
				_isDragging = false;
			}
			else if (mb.ButtonIndex == MouseButton.Right)
				HandleRightClick(mb.Position);
		}

		if (@event is InputEventMouseMotion mm && _dragSelecting && mm.Position.DistanceTo(_dragStart) > 8f)
			_isDragging = true;

		if (@event is InputEventMouseButton mbu && !mbu.Pressed && mbu.ButtonIndex == MouseButton.Left && _dragSelecting)
		{
			_dragSelecting = false;
			if (_isDragging) HandleDragSelect(_dragStart, mbu.Position);
			else HandleLeftClick(mbu.Position);
		}
	}

	private void HandleLeftClick(Vector2 screenPos)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;
		var entities = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		_selectedEntities.Clear();
		if (entities.Count > 0) _selectedEntities.Add(entities[0]);

		if (_selectedEntities.Count == 0)
		{
			var nearby = _sim.GetEntitiesAtPosition(worldPos.Value, 30f);
			foreach (var eid in nearby)
			{
				var id = _sim.Sim.QueryInterface<IdentityComponent>(eid);
				var node = _sim.EntityNodes.GetValueOrDefault(eid);
				GD.Print($"[Click] miss at {worldPos.Value:F1} | nearby: {id?.Name ?? "?"} at {node?.Position:F1} dist={node?.Position.DistanceTo(worldPos.Value):F1} isBuilding={id?.IsBuilding}");
			}
			if (nearby.Count == 0)
				GD.Print($"[Click] miss at {worldPos.Value:F1} | NO entities within 30f at all");
		}
	}

	private void HandleDragSelect(Vector2 start, Vector2 end)
	{
		var sw = ScreenToWorld(start); var ew = ScreenToWorld(end);
		if (sw == null || ew == null) return;
		var center = (sw.Value + ew.Value) / 2;
		var extents = new Vector3(Mathf.Abs(ew.Value.X - sw.Value.X) / 2, 50, Mathf.Abs(ew.Value.Z - sw.Value.Z) / 2);
		_selectedEntities.Clear();
		foreach (var eid in _sim.GetEntitiesInBounds(center, extents))
		{
			var identity = _sim.Sim.QueryInterface<IdentityComponent>(eid);
			if (identity != null && identity.IsUnit) _selectedEntities.Add(eid);
		}
	}

	private void HandleRightClick(Vector2 screenPos)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;

		var targets = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		EntityId? targetEntity = targets.Count > 0 ? targets[0] : null;

		// Rally point: building selected, right-click target
		if (_selectedEntities.Count == 1)
		{
			var only = _selectedEntities.First();
			var rally = _sim.Sim.QueryInterface<RallyPointComponent>(only);
			if (rally != null && targetEntity.HasValue)
			{
				var supply = _sim.Sim.QueryInterface<ResourceSupply>(targetEntity.Value);
				if (supply != null)
				{
					_sim.CommandSetRallyPoint(only, targetEntity);
					return;
				}
			}
		}

		if (_selectedEntities.Count == 0) return;

		bool isResource = false, isEnemy = false;
		foreach (var eid in targets)
		{
			targetEntity = eid;
			isResource = _sim.Sim.QueryInterface<ResourceSupply>(eid) != null;
			var owner = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
			isEnemy = _sim.Sim.QueryInterface<AttackComponent>(eid) != null &&
				owner != null && owner.PlayerId > 1;
			break;
		}

		foreach (var unit in _selectedEntities)
		{
			if (isEnemy && targetEntity.HasValue && _sim.Sim.QueryInterface<AttackComponent>(unit) != null)
			{
				_sim.CommandAttack(unit, targetEntity.Value);
			}
			else if (isResource && targetEntity.HasValue)
			{
				_sim.CommandGather(unit, targetEntity.Value);
			}
			else
			{
				_sim.MoveEntity(unit, worldPos.Value.X, worldPos.Value.Z);
			}
		}
	}

	private Vector3? ScreenToWorld(Vector2 screenPos)
	{
		var from = _camera.ProjectRayOrigin(screenPos);
		var dir = _camera.ProjectRayNormal(screenPos);
		if (dir.Y >= 0) return null;

		// Raymarch against heightmap: coarse steps, then bisect refine.
		float t = 0f;
		const float maxDist = 1000f;
		const float step = 2f;
		float prevT = 0f;
		while (t < maxDist)
		{
			var p = from + dir * t;
			if (p.Y <= TerrainHeightService.Sample(p.X, p.Z))
			{
				float lo = prevT, hi = t;
				for (int i = 0; i < 8; i++)
				{
					float mid = (lo + hi) * 0.5f;
					var m = from + dir * mid;
					if (m.Y <= TerrainHeightService.Sample(m.X, m.Z)) hi = mid;
					else lo = mid;
				}
				return from + dir * hi;
			}
			prevT = t;
			t += step;
		}
		return null;
	}

	public void EnterBuildMode(string template)
	{
		bool hasBuilder = false;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) { hasBuilder = true; break; }
		if (!hasBuilder) return;
		var player = _sim.GetPlayer();
		if (player == null) return;
		var (wood, stone, metal, food, _) = GetBuildCost(template);
		if (!CanAfford(player, wood, stone, metal, food))
		{
			GD.Print($"Cannot afford {template}: needs {wood}W {stone}S {metal}M {food}F");
			return;
		}
		_placeBuildingMode = true;
		_buildTemplate = template;
	}

	public void TrainVillager(bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandTrain(eid, "units/spart/support_civilian", batch: batch);
				break;
			}
	}

	public void TrainSoldier(bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandTrain(eid, "units/spart/infantry_spearman_b", batch: batch);
				break;
			}
	}

	public void TrainSkirmisher(bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandTrain(eid, "units/spart/infantry_javelineer_b", batch: batch);
				break;
			}
	}

	public void TrainUnit(string template, bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandTrain(eid, template, batch: batch);
				break;
			}
	}

	public void ResearchTech(string tech)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ResearcherComponent>(eid) != null)
			{
				_sim.CommandResearch(eid, tech);
				break;
			}
	}

	private void PlaceBuilding(Vector2 screenPos)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;
		var player = _sim.GetPlayer();
		if (player == null) { _placeBuildingMode = false; return; }
		var (wood, stone, metal, food, buildTime) = GetBuildCost(_buildTemplate);
		if (!CanAfford(player, wood, stone, metal, food))
		{
			GD.Print($"Cannot afford {_buildTemplate}: needs {wood}W {stone}S {metal}M {food}F");
			_placeBuildingMode = false;
			return;
		}

		// Placement validation is a presentation-only courtesy pre-filter (reject obviously
		// bad clicks without charging). The authoritative check — and resource charging and
		// foundation spawn — happens in the sim at the execution turn via SimCommandExecutor,
		// identically on every peer, so MP never desyncs on build.
		float halfSize = 3f;
		var stats = _sim.Templates?.ExtractStats(MapBuildTemplateName(_buildTemplate));
		if (stats != null)
		{
			float ob = Mathf.Max(stats.ObstructionSize0.ToFloat(), stats.ObstructionSize1.ToFloat());
			if (ob > 0) halfSize = ob * 0.5f;
		}
		var pr = _sim.Pathfinder.CheckBuildingPlacement(
			ZeroAD.Sim.Maths.Fixed.FromFloat(worldPos.Value.X),
			ZeroAD.Sim.Maths.Fixed.FromFloat(worldPos.Value.Z),
			ZeroAD.Sim.Maths.Fixed.FromFloat(halfSize),
			ZeroAD.Sim.Maths.Fixed.FromFloat(halfSize));
		if (pr != ZeroAD.Sim.Components.PlacementResult.Success)
		{
			GD.Print($"Cannot place {_buildTemplate} at ({worldPos.Value.X:F1},{worldPos.Value.Z:F1}): {pr}");
			// Stay in placement mode so the player can try another spot.
			return;
		}

		_ = buildTime; // build time comes from template data at execution; not needed here.
		string fullTemplate = MapBuildTemplateName(_buildTemplate);
		_placeBuildingMode = false;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null)
			{
				_sim.CommandBuild(eid, fullTemplate, worldPos.Value.X, worldPos.Value.Z);
				break;
			}
	}

	private (int wood, int stone, int metal, int food, float buildTime) GetBuildCost(string name)
	{
		TemplateStats? stats = null;
		try { stats = _sim.Templates?.ExtractStats(MapBuildTemplateName(name)); } catch { }
		if (stats != null && (stats.WoodCost > 0 || stats.StoneCost > 0 || stats.MetalCost > 0 || stats.FoodCost > 0))
			return (stats.WoodCost, stats.StoneCost, stats.MetalCost, stats.FoodCost,
				stats.BuildTime > 0f ? stats.BuildTime : 8.0f);
		var c = FallbackBuildCost(name);
		return (c.wood, c.stone, c.metal, c.food, 8.0f);
	}

	private static bool CanAfford(PlayerComponent player, int wood, int stone, int metal, int food) =>
		player.Wood >= wood && player.Stone >= stone && player.Metal >= metal && player.Food >= food;

	private static string MapBuildTemplateName(string name) => name switch
	{
		"House" => "structures/spart/house",
		"Storehouse" => "structures/spart/storehouse",
		"Farmstead" => "structures/spart/farmstead",
		"Field" => "structures/spart/field",
		"Barracks" => "structures/spart/barracks",
		"Outpost" => "structures/spart/outpost",
		"Tower" => "structures/spart/defense_tower",
		"Forge" => "structures/spart/forge",
		"Market" => "structures/spart/market",
		"Temple" => "structures/spart/temple",
		"Arsenal" => "structures/spart/arsenal",
		_ => $"structures/spart/{name.ToLowerInvariant()}"
	};

	private static (int wood, int stone, int metal, int food) FallbackBuildCost(string name) => name switch
	{
		"House" => (30, 0, 0, 0),
		"Storehouse" => (80, 0, 0, 0),
		"Farmstead" => (80, 0, 0, 0),
		"Field" => (60, 0, 0, 0),
		"Barracks" => (100, 0, 0, 0),
		"Outpost" => (80, 20, 0, 0),
		"Tower" => (100, 50, 0, 0),
		"Forge" => (120, 0, 30, 0),
		"Market" => (100, 0, 0, 0),
		"Temple" => (150, 50, 0, 0),
		"Arsenal" => (150, 0, 50, 0),
		_ => (50, 0, 0, 0)
	};
}

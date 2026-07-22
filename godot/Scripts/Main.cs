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
		BeginGameplay(42, 1, tutorial: true);
	}

	private void StartSinglePlayer(uint seed)
	{
		BeginGameplay(seed, 1);
	}

	private void StartGame(bool isHost, int param1, uint seed)
	{
		_sim.InitWorld();
		uint actualSeed = isHost ? seed : 42;

		if (isHost)
		{
			_mp.StartHost(param1, actualSeed);
			_mp.OnGameStart += () => BeginGameplay(actualSeed, 1);
		}
		else
		{
			_mp.StartClient("127.0.0.1", param1);
			_mp.OnGameStart += () => BeginGameplay(actualSeed, 2);
		}

		_lobby.SetStatus("Waiting for players...");

		if (isHost)
		{
			BeginGameplay(actualSeed, 1);
		}
	}

	private void StartGame(bool isHost, string addr, int port)
	{
		_sim.InitWorld();
		_mp.StartClient(addr, port);
		_mp.OnGameStart += () => BeginGameplay(42, 2);
		_lobby.SetStatus("Connecting...");
	}

	private void BeginGameplay(uint seed, uint playerId, bool tutorial = false)
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
			_sim.InitWorld(templatesPath);
			GD.Print("[Tutorial] InitWorld done");

			_mp.InitTurnManager(_sim.Sim, 2, playerId);
			GD.Print("[Tutorial] InitTurnManager done");

			if (_hud == null)
			{
				_hud = new HUD(_sim, this);
				AddChild(_hud);
			}

			if (_isTutorial)
			{
				_tutorialPanel = new TutorialPanel();
				AddChild(_tutorialPanel);
				_tutorialPanel.OnReadyPressed += () => _sim.Tutorial?.OnReadyPressed();
				_tutorialPanel.OnQuitPressed += QuitTutorial;
				_sim.Events.TutorialMessage += OnTutorialMessage;
			}

			if (_isTutorial)
			{
				GD.Print("[Tutorial] calling SetupTutorialWorld...");
				SetupTutorialWorld();
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
				AddChild(TerrainRenderer.CreateFromHeightmap(pmp));
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
				// The scenario's <Camera> position comes from the original 0 A.D. Atlas editor and
				// doesn't line up with this rewrite's coordinate space (e.g. tutorial stores
				// z=-55, off-map). Frame the player's (P1) town centre instead so the player can
				// actually see and click their base at start. Fall back to the scenario camera
				// only if no P1 civic centre is found.
				float camX = scenario.CameraX, camZ = scenario.CameraZ;
				foreach (var ent in scenario.Entities)
				{
					if (ent.Player != 1 || !ent.IsSimulationEntity) continue;
					if (ent.Template.Contains("civil_centre") || ent.Template.Contains("civic_centre"))
					{
						camX = ent.X; camZ = ent.Z;
						GD.Print($"[Tutorial] framing P1 civic centre at ({camX},{camZ})");
						break;
					}
				}
				float h = TerrainHeightService.Sample(camX, camZ);
				_camera.SetFocus(new Vector3(camX, h, camZ));
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

		if (_mp.NetTurn != null && _mp.IsConnected)
			_mp.TryAdvanceTurn();
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

			var ring = SelectionRing.Create(ringRadius, friendlyColor, enemyColor);
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
					_sim.CommandSetRallyPoint(only, targetEntity, "gather", supply.SpecificType);
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
				SubmitNetCmd(NetCommand.Attack(1, unit.Value, targetEntity.Value.Value));
			}
			else if (isResource && targetEntity.HasValue)
			{
				_sim.CommandGather(unit, targetEntity.Value);
				SubmitNetCmd(NetCommand.Gather(1, unit.Value, targetEntity.Value.Value));
			}
			else
			{
				_sim.MoveEntity(unit, worldPos.Value.X, worldPos.Value.Z);
				var fx = ZeroAD.Sim.Maths.Fixed.FromFloat(worldPos.Value.X);
				var fz = ZeroAD.Sim.Maths.Fixed.FromFloat(worldPos.Value.Z);
				SubmitNetCmd(NetCommand.Move(1, unit.Value, fx, fz));
			}
		}
	}

	private void SubmitNetCmd(NetCommand cmd)
	{
		if (_mp.NetTurn != null)
			_mp.SubmitCommand(cmd);
	}

	/// <summary>
	/// True when a multiplayer session is active and connected. Commands that mutate sim state
	/// (train/build/...) route through the net command queue instead of executing locally so
	/// both clients apply them at the same turn.
	/// </summary>
	private bool IsMultiplayer => _mp.NetTurn != null && _mp.IsConnected;

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
				const string template = "units/spart/support_civilian";
				if (IsMultiplayer)
				{
					// Lockstep: only enqueue via the net command so both clients run the exact
					// same EnqueueTraining at the same turn. Local prediction would double-charge.
					SubmitNetCmd(NetCommand.Train(1, eid.Value, template));
				}
				else
				{
					_sim.CommandTrain(eid, template, batch: batch);
				}
				break;
			}
	}

	public void TrainSoldier(bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				const string template = "units/spart/infantry_spearman_b";
				if (IsMultiplayer)
					SubmitNetCmd(NetCommand.Train(1, eid.Value, template));
				else
					_sim.CommandTrain(eid, template, batch: batch);
				break;
			}
	}

	public void TrainSkirmisher(bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				const string template = "units/spart/infantry_javelineer_b";
				if (IsMultiplayer)
					SubmitNetCmd(NetCommand.Train(1, eid.Value, template));
				else
					_sim.CommandTrain(eid, template, batch: batch);
				break;
			}
	}

	public void TrainUnit(string template, bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				if (IsMultiplayer)
					SubmitNetCmd(NetCommand.Train(1, eid.Value, template));
				else
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

		// Placement validation: terrain (land, in bounds) + obstruction (not on another building).
		// Done before charging resources so a bad click is free. Uses the building's footprint
		// half-size from the template; falls back to a generic 3m half-size if unknown.
		float halfSize = 3f;
		var stats = _sim.Templates?.ExtractStats(_buildTemplate);
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

		player.Wood -= wood;
		player.Stone -= stone;
		player.Metal -= metal;
		player.Food -= food;
		var foundation = _sim.SpawnFoundation(worldPos.Value.X, worldPos.Value.Z, _buildTemplate, buildTime);
		_placeBuildingMode = false;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null)
				_sim.CommandBuild(eid, foundation);
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

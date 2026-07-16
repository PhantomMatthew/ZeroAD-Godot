using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
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

		string? templatesPath = FindTemplatesPath();
		_sim.InitWorld(templatesPath);

		_mp.InitTurnManager(_sim.Sim, 2, playerId);

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
			SetupTutorialWorld();
		else
			SetupGameWorld(playerId);

		GD.Print(_isTutorial
			? "Introductory Tutorial started"
			: $"MS6 Game started: player={playerId}, seed={seed}");
		GD.Print("Controls: LMB=select  RMB=move/gather/attack  Shift+batch train  H=toggle tutorial");
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
		var candidates = new[]
		{
			"../../../binaries/data/mods/public/simulation/templates",
			"../../../../binaries/data/mods/public/simulation/templates",
			"../../binaries/data/mods/public/simulation/templates",
		};
		foreach (string dir in candidates)
		{
			var abs = ProjectSettings.GlobalizePath($"res://{dir}");
			if (System.IO.Directory.Exists(abs))
			{
				GD.Print($"Found templates at: {abs}");
				return abs;
			}
		}
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
				if (water != null)
				{
					var waterMesh = WaterRenderer.CreateWaterPlane(water.Value.height, water.Value.color, pmp.MapSizeMeters);
					AddChild(waterMesh);
					GD.Print($"Water: height={water.Value.height:F1}m color={water.Value.color}");
				}

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
		GD.Print("Using generated terrain (no PMP found)");
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
		SetupTerrain("maps/tutorials/introductory_tutorial.pmp");

		string? dataRoot = FindDataRoot();
		if (dataRoot != null)
		{
			var scenario = _sim.LoadTutorialScenario(dataRoot);
			if (scenario != null)
			{
				float h = TerrainHeightService.Sample(scenario.CameraX, scenario.CameraZ);
				_camera.SetFocus(new Vector3(scenario.CameraX, h, scenario.CameraZ));
			}
		}

		_sim.StartTutorial();
		_tutorialPanel.ShowTutorial();
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
	}

	public override void _Process(double delta)
	{
		if (!_gameStarted) return;

		if (_mp.NetTurn != null && _mp.IsConnected)
			_mp.TryAdvanceTurn();
	}

	public override void _Input(InputEvent @event)
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
		if (player != null && player.Wood >= 50) { _placeBuildingMode = true; _buildTemplate = template; }
	}

	public void TrainVillager(bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandTrain(eid, "units/spart/support_civilian", batch: batch);
				SubmitNetCmd(NetCommand.Train(1, eid.Value));
				break;
			}
	}

	public void TrainSoldier(bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandTrain(eid, "units/spart/infantry_spearman_b", batch: batch);
				SubmitNetCmd(NetCommand.TrainSoldier(1, eid.Value));
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
		if (player == null || player.Wood < 50) { _placeBuildingMode = false; return; }
		player.Wood -= 50;
		var foundation = _sim.SpawnFoundation(worldPos.Value.X, worldPos.Value.Z, _buildTemplate, 8.0f);
		_placeBuildingMode = false;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null)
				_sim.CommandBuild(eid, foundation);
	}
}

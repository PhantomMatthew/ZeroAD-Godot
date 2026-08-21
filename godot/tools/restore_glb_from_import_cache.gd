extends SceneTree
# 用法: godot --headless --script /tmp/restore_scn.gd -- <plan.json> [index-start index-count]
func _init():
	var args := OS.get_cmdline_user_args()
	var plan_path := args[0]
	var start := 0
	var count := 1000000
	if args.size() >= 3:
		start = int(args[1]); count = int(args[2])
	var f := FileAccess.open(plan_path, FileAccess.READ)
	var plan = JSON.parse_string(f.get_as_text())
	var ok := 0; var fail := 0
	for i in range(plan.size()):
		if i < start or i >= start + count: continue
		var e = plan[i]
		var scn = load(e["src"])
		if scn == null or not (scn is PackedScene):
			fail += 1
			print("LOADFAIL ", e["src"])
			continue
		var inst = scn.instantiate()
		var doc = GLTFDocument.new()
		var state = GLTFState.new()
		var err = doc.append_from_scene(inst, state)
		if err != OK:
			fail += 1; print("APPENDFAIL ", e["dst"], " ", err)
			inst.free(); continue
		DirAccess.make_dir_recursive_absolute(e["dst"].get_base_dir())
		err = doc.write_to_filesystem(state, e["dst"])
		if err != OK:
			fail += 1; print("WRITEFAIL ", e["dst"], " ", err)
		else:
			ok += 1
		inst.free()
	print("RESTORED ", ok, " FAIL ", fail)
	quit(0)

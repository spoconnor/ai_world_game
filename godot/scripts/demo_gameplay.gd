extends Node3D

const TILE := 2.0
const GRID_W := 18
const GRID_H := 14

const GRASS := Color(0.45, 0.66, 0.25)
const GRASS_LIGHT := Color(0.58, 0.76, 0.32)
const PATH := Color(0.67, 0.52, 0.31)
const CLIFF := Color(0.43, 0.43, 0.45)
const WATER := Color(0.05, 0.38, 0.62)
const WOOD := Color(0.52, 0.30, 0.13)
const DARK_WOOD := Color(0.31, 0.18, 0.09)
const STONE := Color(0.62, 0.63, 0.62)
const CROP := Color(0.27, 0.58, 0.24)

var _camera_rig: Node3D
var _camera: Camera3D
var _selected_unit: CharacterBody3D
var _selection_ring: MeshInstance3D
var _unit_label: Label
var _task_label: Label
var _resources_label: Label
var _wood := 32
var _stone := 18
var _food := 24

func _ready() -> void:
	_setup_input()
	_setup_world()
	_setup_camera()
	_setup_light()
	_setup_ui()
	_spawn_village()
	_spawn_units()
	_select_unit(get_node("Units/Builder"))

func _process(delta: float) -> void:
	_update_units(delta)
	_update_selection()

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		_handle_click(event.position)
	elif event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_WHEEL_UP:
		_camera_rig.position.y = max(_camera_rig.position.y - 1.2, 10.0)
	elif event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
		_camera_rig.position.y = min(_camera_rig.position.y + 1.2, 24.0)

func _setup_input() -> void:
	_add_action("pan_left", KEY_A)
	_add_action("pan_right", KEY_D)
	_add_action("pan_up", KEY_W)
	_add_action("pan_down", KEY_S)

func _add_action(action: String, keycode: Key) -> void:
	if InputMap.has_action(action):
		return
	InputMap.add_action(action)
	var event := InputEventKey.new()
	event.physical_keycode = keycode
	InputMap.action_add_event(action, event)

func _setup_world() -> void:
	var terrain := Node3D.new()
	terrain.name = "Terrain"
	add_child(terrain)

	for z in range(GRID_H):
		for x in range(GRID_W):
			var height := _height_at(x, z)
			var color := GRASS if (x + z) % 2 == 0 else GRASS_LIGHT
			if _is_path(x, z):
				color = PATH
			if _is_river(x):
				color = WATER
				height = -0.28
			var tile := _make_box(Vector3(TILE, 0.35, TILE), color)
			tile.name = "Tile_%02d_%02d" % [x, z]
			tile.position = _tile_pos(x, z, height)
			terrain.add_child(tile)

			if height > 0.15 and not _is_river(x):
				var cliff := _make_box(Vector3(TILE, 0.9 + height, TILE), CLIFF)
				cliff.position = Vector3(tile.position.x, -0.65, tile.position.z)
				terrain.add_child(cliff)

	_add_river_edges()
	_add_paths()

func _height_at(x: int, z: int) -> float:
	if x <= 2 or x >= GRID_W - 3 or z <= 1 or z >= GRID_H - 2:
		return 0.55
	if x >= 12 and z <= 3:
		return 0.35
	if x <= 5 and z >= 9:
		return 0.25
	return 0.0

func _is_river(x: int) -> bool:
	return x == 6 or x == 7

func _is_path(x: int, z: int) -> bool:
	return (z == 7 and x >= 2 and x <= 15) or (x == 10 and z >= 3 and z <= 9) or (x == 4 and z >= 7 and z <= 11)

func _tile_pos(x: int, z: int, height: float) -> Vector3:
	return Vector3((x - GRID_W * 0.5) * TILE, height, (z - GRID_H * 0.5) * TILE)

func _add_river_edges() -> void:
	for z in range(GRID_H):
		for x in [5, 8]:
			var rock := _make_rock(0.45)
			rock.position = _tile_pos(x, z, 0.25) + Vector3(randf_range(-0.5, 0.5), 0.25, randf_range(-0.6, 0.6))
			add_child(rock)

func _add_paths() -> void:
	for z in range(6, 9):
		var bridge := _make_box(Vector3(4.8, 0.28, 1.45), WOOD)
		bridge.name = "Bridge"
		bridge.position = Vector3(-3.0, 0.52, (z - GRID_H * 0.5) * TILE)
		add_child(bridge)
		for side in [-1, 1]:
			var rail := _make_box(Vector3(4.9, 0.28, 0.18), DARK_WOOD)
			rail.position = bridge.position + Vector3(0, 0.35, side * 0.82)
			add_child(rail)

func _setup_camera() -> void:
	_camera_rig = Node3D.new()
	_camera_rig.name = "CameraRig"
	_camera_rig.position = Vector3(0, 16, 4)
	add_child(_camera_rig)

	_camera = Camera3D.new()
	_camera.name = "Camera3D"
	_camera.current = true
	_camera.fov = 42.0
	_camera.position = Vector3(0, 0, 18)
	_camera.rotation_degrees = Vector3(-58, 0, 0)
	_camera_rig.add_child(_camera)

func _setup_light() -> void:
	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.rotation_degrees = Vector3(-52, -38, 0)
	sun.light_energy = 2.4
	sun.shadow_enabled = true
	add_child(sun)

	var world := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.56, 0.72, 0.90)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.58, 0.64, 0.70)
	env.ambient_light_energy = 0.9
	world.environment = env
	add_child(world)

func _setup_ui() -> void:
	var layer := CanvasLayer.new()
	layer.name = "HUD"
	add_child(layer)

	var panel := PanelContainer.new()
	panel.position = Vector2(18, 18)
	panel.custom_minimum_size = Vector2(360, 116)
	layer.add_child(panel)

	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 6)
	panel.add_child(box)

	_resources_label = Label.new()
	box.add_child(_resources_label)
	_unit_label = Label.new()
	box.add_child(_unit_label)
	_task_label = Label.new()
	box.add_child(_task_label)

	var help := Label.new()
	help.text = "WASD pan | wheel zoom | click units or ground"
	help.modulate = Color(0.86, 0.90, 0.86)
	box.add_child(help)
	_refresh_ui("Ready")

func _spawn_village() -> void:
	var buildings := Node3D.new()
	buildings.name = "Buildings"
	add_child(buildings)

	_make_cabin(Vector3(-1.0, 1.0, -7.0), buildings)
	_make_workbench(Vector3(1.8, 0.8, -2.6), buildings)
	_make_mine(Vector3(6.5, 1.0, -7.5), buildings)
	_make_farm(Vector3(8.5, 0.85, -2.0), buildings)
	_make_logging_area(Vector3(-9.0, 0.9, 7.5), buildings)

	for pos in [Vector3(-8, 1.0, -8), Vector3(-6, 0.8, -5), Vector3(11, 1.2, -8), Vector3(12, 0.9, 4), Vector3(-11, 1.1, 0), Vector3(5, 0.8, 6)]:
		_make_tree(pos, buildings)

func _make_cabin(pos: Vector3, parent: Node) -> void:
	var root := Node3D.new()
	root.name = "Cabin"
	root.position = pos
	parent.add_child(root)
	root.add_child(_make_box(Vector3(2.8, 2.0, 2.3), WOOD, Vector3(0, 1.0, 0)))
	root.add_child(_make_box(Vector3(3.2, 0.35, 2.7), Color(0.08, 0.15, 0.26), Vector3(0, 2.25, 0), Vector3(0, 0, 18)))
	root.add_child(_make_box(Vector3(0.65, 1.1, 0.08), DARK_WOOD, Vector3(0, 0.65, 1.18)))
	root.add_child(_make_box(Vector3(0.45, 0.75, 0.45), STONE, Vector3(-0.95, 2.7, -0.55)))

func _make_workbench(pos: Vector3, parent: Node) -> void:
	var table := _make_box(Vector3(2.2, 0.25, 1.1), WOOD)
	table.position = pos
	parent.add_child(table)
	for x in [-0.8, 0.8]:
		for z in [-0.35, 0.35]:
			parent.add_child(_make_box(Vector3(0.16, 0.8, 0.16), DARK_WOOD, pos + Vector3(x, -0.42, z)))

func _make_mine(pos: Vector3, parent: Node) -> void:
	for offset in [Vector3(-0.9, 0, 0), Vector3(0, 0.2, -0.3), Vector3(0.8, 0, 0.2), Vector3(1.3, -0.1, -0.6)]:
		var rock := _make_rock(0.9)
		rock.position = pos + offset
		parent.add_child(rock)
	var frame := _make_box(Vector3(2.1, 0.25, 0.3), WOOD, pos + Vector3(0.1, 1.6, 0.15))
	parent.add_child(frame)
	for x in [-0.8, 1.0]:
		parent.add_child(_make_box(Vector3(0.22, 1.8, 0.22), WOOD, pos + Vector3(x, 0.75, 0.15)))

func _make_farm(pos: Vector3, parent: Node) -> void:
	for row in range(4):
		var crop := _make_box(Vector3(3.8, 0.12, 0.35), CROP)
		crop.position = pos + Vector3(0, 0.05, row * 0.62)
		parent.add_child(crop)
	for x in [-2.2, 2.2]:
		var fence := _make_box(Vector3(0.16, 0.55, 3.0), WOOD)
		fence.position = pos + Vector3(x, 0.35, 0.9)
		parent.add_child(fence)

func _make_logging_area(pos: Vector3, parent: Node) -> void:
	for i in range(4):
		var log_mesh := _make_cylinder(0.28, 1.6, WOOD)
		log_mesh.position = pos + Vector3(i * 0.45, 0.25, 0)
		log_mesh.rotation_degrees.z = 90
		parent.add_child(log_mesh)

func _make_tree(pos: Vector3, parent: Node) -> void:
	var trunk := _make_cylinder(0.18, 1.3, DARK_WOOD)
	trunk.position = pos + Vector3(0, 0.65, 0)
	parent.add_child(trunk)
	for i in range(3):
		var cone := MeshInstance3D.new()
		var mesh := CylinderMesh.new()
		mesh.bottom_radius = 1.05 - i * 0.16
		mesh.top_radius = 0.0
		mesh.height = 1.6
		cone.mesh = mesh
		cone.material_override = _mat(Color(0.18 + i * 0.04, 0.38 + i * 0.05, 0.16))
		cone.position = pos + Vector3(0, 1.6 + i * 0.65, 0)
		parent.add_child(cone)

func _spawn_units() -> void:
	var units := Node3D.new()
	units.name = "Units"
	add_child(units)
	_make_unit("Builder", Vector3(-1.8, 1.0, 1.5), Color(0.20, 0.33, 0.64), "Repair bridge", units)
	_make_unit("Farmer", Vector3(8.0, 1.0, -1.0), Color(0.74, 0.58, 0.16), "Harvest crops", units)
	_make_unit("Guard", Vector3(7.5, 0.95, 2.8), Color(0.62, 0.16, 0.11), "Watch road", units)
	_make_unit("Scout", Vector3(-10.2, 1.3, -1.2), Color(0.25, 0.48, 0.19), "Survey ridge", units)
	_make_unit("Herbalist", Vector3(8.4, 1.2, 7.0), Color(0.28, 0.54, 0.32), "Gather herbs", units)

func _make_unit(unit_name: String, pos: Vector3, color: Color, task: String, parent: Node) -> void:
	var body := CharacterBody3D.new()
	body.name = unit_name
	body.position = pos
	body.set_meta("task", task)
	body.set_meta("target", pos)
	body.set_meta("speed", 2.2)
	parent.add_child(body)

	body.add_child(_make_cylinder(0.28, 0.85, color, Vector3(0, 0.55, 0)))
	body.add_child(_make_sphere(0.23, Color(0.78, 0.58, 0.38), Vector3(0, 1.12, 0)))
	body.add_child(_make_cylinder(0.18, 0.12, Color(0.09, 0.14, 0.20), Vector3(0, 1.36, 0)))

	var shape := CollisionShape3D.new()
	var capsule := CapsuleShape3D.new()
	capsule.radius = 0.38
	capsule.height = 1.4
	shape.shape = capsule
	shape.position = Vector3(0, 0.7, 0)
	body.add_child(shape)

func _update_units(delta: float) -> void:
	var move := Vector3.ZERO
	move.x = Input.get_axis("pan_left", "pan_right")
	move.z = Input.get_axis("pan_up", "pan_down")
	if move.length() > 0.0:
		_camera_rig.position += move.normalized() * delta * 10.0

	for unit in get_node("Units").get_children():
		var target: Vector3 = unit.get_meta("target")
		var delta_pos: Vector3 = target - unit.position
		delta_pos.y = 0.0
		if delta_pos.length() > 0.1:
			unit.velocity = delta_pos.normalized() * float(unit.get_meta("speed"))
			unit.move_and_slide()
			unit.look_at(unit.position + delta_pos, Vector3.UP)
		else:
			unit.velocity = Vector3.ZERO

func _handle_click(screen_pos: Vector2) -> void:
	var result: Dictionary = _raycast(screen_pos, 200.0)
	if result.is_empty():
		return
	var collider = result["collider"]
	if collider is CharacterBody3D:
		_select_unit(collider)
		return
	if _selected_unit != null:
		var hit_position: Vector3 = result["position"]
		_selected_unit.set_meta("target", hit_position)
		_refresh_ui("Moving to %.1f, %.1f" % [hit_position.x, hit_position.z])

func _raycast(screen_pos: Vector2, length: float) -> Dictionary:
	var origin: Vector3 = _camera.project_ray_origin(screen_pos)
	var end: Vector3 = origin + _camera.project_ray_normal(screen_pos) * length
	var query: PhysicsRayQueryParameters3D = PhysicsRayQueryParameters3D.create(origin, end)
	return get_world_3d().direct_space_state.intersect_ray(query)

func _select_unit(unit: CharacterBody3D) -> void:
	_selected_unit = unit
	_refresh_ui(String(unit.name) + " selected")

func _update_selection() -> void:
	if _selection_ring == null:
		_selection_ring = MeshInstance3D.new()
		var mesh := TorusMesh.new()
		mesh.inner_radius = 0.48
		mesh.outer_radius = 0.55
		_selection_ring.mesh = mesh
		_selection_ring.material_override = _mat(Color(1.0, 0.86, 0.28))
		add_child(_selection_ring)
	if _selected_unit != null:
		_selection_ring.visible = true
		_selection_ring.position = _selected_unit.position + Vector3(0, 0.06, 0)
	else:
		_selection_ring.visible = false

func _refresh_ui(status: String) -> void:
	if _resources_label == null:
		return
	_resources_label.text = "Wood %d   Stone %d   Food %d" % [_wood, _stone, _food]
	if _selected_unit == null:
		_unit_label.text = "No unit selected"
		_task_label.text = status
	else:
		_unit_label.text = "%s | %s" % [_selected_unit.name, _selected_unit.get_meta("task")]
		_task_label.text = status

func _make_box(size: Vector3, color: Color, pos := Vector3.ZERO, rot := Vector3.ZERO) -> MeshInstance3D:
	var mesh_instance := MeshInstance3D.new()
	var mesh := BoxMesh.new()
	mesh.size = size
	mesh_instance.mesh = mesh
	mesh_instance.material_override = _mat(color)
	mesh_instance.position = pos
	mesh_instance.rotation_degrees = rot
	_add_static_collision(mesh_instance, BoxShape3D.new(), size)
	return mesh_instance

func _make_sphere(radius: float, color: Color, pos := Vector3.ZERO) -> MeshInstance3D:
	var mesh_instance := MeshInstance3D.new()
	var mesh := SphereMesh.new()
	mesh.radius = radius
	mesh.height = radius * 2.0
	mesh_instance.mesh = mesh
	mesh_instance.material_override = _mat(color)
	mesh_instance.position = pos
	return mesh_instance

func _make_cylinder(radius: float, height: float, color: Color, pos := Vector3.ZERO) -> MeshInstance3D:
	var mesh_instance := MeshInstance3D.new()
	var mesh := CylinderMesh.new()
	mesh.top_radius = radius
	mesh.bottom_radius = radius
	mesh.height = height
	mesh_instance.mesh = mesh
	mesh_instance.material_override = _mat(color)
	mesh_instance.position = pos
	return mesh_instance

func _make_rock(radius: float) -> MeshInstance3D:
	var rock := _make_sphere(radius, STONE)
	rock.scale.y = randf_range(0.55, 0.9)
	rock.rotation_degrees = Vector3(randf_range(-8, 8), randf_range(0, 180), randf_range(-8, 8))
	return rock

func _add_static_collision(mesh_instance: MeshInstance3D, shape: Shape3D, size: Vector3) -> void:
	var body := StaticBody3D.new()
	var collision := CollisionShape3D.new()
	if shape is BoxShape3D:
		shape.size = size
	collision.shape = shape
	body.add_child(collision)
	mesh_instance.add_child(body)

func _mat(color: Color) -> StandardMaterial3D:
	var material := StandardMaterial3D.new()
	material.albedo_color = color
	material.roughness = 0.82
	return material

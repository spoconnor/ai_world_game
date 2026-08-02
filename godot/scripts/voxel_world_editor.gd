extends Node3D

const GRID_X := 48
const GRID_Z := 48
const GRID_Y := 136
const DEFAULT_VOXEL_HEIGHT := 1.0
const HEIGHT_EXAGGERATION := 0.6
const WATER_LEVEL := 9
const ARROW_SCROLL_STEP := 1
const RENDER_MARGIN := 8
const MAX_RENDER_STRIDE := 32
const SCROLL_ANIMATION_SECONDS := 0.12
const SCROLL_MARGIN := 14.0
const SCROLL_REBUILD_STEP := 8
const PROFILE_REBUILDS := false
const TERRAIN_DETAIL_RADIUS_VOXELS := 5
const CAMERA_TERRAIN_CLEARANCE_LAYERS := 12
const VOXEL_GUIDE_MIN_HEIGHT_METERS := -40.0
const VOXEL_GUIDE_MAX_HEIGHT_METERS := 1600.0
const VOXEL_GUIDE_MIN_LAYER := 4
const VOXEL_GUIDE_MAX_LAYER := 128
const EDITS_PATH := "../world_data/prototype/voxel_edits.json"
const MANIFEST_PATH := "../world_data/prototype/world.json"

const MATERIALS := {
	"air": {"label": "Air", "color": Color(0, 0, 0, 0)},
	"grass": {"label": "Grass", "color": Color(0.34, 0.58, 0.22)},
	"forest_floor": {"label": "Forest", "color": Color(0.18, 0.42, 0.20)},
	"sand": {"label": "Sand", "color": Color(0.70, 0.63, 0.42)},
	"snow": {"label": "Snow", "color": Color(0.82, 0.84, 0.80)},
	"soil": {"label": "Soil", "color": Color(0.36, 0.25, 0.16)},
	"stone": {"label": "Stone", "color": Color(0.48, 0.49, 0.48)},
	"dark_stone": {"label": "Dark Stone", "color": Color(0.28, 0.29, 0.30)},
	"water": {"label": "Water", "color": Color(0.06, 0.32, 0.56, 0.72)},
	"road": {"label": "Road", "color": Color(0.50, 0.39, 0.24)},
	"bridge": {"label": "Bridge", "color": Color(0.48, 0.27, 0.11)},
	"stone_wall": {"label": "Stone Wall", "color": Color(0.56, 0.56, 0.53)},
	"wood_wall": {"label": "Wood Wall", "color": Color(0.42, 0.23, 0.10)},
	"roof": {"label": "Wood Roof", "color": Color(0.28, 0.12, 0.06)}
}

const PALETTE := [
	"grass",
	"forest_floor",
	"sand",
	"snow",
	"soil",
	"stone",
	"dark_stone",
	"water",
	"road",
	"bridge",
	"stone_wall",
	"wood_wall",
	"roof"
]

const NEIGHBOR_OFFSETS: Array[Vector3i] = [
	Vector3i(1, 0, 0),
	Vector3i(-1, 0, 0),
	Vector3i(0, 1, 0),
	Vector3i(0, -1, 0),
	Vector3i(0, 0, 1),
	Vector3i(0, 0, -1)
]

var _voxel_root: Node3D
var _selection: MeshInstance3D
var _camera_rig: Node3D
var _camera: Camera3D
var _palette_button: OptionButton
var _mode_label: Label
var _status_label: Label
var _selected_material := "stone"
var _remove_mode := false
var _hover_coord := Vector3i(-1, -1, -1)
var _hover_normal := Vector3i(0, 1, 0)
var _edits := {}
var _manifest_directory := ""
var _chunk_metadata := {}
var _chunk_cache := {}
var _chunk_samples := 0
var _chunks_x := 0
var _chunks_z := 0
var _world_voxels_x := GRID_X
var _world_voxels_z := GRID_Z
var _view_origin_x := 0
var _view_origin_z := 0
var _render_origin_x := 0
var _render_origin_z := 0
var _render_stride := 1
var _cell_size := 1.0
var _voxel_height := DEFAULT_VOXEL_HEIGHT
var _terrain_min_height := -250.0
var _terrain_max_height := 3600.0
var _yaw := deg_to_rad(42.0)
var _pitch := deg_to_rad(-52.0)
var _distance := 68.0
var _target := Vector3.ZERO
var _left_down := false
var _right_down := false
var _left_drag_pixels := 0.0
var _scroll_tween: Tween
var _materials := {}
var _cube_mesh := BoxMesh.new()
var _shape := BoxShape3D.new()
var _profile_material_calls := 0
var _profile_surface_height_calls := 0
var _profile_biome_calls := 0
var _profile_chunk_load_calls := 0
var _profile_chunk_cache_hits := 0
var _profile_chunk_cache_misses := 0
var _profile_chunk_bytes_read := 0
var _profile_chunk_read_usec := 0

func _ready() -> void:
	_voxel_root = Node3D.new()
	_voxel_root.name = "Voxels"
	add_child(_voxel_root)

	_load_source_terrain()
	_configure_voxel_geometry()
	_load_edits()
	_setup_camera()
	_setup_light()
	_setup_selection()
	_setup_ui()
	_rebuild_voxels()

func _process(_delta: float) -> void:
	_update_hover()

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		_handle_mouse_button(event)
	elif event is InputEventMouseMotion:
		_handle_mouse_motion(event)
	elif event is InputEventKey and event.pressed and not event.echo:
		_handle_key(event)

func _setup_camera() -> void:
	_camera_rig = Node3D.new()
	_camera_rig.name = "CameraRig"
	add_child(_camera_rig)
	_camera = Camera3D.new()
	_camera.name = "Camera3D"
	_camera.current = true
	_camera.fov = 42.0
	_configure_camera_clip()
	_camera_rig.add_child(_camera)
	_target = _view_center_target()
	_update_camera()

func _setup_light() -> void:
	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.rotation_degrees = Vector3(-54, -36, 0)
	sun.light_energy = 2.6
	sun.shadow_enabled = true
	add_child(sun)

	var env_node := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.55, 0.70, 0.88)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.62, 0.65, 0.68)
	env.ambient_light_energy = 0.9
	env_node.environment = env
	add_child(env_node)

func _setup_selection() -> void:
	_selection = MeshInstance3D.new()
	_selection.name = "Selection"
	var mesh := BoxMesh.new()
	mesh.size = Vector3(_cell_size * 1.06, _voxel_height * 1.06, _cell_size * 1.06)
	_selection.mesh = mesh
	var material := StandardMaterial3D.new()
	material.albedo_color = Color(1.0, 0.86, 0.22, 0.22)
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	_selection.material_override = material
	_selection.visible = false
	add_child(_selection)

func _setup_ui() -> void:
	var layer := CanvasLayer.new()
	layer.name = "HUD"
	add_child(layer)

	var panel := PanelContainer.new()
	panel.position = Vector2(16, 16)
	panel.custom_minimum_size = Vector2(370, 172)
	layer.add_child(panel)

	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 8)
	panel.add_child(box)

	var title := Label.new()
	title.text = "Voxel World Editor"
	box.add_child(title)

	_palette_button = OptionButton.new()
	for material_id in PALETTE:
		_palette_button.add_item(String(MATERIALS[material_id]["label"]))
		_palette_button.set_item_metadata(_palette_button.get_item_count() - 1, material_id)
	_palette_button.select(PALETTE.find(_selected_material))
	_palette_button.item_selected.connect(_on_palette_selected)
	box.add_child(_palette_button)

	_mode_label = Label.new()
	box.add_child(_mode_label)
	_status_label = Label.new()
	box.add_child(_status_label)
	_refresh_ui("Loaded %d persisted edits" % _edits.size())

func _handle_mouse_button(event: InputEventMouseButton) -> void:
	match event.button_index:
		MOUSE_BUTTON_LEFT:
			if event.pressed:
				_left_down = true
				_left_drag_pixels = 0.0
			else:
				if _left_drag_pixels < 4.0 and _hover_coord.x >= 0:
					_apply_edit()
				_left_down = false
		MOUSE_BUTTON_RIGHT:
			_right_down = event.pressed
		MOUSE_BUTTON_WHEEL_UP:
			if event.pressed:
				_distance = max(_min_camera_distance(), _distance * 0.88)
				_update_lod_for_distance()
				_update_camera()
		MOUSE_BUTTON_WHEEL_DOWN:
			if event.pressed:
				_distance = min(_max_camera_distance(), _distance * 1.12)
				_update_lod_for_distance()
				_update_camera()

func _handle_mouse_motion(event: InputEventMouseMotion) -> void:
	if _left_down and not _right_down:
		_left_drag_pixels += event.relative.length()
		_yaw -= event.relative.x * 0.006
		_pitch = clamp(_pitch - event.relative.y * 0.006, deg_to_rad(-82), deg_to_rad(-18))
		_update_camera()
	elif _right_down:
		var camera_basis := _camera.global_transform.basis
		var right := camera_basis.x
		var forward := -camera_basis.z
		forward.y = 0.0
		forward = forward.normalized()
		_target += (-right * event.relative.x + forward * event.relative.y) * max(_distance * 0.0014, _cell_size * 0.08)
		_scroll_window_for_target()
		_update_camera()

func _handle_key(event: InputEventKey) -> void:
	match event.physical_keycode:
		KEY_LEFT:
			_move_view(-ARROW_SCROLL_STEP, 0)
		KEY_RIGHT:
			_move_view(ARROW_SCROLL_STEP, 0)
		KEY_UP:
			_move_view(0, -ARROW_SCROLL_STEP)
		KEY_DOWN:
			_move_view(0, ARROW_SCROLL_STEP)
		KEY_HOME:
			_center_view()
			_reset_scroll_animation()
			_target = _view_center_target()
			_position_target_above_terrain()
			_update_camera()
			_rebuild_voxels()
			_refresh_ui("Centered world view")
		KEY_X:
			_remove_mode = not _remove_mode
			_refresh_ui("Mode changed")
		KEY_S:
			_save_edits()
			_refresh_ui("Saved %d edits" % _edits.size())
		KEY_R:
			_edits.clear()
			_save_edits()
			_reset_scroll_animation()
			_rebuild_voxels()
			_refresh_ui("Cleared persisted edits")
		KEY_BRACKETLEFT:
			_step_palette(-1)
		KEY_BRACKETRIGHT:
			_step_palette(1)

func _on_palette_selected(index: int) -> void:
	_selected_material = String(_palette_button.get_item_metadata(index))
	_remove_mode = false
	_refresh_ui("Selected %s" % MATERIALS[_selected_material]["label"])

func _step_palette(direction: int) -> void:
	var index := PALETTE.find(_selected_material)
	index = wrapi(index + direction, 0, PALETTE.size())
	_palette_button.select(index)
	_on_palette_selected(index)

func _apply_edit() -> void:
	var coord := _hover_coord
	var material_id := "air"
	if not _remove_mode:
		coord += _hover_normal
		if not _in_bounds(coord):
			return
		material_id = _selected_material

	_edits[_key(coord)] = material_id
	_save_edits()
	_reset_scroll_animation()
	_rebuild_voxels()
	_refresh_ui("%s at %s" % ["Removed" if material_id == "air" else "Placed " + material_id, _key(coord)])

func _update_hover() -> void:
	if _camera == null:
		return
	var mouse := get_viewport().get_mouse_position()
	var origin := _camera.project_ray_origin(mouse)
	var end: Vector3 = origin + _camera.project_ray_normal(mouse) * max(500.0, _camera.far)
	var query := PhysicsRayQueryParameters3D.create(origin, end)
	var result := get_world_3d().direct_space_state.intersect_ray(query)
	if result.is_empty() or not (result.get("collider") is StaticBody3D):
		_hover_coord = Vector3i(-1, -1, -1)
		_selection.visible = false
		return

	var body: StaticBody3D = result["collider"]
	if not body.has_meta("coord"):
		_hover_coord = Vector3i(-1, -1, -1)
		_selection.visible = false
		return

	_hover_coord = body.get_meta("coord")
	var normal: Vector3 = result.get("normal", Vector3.UP)
	_hover_normal = Vector3i(roundi(normal.x), roundi(normal.y), roundi(normal.z))
	if _hover_normal == Vector3i.ZERO:
		_hover_normal = Vector3i(0, 1, 0)
	_selection.visible = true
	_selection.position = _coord_to_world(_hover_coord) + _voxel_root.position

func _rebuild_voxels() -> void:
	var total_start := Time.get_ticks_usec()
	var old_children := _voxel_root.get_child_count()
	var delete_start := Time.get_ticks_usec()
	for child in _voxel_root.get_children():
		child.free()
	var delete_usec := Time.get_ticks_usec() - delete_start

	_reset_rebuild_profile()
	var preload_start := Time.get_ticks_usec()
	_preload_window_chunks()
	var preload_usec := Time.get_ticks_usec() - preload_start

	var cells_checked := 0
	var solid_cells := 0
	var exposed_cells := 0
	var material_cache_usec := 0
	var scan_usec := 0
	var exposure_usec := 0
	var add_usec := 0
	var cache_start := Time.get_ticks_usec()
	var cache_size_x := GRID_X + RENDER_MARGIN * 2 + 2
	var cache_size_y := GRID_Y + 2
	var cache_size_z := GRID_Z + RENDER_MARGIN * 2 + 2
	var cache_stride_y := cache_size_x
	var cache_stride_z := cache_size_x * cache_size_y
	var column_count := cache_size_x * cache_size_z
	var surface_cache := PackedInt32Array()
	surface_cache.resize(column_count)
	var biome_cache: Array[String] = []
	biome_cache.resize(column_count)
	for cache_z in range(cache_size_z):
		for cache_x in range(cache_size_x):
			var local_x := (cache_x - RENDER_MARGIN - 1) * _render_stride
			var local_z := (cache_z - RENDER_MARGIN - 1) * _render_stride
			var global_x := _render_origin_x + local_x
			var global_z := _render_origin_z + local_z
			var column_index := cache_x + cache_z * cache_size_x
			if global_x < 0 or global_x >= _world_voxels_x or global_z < 0 or global_z >= _world_voxels_z:
				surface_cache[column_index] = -1
				biome_cache[column_index] = "grassland"
			else:
				surface_cache[column_index] = _surface_height_for_lod(global_x, global_z)
				biome_cache[column_index] = _biome_at(global_x, global_z)
	var center_x: int = floori(float(_world_voxels_x) * 0.5)
	var center_z: int = floori(float(_world_voxels_z) * 0.5)
	var house_floor_y := _surface_height(center_x + 10, center_z + 11) + 1
	var material_cache: Array[String] = []
	material_cache.resize(cache_size_x * cache_size_y * cache_size_z)
	for cache_z in range(cache_size_z):
		for cache_x in range(cache_size_x):
			var column_index := cache_x + cache_z * cache_size_x
			var surface_y := surface_cache[column_index]
			var biome := biome_cache[column_index]
			for cache_y in range(cache_size_y):
				var local_x := (cache_x - RENDER_MARGIN - 1) * _render_stride
				var local_y := cache_y - 1
				var local_z := (cache_z - RENDER_MARGIN - 1) * _render_stride
				var coord := Vector3i(_render_origin_x + local_x, local_y, _render_origin_z + local_z)
				var cache_index := cache_x + cache_y * cache_stride_y + cache_z * cache_stride_z
				material_cache[cache_index] = "air" if not _in_bounds(coord) else _material_at_cached_column(coord, surface_y, biome, house_floor_y)
	material_cache_usec = Time.get_ticks_usec() - cache_start

	var scan_start := Time.get_ticks_usec()
	for z in range(-RENDER_MARGIN, GRID_Z + RENDER_MARGIN):
		for x in range(-RENDER_MARGIN, GRID_X + RENDER_MARGIN):
			for y in range(GRID_Y):
				cells_checked += 1
				var coord := Vector3i(_render_origin_x + x * _render_stride, y, _render_origin_z + z * _render_stride)
				var cache_x := x + RENDER_MARGIN + 1
				var cache_y := y + 1
				var cache_z := z + RENDER_MARGIN + 1
				var cache_index := cache_x + cache_y * cache_stride_y + cache_z * cache_stride_z
				var material_id := material_cache[cache_index]
				if material_id == "air":
					continue
				solid_cells += 1
				var exposure_start := Time.get_ticks_usec() if PROFILE_REBUILDS else 0
				var exposed := (
					material_cache[cache_index + 1] == "air"
					or material_cache[cache_index + 1] == "water"
					or material_cache[cache_index - 1] == "air"
					or material_cache[cache_index - 1] == "water"
					or material_cache[cache_index + cache_stride_y] == "air"
					or material_cache[cache_index + cache_stride_y] == "water"
					or material_cache[cache_index - cache_stride_y] == "air"
					or material_cache[cache_index - cache_stride_y] == "water"
					or material_cache[cache_index + cache_stride_z] == "air"
					or material_cache[cache_index + cache_stride_z] == "water"
					or material_cache[cache_index - cache_stride_z] == "air"
					or material_cache[cache_index - cache_stride_z] == "water"
				)
				if PROFILE_REBUILDS:
					exposure_usec += Time.get_ticks_usec() - exposure_start
				if not exposed:
					continue
				exposed_cells += 1
				var add_start := Time.get_ticks_usec() if PROFILE_REBUILDS else 0
				_add_voxel_body(coord, material_id)
				if PROFILE_REBUILDS:
					add_usec += Time.get_ticks_usec() - add_start
	scan_usec = Time.get_ticks_usec() - scan_start

	if PROFILE_REBUILDS:
		var total_usec := Time.get_ticks_usec() - total_start
		print(
			"Voxel rebuild: total=%.2fms delete=%.2fms preload=%.2fms material_cache=%.2fms scan=%.2fms exposure=%.2fms add_nodes=%.2fms old_nodes=%d cells=%d solid=%d exposed=%d material_calls=%d surface_height_calls=%d biome_calls=%d chunk_loads=%d cache_hits=%d cache_misses=%d chunk_read=%.2fms bytes=%d" % [
				float(total_usec) / 1000.0,
				float(delete_usec) / 1000.0,
				float(preload_usec) / 1000.0,
				float(material_cache_usec) / 1000.0,
				float(scan_usec) / 1000.0,
				float(exposure_usec) / 1000.0,
				float(add_usec) / 1000.0,
				old_children,
				cells_checked,
				solid_cells,
				exposed_cells,
				_profile_material_calls,
				_profile_surface_height_calls,
				_profile_biome_calls,
				_profile_chunk_load_calls,
				_profile_chunk_cache_hits,
				_profile_chunk_cache_misses,
				float(_profile_chunk_read_usec) / 1000.0,
				_profile_chunk_bytes_read
			]
		)

func _add_voxel_body(coord: Vector3i, material_id: String) -> void:
	var body := StaticBody3D.new()
	body.name = "Voxel_%02d_%02d_%02d" % [coord.x, coord.y, coord.z]
	body.set_meta("coord", coord)
	body.position = _coord_to_world(coord)
	_voxel_root.add_child(body)

	var mesh_instance := MeshInstance3D.new()
	mesh_instance.mesh = _cube_mesh
	mesh_instance.material_override = _material(material_id)
	body.add_child(mesh_instance)

	var collision := CollisionShape3D.new()
	collision.shape = _shape
	body.add_child(collision)

func _material(material_id: String) -> StandardMaterial3D:
	if _materials.has(material_id):
		return _materials[material_id]
	var material := StandardMaterial3D.new()
	material.albedo_color = MATERIALS.get(material_id, MATERIALS["stone"])["color"]
	material.roughness = 0.86
	if material_id == "water":
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		material.roughness = 0.35
	_materials[material_id] = material
	return material

func _material_at(coord: Vector3i) -> String:
	if PROFILE_REBUILDS:
		_profile_material_calls += 1
	var key := _key(coord)
	if _edits.has(key):
		return String(_edits[key])
	var authored := _authored_material_at(coord)
	if authored != "air":
		return authored
	return _base_material_at(coord)

func _material_at_cached_column(coord: Vector3i, surface_y: int, biome: String, house_floor_y: int) -> String:
	if PROFILE_REBUILDS:
		_profile_material_calls += 1
	var key := _key(coord)
	if _edits.has(key):
		return String(_edits[key])
	var authored := _authored_material_at_cached_column(coord, surface_y, house_floor_y)
	if authored != "air":
		return authored
	return _base_material_at_cached_column(coord, surface_y, biome)

func _base_material_at_cached_column(coord: Vector3i, surface_y: int, biome: String) -> String:
	if coord.y > surface_y:
		if biome == "water" and coord.y <= WATER_LEVEL:
			return "water"
		return "air"
	if coord.y == surface_y:
		return _surface_material(biome)
	if coord.y >= surface_y - 3:
		return "soil"
	return "dark_stone" if coord.y < 4 else "stone"

func _base_material_at(coord: Vector3i) -> String:
	var surface_y := _surface_height(coord.x, coord.z)
	var biome := _biome_at(coord.x, coord.z)
	if coord.y > surface_y:
		if biome == "water" and coord.y <= WATER_LEVEL:
			return "water"
		return "air"
	if coord.y == surface_y:
		return _surface_material(biome)
	if coord.y >= surface_y - 3:
		return "soil"
	return "dark_stone" if coord.y < 4 else "stone"

func _authored_material_at(coord: Vector3i) -> String:
	var center_x: int = floori(float(_world_voxels_x) * 0.5)
	var center_z: int = floori(float(_world_voxels_z) * 0.5)
	var road_z: int = center_z
	if abs(coord.z - road_z) <= 1 and coord.y == _surface_height(coord.x, coord.z) + 1:
		if coord.x >= center_x - 64 and coord.x <= center_x + 64:
			return "bridge" if abs(coord.x - center_x) <= 2 else "road"

	var wall_z := center_z - 14
	if coord.z == wall_z and coord.x >= center_x - 16 and coord.x <= center_x - 5:
		var surface := _surface_height(coord.x, coord.z)
		if coord.y >= surface + 1 and coord.y <= surface + 3:
			return "stone_wall"

	var house_x0 := center_x + 6
	var house_z0 := center_z + 7
	var house_x1 := center_x + 15
	var house_z1 := center_z + 15
	var surface_y := _surface_height(center_x + 10, center_z + 11) + 1
	var on_house_edge := coord.x == house_x0 or coord.x == house_x1 or coord.z == house_z0 or coord.z == house_z1
	if coord.x >= house_x0 and coord.x <= house_x1 and coord.z >= house_z0 and coord.z <= house_z1:
		if on_house_edge and coord.y >= surface_y and coord.y <= surface_y + 3:
			return "wood_wall"
		if coord.y == surface_y + 4:
			return "roof"
		if coord.y == surface_y + 5 and coord.x >= house_x0 + 2 and coord.x <= house_x1 - 2 and coord.z >= house_z0 + 2 and coord.z <= house_z1 - 2:
			return "roof"

	return "air"

func _authored_material_at_cached_column(coord: Vector3i, surface_y: int, house_floor_y: int) -> String:
	var center_x: int = floori(float(_world_voxels_x) * 0.5)
	var center_z: int = floori(float(_world_voxels_z) * 0.5)
	var road_z: int = center_z
	if abs(coord.z - road_z) <= 1 and coord.y == surface_y + 1:
		if coord.x >= center_x - 64 and coord.x <= center_x + 64:
			return "bridge" if abs(coord.x - center_x) <= 2 else "road"

	var wall_z := center_z - 14
	if coord.z == wall_z and coord.x >= center_x - 16 and coord.x <= center_x - 5:
		if coord.y >= surface_y + 1 and coord.y <= surface_y + 3:
			return "stone_wall"

	var house_x0 := center_x + 6
	var house_z0 := center_z + 7
	var house_x1 := center_x + 15
	var house_z1 := center_z + 15
	var on_house_edge := coord.x == house_x0 or coord.x == house_x1 or coord.z == house_z0 or coord.z == house_z1
	if coord.x >= house_x0 and coord.x <= house_x1 and coord.z >= house_z0 and coord.z <= house_z1:
		if on_house_edge and coord.y >= house_floor_y and coord.y <= house_floor_y + 3:
			return "wood_wall"
		if coord.y == house_floor_y + 4:
			return "roof"
		if coord.y == house_floor_y + 5 and coord.x >= house_x0 + 2 and coord.x <= house_x1 - 2 and coord.z >= house_z0 + 2 and coord.z <= house_z1 - 2:
			return "roof"

	return "air"

func _is_exposed(coord: Vector3i) -> bool:
	for offset: Vector3i in NEIGHBOR_OFFSETS:
		var neighbor: Vector3i = coord + offset
		var neighbor_material: String = "air" if not _in_bounds(neighbor) else _material_at(neighbor)
		if neighbor_material == "air" or neighbor_material == "water":
			return true
	return false

func _surface_height(x: int, z: int) -> int:
	if PROFILE_REBUILDS:
		_profile_surface_height_calls += 1
	if _chunk_samples > 0 and not _chunk_metadata.is_empty():
		var height_meters := _height_meters_at(x, z)
		return _surface_height_from_meters(height_meters, x, z)

	var hill := sin(float(x) * 0.027) * 2.2 + cos(float(z) * 0.021) * 2.0
	var valley := -3.0 * exp(-pow(float(x - floori(float(_world_voxels_x) * 0.5)), 2.0) / 2400.0)
	return clampi(roundi(11.0 + hill + valley), 3, GRID_Y - 7)

func _surface_height_for_lod(x: int, z: int) -> int:
	return _surface_height(x, z)

func _surface_height_from_meters(height_meters: float, x: int, z: int) -> int:
	var normalized := inverse_lerp(VOXEL_GUIDE_MIN_HEIGHT_METERS, VOXEL_GUIDE_MAX_HEIGHT_METERS, height_meters)
	normalized = clampf(pow(clampf(normalized, 0.0, 1.0), 0.72), 0.0, 1.0)
	var guide_height := roundi(lerpf(float(VOXEL_GUIDE_MIN_LAYER), float(VOXEL_GUIDE_MAX_LAYER), normalized))
	var detail_offset := _terrain_detail_offset(x, z, height_meters)
	return clampi(guide_height + detail_offset, 2, GRID_Y - 1)

func _terrain_detail_offset(x: int, z: int, height_meters: float) -> int:
	var broad := _value_noise_2d(float(x) * 0.035, float(z) * 0.035)
	var mid := _value_noise_2d(float(x) * 0.065 + 37.1, float(z) * 0.065 - 19.4)
	var fine := _value_noise_2d(float(x) * 0.12 - 11.6, float(z) * 0.12 + 51.8)
	var shaped := broad * 0.70 + mid * 0.24 + fine * 0.06
	shaped = sign(shaped) * pow(abs(shaped), 1.15)

	var water_damping := clampf(inverse_lerp(-80.0, 35.0, height_meters), 0.18, 1.0)
	return clampi(roundi(shaped * float(TERRAIN_DETAIL_RADIUS_VOXELS) * water_damping), -TERRAIN_DETAIL_RADIUS_VOXELS, TERRAIN_DETAIL_RADIUS_VOXELS)

func _value_noise_2d(x: float, z: float) -> float:
	var x0 := floori(x)
	var z0 := floori(z)
	var tx := x - float(x0)
	var tz := z - float(z0)
	var sx := tx * tx * (3.0 - 2.0 * tx)
	var sz := tz * tz * (3.0 - 2.0 * tz)
	var a := _hash_noise_2d(x0, z0)
	var b := _hash_noise_2d(x0 + 1, z0)
	var c := _hash_noise_2d(x0, z0 + 1)
	var d := _hash_noise_2d(x0 + 1, z0 + 1)
	return lerpf(lerpf(a, b, sx), lerpf(c, d, sx), sz)

func _hash_noise_2d(x: int, z: int) -> float:
	var n := sin(float(x) * 127.1 + float(z) * 311.7) * 43758.5453123
	return (n - floor(n)) * 2.0 - 1.0

func _biome_at(x: int, z: int) -> String:
	if PROFILE_REBUILDS:
		_profile_biome_calls += 1
	if abs(x - floori(float(_world_voxels_x) * 0.5)) <= 2:
		return "water"
	if _chunk_samples > 0 and not _chunk_metadata.is_empty():
		return _biome_id_from_index(_biome_index_at(x, z))
	if z < floori(float(_world_voxels_z) * 0.2):
		return "alpine"
	if z > floori(float(_world_voxels_z) * 0.8):
		return "woodland"
	if x < floori(float(_world_voxels_x) * 0.2):
		return "coast"
	return "grassland"

func _surface_material(biome: String) -> String:
	match biome:
		"ocean", "river", "lake", "water":
			return "sand"
		"coast", "arid_scrub":
			return "sand"
		"woodland", "temperate_rainforest", "mountain_forest", "highland_forest":
			return "forest_floor"
		"alpine", "cold_steppe":
			return "snow"
		"dry_mountains":
			return "stone"
		_:
			return "grass"

func _load_source_terrain() -> void:
	var manifest_path := _resolve_path(MANIFEST_PATH)
	if not FileAccess.file_exists(manifest_path):
		_center_view()
		return
	var file := FileAccess.open(manifest_path, FileAccess.READ)
	if file == null:
		_center_view()
		return
	var manifest = JSON.parse_string(file.get_as_text())
	if typeof(manifest) != TYPE_DICTIONARY:
		_center_view()
		return

	var chunks: Array = manifest.get("chunks", [])
	var chunk_grid: Dictionary = manifest.get("chunk_grid", {})
	var scale_info: Dictionary = manifest.get("scale", {})
	var height_info: Dictionary = manifest.get("height", {})
	_chunk_samples = int(chunk_grid.get("samples_x", 0))
	_chunks_x = int(chunk_grid.get("chunks_x", 0))
	_chunks_z = int(chunk_grid.get("chunks_y", 0))
	_terrain_min_height = float(height_info.get("min_height", -250.0))
	_terrain_max_height = float(height_info.get("max_height", 3600.0))
	var meters_per_unit := float(scale_info.get("meters_per_godot_unit", 1.0))
	var chunk_size_meters := float(scale_info.get("chunk_size_meters", 0.0))
	if chunks.is_empty() or _chunk_samples <= 0 or _chunks_x <= 0 or _chunks_z <= 0:
		_center_view()
		return

	_manifest_directory = manifest_path.get_base_dir()
	if meters_per_unit > 0.0 and chunk_size_meters > 0.0:
		_cell_size = chunk_size_meters / float(_chunk_samples - 1) / meters_per_unit
	_world_voxels_x = _chunks_x * (_chunk_samples - 1) + 1
	_world_voxels_z = _chunks_z * (_chunk_samples - 1) + 1
	for chunk in chunks:
		if typeof(chunk) != TYPE_DICTIONARY:
			continue
		var chunk_x := int(chunk.get("x", 0))
		var chunk_z := int(chunk.get("y", 0))
		_chunk_metadata[_chunk_key(chunk_x, chunk_z)] = chunk
	_center_view()

func _configure_voxel_geometry() -> void:
	_voxel_height = max(DEFAULT_VOXEL_HEIGHT, _cell_size * HEIGHT_EXAGGERATION)
	var render_cell_size := _render_cell_size()
	_cube_mesh.size = Vector3(render_cell_size, _voxel_height, render_cell_size)
	_shape.size = Vector3(render_cell_size, _voxel_height, render_cell_size)
	if _selection != null:
		var selection_mesh := _selection.mesh as BoxMesh
		if selection_mesh != null:
			selection_mesh.size = Vector3(render_cell_size * 1.06, _voxel_height * 1.06, render_cell_size * 1.06)
	if is_equal_approx(_distance, 68.0):
		_distance = _default_camera_distance()
	else:
		_distance = clampf(_distance, _min_camera_distance(), _max_camera_distance())
	_configure_camera_clip()

func _configure_camera_clip() -> void:
	if _camera == null:
		return
	var rendered_width := float(GRID_X + RENDER_MARGIN * 2) * _render_cell_size()
	var rendered_depth := float(GRID_Z + RENDER_MARGIN * 2) * _render_cell_size()
	var rendered_height := float(GRID_Y) * _voxel_height
	var rendered_diagonal := Vector3(rendered_width, rendered_height, rendered_depth).length()
	_camera.near = max(0.1, min(_voxel_height, _cell_size) * 0.02)
	_camera.far = max(4000.0, _max_camera_distance() + rendered_diagonal * 1.5)

func _load_chunk_data(chunk_x: int, chunk_z: int) -> Dictionary:
	if PROFILE_REBUILDS:
		_profile_chunk_load_calls += 1
	var key := _chunk_key(chunk_x, chunk_z)
	if _chunk_cache.has(key):
		if PROFILE_REBUILDS:
			_profile_chunk_cache_hits += 1
		return _chunk_cache[key]
	if not _chunk_metadata.has(key):
		return {}
	if PROFILE_REBUILDS:
		_profile_chunk_cache_misses += 1

	var chunk: Dictionary = _chunk_metadata[key]
	var height_bytes := _read_expected_bytes(
		_manifest_directory.path_join(String(chunk.get("height_file", ""))),
		_chunk_samples * _chunk_samples * 2
	)
	if height_bytes.is_empty():
		return {}

	var biome_bytes := _read_expected_bytes(
		_manifest_directory.path_join(String(chunk.get("biome_file", ""))),
		_chunk_samples * _chunk_samples
	)
	var data: Dictionary = {
		"height": height_bytes,
		"biome": biome_bytes
	}
	_chunk_cache[key] = data
	return data

func _preload_window_chunks() -> void:
	if _chunk_samples <= 1 or _chunk_metadata.is_empty():
		return
	var intervals: int = _chunk_samples - 1
	var min_world_x := _render_origin_x - RENDER_MARGIN * _render_stride
	var max_world_x := _render_origin_x + (GRID_X + RENDER_MARGIN - 1) * _render_stride
	var min_world_z := _render_origin_z - RENDER_MARGIN * _render_stride
	var max_world_z := _render_origin_z + (GRID_Z + RENDER_MARGIN - 1) * _render_stride
	var min_chunk_x: int = clampi(floori(float(min_world_x) / float(intervals)) - 1, 0, _chunks_x - 1)
	var max_chunk_x: int = clampi(floori(float(max_world_x) / float(intervals)) + 1, 0, _chunks_x - 1)
	var min_chunk_z: int = clampi(floori(float(min_world_z) / float(intervals)) - 1, 0, _chunks_z - 1)
	var max_chunk_z: int = clampi(floori(float(max_world_z) / float(intervals)) + 1, 0, _chunks_z - 1)
	var keep_keys: Dictionary = {}
	for chunk_z in range(min_chunk_z, max_chunk_z + 1):
		for chunk_x in range(min_chunk_x, max_chunk_x + 1):
			var key: String = _chunk_key(chunk_x, chunk_z)
			keep_keys[key] = true
			_load_chunk_data(chunk_x, chunk_z)
	for key: String in _chunk_cache.keys():
		if not keep_keys.has(key):
			_chunk_cache.erase(key)

func _read_expected_bytes(path: String, expected_bytes: int) -> PackedByteArray:
	var read_start := Time.get_ticks_usec() if PROFILE_REBUILDS else 0
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		if PROFILE_REBUILDS:
			_profile_chunk_read_usec += Time.get_ticks_usec() - read_start
		return PackedByteArray()
	var bytes := file.get_buffer(file.get_length())
	if PROFILE_REBUILDS:
		_profile_chunk_read_usec += Time.get_ticks_usec() - read_start
	if bytes.size() != expected_bytes:
		return PackedByteArray()
	if PROFILE_REBUILDS:
		_profile_chunk_bytes_read += bytes.size()
	return bytes

func _reset_rebuild_profile() -> void:
	_profile_material_calls = 0
	_profile_surface_height_calls = 0
	_profile_biome_calls = 0
	_profile_chunk_load_calls = 0
	_profile_chunk_cache_hits = 0
	_profile_chunk_cache_misses = 0
	_profile_chunk_bytes_read = 0
	_profile_chunk_read_usec = 0

func _height_meters_at(global_x: int, global_z: int) -> float:
	var sample: Dictionary = _global_sample(global_x, global_z)
	var data: Dictionary = _load_chunk_data(int(sample["chunk_x"]), int(sample["chunk_z"]))
	if data.is_empty():
		return 0.0
	var bytes: PackedByteArray = data["height"]
	var sample_index: int = int(sample["local_z"]) * _chunk_samples + int(sample["local_x"])
	var raw := bytes.decode_u16(sample_index * 2)
	return _terrain_min_height + raw / 65535.0 * (_terrain_max_height - _terrain_min_height)

func _biome_index_at(global_x: int, global_z: int) -> int:
	var sample: Dictionary = _global_sample(global_x, global_z)
	var data: Dictionary = _load_chunk_data(int(sample["chunk_x"]), int(sample["chunk_z"]))
	if data.is_empty() or not data.has("biome"):
		return 4
	var bytes: PackedByteArray = data["biome"]
	if bytes.is_empty():
		return 4
	var sample_index: int = int(sample["local_z"]) * _chunk_samples + int(sample["local_x"])
	return bytes[sample_index]

func _global_sample(global_x: int, global_z: int) -> Dictionary:
	var intervals: int = _chunk_samples - 1
	var clamped_x: int = clampi(global_x, 0, _world_voxels_x - 1)
	var clamped_z: int = clampi(global_z, 0, _world_voxels_z - 1)
	var chunk_x: int = clampi(floori(float(clamped_x) / float(intervals)), 0, _chunks_x - 1)
	var chunk_z: int = clampi(floori(float(clamped_z) / float(intervals)), 0, _chunks_z - 1)
	var local_x: int = clamped_x - chunk_x * intervals
	var local_z: int = clamped_z - chunk_z * intervals
	return {
		"chunk_x": chunk_x,
		"chunk_z": chunk_z,
		"local_x": local_x,
		"local_z": local_z
	}

func _biome_id_from_index(index: int) -> String:
	match index:
		0:
			return "ocean"
		1:
			return "coast"
		2:
			return "river"
		3:
			return "lake"
		4:
			return "grassland"
		5:
			return "woodland"
		6:
			return "temperate_rainforest"
		7:
			return "mountain_forest"
		8:
			return "dry_mountains"
		9:
			return "highland_forest"
		10:
			return "cold_steppe"
		11:
			return "alpine"
		12:
			return "arid_scrub"
		_:
			return "grassland"

func _load_edits() -> void:
	var path := _resolve_path(EDITS_PATH)
	if not FileAccess.file_exists(path):
		return
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return
	var parsed = JSON.parse_string(file.get_as_text())
	if typeof(parsed) != TYPE_DICTIONARY:
		return
	for edit in parsed.get("edits", []):
		if typeof(edit) != TYPE_DICTIONARY:
			continue
		var coord := Vector3i(int(edit.get("x", -1)), int(edit.get("y", -1)), int(edit.get("z", -1)))
		if _in_bounds(coord):
			_edits[_key(coord)] = String(edit.get("material", "air"))

func _save_edits() -> void:
	var path := _resolve_path(EDITS_PATH)
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var edits := []
	for key in _edits.keys():
		var parts := String(key).split(",")
		if parts.size() != 3:
			continue
		edits.append({
			"x": int(parts[0]),
			"y": int(parts[1]),
			"z": int(parts[2]),
			"material": String(_edits[key])
		})
	var payload := {
		"format_version": 1,
		"active_window_size": {"x": GRID_X, "y": GRID_Y, "z": GRID_Z},
		"render_stride": _render_stride,
		"world_size": {"x": _world_voxels_x, "y": GRID_Y, "z": _world_voxels_z},
		"edits": edits
	}
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(payload, "\t"))

func _refresh_ui(status: String) -> void:
	if _mode_label == null:
		return
	_mode_label.text = "Mode: %s | Material: %s" % ["remove" if _remove_mode else "place", MATERIALS[_selected_material]["label"]]
	_status_label.text = "%s\nWindow %d,%d to %d,%d of %d x %d | LOD x%d\nLeft click edit | left drag orbit | right drag pan | wheel zoom | arrows scroll | Home center | X remove | S save | R reset edits | [ ] palette" % [
		status,
		_view_origin_x,
		_view_origin_z,
		min(_view_origin_x + (GRID_X - 1) * _render_stride, _world_voxels_x - 1),
		min(_view_origin_z + (GRID_Z - 1) * _render_stride, _world_voxels_z - 1),
		_world_voxels_x,
		_world_voxels_z,
		_render_stride
	]

func _update_camera() -> void:
	_position_target_above_terrain()
	var offset := Vector3(
		cos(_pitch) * sin(_yaw),
		-sin(_pitch),
		cos(_pitch) * cos(_yaw)
	) * _distance
	_camera.global_position = _target + offset
	_camera.look_at(_target, Vector3.UP)

func _view_center_target() -> Vector3:
	var target := Vector3(_visible_render_width() * 0.5, 0.0, _visible_render_depth() * 0.5)
	target.y = _terrain_target_height(target.x, target.z)
	return target

func _position_target_above_terrain() -> void:
	_target.x = clampf(_target.x, 0.0, _visible_render_width())
	_target.z = clampf(_target.z, 0.0, _visible_render_depth())
	_target.y = _terrain_target_height(_target.x, _target.z)

func _terrain_target_height(local_x: float, local_z: float) -> float:
	var global_x := clampi(_view_origin_x + floori(local_x / _render_cell_size()) * _render_stride, 0, _world_voxels_x - 1)
	var global_z := clampi(_view_origin_z + floori(local_z / _render_cell_size()) * _render_stride, 0, _world_voxels_z - 1)
	var surface_y := _surface_height(global_x, global_z)
	var clearance_layers := maxi(CAMERA_TERRAIN_CLEARANCE_LAYERS, TERRAIN_DETAIL_RADIUS_VOXELS + 2)
	return float(mini(surface_y + clearance_layers, GRID_Y - 1)) * _voxel_height

func _min_camera_distance() -> float:
	return max(8.0, _cell_size * 4.0)

func _max_camera_distance() -> float:
	return max(180.0, max(float(GRID_X), float(GRID_Z)) * _cell_size * float(MAX_RENDER_STRIDE) * 4.0)

func _default_camera_distance() -> float:
	return clampf(max(float(GRID_X), float(GRID_Z)) * _cell_size * 1.4, _min_camera_distance(), _max_camera_distance())

func _coord_to_world(coord: Vector3i) -> Vector3:
	return Vector3(
		(float(coord.x - _render_origin_x) / float(_render_stride) + 0.5) * _render_cell_size(),
		float(coord.y) * _voxel_height,
		(float(coord.z - _render_origin_z) / float(_render_stride) + 0.5) * _render_cell_size()
	)

func _in_bounds(coord: Vector3i) -> bool:
	return coord.x >= 0 and coord.x < _world_voxels_x and coord.y >= 0 and coord.y < GRID_Y and coord.z >= 0 and coord.z < _world_voxels_z

func _key(coord: Vector3i) -> String:
	return "%d,%d,%d" % [coord.x, coord.y, coord.z]

func _chunk_key(chunk_x: int, chunk_z: int) -> String:
	return "%d,%d" % [chunk_x, chunk_z]

func _center_view() -> void:
	_view_origin_x = clampi(floori(float(_world_voxels_x - _view_span_x()) * 0.5), 0, _max_view_origin_x())
	_view_origin_z = clampi(floori(float(_world_voxels_z - _view_span_z()) * 0.5), 0, _max_view_origin_z())
	_render_origin_x = _view_origin_x
	_render_origin_z = _view_origin_z

func _move_view(delta_x: int, delta_z: int) -> void:
	var previous_origin_x := _view_origin_x
	var previous_origin_z := _view_origin_z
	var current_visual_offset := _voxel_root.position
	_view_origin_x = clampi(_view_origin_x + delta_x, 0, _max_view_origin_x())
	_view_origin_z = clampi(_view_origin_z + delta_z, 0, _max_view_origin_z())
	var applied_x := _view_origin_x - previous_origin_x
	var applied_z := _view_origin_z - previous_origin_z
	if applied_x == 0 and applied_z == 0:
		_refresh_ui("World edge")
		return
	_target = _view_center_target()
	_update_camera()
	if _view_fits_render_cache():
		_animate_voxel_scroll(_visual_offset_for_view(), current_visual_offset)
	else:
		_render_origin_x = _view_origin_x
		_render_origin_z = _view_origin_z
		_rebuild_voxels()
		var rebuild_start_offset := current_visual_offset + Vector3(float(applied_x), 0.0, float(applied_z)) * _cell_size
		_animate_voxel_scroll(_visual_offset_for_view(), rebuild_start_offset)
	_refresh_ui("Scrolled world view")

func _view_fits_render_cache() -> bool:
	return (
		_view_origin_x >= _render_origin_x - RENDER_MARGIN * _render_stride
		and _view_origin_z >= _render_origin_z - RENDER_MARGIN * _render_stride
		and _view_origin_x + _view_span_x() <= _render_origin_x + (GRID_X + RENDER_MARGIN) * _render_stride
		and _view_origin_z + _view_span_z() <= _render_origin_z + (GRID_Z + RENDER_MARGIN) * _render_stride
	)

func _visual_offset_for_view() -> Vector3:
	return Vector3(float(_render_origin_x - _view_origin_x), 0.0, float(_render_origin_z - _view_origin_z)) * _cell_size

func _animate_voxel_scroll(target_visual_offset: Vector3, start_visual_offset: Vector3) -> void:
	if _scroll_tween != null:
		_scroll_tween.kill()
	_scroll_tween = create_tween()
	_scroll_tween.set_trans(Tween.TRANS_SINE)
	_scroll_tween.set_ease(Tween.EASE_OUT)
	_voxel_root.position = start_visual_offset
	_scroll_tween.tween_property(_voxel_root, "position", target_visual_offset, SCROLL_ANIMATION_SECONDS)

func _reset_scroll_animation() -> void:
	if _scroll_tween != null:
		_scroll_tween.kill()
		_scroll_tween = null
	_voxel_root.position = _visual_offset_for_view()

func _scroll_window_for_target() -> void:
	var delta_x: int = 0
	var delta_z: int = 0
	var target_grid_x := _target.x / _render_cell_size()
	var target_grid_z := _target.z / _render_cell_size()
	if target_grid_x < SCROLL_MARGIN:
		delta_x = -SCROLL_REBUILD_STEP
	elif target_grid_x > GRID_X - SCROLL_MARGIN:
		delta_x = SCROLL_REBUILD_STEP
	if target_grid_z < SCROLL_MARGIN:
		delta_z = -SCROLL_REBUILD_STEP
	elif target_grid_z > GRID_Z - SCROLL_MARGIN:
		delta_z = SCROLL_REBUILD_STEP
	if delta_x == 0 and delta_z == 0:
		return

	var previous_origin_x: int = _view_origin_x
	var previous_origin_z: int = _view_origin_z
	_view_origin_x = clampi(_view_origin_x + delta_x * _render_stride, 0, _max_view_origin_x())
	_view_origin_z = clampi(_view_origin_z + delta_z * _render_stride, 0, _max_view_origin_z())
	var applied_x: int = _view_origin_x - previous_origin_x
	var applied_z: int = _view_origin_z - previous_origin_z
	if applied_x == 0 and applied_z == 0:
		_target.x = clampf(_target.x, 0.0, float(GRID_X - 1) * _render_cell_size())
		_target.z = clampf(_target.z, 0.0, float(GRID_Z - 1) * _render_cell_size())
		return

	_target.x -= float(applied_x) * _cell_size
	_target.z -= float(applied_z) * _cell_size
	_position_target_above_terrain()
	_render_origin_x = _view_origin_x
	_render_origin_z = _view_origin_z
	_reset_scroll_animation()
	_rebuild_voxels()
	_refresh_ui("Scrolled world view")

func _render_cell_size() -> float:
	return _cell_size * float(_render_stride)

func _visible_render_width() -> float:
	return min(float(_view_span_x()), float(_world_voxels_x)) * _cell_size

func _visible_render_depth() -> float:
	return min(float(_view_span_z()), float(_world_voxels_z)) * _cell_size

func _view_span_x() -> int:
	return GRID_X * _render_stride

func _view_span_z() -> int:
	return GRID_Z * _render_stride

func _max_view_origin_x() -> int:
	return maxi(0, _world_voxels_x - _view_span_x())

func _max_view_origin_z() -> int:
	return maxi(0, _world_voxels_z - _view_span_z())

func _desired_render_stride() -> int:
	var base_distance: float = max(float(GRID_X), float(GRID_Z)) * _cell_size
	if _distance > base_distance * 6.5:
		return 32
	if _distance > base_distance * 5.0:
		return 16
	if _distance > base_distance * 3.5:
		return 8
	if _distance > base_distance * 2.5:
		return 4
	if _distance > base_distance * 1.8:
		return 2
	return 1

func _update_lod_for_distance() -> void:
	var desired_stride := _desired_render_stride()
	if desired_stride == _render_stride:
		return
	var center_x := _view_origin_x + floori(float(_view_span_x()) * 0.5)
	var center_z := _view_origin_z + floori(float(_view_span_z()) * 0.5)
	_render_stride = desired_stride
	_configure_voxel_geometry()
	_view_origin_x = clampi(center_x - floori(float(_view_span_x()) * 0.5), 0, _max_view_origin_x())
	_view_origin_z = clampi(center_z - floori(float(_view_span_z()) * 0.5), 0, _max_view_origin_z())
	_render_origin_x = _view_origin_x
	_render_origin_z = _view_origin_z
	_reset_scroll_animation()
	_target = _view_center_target()
	_rebuild_voxels()
	_refresh_ui("Zoom detail changed")

func _resolve_path(path: String) -> String:
	if path.begins_with("res://") or path.begins_with("user://"):
		return ProjectSettings.globalize_path(path)
	if path.is_absolute_path():
		return path
	return ProjectSettings.globalize_path("res://").path_join(path).simplify_path()

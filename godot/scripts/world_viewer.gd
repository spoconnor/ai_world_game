extends Node3D

@export var manifest_path := "../world_data/prototype/world.json"
@export var vertical_exaggeration := 1.0
@export var water_level_meters := 0.0

const BIOME_COLORS := {
	"ocean": Color(0.09, 0.24, 0.42),
	"coast": Color(0.70, 0.66, 0.48),
	"grassland": Color(0.43, 0.62, 0.28),
	"woodland": Color(0.26, 0.48, 0.25),
	"temperate_rainforest": Color(0.13, 0.38, 0.25),
	"mountain_forest": Color(0.22, 0.36, 0.27),
	"dry_mountains": Color(0.50, 0.45, 0.37),
	"highland_forest": Color(0.25, 0.43, 0.34),
	"cold_steppe": Color(0.52, 0.56, 0.48),
	"alpine": Color(0.70, 0.72, 0.70),
	"arid_scrub": Color(0.56, 0.50, 0.31),
}

var _terrain_root: Node3D
var _water: MeshInstance3D

func _ready() -> void:
	_terrain_root = Node3D.new()
	_terrain_root.name = "Terrain"
	add_child(_terrain_root)

	var manifest := _load_manifest(manifest_path)
	if manifest.is_empty():
		push_warning("No world manifest found at %s. Generate one with the terrain_tool first." % manifest_path)
		return

	_build_world(manifest)

func _load_manifest(path: String) -> Dictionary:
	var resolved_path := _resolve_path(path)
	if not FileAccess.file_exists(resolved_path):
		return {}

	var file := FileAccess.open(resolved_path, FileAccess.READ)
	if file == null:
		push_error("Unable to open world manifest: %s" % resolved_path)
		return {}

	var parsed = JSON.parse_string(file.get_as_text())
	if typeof(parsed) != TYPE_DICTIONARY:
		push_error("World manifest is not a JSON object: %s" % resolved_path)
		return {}

	parsed["_manifest_directory"] = resolved_path.get_base_dir()
	return parsed

func _build_world(manifest: Dictionary) -> void:
	var scale := manifest.get("scale", {})
	var height := manifest.get("height", {})
	var chunk_grid := manifest.get("chunk_grid", {})
	var chunks: Array = manifest.get("chunks", [])

	var meters_per_unit := float(scale.get("meters_per_godot_unit", 10.0))
	var chunk_size_meters := float(scale.get("chunk_size_meters", 10000.0))
	var world_width_meters := float(scale.get("world_width_meters", 0.0))
	var world_height_meters := float(scale.get("world_height_meters", 0.0))
	var min_height := float(height.get("min_height", -250.0))
	var max_height := float(height.get("max_height", 3600.0))
	var samples_x := int(chunk_grid.get("samples_x", 129))
	var samples_y := int(chunk_grid.get("samples_y", 129))

	if world_width_meters <= 0.0 or world_height_meters <= 0.0 or samples_x < 2 or samples_y < 2:
		push_error("World manifest has invalid scale or chunk grid metadata.")
		return

	var manifest_directory := String(manifest.get("_manifest_directory", ""))
	var world_width_units := world_width_meters / meters_per_unit
	var world_height_units := world_height_meters / meters_per_unit
	var chunk_size_units := chunk_size_meters / meters_per_unit
	var world_offset := Vector3(-world_width_units * 0.5, 0.0, -world_height_units * 0.5)

	for chunk in chunks:
		if typeof(chunk) != TYPE_DICTIONARY:
			continue
		var mesh_instance := _build_chunk_mesh(
			chunk,
			manifest_directory,
			samples_x,
			samples_y,
			chunk_size_units,
			meters_per_unit,
			min_height,
			max_height
		)
		if mesh_instance != null:
			mesh_instance.position = world_offset
			_terrain_root.add_child(mesh_instance)

	_build_water_plane(world_width_units, world_height_units, water_level_meters / meters_per_unit, world_offset)

	var camera_rig := get_node_or_null("CameraRig")
	if camera_rig != null and camera_rig.has_method("focus_on"):
		var max_height_units := max(abs(min_height), abs(max_height)) / meters_per_unit * vertical_exaggeration
		var center := Vector3(0.0, max_height_units * 0.2, 0.0)
		var radius := max(world_width_units, world_height_units) * 0.58
		camera_rig.focus_on(center, radius)

func _build_chunk_mesh(
	chunk: Dictionary,
	manifest_directory: String,
	samples_x: int,
	samples_y: int,
	chunk_size_units: float,
	meters_per_unit: float,
	min_height: float,
	max_height: float
) -> MeshInstance3D:
	var height_file := String(chunk.get("height_file", ""))
	var height_path := manifest_directory.path_join(height_file)
	var expected_bytes := samples_x * samples_y * 2
	var file := FileAccess.open(height_path, FileAccess.READ)

	if file == null:
		push_warning("Missing chunk height file: %s" % height_path)
		return null

	var bytes := file.get_buffer(file.get_length())
	if bytes.size() != expected_bytes:
		push_warning("Skipping %s: expected %d bytes, found %d." % [height_path, expected_bytes, bytes.size()])
		return null

	var chunk_x := int(chunk.get("x", 0))
	var chunk_y := int(chunk.get("y", 0))
	var step_x := chunk_size_units / float(samples_x - 1)
	var step_y := chunk_size_units / float(samples_y - 1)
	var vertices := PackedVector3Array()
	var indices := PackedInt32Array()
	vertices.resize(samples_x * samples_y)

	for local_y in range(samples_y):
		for local_x in range(samples_x):
			var sample_index := local_y * samples_x + local_x
			var raw_height := bytes.decode_u16(sample_index * 2)
			var normalized_height := raw_height / 65535.0
			var height_meters := min_height + normalized_height * (max_height - min_height)
			var vertex := Vector3(
				chunk_x * chunk_size_units + local_x * step_x,
				height_meters / meters_per_unit * vertical_exaggeration,
				chunk_y * chunk_size_units + local_y * step_y
			)
			vertices[sample_index] = vertex

	for local_y in range(samples_y - 1):
		for local_x in range(samples_x - 1):
			var a := local_y * samples_x + local_x
			var b := a + 1
			var c := a + samples_x
			var d := c + 1
			indices.append_array([a, c, b, b, c, d])

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_INDEX] = indices

	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)

	var surface_tool := SurfaceTool.new()
	surface_tool.create_from(mesh, 0)
	surface_tool.generate_normals()
	mesh = surface_tool.commit()

	var mesh_instance := MeshInstance3D.new()
	mesh_instance.name = "Chunk_%03d_%03d" % [chunk_x, chunk_y]
	mesh_instance.mesh = mesh
	mesh_instance.material_override = _make_biome_material(String(chunk.get("dominant_biome", "grassland")))
	return mesh_instance

func _build_water_plane(width: float, height: float, water_y: float, world_offset: Vector3) -> void:
	var plane := PlaneMesh.new()
	plane.size = Vector2(width, height)

	var material := StandardMaterial3D.new()
	material.albedo_color = Color(0.07, 0.22, 0.42, 0.62)
	material.roughness = 0.38
	material.metallic = 0.0
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA

	_water = MeshInstance3D.new()
	_water.name = "Water"
	_water.mesh = plane
	_water.material_override = material
	_water.position = world_offset + Vector3(width * 0.5, water_y, height * 0.5)
	add_child(_water)

func _make_biome_material(biome: String) -> StandardMaterial3D:
	var material := StandardMaterial3D.new()
	var color: Color = BIOME_COLORS.get(biome, Color(0.45, 0.55, 0.36))
	material.albedo_color = color
	material.roughness = 0.9
	return material

func _resolve_path(path: String) -> String:
	if path.begins_with("res://") or path.begins_with("user://"):
		return ProjectSettings.globalize_path(path)
	if path.is_absolute_path():
		return path
	return ProjectSettings.globalize_path("res://").path_join(path).simplify_path()

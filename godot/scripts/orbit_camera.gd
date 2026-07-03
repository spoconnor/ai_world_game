extends Node3D

@export var target := Vector3.ZERO
@export var distance := 6500.0
@export var min_distance := 30.0
@export var max_distance := 80000.0
@export var rotate_speed := 0.006
@export var pan_speed := 0.0012
@export var zoom_step := 0.12
@export var min_pitch_degrees := -82.0
@export var max_pitch_degrees := -8.0

var _yaw := 0.0
var _pitch := deg_to_rad(-48.0)
var _left_down := false
var _right_down := false
var _middle_down := false
var _camera: Camera3D

func _ready() -> void:
	_camera = get_node_or_null("Camera3D")
	if _camera == null:
		push_error("CameraRig requires a Camera3D child.")
		return

	_yaw = rotation.y
	_update_camera()

func focus_on(bounds_center: Vector3, bounds_radius: float) -> void:
	target = bounds_center
	distance = clamp(bounds_radius * 1.7, min_distance, max_distance)
	_update_camera()

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		_handle_mouse_button(event)
	elif event is InputEventMouseMotion:
		_handle_mouse_motion(event)

func _handle_mouse_button(event: InputEventMouseButton) -> void:
	match event.button_index:
		MOUSE_BUTTON_LEFT:
			_left_down = event.pressed
		MOUSE_BUTTON_RIGHT:
			_right_down = event.pressed
		MOUSE_BUTTON_MIDDLE:
			_middle_down = event.pressed
		MOUSE_BUTTON_WHEEL_UP:
			if event.pressed:
				_zoom(-1.0)
		MOUSE_BUTTON_WHEEL_DOWN:
			if event.pressed:
				_zoom(1.0)

func _handle_mouse_motion(event: InputEventMouseMotion) -> void:
	if _camera == null:
		return

	if _left_down and not _right_down:
		_yaw -= event.relative.x * rotate_speed
		_pitch = clamp(
			_pitch - event.relative.y * rotate_speed,
			deg_to_rad(min_pitch_degrees),
			deg_to_rad(max_pitch_degrees)
		)
		_update_camera()
	elif _right_down or _middle_down or (_left_down and _right_down):
		var camera_basis: Basis = _camera.global_transform.basis
		var right: Vector3 = camera_basis.x
		var forward: Vector3 = -camera_basis.z
		forward.y = 0.0
		forward = forward.normalized()
		var pan_amount: float = max(distance * pan_speed, 0.2)
		target += (-right * event.relative.x + forward * event.relative.y) * pan_amount
		_update_camera()

func _zoom(direction: float) -> void:
	distance = clamp(distance * (1.0 + direction * zoom_step), min_distance, max_distance)
	_update_camera()

func _update_camera() -> void:
	if _camera == null:
		return

	var offset := Vector3(
		cos(_pitch) * sin(_yaw),
		-sin(_pitch),
		cos(_pitch) * cos(_yaw)
	) * distance
	_camera.global_position = target + offset
	_camera.look_at(target, Vector3.UP)

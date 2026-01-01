extends CharacterBody3D

@export var camera_3d: Camera3D
@export var move_speed: float = 20.0
@export var mouse_sensitivity: float = 0.002

# var gravity: float = ProjectSettings.get_setting("physics/3d/default_gravity")
var gravity: float = 0
var look_angle_x: float = 0.0  # pitch

func _ready() -> void:
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED

func _input(event: InputEvent) -> void:
	if event is InputEventMouseMotion:
		rotation.y -= event.relative.x * mouse_sensitivity

		look_angle_x -= event.relative.y * mouse_sensitivity
		look_angle_x = clamp(look_angle_x, deg_to_rad(-80.0), deg_to_rad(80.0))
		if camera_3d:
			camera_3d.rotation.x = look_angle_x

	# Optional: press Esc to free the mouse
	if event.is_action_pressed("ui_cancel"):
		Input.set_mouse_mode(Input.MOUSE_MODE_VISIBLE)

func _physics_process(delta: float) -> void:
	# Basic gravity
	if not is_on_floor():
		velocity.y -= gravity * delta
	else:
		pass
	if Input.is_action_pressed("jump"):
		position.y += move_speed / 100
	if Input.is_action_pressed("sneak"):
		position.y -= move_speed / 100
	if Input.is_key_pressed(KEY_CTRL):
		move_speed = 150
	else:
		move_speed = 20

	var input_dir: Vector2 = Input.get_vector("left", "right", "forward", "backward")
	var direction: Vector3 = (transform.basis * Vector3(input_dir.x, 0, input_dir.y)).normalized()

	if direction != Vector3.ZERO:
		velocity.x = direction.x * move_speed
		velocity.z = direction.z * move_speed
	else:
		velocity.x = move_toward(velocity.x, 0.0, move_speed)
		velocity.z = move_toward(velocity.z, 0.0, move_speed)

	move_and_slide()

	if camera_3d:
		camera_3d.global_position = global_position

# GDScript because XR Tools' PlayerBody only runs providers that extend its class.
class_name WowVRMovementFly
extends XRToolsMovementProvider

## Flies where the head is looking, including up and down, colliding with what's loaded.

const DEADZONE := 0.2
const RESPONSE := 8.0

@export var order: int = 30

## Yards per second at full stick.
@export var speed: float = 30.0

## The controller whose stick steers.
@export var controller: XRController3D

@onready var _camera := XRHelpers.get_xr_camera(self)


func physics_movement(delta: float, player_body: XRToolsPlayerBody, disabled: bool) -> bool:
	if disabled or not is_active or not controller:
		return false

	var stick := controller.get_vector2("primary")
	if stick.length() < DEADZONE:
		stick = Vector2.ZERO

	var target := _camera.global_basis * Vector3(stick.x, 0, -stick.y) * speed
	var weight := clampf(RESPONSE * delta, 0.0, 1.0)
	player_body.velocity = player_body.move_player(player_body.velocity.lerp(target, weight))
	return true

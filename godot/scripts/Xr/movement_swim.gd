# GDScript because XR Tools' PlayerBody only runs providers that extend its class.
class_name WowVRMovementSwim
extends XRToolsMovementProvider

## Swims where the head is looking, and floats up to the surface when the stick is let go.
## The numbers match DesktopPlayer's swimming.

const DEADZONE := 0.2
const RESPONSE := 4.0
const BUOYANCY := 2.0
const MAX_RISE := 2.5

## After flying, so that it can replace what walking asked for.
@export var order: int = 31

## Yards per second at full stick.
@export var speed: float = 4.7

## The controller whose stick steers.
@export var controller: XRController3D

## Set from XrPlayer: where the feet rest when floating, in world space.
var float_height := 0.0

@onready var _camera := XRHelpers.get_xr_camera(self)


func physics_movement(delta: float, player_body: XRToolsPlayerBody, disabled: bool) -> bool:
	if disabled or not is_active or not controller:
		return false

	var stick := controller.get_vector2("primary")
	if stick.length() < DEADZONE:
		stick = Vector2.ZERO

	var target := _camera.global_basis * Vector3(stick.x, 0, -stick.y) * speed
	var feet := player_body.global_position.y
	if stick == Vector2.ZERO:
		target.y = clampf((float_height - feet) * BUOYANCY, -MAX_RISE, MAX_RISE)
	elif feet >= float_height:
		# Can't swim up out of the water, only along its surface.
		target.y = minf(target.y, (float_height - feet) * BUOYANCY)

	var weight := clampf(RESPONSE * delta, 0.0, 1.0)
	player_body.velocity = player_body.move_player(player_body.velocity.lerp(target, weight))
	return true

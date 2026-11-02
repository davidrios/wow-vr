# GDScript because XR Tools' PlayerBody only runs providers that extend its class.
class_name WowVRMovementStep
extends XRToolsMovementProvider

## Lets a walking player climb steps, which a capsule can't. Matches DesktopPlayer.TryStepUp.

## How high a step can be, in yards.
const STEP_HEIGHT := 1.2

## How far ahead to look for a step, in yards.
const PROBE := 0.3

## Steeper landings than this are walls, in radians.
const MAX_LANDING_ANGLE := deg_to_rad(50.0)

## Runs after walking input is known, and before the body moves.
@export var order: int = 20


func physics_movement(_delta: float, player_body: XRToolsPlayerBody, disabled: bool) -> bool:
	if disabled or not player_body.on_ground:
		return false

	var input := player_body.ground_control_velocity
	if input.length() < 0.1:
		return false

	var camera := player_body.camera_node.global_basis
	var forward := camera.z.slide(Vector3.UP).normalized() * -input.y
	var right := camera.x.slide(Vector3.UP).normalized() * input.x
	var direction := (forward + right).normalized()

	var start := player_body.global_transform
	var ahead := direction * PROBE
	var up := Vector3.UP * STEP_HEIGHT

	# Only a face too steep to walk up is a step. Slopes look the same to this test, and
	# lifting the player on those would make them faster uphill.
	var blocker := KinematicCollision3D.new()
	if not player_body.test_move(start, ahead, blocker):
		return false
	if blocker.get_angle(0, Vector3.UP) <= MAX_LANDING_ANGLE or player_body.test_move(start, up):
		return false

	var raised := start.translated(up)
	if player_body.test_move(raised, ahead):
		return false

	var over := raised.translated(ahead)
	var landing := KinematicCollision3D.new()
	if not player_body.test_move(over, Vector3.DOWN * STEP_HEIGHT, landing):
		return false
	if landing.get_angle(0, Vector3.UP) > MAX_LANDING_ANGLE:
		return false

	player_body.global_position = over.origin + landing.get_travel()
	return false

/**************************************************************************/
/*  humanoid_ik_3d.cpp                                                    */
/**************************************************************************/

#include "humanoid_ik_3d.h"

#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/core/math.hpp>
#include <godot_cpp/variant/quaternion.hpp>
#include <godot_cpp/variant/vector2.hpp>

using namespace godot;

// The least share of the spine kept upright, as a cosine, when the head is far ahead of the hips.
static const float LEAST_SPINE_UPRIGHT = 0.35;
// How far the hips may rise above where the animation has them, as a share of the spine.
static const float MAX_HIP_RISE = 0.05;
// How far toward the body's middle an elbow may point, as the cosine's share.
static const float ELBOW_INWARD = 0.15;
static const Vector3 UP = Vector3(0, 1, 0);

void HumanoidIK3D::_skeleton_changed(Skeleton3D *p_old, Skeleton3D *p_new) {
	rig_dirty = true;
}

int HumanoidIK3D::_find(BoneRole p_role) const {
	Skeleton3D *skeleton = get_skeleton();
	if (!skeleton || bone_names[p_role].is_empty()) {
		return -1;
	}
	return skeleton->find_bone(bone_names[p_role]);
}

void HumanoidIK3D::_update_rig() {
	rig_dirty = false;
	rig_valid = false;
	Skeleton3D *skeleton = get_skeleton();
	if (!skeleton) {
		return;
	}

	hips = _find(BONE_HIPS);
	head = _find(BONE_HEAD);
	spine.clear();
	for (int i = 0; i < spine_bone_names.size(); i++) {
		int bone = skeleton->find_bone(spine_bone_names[i]);
		if (bone < 0) {
			return;
		}
		spine.push_back(bone);
	}
	if (hips < 0 || head < 0 || spine.empty()) {
		return;
	}

	switch (forward_axis) {
		case FORWARD_AXIS_PLUS_X:
			forward = Vector3(1, 0, 0);
			break;
		case FORWARD_AXIS_MINUS_X:
			forward = Vector3(-1, 0, 0);
			break;
		case FORWARD_AXIS_PLUS_Z:
			forward = Vector3(0, 0, 1);
			break;
		case FORWARD_AXIS_MINUS_Z:
			forward = Vector3(0, 0, -1);
			break;
	}
	right = forward.cross(UP);

	// A target node faces -Z with +Y up and +X to its right.
	model_to_target = Basis(right, UP, -forward).inverse();

	head_rest = skeleton->get_bone_global_rest(head).origin;
	if (eye_position != Vector3()) {
		eye_rest = eye_position;
	} else {
		// A guess for a model that doesn't say: a little above and ahead of the head's pivot.
		float height = MAX(head_rest.y, 0.1f);
		eye_rest = head_rest + UP * (height * 0.035f) + forward * (height * 0.055f);
	}
	spine_length = head_rest.distance_to(skeleton->get_bone_global_rest(spine[0]).origin);

	frozen.clear();
	frozen.push_back(hips);
	frozen.insert(frozen.end(), spine.begin(), spine.end());

	for (int side = 0; side < 2; side++) {
		Arm &arm = arms[side];
		int base = side == 0 ? BONE_LEFT_SHOULDER : BONE_RIGHT_SHOULDER;
		arm = Arm();
		arm.left = side == 0;
		arm.shoulder = _find(BoneRole(base));
		arm.upper = _find(BoneRole(base + 1));
		arm.lower = _find(BoneRole(base + 2));
		arm.hand = _find(BoneRole(base + 3));
		int fingers = _find(BoneRole(base + 4));
		int thumb = _find(BoneRole(base + 5));
		if (arm.upper < 0 || arm.lower < 0 || arm.hand < 0) {
			arm.upper = -1;
			continue;
		}

		// The chain from the upper arm to the hand, which must pass the lower arm.
		bool through_lower = false;
		for (int bone = arm.hand; bone >= 0; bone = skeleton->get_bone_parent(bone)) {
			arm.chain.insert(arm.chain.begin(), bone);
			through_lower |= bone == arm.lower;
			if (bone == arm.upper) {
				break;
			}
		}
		if (arm.chain.empty() || arm.chain.front() != arm.upper || !through_lower) {
			arm.upper = -1;
			continue;
		}

		Vector3 shoulder_at = skeleton->get_bone_global_rest(arm.upper).origin;
		Vector3 elbow_at = skeleton->get_bone_global_rest(arm.lower).origin;
		Vector3 wrist_at = skeleton->get_bone_global_rest(arm.hand).origin;
		arm.upper_rest = elbow_at - shoulder_at;
		arm.lower_rest = wrist_at - elbow_at;

		// The elbow bends the forearm forward. An arm nearly straight at rest says nothing about
		// its axis, so it's taken as across the body, which is what that bend means.
		Vector3 hinge = arm.upper_rest.cross(arm.lower_rest);
		arm.hinge_rest = hinge.length() > 0.05f * arm.upper_rest.length() * arm.lower_rest.length() ? hinge.normalized() : right;

		Vector3 along = fingers >= 0 ? skeleton->get_bone_global_rest(fingers).origin - wrist_at : arm.lower_rest;
		Vector3 thumb_side = thumb >= 0 ? skeleton->get_bone_global_rest(thumb).origin - wrist_at : forward;
		Vector3 back = arm.left ? thumb_side.cross(along) : along.cross(thumb_side);
		arm.hand_rest = _frame(along, back);
		arm.elbow_from_hand = elbow_at - wrist_at;

		if (arm.shoulder >= 0) {
			frozen.push_back(arm.shoulder);
		}
		frozen.insert(frozen.end(), arm.chain.begin(), arm.chain.end());
	}

	for (int side = 0; side < 2; side++) {
		int base = side == 0 ? BONE_LEFT_UPPER_LEG : BONE_RIGHT_UPPER_LEG;
		legs[side].upper = _find(BoneRole(base));
		legs[side].lower = _find(BoneRole(base + 1));
		legs[side].foot = _find(BoneRole(base + 2));
		if (legs[side].upper < 0 || legs[side].lower < 0 || legs[side].foot < 0) {
			legs[side] = Leg();
		}
	}

	rig_valid = true;
}

Node3D *HumanoidIK3D::_get_target(Target p_target) const {
	if (targets[p_target].is_empty()) {
		return nullptr;
	}
	return Object::cast_to<Node3D>(get_node_or_null(targets[p_target]));
}

bool HumanoidIK3D::_target_in_skeleton(Target p_target, const Transform3D &p_to_skeleton, Transform3D &r_target) const {
	Node3D *node = _get_target(p_target);
	if (!node || !node->is_inside_tree()) {
		return false;
	}
	Transform3D global = node->get_global_transform();
	r_target = Transform3D(p_to_skeleton.basis.orthonormalized() * global.basis.orthonormalized(), p_to_skeleton.xform(global.origin));
	return true;
}

void HumanoidIK3D::_process_modification_with_delta(double p_delta) {
	if (rig_dirty) {
		_update_rig();
	}
	Skeleton3D *skeleton = get_skeleton();
	if (!rig_valid || !skeleton) {
		return;
	}

	Transform3D to_skeleton = skeleton->get_global_transform().affine_inverse();
	Transform3D head_target_transform;
	if (!_target_in_skeleton(TARGET_HEAD, to_skeleton, head_target_transform)) {
		return;
	}

	// The upper body starts from rest, so the animation's sway and swing don't show through.
	if (freeze_upper_body) {
		for (int bone : frozen) {
			skeleton->set_bone_pose_rotation(bone, skeleton->get_bone_rest(bone).basis.get_rotation_quaternion());
		}
	}

	Transform3D targets_in_skeleton[TARGET_MAX];
	bool has[TARGET_MAX] = {};
	for (int i = TARGET_LEFT_HAND; i < TARGET_MAX; i++) {
		has[i] = _target_in_skeleton(Target(i), to_skeleton, targets_in_skeleton[i]);
	}

	Basis head_turn = head_target_transform.basis * model_to_target;
	Vector3 head_target = head_target_transform.origin + head_turn.xform(head_rest - eye_rest);

	const Transform3D *feet[2] = { has[TARGET_LEFT_FOOT] ? &targets_in_skeleton[TARGET_LEFT_FOOT] : nullptr, has[TARGET_RIGHT_FOOT] ? &targets_in_skeleton[TARGET_RIGHT_FOOT] : nullptr };
	knee_targets[0] = has[TARGET_LEFT_KNEE] ? &targets_in_skeleton[TARGET_LEFT_KNEE] : nullptr;
	knee_targets[1] = has[TARGET_RIGHT_KNEE] ? &targets_in_skeleton[TARGET_RIGHT_KNEE] : nullptr;
	_place_hips(head_target, has[TARGET_HIPS] ? &targets_in_skeleton[TARGET_HIPS] : nullptr, feet, to_skeleton);
	_bend_spine(head_target, head_turn, has[TARGET_CHEST] ? &targets_in_skeleton[TARGET_CHEST] : nullptr);
	_set_global_rotation(head, head_turn * _rest_basis(head));

	for (int side = 0; side < 2; side++) {
		Target hand = side == 0 ? TARGET_LEFT_HAND : TARGET_RIGHT_HAND;
		Target elbow = side == 0 ? TARGET_LEFT_ELBOW : TARGET_RIGHT_ELBOW;
		if (arms[side].upper >= 0 && has[hand]) {
			_solve_arm(arms[side], targets_in_skeleton[hand], has[elbow] ? &targets_in_skeleton[elbow] : nullptr);
		}
	}
}

// Without a hips target, lowers the hips so the spine, at its standing length, can reach the
// head: straight down for a crouch, less when the head is ahead, as in a bow. With one, puts them
// there. Either way the legs bend to keep the feet where the animation put them, or where their
// targets are.
void HumanoidIK3D::_place_hips(const Vector3 &p_head_target, const Transform3D *p_hips_target, const Transform3D *p_feet[2], const Transform3D &p_to_skeleton) {
	Skeleton3D *skeleton = get_skeleton();
	Vector3 feet_before[2];
	Basis feet_turn_before[2];
	for (int side = 0; side < 2; side++) {
		if (legs[side].foot >= 0) {
			Transform3D foot = skeleton->get_bone_global_pose(legs[side].foot);
			feet_before[side] = foot.origin;
			feet_turn_before[side] = foot.basis.orthonormalized();
		}
	}

	bool moved = false;
	if (p_hips_target) {
		_set_global_position(hips, p_hips_target->origin);
		_set_global_rotation(hips, (p_hips_target->basis * model_to_target) * _rest_basis(hips));
		moved = true;
	} else {
		Vector3 low = _position(spine[0]);
		Vector2 ahead(p_head_target.x - low.x, p_head_target.z - low.z);
		float length = spine_length;

		// Far ahead or aside, the hips follow along the ground too.
		Vector3 shift;
		if (ahead.length() > 0.9f * length) {
			Vector2 move = ahead.normalized() * (ahead.length() - 0.9f * length);
			shift = Vector3(move.x, 0, move.y);
			ahead -= move;
		}

		float upright = Math::sqrt(MAX(length * length - ahead.length_squared(), LEAST_SPINE_UPRIGHT * LEAST_SPINE_UPRIGHT * length * length));
		Vector3 at = _position(hips);

		// Models that stand hunched may straighten up to their rest height.
		float rise = MAX(0.0f, (float)skeleton->get_bone_global_rest(hips).origin.y - at.y) + MAX_HIP_RISE * length;
		float drop = CLAMP(low.y + upright - p_head_target.y, -rise, max_hip_drop * at.y);
		if (Math::abs(drop) > 1e-4f || shift != Vector3()) {
			_set_global_position(hips, at + shift - UP * drop);
			moved = true;
		}
	}

	if (moved || p_feet[0] || p_feet[1]) {
		_solve_legs(feet_before, feet_turn_before, p_feet, p_to_skeleton);
	}
}

void HumanoidIK3D::_solve_legs(const Vector3 p_feet_before[2], const Basis p_feet_turn_before[2], const Transform3D *p_feet[2], const Transform3D &p_to_skeleton) {
	for (int side = 0; side < 2; side++) {
		const Leg &leg = legs[side];
		if (leg.foot < 0) {
			continue;
		}

		Vector3 target = p_feet[side] ? p_feet[side]->origin : p_feet_before[side];
		Vector3 thigh = _position(leg.upper);
		Vector3 knee = _position(leg.lower);
		Vector3 foot = _position(leg.foot);

		// A knee target says which way it bends; without one it keeps bending as the animation
		// bends it, or forward if it's straight.
		Vector3 pole;
		if (knee_targets[side]) {
			pole = knee_targets[side]->origin - thigh;
		} else {
			Vector3 line = (foot - thigh).normalized();
			pole = knee - thigh - line * (knee - thigh).dot(line);
			if (pole.length_squared() < 1e-6f) {
				pole = forward;
			}
		}

		_two_bone(leg.upper, leg.lower, leg.foot, target, pole);
		Basis foot_turn = p_feet[side] ? (p_feet[side]->basis * model_to_target) * _rest_basis(leg.foot) : p_feet_turn_before[side];
		_set_global_rotation(leg.foot, foot_turn);
	}
}

// Bends the spine through the head's target with FABRIK, starting from standing bent a little
// back so it rounds as a back does, then shares out the head's turn from the body along it. With a
// chest target, the spine below the chest reaches that instead, and the chest turns as it does.
void HumanoidIK3D::_bend_spine(const Vector3 &p_head_target, const Basis &p_head_turn, const Transform3D *p_chest) {
	int count = (int)spine.size();
	int top = spine.back();
	int reach = p_chest ? count - 1 : count; // Bones solved by FABRIK.
	Vector3 goal = p_chest ? p_chest->origin : p_head_target;

	if (reach > 0) {
		std::vector<Vector3> joints(reach + 1);
		std::vector<float> lengths(reach);
		for (int i = 0; i < reach; i++) {
			joints[i] = _position(spine[i]);
		}
		joints[reach] = p_chest ? _position(top) : _position(head);
		float total = 0;
		for (int i = 0; i < reach; i++) {
			lengths[i] = joints[i].distance_to(joints[i + 1]);
			total += lengths[i];
		}
		for (int i = 1; i < reach; i++) {
			joints[i] -= forward * (0.04f * total);
		}

		Vector3 root = joints[0];
		for (int iteration = 0; iteration < 12; iteration++) {
			joints[reach] = goal;
			for (int i = reach - 1; i >= 0; i--) {
				joints[i] = joints[i + 1] + (joints[i] - joints[i + 1]).normalized() * lengths[i];
			}
			joints[0] = root;
			for (int i = 0; i < reach; i++) {
				joints[i + 1] = joints[i] + (joints[i + 1] - joints[i]).normalized() * lengths[i];
			}
			if (joints[reach].distance_squared_to(goal) < 1e-6f) {
				break;
			}
		}

		for (int i = 0; i < reach; i++) {
			Vector3 from = _position(spine[i]);
			Vector3 next = i + 1 < count ? _position(spine[i + 1]) : _position(head);
			_rotate(spine[i], _arc(next - from, joints[i + 1] - from));
		}
	}

	if (p_chest) {
		_set_global_rotation(top, (p_chest->basis * model_to_target) * _rest_basis(top));
		return;
	}

	// The head's turn from the body, about the up axis, shared up the spine, more toward the top.
	Vector3 look = p_head_turn.xform(forward);
	Vector3 flat(look.x, 0, look.z);
	if (flat.length_squared() < 0.04f) {
		// Looking straight up or down, the top of the head says which way it faces.
		Vector3 top_of_head = p_head_turn.xform(UP) * (look.y < 0 ? 1.0f : -1.0f);
		flat = Vector3(top_of_head.x, 0, top_of_head.z);
	}
	float yaw = Math::atan2(forward.cross(flat).dot(UP), forward.dot(flat));
	yaw = CLAMP(yaw, -1.3f, 1.3f);
	float weights = count * (count + 1) / 2.0f;
	for (int i = 0; i < count; i++) {
		Vector3 from = _position(spine[i]);
		Vector3 next = i + 1 < count ? _position(spine[i + 1]) : _position(head);
		Vector3 axis = next - from;
		if (axis.length_squared() > 1e-8f) {
			_rotate(spine[i], Basis(axis.normalized(), yaw * spine_twist * (i + 1) / weights));
		}
	}
}

// Each arm is a hinge. The elbow goes where the forearm would be for the wrist's turn, blended
// with down and out, never up while the hand is below the shoulder, and never far toward the
// body's middle, unless an elbow target says where. The forearm takes a share of the hand's twist.
void HumanoidIK3D::_solve_arm(Arm &p_arm, const Transform3D &p_wrist, const Transform3D *p_elbow) {
	Vector3 wrist = p_wrist.origin;
	Vector3 fingers = -p_wrist.basis.get_column(2);
	Vector3 back = p_wrist.basis.get_column(1);
	Basis hand_turn = _frame(fingers, back) * p_arm.hand_rest.inverse();
	Basis chest = _turn(spine.back());

	float upper_length = _position(p_arm.upper).distance_to(_position(p_arm.lower));
	float lower_length = _position(p_arm.lower).distance_to(_position(p_arm.hand));
	if (p_arm.shoulder >= 0) {
		_raise_shoulder(p_arm, wrist, chest, upper_length + lower_length);
	}

	Vector3 shoulder = _position(p_arm.upper);
	float side = p_arm.left ? -1.0f : 1.0f;
	Vector3 down = chest.xform(forward * -0.3f - UP + right * (0.5f * side));
	Vector3 hint = p_elbow ? p_elbow->origin - shoulder : wrist + hand_turn.xform(p_arm.elbow_from_hand) - shoulder;
	float raised = CLAMP(1.5f * (wrist - shoulder).dot(chest.xform(UP)) / (upper_length + lower_length), 0.0f, 1.0f);
	Vector3 axes[2] = { chest.xform(UP), chest.xform(right * -side) };
	float limits[2] = { raised, ELBOW_INWARD };

	Vector3 reached;
	Vector3 elbow = _place_elbow(shoulder, wrist, upper_length, lower_length, hint, down, p_elbow ? 1.0f : elbow_from_wrist, reached, axes, limits, p_elbow ? 0 : 2);

	// A hinge: the upper arm and forearm both keep the elbow's axis across the plane they bend in.
	Vector3 hinge = (elbow - shoulder).cross(reached - elbow);
	hinge = hinge.length_squared() > 1e-10f ? hinge.normalized() : down.cross(wrist - shoulder).normalized();
	Basis upper_turn = _frame(elbow - shoulder, hinge) * _frame(p_arm.upper_rest, p_arm.hinge_rest).inverse();
	Basis lower_turn = _frame(reached - elbow, hinge) * _frame(p_arm.lower_rest, p_arm.hinge_rest).inverse();

	// The forearm takes some of the hand's twist about it, so the wrist doesn't wring.
	Vector3 axis = p_arm.lower_rest.normalized();
	Quaternion relative = (lower_turn.inverse() * hand_turn).get_rotation_quaternion();
	float along = Vector3(relative.x, relative.y, relative.z).dot(axis);
	Quaternion twist(axis.x * along, axis.y * along, axis.z * along, relative.w);
	if (twist.length_squared() > 1e-8f) {
		twist = twist.normalized();
		float angle = 2.0f * Math::atan2(Vector3(twist.x, twist.y, twist.z).dot(axis), twist.w);
		lower_turn = lower_turn * Basis(axis, Math::wrapf(angle, (float)-Math::PI, (float)Math::PI) * forearm_twist);
	}

	_set_global_rotation(p_arm.upper, upper_turn * _rest_basis(p_arm.upper));
	_set_global_rotation(p_arm.lower, lower_turn * _rest_basis(p_arm.lower));
	_set_global_rotation(p_arm.hand, hand_turn * _rest_basis(p_arm.hand));
}

// The collarbone lifts the shoulder for a hand above it, and swings it forward for one reaching ahead.
void HumanoidIK3D::_raise_shoulder(const Arm &p_arm, const Vector3 &p_wrist, const Basis &p_chest, float p_arm_length) {
	Vector3 reach = p_chest.inverse().xform(p_wrist - _position(p_arm.upper));
	float distance = reach.length();
	float ahead_amount = reach.dot(forward);
	float up_amount = reach.dot(UP);
	float stretch = CLAMP((distance / p_arm_length - 0.85f) / 0.15f, 0.0f, 1.0f);
	float up = CLAMP(up_amount / p_arm_length + 0.1f, 0.0f, 1.0f);
	float ahead = CLAMP((ahead_amount / p_arm_length - 0.4f) / 0.6f, 0.0f, 1.0f);
	float lift = max_shoulder_lift * MAX(up, up_amount > 0 ? 0.3f * stretch : 0.0f);
	float swing = max_shoulder_reach * MAX(ahead, stretch * CLAMP(ahead_amount / MAX(distance, 1e-4f), 0.0f, 1.0f));
	if (lift <= 0 && swing <= 0) {
		return;
	}

	// About the body's forward axis lifts the left shoulder with a positive turn; about up, swings it back.
	float side = p_arm.left ? -1.0f : 1.0f;
	_rotate(p_arm.shoulder, Basis(p_chest.xform(forward).normalized(), -side * lift) * Basis(p_chest.xform(UP).normalized(), side * swing));
}

// Where the elbow goes for the hand to reach the wrist: on the circle the arm's lengths allow,
// toward the hint, blended with p_down, and at most p_limits along p_limit_axes. r_reached is where
// the hand ends up, short of the wrist when it's out of reach.
Vector3 HumanoidIK3D::_place_elbow(const Vector3 &p_shoulder, const Vector3 &p_wrist, float p_upper_length, float p_lower_length, const Vector3 &p_hint, const Vector3 &p_down, float p_hint_weight, Vector3 &r_reached, const Vector3 *p_limit_axes, const float *p_limits, int p_limit_count) {
	Vector3 reach = p_wrist - p_shoulder;
	Vector3 to_goal = reach.length_squared() > 1e-8f ? reach.normalized() : -UP;
	float distance = CLAMP(reach.length(), Math::abs(p_upper_length - p_lower_length) + 1e-3f, (p_upper_length + p_lower_length) * 0.9999f);
	r_reached = p_shoulder + to_goal * distance;

	auto across = [&](const Vector3 &v) { return v - to_goal * v.dot(to_goal); };
	Vector3 fallback = across(p_down);
	fallback = fallback.length_squared() > 1e-8f ? fallback.normalized() : across(Vector3(0, 0, 1)).normalized();
	Vector3 from_hint = across(p_hint);
	Vector3 pole = from_hint.length_squared() > 1e-6f ? (from_hint.normalized() * p_hint_weight + fallback * (1.0f - p_hint_weight)).normalized() : fallback;

	for (int i = 0; i < p_limit_count; i++) {
		if (pole.dot(p_limit_axes[i]) > p_limits[i]) {
			Vector3 held = across(pole - p_limit_axes[i] * (pole.dot(p_limit_axes[i]) - p_limits[i]));
			pole = held.length_squared() > 1e-6f ? held.normalized() : fallback;
		}
	}

	float cosine = CLAMP((p_upper_length * p_upper_length + distance * distance - p_lower_length * p_lower_length) / (2 * p_upper_length * distance), -1.0f, 1.0f);
	return p_shoulder + to_goal * (p_upper_length * cosine) + pole * (p_upper_length * Math::sqrt(1 - cosine * cosine));
}

// A two-bone chain bent toward the pole so its end reaches the target, keeping each bone's twist.
void HumanoidIK3D::_two_bone(int p_upper, int p_lower, int p_end, const Vector3 &p_target, const Vector3 &p_pole) {
	Vector3 a = _position(p_upper);
	float upper_length = a.distance_to(_position(p_lower));
	float lower_length = _position(p_lower).distance_to(_position(p_end));
	if (upper_length < 1e-4f || lower_length < 1e-4f) {
		return;
	}

	Vector3 reached;
	Vector3 elbow = _place_elbow(a, p_target, upper_length, lower_length, p_pole, p_pole, 1.0f, reached, nullptr, nullptr, 0);
	_rotate(p_upper, _arc(_position(p_lower) - a, elbow - a));
	_rotate(p_lower, _arc(_position(p_end) - elbow, reached - elbow));
}

Vector3 HumanoidIK3D::_position(int p_bone) const {
	return get_skeleton()->get_bone_global_pose(p_bone).origin;
}

Basis HumanoidIK3D::_rest_basis(int p_bone) const {
	return get_skeleton()->get_bone_global_rest(p_bone).basis.orthonormalized();
}

Basis HumanoidIK3D::_turn(int p_bone) const {
	return get_skeleton()->get_bone_global_pose(p_bone).basis.orthonormalized() * _rest_basis(p_bone).inverse();
}

void HumanoidIK3D::_rotate(int p_bone, const Basis &p_turn) {
	_set_global_rotation(p_bone, p_turn * get_skeleton()->get_bone_global_pose(p_bone).basis.orthonormalized());
}

void HumanoidIK3D::_set_global_rotation(int p_bone, const Basis &p_global) {
	Skeleton3D *skeleton = get_skeleton();
	int parent = skeleton->get_bone_parent(p_bone);
	Basis parent_basis = parent >= 0 ? skeleton->get_bone_global_pose(parent).basis.orthonormalized() : Basis();
	skeleton->set_bone_pose_rotation(p_bone, (parent_basis.inverse() * p_global).orthonormalized().get_rotation_quaternion());
}

void HumanoidIK3D::_set_global_position(int p_bone, const Vector3 &p_global) {
	Skeleton3D *skeleton = get_skeleton();
	int parent = skeleton->get_bone_parent(p_bone);
	Vector3 local = parent >= 0 ? skeleton->get_bone_global_pose(parent).affine_inverse().xform(p_global) : p_global;
	skeleton->set_bone_pose_position(p_bone, local);
}

Basis HumanoidIK3D::_frame(const Vector3 &p_along, const Vector3 &p_side) {
	Vector3 x = p_along.normalized();
	Vector3 y = (p_side - x * p_side.dot(x)).normalized();
	return Basis(x, y, x.cross(y));
}

Basis HumanoidIK3D::_arc(const Vector3 &p_from, const Vector3 &p_to) {
	if (p_from.length_squared() < 1e-10f || p_to.length_squared() < 1e-10f) {
		return Basis();
	}
	Vector3 from = p_from.normalized();
	Vector3 to = p_to.normalized();
	if (from.dot(to) > 0.999999f) {
		return Basis();
	}
	return Basis(Quaternion(from, to));
}

bool HumanoidIK3D::is_rig_valid() {
	if (rig_dirty) {
		_update_rig();
	}
	return rig_valid;
}

float HumanoidIK3D::get_eye_height() {
	return is_rig_valid() ? eye_rest.y : 0.0f;
}

float HumanoidIK3D::get_upright_eye_height() {
	if (!is_rig_valid()) {
		return 0.0f;
	}
	// The lowest spine bone's height, the spine straight up from there, then the eyes above the head's pivot.
	float low = get_skeleton()->get_bone_global_rest(spine[0]).origin.y;
	return low + spine_length + (eye_rest.y - head_rest.y);
}

float HumanoidIK3D::get_head_height() {
	return is_rig_valid() ? head_rest.y : 0.0f;
}

float HumanoidIK3D::get_spine_length() {
	return is_rig_valid() ? spine_length : 0.0f;
}

Vector3 HumanoidIK3D::get_head_pivot_for(const Transform3D &p_eyes, float p_scale) {
	if (!is_rig_valid()) {
		return p_eyes.origin;
	}
	return p_eyes.origin + (p_eyes.basis.orthonormalized() * model_to_target).xform(head_rest - eye_rest) * p_scale;
}

void HumanoidIK3D::set_bone_name(int p_role, const StringName &p_name) {
	ERR_FAIL_INDEX(p_role, BONE_MAX);
	bone_names[p_role] = p_name;
	rig_dirty = true;
}

StringName HumanoidIK3D::get_bone_name(int p_role) const {
	ERR_FAIL_INDEX_V(p_role, BONE_MAX, StringName());
	return bone_names[p_role];
}

void HumanoidIK3D::set_spine_bones(const PackedStringArray &p_names) {
	spine_bone_names = p_names;
	rig_dirty = true;
}

PackedStringArray HumanoidIK3D::get_spine_bones() const {
	return spine_bone_names;
}

void HumanoidIK3D::set_target(int p_target, const NodePath &p_path) {
	ERR_FAIL_INDEX(p_target, TARGET_MAX);
	targets[p_target] = p_path;
}

NodePath HumanoidIK3D::get_target(int p_target) const {
	ERR_FAIL_INDEX_V(p_target, TARGET_MAX, NodePath());
	return targets[p_target];
}

void HumanoidIK3D::set_forward_axis(ForwardAxis p_axis) {
	forward_axis = p_axis;
	rig_dirty = true;
}

HumanoidIK3D::ForwardAxis HumanoidIK3D::get_forward_axis() const {
	return forward_axis;
}

void HumanoidIK3D::set_eye_position(const Vector3 &p_position) {
	eye_position = p_position;
	rig_dirty = true;
}

Vector3 HumanoidIK3D::get_eye_position() const {
	return eye_position;
}

void HumanoidIK3D::set_freeze_upper_body(bool p_freeze) {
	freeze_upper_body = p_freeze;
}

bool HumanoidIK3D::is_freezing_upper_body() const {
	return freeze_upper_body;
}

void HumanoidIK3D::set_spine_twist(float p_share) {
	spine_twist = p_share;
}

float HumanoidIK3D::get_spine_twist() const {
	return spine_twist;
}

void HumanoidIK3D::set_forearm_twist(float p_share) {
	forearm_twist = p_share;
}

float HumanoidIK3D::get_forearm_twist() const {
	return forearm_twist;
}

void HumanoidIK3D::set_elbow_from_wrist(float p_share) {
	elbow_from_wrist = p_share;
}

float HumanoidIK3D::get_elbow_from_wrist() const {
	return elbow_from_wrist;
}

void HumanoidIK3D::set_max_shoulder_lift(float p_radians) {
	max_shoulder_lift = p_radians;
}

float HumanoidIK3D::get_max_shoulder_lift() const {
	return max_shoulder_lift;
}

void HumanoidIK3D::set_max_shoulder_reach(float p_radians) {
	max_shoulder_reach = p_radians;
}

float HumanoidIK3D::get_max_shoulder_reach() const {
	return max_shoulder_reach;
}

void HumanoidIK3D::set_max_hip_drop(float p_share) {
	max_hip_drop = p_share;
}

float HumanoidIK3D::get_max_hip_drop() const {
	return max_hip_drop;
}

void HumanoidIK3D::_bind_methods() {
	ClassDB::bind_method(D_METHOD("set_bone_name", "role", "name"), &HumanoidIK3D::set_bone_name);
	ClassDB::bind_method(D_METHOD("get_bone_name", "role"), &HumanoidIK3D::get_bone_name);
	ClassDB::bind_method(D_METHOD("set_spine_bones", "names"), &HumanoidIK3D::set_spine_bones);
	ClassDB::bind_method(D_METHOD("get_spine_bones"), &HumanoidIK3D::get_spine_bones);
	ClassDB::bind_method(D_METHOD("set_target", "target", "path"), &HumanoidIK3D::set_target);
	ClassDB::bind_method(D_METHOD("get_target", "target"), &HumanoidIK3D::get_target);
	ClassDB::bind_method(D_METHOD("set_forward_axis", "axis"), &HumanoidIK3D::set_forward_axis);
	ClassDB::bind_method(D_METHOD("get_forward_axis"), &HumanoidIK3D::get_forward_axis);
	ClassDB::bind_method(D_METHOD("set_eye_position", "position"), &HumanoidIK3D::set_eye_position);
	ClassDB::bind_method(D_METHOD("get_eye_position"), &HumanoidIK3D::get_eye_position);
	ClassDB::bind_method(D_METHOD("set_freeze_upper_body", "freeze"), &HumanoidIK3D::set_freeze_upper_body);
	ClassDB::bind_method(D_METHOD("is_freezing_upper_body"), &HumanoidIK3D::is_freezing_upper_body);
	ClassDB::bind_method(D_METHOD("set_spine_twist", "share"), &HumanoidIK3D::set_spine_twist);
	ClassDB::bind_method(D_METHOD("get_spine_twist"), &HumanoidIK3D::get_spine_twist);
	ClassDB::bind_method(D_METHOD("set_forearm_twist", "share"), &HumanoidIK3D::set_forearm_twist);
	ClassDB::bind_method(D_METHOD("get_forearm_twist"), &HumanoidIK3D::get_forearm_twist);
	ClassDB::bind_method(D_METHOD("set_elbow_from_wrist", "share"), &HumanoidIK3D::set_elbow_from_wrist);
	ClassDB::bind_method(D_METHOD("get_elbow_from_wrist"), &HumanoidIK3D::get_elbow_from_wrist);
	ClassDB::bind_method(D_METHOD("set_max_shoulder_lift", "radians"), &HumanoidIK3D::set_max_shoulder_lift);
	ClassDB::bind_method(D_METHOD("get_max_shoulder_lift"), &HumanoidIK3D::get_max_shoulder_lift);
	ClassDB::bind_method(D_METHOD("set_max_shoulder_reach", "radians"), &HumanoidIK3D::set_max_shoulder_reach);
	ClassDB::bind_method(D_METHOD("get_max_shoulder_reach"), &HumanoidIK3D::get_max_shoulder_reach);
	ClassDB::bind_method(D_METHOD("set_max_hip_drop", "share"), &HumanoidIK3D::set_max_hip_drop);
	ClassDB::bind_method(D_METHOD("get_max_hip_drop"), &HumanoidIK3D::get_max_hip_drop);
	ClassDB::bind_method(D_METHOD("is_rig_valid"), &HumanoidIK3D::is_rig_valid);
	ClassDB::bind_method(D_METHOD("get_eye_height"), &HumanoidIK3D::get_eye_height);
	ClassDB::bind_method(D_METHOD("get_upright_eye_height"), &HumanoidIK3D::get_upright_eye_height);
	ClassDB::bind_method(D_METHOD("get_head_height"), &HumanoidIK3D::get_head_height);
	ClassDB::bind_method(D_METHOD("get_spine_length"), &HumanoidIK3D::get_spine_length);
	ClassDB::bind_method(D_METHOD("get_head_pivot_for", "eyes", "scale"), &HumanoidIK3D::get_head_pivot_for);

	ADD_PROPERTY(PropertyInfo(Variant::INT, "forward_axis", PROPERTY_HINT_ENUM, "+X,-X,+Z,-Z"), "set_forward_axis", "get_forward_axis");
	ADD_PROPERTY(PropertyInfo(Variant::VECTOR3, "eye_position"), "set_eye_position", "get_eye_position");

	ADD_GROUP("Bones", "");
	static const char *bone_properties[BONE_MAX] = {
		"hips_bone",
		"head_bone",
		"left_shoulder_bone",
		"left_upper_arm_bone",
		"left_lower_arm_bone",
		"left_hand_bone",
		"left_fingers_bone",
		"left_thumb_bone",
		"right_shoulder_bone",
		"right_upper_arm_bone",
		"right_lower_arm_bone",
		"right_hand_bone",
		"right_fingers_bone",
		"right_thumb_bone",
		"left_upper_leg_bone",
		"left_lower_leg_bone",
		"left_foot_bone",
		"right_upper_leg_bone",
		"right_lower_leg_bone",
		"right_foot_bone",
	};
	for (int i = 0; i < BONE_MAX; i++) {
		ADD_PROPERTYI(PropertyInfo(Variant::STRING_NAME, bone_properties[i]), "set_bone_name", "get_bone_name", i);
	}
	ADD_PROPERTY(PropertyInfo(Variant::PACKED_STRING_ARRAY, "spine_bones"), "set_spine_bones", "get_spine_bones");

	ADD_GROUP("Targets", "");
	static const char *target_properties[TARGET_MAX] = {
		"head_target",
		"left_hand_target",
		"right_hand_target",
		"hips_target",
		"chest_target",
		"left_foot_target",
		"right_foot_target",
		"left_knee_target",
		"right_knee_target",
		"left_elbow_target",
		"right_elbow_target",
	};
	for (int i = 0; i < TARGET_MAX; i++) {
		ADD_PROPERTYI(PropertyInfo(Variant::NODE_PATH, target_properties[i], PROPERTY_HINT_NODE_PATH_VALID_TYPES, "Node3D"), "set_target", "get_target", i);
	}

	ADD_GROUP("Solving", "");
	ADD_PROPERTY(PropertyInfo(Variant::BOOL, "freeze_upper_body"), "set_freeze_upper_body", "is_freezing_upper_body");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "spine_twist", PROPERTY_HINT_RANGE, "0,1,0.01"), "set_spine_twist", "get_spine_twist");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "forearm_twist", PROPERTY_HINT_RANGE, "0,1,0.01"), "set_forearm_twist", "get_forearm_twist");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "elbow_from_wrist", PROPERTY_HINT_RANGE, "0,1,0.01"), "set_elbow_from_wrist", "get_elbow_from_wrist");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "max_shoulder_lift", PROPERTY_HINT_RANGE, "0,90,0.1,radians_as_degrees"), "set_max_shoulder_lift", "get_max_shoulder_lift");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "max_shoulder_reach", PROPERTY_HINT_RANGE, "0,90,0.1,radians_as_degrees"), "set_max_shoulder_reach", "get_max_shoulder_reach");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "max_hip_drop", PROPERTY_HINT_RANGE, "0,1,0.01"), "set_max_hip_drop", "get_max_hip_drop");

	BIND_ENUM_CONSTANT(FORWARD_AXIS_PLUS_X);
	BIND_ENUM_CONSTANT(FORWARD_AXIS_MINUS_X);
	BIND_ENUM_CONSTANT(FORWARD_AXIS_PLUS_Z);
	BIND_ENUM_CONSTANT(FORWARD_AXIS_MINUS_Z);
	BIND_ENUM_CONSTANT(BONE_HIPS);
	BIND_ENUM_CONSTANT(BONE_HEAD);
	BIND_ENUM_CONSTANT(BONE_LEFT_SHOULDER);
	BIND_ENUM_CONSTANT(BONE_LEFT_UPPER_ARM);
	BIND_ENUM_CONSTANT(BONE_LEFT_LOWER_ARM);
	BIND_ENUM_CONSTANT(BONE_LEFT_HAND);
	BIND_ENUM_CONSTANT(BONE_LEFT_FINGERS);
	BIND_ENUM_CONSTANT(BONE_LEFT_THUMB);
	BIND_ENUM_CONSTANT(BONE_RIGHT_SHOULDER);
	BIND_ENUM_CONSTANT(BONE_RIGHT_UPPER_ARM);
	BIND_ENUM_CONSTANT(BONE_RIGHT_LOWER_ARM);
	BIND_ENUM_CONSTANT(BONE_RIGHT_HAND);
	BIND_ENUM_CONSTANT(BONE_RIGHT_FINGERS);
	BIND_ENUM_CONSTANT(BONE_RIGHT_THUMB);
	BIND_ENUM_CONSTANT(BONE_LEFT_UPPER_LEG);
	BIND_ENUM_CONSTANT(BONE_LEFT_LOWER_LEG);
	BIND_ENUM_CONSTANT(BONE_LEFT_FOOT);
	BIND_ENUM_CONSTANT(BONE_RIGHT_UPPER_LEG);
	BIND_ENUM_CONSTANT(BONE_RIGHT_LOWER_LEG);
	BIND_ENUM_CONSTANT(BONE_RIGHT_FOOT);
	BIND_ENUM_CONSTANT(BONE_MAX);
	BIND_ENUM_CONSTANT(TARGET_HEAD);
	BIND_ENUM_CONSTANT(TARGET_LEFT_HAND);
	BIND_ENUM_CONSTANT(TARGET_RIGHT_HAND);
	BIND_ENUM_CONSTANT(TARGET_HIPS);
	BIND_ENUM_CONSTANT(TARGET_CHEST);
	BIND_ENUM_CONSTANT(TARGET_LEFT_FOOT);
	BIND_ENUM_CONSTANT(TARGET_RIGHT_FOOT);
	BIND_ENUM_CONSTANT(TARGET_LEFT_KNEE);
	BIND_ENUM_CONSTANT(TARGET_RIGHT_KNEE);
	BIND_ENUM_CONSTANT(TARGET_LEFT_ELBOW);
	BIND_ENUM_CONSTANT(TARGET_RIGHT_ELBOW);
	BIND_ENUM_CONSTANT(TARGET_MAX);
}

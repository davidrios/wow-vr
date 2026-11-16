/**************************************************************************/
/*  humanoid_ik_3d.h                                                      */
/**************************************************************************/
/* Poses a humanoid skeleton from a few tracked points, the way social VR */
/* avatars follow their players: the head, the hands, and optionally the  */
/* hips, chest, feet, knees and elbows from full body trackers.           */
/*                                                                        */
/* Written to be ported into the engine as scene/3d/humanoid_ik_3d.h: it  */
/* uses only the engine's own types and follows its style.                */
/**************************************************************************/

#pragma once

#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/core/math.hpp>
#include <godot_cpp/classes/skeleton3d.hpp>
#include <godot_cpp/classes/skeleton_modifier3d.hpp>
#include <godot_cpp/variant/basis.hpp>
#include <godot_cpp/variant/node_path.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>
#include <godot_cpp/variant/string_name.hpp>
#include <godot_cpp/variant/transform3d.hpp>
#include <godot_cpp/variant/vector3.hpp>

#include <vector>

namespace godot {

class HumanoidIK3D : public SkeletonModifier3D {
	GDCLASS(HumanoidIK3D, SkeletonModifier3D);

public:
	// Which way the model faces in its skeleton's space. Up is always +Y.
	enum ForwardAxis {
		FORWARD_AXIS_PLUS_X,
		FORWARD_AXIS_MINUS_X,
		FORWARD_AXIS_PLUS_Z,
		FORWARD_AXIS_MINUS_Z,
	};

	// The bones this solves, set by name. Leave optional ones empty.
	enum BoneRole {
		BONE_HIPS, // Holds both the spine and the legs.
		BONE_HEAD,
		BONE_LEFT_SHOULDER, // The collarbone. Optional.
		BONE_LEFT_UPPER_ARM,
		BONE_LEFT_LOWER_ARM,
		BONE_LEFT_HAND,
		BONE_LEFT_FINGERS, // A middle finger bone, whose rest says which way the fingers point.
		BONE_LEFT_THUMB, // A thumb bone, whose rest says which side the thumb is on.
		BONE_RIGHT_SHOULDER,
		BONE_RIGHT_UPPER_ARM,
		BONE_RIGHT_LOWER_ARM,
		BONE_RIGHT_HAND,
		BONE_RIGHT_FINGERS,
		BONE_RIGHT_THUMB,
		BONE_LEFT_UPPER_LEG, // Optional, with the rest of the leg.
		BONE_LEFT_LOWER_LEG,
		BONE_LEFT_FOOT,
		BONE_RIGHT_UPPER_LEG,
		BONE_RIGHT_LOWER_LEG,
		BONE_RIGHT_FOOT,
		BONE_MAX,
	};

	// The tracked points. All are Node3Ds in Godot's own convention: -Z forward, +Y up.
	enum Target {
		TARGET_HEAD, // The eyes: an XRCamera3D, say. Required.
		TARGET_LEFT_HAND, // The wrist: -Z along the fingers, +Y out of the back of the hand, as OpenXR's hand joints.
		TARGET_RIGHT_HAND,
		TARGET_HIPS, // The middle of the hip joints. Optional, as are the rest.
		TARGET_CHEST, // The top of the chest, between the shoulders.
		TARGET_LEFT_FOOT, // The ankle: -Z along the toes, +Y up from the top of the foot.
		TARGET_RIGHT_FOOT,
		TARGET_LEFT_KNEE, // Only its position counts: which way the leg bends.
		TARGET_RIGHT_KNEE,
		TARGET_LEFT_ELBOW,
		TARGET_RIGHT_ELBOW,
		TARGET_MAX,
	};

private:
	struct Arm {
		int shoulder = -1;
		int upper = -1;
		int lower = -1;
		int hand = -1;
		bool left = false;
		Vector3 upper_rest; // Shoulder joint to elbow, at rest.
		Vector3 lower_rest; // Elbow to wrist.
		Vector3 hinge_rest; // The elbow's axis.
		Basis hand_rest; // Fingers along X, the back of the hand along Y.
		Vector3 elbow_from_hand; // From the wrist to the elbow, at rest.
		std::vector<int> chain; // Upper arm to hand, with any bones between.
	};

	struct Leg {
		int upper = -1;
		int lower = -1;
		int foot = -1;
	};

	StringName bone_names[BONE_MAX];
	PackedStringArray spine_bone_names;
	NodePath targets[TARGET_MAX];
	ForwardAxis forward_axis = FORWARD_AXIS_PLUS_Z;
	Vector3 eye_position;

	bool freeze_upper_body = true;
	float spine_twist = 0.45;
	float forearm_twist = 0.5;
	float elbow_from_wrist = 0.7;
	float max_shoulder_lift = Math::PI * 28.0 / 180.0;
	float max_shoulder_reach = Math::PI * 20.0 / 180.0;
	float max_hip_drop = 0.65;

	// What the rig was found to be, made again when bones or the skeleton change.
	bool rig_dirty = true;
	bool rig_valid = false;
	int hips = -1;
	int head = -1;
	std::vector<int> spine; // Lowest first, up to the bone the head hangs on.
	Arm arms[2];
	Leg legs[2];
	std::vector<int> frozen;
	Vector3 forward;
	Vector3 right;
	Basis model_to_target; // The model's axes to a target node's.
	Vector3 eye_rest;
	Vector3 head_rest;
	float spine_length = 0.0;
	const Transform3D *knee_targets[2] = { nullptr, nullptr }; // This frame's, while solving.

	void _update_rig();
	int _find(BoneRole p_role) const;
	Node3D *_get_target(Target p_target) const;
	bool _target_in_skeleton(Target p_target, const Transform3D &p_to_skeleton, Transform3D &r_target) const;

	void _place_hips(const Vector3 &p_head_target, const Transform3D *p_hips_target, const Transform3D *p_feet[2], const Transform3D &p_to_skeleton);
	void _solve_legs(const Vector3 p_feet_before[2], const Basis p_feet_turn_before[2], const Transform3D *p_feet[2], const Transform3D &p_to_skeleton);
	void _bend_spine(const Vector3 &p_head_target, const Basis &p_head_turn, const Transform3D *p_chest);
	void _solve_arm(Arm &p_arm, const Transform3D &p_wrist, const Transform3D *p_elbow);
	void _raise_shoulder(const Arm &p_arm, const Vector3 &p_wrist, const Basis &p_chest, float p_arm_length);
	static Vector3 _place_elbow(const Vector3 &p_shoulder, const Vector3 &p_wrist, float p_upper_length, float p_lower_length, const Vector3 &p_hint, const Vector3 &p_down, float p_hint_weight, Vector3 &r_reached, const Vector3 *p_limit_axes, const float *p_limits, int p_limit_count);
	void _two_bone(int p_upper, int p_lower, int p_end, const Vector3 &p_target, const Vector3 &p_pole);

	Vector3 _position(int p_bone) const;
	Basis _turn(int p_bone) const; // The bone's turn from rest, in the skeleton's space.
	Basis _rest_basis(int p_bone) const;
	void _rotate(int p_bone, const Basis &p_turn); // Turns a bone by p_turn in the skeleton's space, about its pivot.
	void _set_global_rotation(int p_bone, const Basis &p_global);
	void _set_global_position(int p_bone, const Vector3 &p_global);
	static Basis _frame(const Vector3 &p_along, const Vector3 &p_side);
	static Basis _arc(const Vector3 &p_from, const Vector3 &p_to);

protected:
	static void _bind_methods();

public:
	void _process_modification_with_delta(double p_delta) override;
	void _skeleton_changed(Skeleton3D *p_old, Skeleton3D *p_new) override;

	void set_bone_name(int p_role, const StringName &p_name);
	StringName get_bone_name(int p_role) const;
	void set_spine_bones(const PackedStringArray &p_names);
	PackedStringArray get_spine_bones() const;
	void set_target(int p_target, const NodePath &p_path);
	NodePath get_target(int p_target) const;

	void set_forward_axis(ForwardAxis p_axis);
	ForwardAxis get_forward_axis() const;
	void set_eye_position(const Vector3 &p_position);
	Vector3 get_eye_position() const;
	void set_freeze_upper_body(bool p_freeze);
	bool is_freezing_upper_body() const;
	void set_spine_twist(float p_share);
	float get_spine_twist() const;
	void set_forearm_twist(float p_share);
	float get_forearm_twist() const;
	void set_elbow_from_wrist(float p_share);
	float get_elbow_from_wrist() const;
	void set_max_shoulder_lift(float p_radians);
	float get_max_shoulder_lift() const;
	void set_max_shoulder_reach(float p_radians);
	float get_max_shoulder_reach() const;
	void set_max_hip_drop(float p_share);
	float get_max_hip_drop() const;

	// What a game needs to fit the model to its player.
	bool is_rig_valid();
	float get_eye_height();
	// How high the eyes would be with the spine straight up, which is what to fit a hunched model to a player by.
	float get_upright_eye_height();
	float get_head_height();
	float get_spine_length();
	Vector3 get_head_pivot_for(const Transform3D &p_eyes, float p_scale);
};

} // namespace godot

VARIANT_ENUM_CAST(HumanoidIK3D::ForwardAxis);
VARIANT_ENUM_CAST(HumanoidIK3D::BoneRole);
VARIANT_ENUM_CAST(HumanoidIK3D::Target);

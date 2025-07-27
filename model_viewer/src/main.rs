pub mod camera_controller;
pub mod camera_views;
pub mod grid;

use std::{f32::consts::PI, path::PathBuf};

use crate::camera_controller::{CameraController, CameraControllerPlugin};
use crate::camera_views::{CameraViewsController, CameraViewsPlugin};
use crate::grid::GridPlugin;

#[cfg(not(target_arch = "wasm32"))]
use bevy::pbr::wireframe::WireframePlugin;
use bevy::prelude::*;
use bevy_asset::{
    UnapprovedPathMode,
    io::{AssetSource, AssetSourceId},
};
use bevy_obj::ObjPlugin;
use wow_vr_lib::{
    m2::{M2Asset, M2Plugin},
    mpq::MpqAssetReader,
};

fn main() {
    let mut plugin = AssetPlugin::default();
    plugin.mode = AssetMode::Unprocessed;
    plugin.unapproved_path_mode = UnapprovedPathMode::Allow;

    let base_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("Data");

    App::new()
        .register_asset_source(
            AssetSourceId::Name("mpq".into()),
            AssetSource::build().with_reader(move || {
                Box::new(MpqAssetReader::new(&[
                    base_path.join("common.MPQ").as_path(),
                    base_path.join("common-2.MPQ").as_path(),
                    base_path.join("expansion.MPQ").as_path(),
                    base_path.join("lichking.MPQ").as_path(),
                    base_path.join("patch.MPQ").as_path(),
                    base_path.join("patch-2.MPQ").as_path(),
                    base_path.join("patch-3.MPQ").as_path(),
                    base_path.join("enUS/locale-enUS.MPQ").as_path(),
                    base_path.join("enUS/patch-enUS.MPQ").as_path(),
                    base_path.join("enUS/patch-enUS-2.MPQ").as_path(),
                    base_path.join("enUS/patch-enUS-3.MPQ").as_path(),
                ]))
            }),
        )
        .add_plugins((
            DefaultPlugins
                .set(ImagePlugin::default_linear())
                .set(plugin),
            #[cfg(not(target_arch = "wasm32"))]
            WireframePlugin::default(),
            ObjPlugin::default(),
            M2Plugin::default(),
            CameraControllerPlugin,
            CameraViewsPlugin,
            GridPlugin,
        ))
        .add_systems(Startup, setup)
        .add_systems(Update, (rotate, test_update))
        .run();
}

#[derive(Component)]
struct Shape;

#[derive(Component, Debug)]
struct M2Component {
    m2: Handle<M2Asset>,
    skin_id: usize,
    is_loaded: bool,
    scale: f32,
    rotation: f32,
}

fn setup(commands: Commands, asset_server: Res<AssetServer>) {
    setup2(commands, asset_server).unwrap()
}

fn setup2(mut commands: Commands, asset_server: Res<AssetServer>) -> Result<()> {
    commands.spawn(M2Component {
        m2: asset_server
            .load("mpq://world/lordaeron/tirisfalglade/passivedoodads/trees/tirisfallgladecanopytree07.m2"),
        skin_id: 0,
        is_loaded: false,
        scale: 0.15,
        rotation: 0.0,
    });

    commands.spawn((
        PointLight {
            shadows_enabled: true,
            intensity: 10_000_000.,
            range: 100.0,
            shadow_depth_bias: 0.2,
            ..default()
        },
        Transform::from_xyz(8.0, 32.0, 8.0),
    ));

    commands.spawn((
        Camera3d::default(),
        Projection::default(),
        Transform::from_xyz(0.0, 2., 10.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y),
        CameraController {
            mouse_key_cursor_grab: MouseButton::Right,
            ..default()
        },
        CameraViewsController,
    ));

    Ok(())
}

fn rotate(
    key_input: Res<ButtonInput<KeyCode>>,
    mut query: Query<&mut Transform, With<Shape>>,
    time: Res<Time>,
) {
    if key_input.pressed(KeyCode::KeyR) {
        let mut rotation = time.delta_secs() * 2.;
        if key_input.pressed(KeyCode::ShiftLeft) || key_input.pressed(KeyCode::ShiftRight) {
            rotation = rotation * -1.;
        }

        for mut transform in &mut query {
            transform.rotate_y(rotation);
        }
    }
}

fn test_update(
    mut query: Query<&mut M2Component>,
    mut m2s: ResMut<Assets<M2Asset>>,
    mut commands: Commands,
) {
    for mut m2component in &mut query {
        if m2component.is_loaded {
            continue;
        }
        if let Some(m2) = m2s.get_mut(&m2component.m2) {
            let meshes = &m2.meshes[&(m2component.skin_id as u32)];
            commands
                .spawn((
                    Transform::from_xyz(0., 0., 0.)
                        .with_scale(Vec3::ONE * m2component.scale)
                        .with_rotation(Quat::from_rotation_y(m2component.rotation * (PI / 180.0))),
                    Visibility::default(),
                    Shape,
                ))
                .with_children(|parent| {
                    for mesh in meshes {
                        parent.spawn((
                            Mesh3d(mesh.mesh.clone()),
                            MeshMaterial3d(
                                m2.materials[m2component.skin_id][&mesh.material].clone(),
                            ),
                        ));
                    }
                });
            m2component.is_loaded = true;
        }
    }
}

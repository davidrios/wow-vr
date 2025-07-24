use std::{f32::consts::PI, path::PathBuf};

#[cfg(not(target_arch = "wasm32"))]
use bevy::pbr::wireframe::{WireframeConfig, WireframePlugin};
use bevy::{
    color::palettes::basic::SILVER,
    prelude::*,
    render::{
        render_asset::RenderAssetUsages,
        render_resource::{Extent3d, TextureDimension, TextureFormat},
    },
};
use bevy_asset::UnapprovedPathMode;
use bevy_obj::ObjPlugin;
use wow_vr_lib::{m2, mpq::MPQCollection};

fn main() {
    let mut plugin = AssetPlugin::default();
    plugin.mode = AssetMode::Unprocessed;
    plugin.unapproved_path_mode = UnapprovedPathMode::Allow;

    App::new()
        .add_plugins((
            DefaultPlugins
                .set(ImagePlugin::default_nearest())
                .set(plugin),
            #[cfg(not(target_arch = "wasm32"))]
            WireframePlugin::default(),
            ObjPlugin::default(),
        ))
        .add_systems(Startup, setup)
        .add_systems(
            Update,
            (
                rotate,
                #[cfg(not(target_arch = "wasm32"))]
                toggle_wireframe,
            ),
        )
        .run();
}

/// A marker component for our shapes so we can query them separately from the ground plane
#[derive(Component)]
struct Shape;

const SHAPES_X_EXTENT: f32 = 14.0;
const EXTRUSION_X_EXTENT: f32 = 16.0;
const Z_EXTENT: f32 = 5.0;

fn setup(
    commands: Commands,
    meshes: ResMut<Assets<Mesh>>,
    images: ResMut<Assets<Image>>,
    materials: ResMut<Assets<StandardMaterial>>,
    asset_server: Res<AssetServer>,
) {
    setup2(commands, meshes, images, materials, asset_server).unwrap()
}

fn setup2(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut images: ResMut<Assets<Image>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    asset_server: Res<AssetServer>,
) -> Result<()> {
    let debug_material = materials.add(StandardMaterial {
        base_color_texture: Some(images.add(uv_debug_texture())),
        ..default()
    });

    let base_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("Data");

    let mut mpq_col = MPQCollection::load(&vec![
        base_path.join("common.MPQ").as_path(),
        base_path.join("common-2.MPQ").as_path(),
        base_path.join("expansion.MPQ").as_path(),
        base_path.join("lichking.MPQ").as_path(),
        base_path.join("patch.MPQ").as_path(),
        base_path.join("patch-2.MPQ").as_path(),
        base_path.join("patch-3.MPQ").as_path(),
    ])?;

    let fname = "world/azeroth/bootybay/passivedoodad/fishingbox/fishingbox.m2";
    let mut m2_obj = m2::load_from_mpq(&mut mpq_col, fname)?;
    m2_obj.load_textures(&mut mpq_col)?;
    let m2_mat = materials.add(StandardMaterial {
        base_color_texture: Some(images.add(*m2_obj.textures.pop_front().unwrap())),
        ..default()
    });

    let fname2 = "World\\GENERIC\\HUMAN\\PASSIVE DOODADS\\Bottles\\Bottle01.m2";
    let mut m2_obj2 = m2::load_from_mpq(&mut mpq_col, fname2)?;
    m2_obj2.load_textures(&mut mpq_col)?;
    let m2_mat2 = materials.add(StandardMaterial {
        base_color_texture: Some(images.add(*m2_obj2.textures.pop_front().unwrap())),
        ..default()
    });

    let fishingbox = Mesh3d::from(
        asset_server.load(
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("..")
                .join("Data")
                .join("exports/world/azeroth/bootybay/passivedoodad/fishingbox/fishingbox.obj"),
        ),
    );
    commands.spawn((
        fishingbox,
        MeshMaterial3d(debug_material.clone()),
        Transform::from_xyz(
            -SHAPES_X_EXTENT / 2. + 6 as f32 / (7 - 1) as f32 * SHAPES_X_EXTENT,
            2.0,
            Z_EXTENT / 2.,
        )
        .with_rotation(Quat::from_rotation_x(-PI / 4.)),
        Shape,
    ));

    let shapes = [
        meshes.add(Cuboid::default()),
        meshes.add(Tetrahedron::default()),
        meshes.add(Capsule3d::default()),
        meshes.add(Torus::default()),
        // meshes.add(Cylinder::default()),
        meshes.add(m2_obj.to_mesh()?),
        // meshes.add(Cone::default()),
        meshes.add(m2_obj2.to_mesh()?),
        // meshes.add(ConicalFrustum::default()),
        meshes.add(Sphere::default().mesh().ico(5)?),
        // meshes.add(Sphere::default().mesh().uv(32, 18)),
    ];

    let shape_textures = [
        &debug_material,
        &debug_material,
        &debug_material,
        &debug_material,
        // &debug_material,
        &m2_mat,
        // &debug_material,
        &m2_mat2,
        &debug_material,
    ];

    let extrusions = [
        meshes.add(Extrusion::new(Rectangle::default(), 1.)),
        meshes.add(Extrusion::new(Capsule2d::default(), 1.)),
        meshes.add(Extrusion::new(Annulus::default(), 1.)),
        meshes.add(Extrusion::new(Circle::default(), 1.)),
        meshes.add(Extrusion::new(Ellipse::default(), 1.)),
        meshes.add(Extrusion::new(RegularPolygon::default(), 1.)),
        meshes.add(Extrusion::new(Triangle2d::default(), 1.)),
    ];

    let num_shapes = shapes.len() + 1;

    for (i, shape) in shapes.into_iter().enumerate() {
        commands.spawn((
            Mesh3d(shape),
            MeshMaterial3d(shape_textures[i].clone()),
            Transform::from_xyz(
                -SHAPES_X_EXTENT / 2. + i as f32 / (num_shapes - 1) as f32 * SHAPES_X_EXTENT,
                2.0,
                Z_EXTENT / 2.,
            ),
            // .with_rotation(Quat::from_rotation_x(-PI / 4.)),
            Shape,
        ));
    }

    let num_extrusions = extrusions.len();

    for (i, shape) in extrusions.into_iter().enumerate() {
        commands.spawn((
            Mesh3d(shape),
            MeshMaterial3d(debug_material.clone()),
            Transform::from_xyz(
                -EXTRUSION_X_EXTENT / 2.
                    + i as f32 / (num_extrusions - 1) as f32 * EXTRUSION_X_EXTENT,
                2.0,
                -Z_EXTENT / 2.,
            ),
            // .with_rotation(Quat::from_rotation_x(-PI / 4.)),
            Shape,
        ));
    }

    commands.spawn((
        PointLight {
            shadows_enabled: true,
            intensity: 10_000_000.,
            range: 100.0,
            shadow_depth_bias: 0.2,
            ..default()
        },
        Transform::from_xyz(8.0, 16.0, 8.0),
    ));

    // ground plane
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(50.0, 50.0).subdivisions(10))),
        MeshMaterial3d(materials.add(Color::from(SILVER))),
    ));

    commands.spawn((
        Camera3d::default(),
        Transform::from_xyz(0.0, 7., 14.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y),
    ));

    #[cfg(not(target_arch = "wasm32"))]
    commands.spawn((
        Text::new("Press space to toggle wireframes"),
        Node {
            position_type: PositionType::Absolute,
            top: Val::Px(12.0),
            left: Val::Px(12.0),
            ..default()
        },
    ));

    Ok(())
}

fn rotate(mut query: Query<&mut Transform, With<Shape>>, time: Res<Time>) {
    for mut transform in &mut query {
        transform.rotate_y(time.delta_secs() / 2.);
    }
}

/// Creates a colorful test pattern
fn uv_debug_texture() -> Image {
    const TEXTURE_SIZE: usize = 8;

    let mut palette: [u8; 32] = [
        255, 102, 159, 255, 255, 159, 102, 255, 236, 255, 102, 255, 121, 255, 102, 255, 102, 255,
        198, 255, 102, 198, 255, 255, 121, 102, 255, 255, 236, 102, 255, 255,
    ];

    let mut texture_data = [0; TEXTURE_SIZE * TEXTURE_SIZE * 4];
    for y in 0..TEXTURE_SIZE {
        let offset = TEXTURE_SIZE * y * 4;
        texture_data[offset..(offset + TEXTURE_SIZE * 4)].copy_from_slice(&palette);
        palette.rotate_right(4);
    }

    Image::new_fill(
        Extent3d {
            width: TEXTURE_SIZE as u32,
            height: TEXTURE_SIZE as u32,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        &texture_data,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::RENDER_WORLD,
    )
}

#[cfg(not(target_arch = "wasm32"))]
fn toggle_wireframe(
    mut wireframe_config: ResMut<WireframeConfig>,
    keyboard: Res<ButtonInput<KeyCode>>,
) {
    if keyboard.just_pressed(KeyCode::Space) {
        wireframe_config.global = !wireframe_config.global;
    }
}

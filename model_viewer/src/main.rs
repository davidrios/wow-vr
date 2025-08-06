pub mod camera_controller;
pub mod camera_views;
pub mod grid;

use std::{f32::consts::PI, path::PathBuf};

use crate::camera_controller::{CameraController, CameraControllerPlugin};
use crate::camera_views::{CameraViewsController, CameraViewsPlugin};
use crate::grid::GridPlugin;

use bevy::input::keyboard::keyboard_input_system;
#[cfg(not(target_arch = "wasm32"))]
use bevy::pbr::wireframe::WireframePlugin;
use bevy::prelude::*;
use bevy::render::camera::Viewport;
use bevy::render::view::RenderLayers;
use bevy::window::PrimaryWindow;
use bevy_asset::{
    UnapprovedPathMode,
    io::{AssetSource, AssetSourceId},
};
use bevy_egui::input::egui_wants_any_keyboard_input;
use bevy_egui::{
    EguiContext, EguiContexts, EguiGlobalSettings, EguiPlugin, EguiPrimaryContextPass,
    PrimaryEguiContext, egui,
};
use bevy_obj::ObjPlugin;
use egui_extras::TableBuilder;
use wow_vr_lib::mpq::MpqCollection;
use wow_vr_lib::{
    m2::{M2Asset, M2Plugin},
    mpq::MpqAssetReader,
};

const MPQ_FILES: [&str; 13] = [
    "common.MPQ",
    "common-2.MPQ",
    "expansion.MPQ",
    "lichking.MPQ",
    "patch.MPQ",
    "patch-2.MPQ",
    "patch-3.MPQ",
    "enUS/locale-enUS.MPQ",
    "enUS/expansion-locale-enUS.MPQ",
    "enUS/lichking-locale-enUS.MPQ",
    "enUS/patch-enUS.MPQ",
    "enUS/patch-enUS-2.MPQ",
    "enUS/patch-enUS-3.MPQ",
];

#[derive(Resource)]
pub struct MpqFileList(Vec<String>);

#[derive(Resource)]
pub struct SelectedModel {
    path: String,
    // entity: Option<Entity>,
}

fn main() {
    let mut plugin = AssetPlugin::default();
    plugin.mode = AssetMode::Unprocessed;
    plugin.unapproved_path_mode = UnapprovedPathMode::Allow;

    let base_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("Data");

    let mpq_files: Vec<PathBuf> = MPQ_FILES.iter().map(|p| base_path.join(p)).collect();
    let mpq_collection = MpqCollection::load(mpq_files).unwrap();
    let mut file_list: Vec<String> = mpq_collection
        .file_list()
        .iter()
        .filter(|f| f.ends_with(".m2") || f.ends_with(".M2"))
        .map(|f| (*f).to_owned())
        .collect();
    file_list.sort();

    App::new()
        .insert_resource(MpqFileList(file_list))
        .insert_resource(SelectedModel {
            path: "".into(),
            // entity: None,
        })
        .register_asset_source(
            AssetSourceId::Name("mpq".into()),
            AssetSource::build().with_reader(move || {
                Box::new(MpqAssetReader::new(
                    MPQ_FILES.iter().map(|p| base_path.join(p)).collect(),
                ))
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
            EguiPlugin::default(),
        ))
        .add_systems(Startup, setup)
        .add_systems(EguiPrimaryContextPass, draw_ui)
        .add_systems(
            Update,
            keyboard_input_system.run_if(not(egui_wants_any_keyboard_input)),
        )
        .add_systems(Update, (rotate, spawn_model))
        .run();
}

#[derive(Component)]
struct Shape;

#[derive(Component, Debug)]
struct M2Component {
    m2: Option<Handle<M2Asset>>,
    skin_id: usize,
    scale: f32,
    rotation: f32,
    entity: Option<Entity>,
}

fn setup(mut commands: Commands, mut egui_global_settings: ResMut<EguiGlobalSettings>) {
    egui_global_settings.auto_create_primary_context = false;

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

    commands.spawn((
        PrimaryEguiContext,
        Camera2d,
        RenderLayers::none(),
        Camera {
            order: 1,
            ..default()
        },
    ));

    commands.spawn(M2Component {
        // m2: asset_server.load(format!("mpq://{}", new_selection)),
        m2: None,
        entity: None,
        skin_id: 0,
        scale: 1.0,
        rotation: 0.0,
    });
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

fn spawn_model(
    mut m2component: Single<&mut M2Component>,
    mut m2s: ResMut<Assets<M2Asset>>,
    mut commands: Commands,
) {
    if m2component.m2.is_none() || m2component.entity.is_some() {
        return;
    }

    if let Some(m2) = m2s.get_mut(m2component.m2.as_mut().unwrap()) {
        dbg!(&m2);
        let meshes = &m2.meshes[&(m2component.skin_id as u32)];
        let spawned = commands
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
                        MeshMaterial3d(m2.materials[m2component.skin_id][&mesh.material].clone()),
                    ));
                }
            })
            .id();

        m2component.entity = Some(spawned);
    }
}

fn draw_ui(
    mut contexts: EguiContexts,
    mut commands: Commands,
    mut camera: Single<&mut Camera, Without<EguiContext>>,
    window: Single<&mut Window, With<PrimaryWindow>>,
    mpq_file_list: Res<MpqFileList>,
    mut hovered: Local<String>,
    mut selected: ResMut<SelectedModel>,
    mut filter: Local<String>,
    mut filtered: Local<Vec<String>>,
    asset_server: Res<AssetServer>,
    mut m2component: Single<&mut M2Component>,
) -> Result {
    let ctx = contexts.ctx_mut()?;

    if filter.len() == 0 && filtered.len() == 0 && mpq_file_list.0.len() > 0 {
        *filtered = mpq_file_list.0.iter().map(String::to_owned).collect();
    }

    let mut new_selection: Option<&str> = None;
    let mut is_hovered = false;

    let mut left = egui::SidePanel::left("left_panel")
        .resizable(true)
        .min_width(400.0)
        .show(ctx, |ui| {
            let available_height = ui.available_height();
            ui.horizontal(|ui| {
                ui.label("Filter:");
                ui.add(egui::TextEdit::singleline(&mut *filter).desired_width(f32::INFINITY));
                if ui.input(|i| i.keys_down.len() > 0) {
                    *filtered = mpq_file_list
                        .0
                        .iter()
                        .filter(|f| f.contains(&*filter))
                        .map(String::to_owned)
                        .collect();
                }
            });
            let _table = TableBuilder::new(ui)
                .striped(true)
                .resizable(false)
                .column(egui_extras::Column::remainder())
                .min_scrolled_height(0.0)
                .max_scroll_height(available_height)
                .sense(egui::Sense::click())
                .header(20.0, |mut header| {
                    header.col(|ui| {
                        ui.strong("Model list");
                    });
                })
                .body(|body| {
                    // for i in &mpq_file_list.0 {
                    body.rows(18.0, filtered.len(), |mut row| {
                        let i = &filtered[row.index()];
                        if *hovered == *i {
                            row.set_hovered(true);
                        }
                        let self_selected = *selected.path == *i;
                        if self_selected {
                            row.set_selected(true);
                        }
                        row.col(|ui| {
                            let label = ui.label(i);
                            if label.hovered() {
                                is_hovered = true;
                                *hovered = i.clone();
                            }
                            if label.clicked() {
                                if self_selected {
                                    new_selection = Some("".into());
                                } else {
                                    new_selection = Some(i);
                                }
                            }
                            label.on_hover_cursor(egui::CursorIcon::Default);
                        });
                        if row.response().clicked() {
                            if self_selected {
                                new_selection = Some("".into());
                            } else {
                                new_selection = Some(i);
                            }
                        }
                    });
                    // }
                });

            if !is_hovered {
                if hovered.len() > 0 {
                    *hovered = "".into();
                }
            }

            ui.allocate_rect(ui.available_rect_before_wrap(), egui::Sense::hover());
        })
        .response
        .rect
        .width();

    if let Some(new_selection) = new_selection {
        if new_selection != selected.path {
            selected.path = new_selection.into();
            if let Some(id) = m2component.entity {
                commands.entity(id).despawn();
                m2component.entity = None;
            }
            if new_selection != "" {
                m2component.m2 = Some(asset_server.load(format!("mpq://{}", new_selection)));
            }
        }
    }

    left *= window.scale_factor();

    let pos = UVec2::new(left as u32, 0);
    let size = UVec2::new(window.physical_width(), window.physical_height()) - pos;

    camera.viewport = Some(Viewport {
        physical_position: pos,
        physical_size: size,
        ..default()
    });

    Ok(())
}

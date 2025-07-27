#[cfg(not(target_arch = "wasm32"))]
use bevy::pbr::wireframe::WireframeConfig;
use bevy::prelude::*;
use std::f32::consts::*;

pub struct GridPlugin;

impl Plugin for GridPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Startup, setup);
        app.add_systems(
            Update,
            (
                draw_grid,
                #[cfg(not(target_arch = "wasm32"))]
                toggle_wireframe,
            ),
        );
    }
}

fn setup(
    mut commands: Commands,
    mut config_store: ResMut<GizmoConfigStore>,
    #[cfg(not(target_arch = "wasm32"))] mut wireframe_config: ResMut<WireframeConfig>,
) {
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
    #[cfg(not(target_arch = "wasm32"))]
    {
        wireframe_config.default_color = Color::linear_rgba(0.1, 0.1, 0.1, 0.1);
    }

    for (_, config, _) in config_store.iter_mut() {
        config.line.width = 0.1;
    }
}

fn draw_grid(mut gizmos: Gizmos) {
    gizmos.grid(
        Quat::from_rotation_x(PI / 2.),
        UVec2::splat(100),
        Vec2::new(2., 2.),
        LinearRgba::gray(0.65),
    );
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

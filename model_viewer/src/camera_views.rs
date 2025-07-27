use bevy::{prelude::*, render::camera::ScalingMode};

pub struct CameraViewsPlugin;

impl Plugin for CameraViewsPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Update, (update_camera, update_projection));
    }
}

#[derive(Component)]
pub struct CameraViewsController;

fn update_camera(
    key_input: Res<ButtonInput<KeyCode>>,
    mut query: Query<&mut Transform, (With<CameraViewsController>, With<Camera>)>,
) {
    let Ok(mut transform) = query.single_mut() else {
        return;
    };

    if key_input.just_pressed(KeyCode::Digit1) {
        *transform = Transform::from_xyz(0.0, 2., 10.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y);
    }
    if key_input.just_pressed(KeyCode::Digit2) {
        *transform = Transform::from_xyz(0.0, 2., -10.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y);
    }
    if key_input.just_pressed(KeyCode::Digit3) {
        *transform = Transform::from_xyz(5.0, 5., 10.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y);
    }
    if key_input.just_pressed(KeyCode::Digit4) {
        *transform = Transform::from_xyz(10.0, 2., 0.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y);
    }
    if key_input.just_pressed(KeyCode::Digit5) {
        *transform = Transform::from_xyz(-10.0, 2., 0.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y);
    }
    if key_input.just_pressed(KeyCode::Digit9) {
        *transform = Transform::from_xyz(0.0, 10., 0.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y);
    }
    if key_input.just_pressed(KeyCode::Digit0) {
        *transform = Transform::from_xyz(0.0, -10., 0.0).looking_at(Vec3::new(0., 1., 0.), Vec3::Y);
    }
}

fn update_projection(
    key_input: Res<ButtonInput<KeyCode>>,
    mut query: Query<&mut Projection, (With<CameraViewsController>, With<Camera>)>,
    mut is_ortho: Local<bool>,
) {
    if !key_input.just_pressed(KeyCode::KeyO) {
        return;
    }
    let Ok(mut projection) = query.single_mut() else {
        return;
    };

    *is_ortho = !*is_ortho;

    if *is_ortho {
        *projection = Projection::Orthographic(OrthographicProjection {
            scale: 0.01,
            near: 0.0,
            far: 100.0,
            viewport_origin: Vec2::new(0.5, 0.5),
            scaling_mode: ScalingMode::WindowSize,
            area: Rect::new(-1.0, -1.0, 1.0, 1.0),
        });
    } else {
        *projection = Projection::Perspective(PerspectiveProjection::default());
    }
}

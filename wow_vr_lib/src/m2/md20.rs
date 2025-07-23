use std::{
    io::{Cursor, Seek},
    sync::Arc,
};

use bevy::math::{U8Vec4, Vec2, Vec3};
use bitflags::bitflags;
use byteorder::{LittleEndian, ReadBytesExt};
use custom_debug::Debug;

use crate::{common_types::CAaBox, errors::Error, utils};

use super::Magic;

bitflags! {
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct Flags: u32 {
        const TILT_X = 0x1;
        const TILT_Y = 0x2;
        const USE_TEXTURE_COMBINER_COMBOS = 0x8;
        const ANIM_IS_CHUNKED = 0x200000;
    }
}

#[derive(Debug)]
pub struct ArrayPositions {
    model_name: utils::OffsetSize,
    global_loops: utils::OffsetSize,
    animations: utils::OffsetSize,
    animation_lookup: utils::OffsetSize,
    bones: utils::OffsetSize,
    vertices: utils::OffsetSize,
    colors: utils::OffsetSize,
    textures: utils::OffsetSize,
    texture_weights: utils::OffsetSize,
    texture_transforms: utils::OffsetSize,
    replaceable_texture_lookups: utils::OffsetSize,
    materials: utils::OffsetSize,
    texture_combos: utils::OffsetSize,
    transparency_lookups: utils::OffsetSize,
    texture_transform_lookups: utils::OffsetSize,
    bone_indices: utils::OffsetSize,
    bone_combos: utils::OffsetSize,
    texture_coords_combos: utils::OffsetSize,
    collision_indices: utils::OffsetSize,
    collision_positions: utils::OffsetSize,
    collision_facenormals: utils::OffsetSize,
    attachments: utils::OffsetSize,
    attachments_lookup_table: utils::OffsetSize,
    events: utils::OffsetSize,
    lights: utils::OffsetSize,
    cameras: utils::OffsetSize,
    camera_lookup_table: utils::OffsetSize,
    ribbon_emitters: utils::OffsetSize,
    particle_emitters: utils::OffsetSize,
    texture_combiner_combos: Option<utils::OffsetSize>,
}

#[derive(Debug)]
pub struct VerticeData {
    #[debug(with = utils::buf_len_fmt)]
    vertices: Vec<Vec3>,
    #[debug(with = utils::buf_len_fmt)]
    uv: Vec<Vec2>,
    #[debug(with = utils::buf_len_fmt)]
    uv2: Vec<Vec2>,
    #[debug(with = utils::buf_len_fmt)]
    normals: Vec<Vec3>,
    #[debug(with = utils::buf_len_fmt)]
    bone_weights: Vec<U8Vec4>,
    #[debug(with = utils::buf_len_fmt)]
    bone_indices: Vec<U8Vec4>,
}

impl VerticeData {
    pub fn vertices(&mut self) -> &Vec<Vec3> {
        &self.vertices
    }
    pub fn uv(&mut self) -> &Vec<Vec2> {
        &self.uv
    }
    pub fn uv2(&mut self) -> &Vec<Vec2> {
        &self.uv2
    }
    pub fn normals(&mut self) -> &Vec<Vec3> {
        &self.normals
    }
    pub fn bone_weights(&mut self) -> &Vec<U8Vec4> {
        &self.bone_weights
    }
    pub fn bone_indices(&mut self) -> &Vec<U8Vec4> {
        &self.bone_indices
    }
}

#[derive(Debug)]
pub struct ArrayData {
    vertice_data: Option<VerticeData>,
}

#[derive(Debug)]
pub struct Data {
    #[debug(with = utils::buf_len_fmt)]
    raw_data: Arc<Vec<u8>>,
    offset: u64,
    version: u32,
    model_name: Option<String>,
    flags: Flags,
    array_positions: ArrayPositions,
    array_data: ArrayData,
    view_count: usize,
    bounding_box: CAaBox,
    bounding_sphere_radius: f32,
    collision_box: CAaBox,
    collision_sphere_radius: f32,
}

impl Data {
    pub fn version(&self) -> u32 {
        self.version
    }

    pub fn model_name(&mut self) -> &str {
        self.model_name.get_or_insert(
            read_string_from_buffer(&self.raw_data, &self.array_positions.model_name)
                .unwrap_or_else(|_| String::from("<ERROR>")),
        )
    }

    pub fn flags(&self) -> &Flags {
        &self.flags
    }

    pub fn view_count(&self) -> usize {
        self.view_count
    }

    pub fn vertice_data(&mut self) -> Result<&mut VerticeData, Error> {
        Ok(self.array_data.vertice_data.get_or_insert({
            let vertice_count = self.array_positions.vertices.size as usize / 3;
            let mut data_c = Cursor::new(self.raw_data.as_ref());
            data_c
                .seek_relative(self.offset as i64 + self.array_positions.vertices.offset as i64)?;
            let mut vertices = Vec::with_capacity(vertice_count);
            let mut normals = Vec::with_capacity(vertice_count);
            let mut uv = Vec::with_capacity(vertice_count);
            let mut uv2 = Vec::with_capacity(vertice_count);
            let mut bone_weights = Vec::with_capacity(vertice_count);
            let mut bone_indices = Vec::with_capacity(vertice_count);
            for _ in 0..vertice_count {
                vertices.push(utils::read_vec3(&mut data_c)?);
                bone_weights.push(utils::read_u8vec4(&mut data_c)?);
                bone_indices.push(utils::read_u8vec4(&mut data_c)?);
                normals.push(utils::read_vec3(&mut data_c)?);
                uv.push(utils::read_vec2(&mut data_c)?);
                uv2.push(utils::read_vec2(&mut data_c)?);
            }

            VerticeData {
                vertices,
                uv,
                uv2,
                normals,
                bone_weights,
                bone_indices,
            }
        }))
    }

    pub fn triangles(&mut self) -> Result<Vec<u32>, Error> {
        Ok(vec![
            0, 3, 1, // t1
            1, 3, 2, // t2
        ])
    }
}

pub fn parse_chunk(raw_data: &Arc<Vec<u8>>, ofs: u64) -> Result<Box<Data>, Error> {
    let raw_data = Arc::clone(raw_data);

    let mut data_c = Cursor::new(raw_data.as_ref());
    let magic: Magic = data_c.read_u32::<LittleEndian>()?.try_into()?;
    if magic != Magic::MD20 {
        return Err(Error::InvalidMagicValue(magic as u32));
    }

    let version = data_c.read_u32::<LittleEndian>()?;
    let model_name = get_offsetsize(&mut data_c)?;
    let flags = Flags::from_bits_retain(data_c.read_u32::<LittleEndian>()?);
    let global_loops = get_offsetsize(&mut data_c)?;
    let animations = get_offsetsize(&mut data_c)?;
    let animation_lookup = get_offsetsize(&mut data_c)?;
    let bones = get_offsetsize(&mut data_c)?;
    let bone_indices = get_offsetsize(&mut data_c)?;
    let vertices = get_offsetsize(&mut data_c)?;
    let view_count = data_c.read_u32::<LittleEndian>()? as usize;
    let colors = get_offsetsize(&mut data_c)?;
    let textures = get_offsetsize(&mut data_c)?;
    let texture_weights = get_offsetsize(&mut data_c)?;
    let texture_transforms = get_offsetsize(&mut data_c)?;
    let replaceable_texture_lookups = get_offsetsize(&mut data_c)?;
    let materials = get_offsetsize(&mut data_c)?;
    let bone_combos = get_offsetsize(&mut data_c)?;
    let texture_combos = get_offsetsize(&mut data_c)?;
    let texture_coords_combos = get_offsetsize(&mut data_c)?;
    let transparency_lookups = get_offsetsize(&mut data_c)?;
    let texture_transform_lookups = get_offsetsize(&mut data_c)?;
    let bounding_box = CAaBox::from(&mut data_c)?;
    let bounding_sphere_radius = data_c.read_f32::<LittleEndian>()?;
    let collision_box = CAaBox::from(&mut data_c)?;
    let collision_sphere_radius = data_c.read_f32::<LittleEndian>()?;
    let collision_indices = get_offsetsize(&mut data_c)?;
    let collision_positions = get_offsetsize(&mut data_c)?;
    let collision_facenormals = get_offsetsize(&mut data_c)?;
    let attachments = get_offsetsize(&mut data_c)?;
    let attachments_lookup_table = get_offsetsize(&mut data_c)?;
    let events = get_offsetsize(&mut data_c)?;
    let lights = get_offsetsize(&mut data_c)?;
    let cameras = get_offsetsize(&mut data_c)?;
    let camera_lookup_table = get_offsetsize(&mut data_c)?;
    let ribbon_emitters = get_offsetsize(&mut data_c)?;
    let particle_emitters = get_offsetsize(&mut data_c)?;
    let texture_combiner_combos = if flags.contains(Flags::USE_TEXTURE_COMBINER_COMBOS) {
        Some(get_offsetsize(&mut data_c)?)
    } else {
        None
    };

    Ok(Box::new(Data {
        raw_data,
        offset: ofs,
        version,
        model_name: None,
        flags,
        bounding_box,
        bounding_sphere_radius,
        collision_box,
        collision_sphere_radius,
        view_count,
        array_positions: ArrayPositions {
            model_name,
            global_loops,
            animations,
            animation_lookup,
            bones,
            bone_indices,
            vertices,
            colors,
            textures,
            texture_weights,
            texture_transforms,
            replaceable_texture_lookups,
            materials,
            bone_combos,
            texture_combos,
            texture_coords_combos,
            transparency_lookups,
            texture_transform_lookups,
            collision_indices,
            collision_positions,
            collision_facenormals,
            attachments,
            attachments_lookup_table,
            events,
            lights,
            cameras,
            camera_lookup_table,
            ribbon_emitters,
            particle_emitters,
            texture_combiner_combos,
        },
        array_data: ArrayData { vertice_data: None },
    }))
}

fn get_offsetsize(data_c: &mut Cursor<&Vec<u8>>) -> Result<utils::OffsetSize, Error> {
    Ok(utils::OffsetSize {
        size: data_c.read_u32::<LittleEndian>()? as u64,
        // const modelNameOfs = this.data.readUInt32LE();
        offset: data_c.read_u32::<LittleEndian>()? as u64,
    })
}

pub fn read_string_from_buffer(buffer: &[u8], os: &utils::OffsetSize) -> Result<String, Error> {
    let mut name_buf = (&buffer[os.offset as usize..(os.offset + os.size) as usize]).to_vec();
    if name_buf.last() == Some(&0) {
        name_buf.pop();
    }

    Ok(String::from_utf8(name_buf)?)
}

use std::{io::Cursor, sync::Arc};

use bitflags::bitflags;
use byteorder::{LittleEndian, ReadBytesExt};
use custom_debug::Debug;

use crate::{common_types::CAaBox, utils};

use super::{Error, Magic};

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
pub struct OffsetSize {
    offset: u64,
    size: u64,
}

#[derive(Debug)]
pub struct ArrayPositions {
    model_name: OffsetSize,
    global_loops: OffsetSize,
    animations: OffsetSize,
    animation_lookup: OffsetSize,
    bones: OffsetSize,
    vertices: OffsetSize,
    view_count: u32,
    colors: OffsetSize,
    textures: OffsetSize,
    texture_weights: OffsetSize,
    texture_transforms: OffsetSize,
    replaceable_texture_lookups: OffsetSize,
    materials: OffsetSize,
    texture_combos: OffsetSize,
    transparency_lookups: OffsetSize,
    texture_transform_lookups: OffsetSize,
    bone_indices: OffsetSize,
    bone_combos: OffsetSize,
    texture_coords_combos: OffsetSize,
    collision_indices: OffsetSize,
    collision_positions: OffsetSize,
    collision_facenormals: OffsetSize,
    attachments: OffsetSize,
    attachments_lookup_table: OffsetSize,
    events: OffsetSize,
    lights: OffsetSize,
    cameras: OffsetSize,
    camera_lookup_table: OffsetSize,
    ribbon_emitters: OffsetSize,
    particle_emitters: OffsetSize,
    texture_combiner_combos: Option<OffsetSize>,
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

    pub fn vertices(&mut self) -> Result<Vec<[f32; 3]>, Error> {
        Ok(vec![
            [0.0, 0.0, 0.0],
            [1.0, 2.0, 0.0],
            [2.0, 2.0, 0.0],
            [1.0, 0.0, 0.0],
        ])
    }

    pub fn uv_0(&mut self) -> Result<Vec<[f32; 2]>, Error> {
        Ok(vec![[0.0, 1.0], [0.5, 0.0], [1.0, 0.0], [0.5, 1.0]])
    }

    pub fn normals(&mut self) -> Result<Vec<[f32; 3]>, Error> {
        Ok(vec![
            [0.0, 0.0, 1.0],
            [0.0, 0.0, 1.0],
            [0.0, 0.0, 1.0],
            [0.0, 0.0, 1.0],
        ])
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
    let view_count = data_c.read_u32::<LittleEndian>()?;
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
        array_positions: ArrayPositions {
            model_name,
            global_loops,
            animations,
            animation_lookup,
            bones,
            bone_indices,
            vertices,
            view_count,
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
    }))
}

fn get_offsetsize(data_c: &mut Cursor<&Vec<u8>>) -> Result<OffsetSize, Error> {
    Ok(OffsetSize {
        size: data_c.read_u32::<LittleEndian>()? as u64,
        // const modelNameOfs = this.data.readUInt32LE();
        offset: data_c.read_u32::<LittleEndian>()? as u64,
    })
}

pub fn read_string_from_buffer(buffer: &[u8], os: &OffsetSize) -> Result<String, Error> {
    let mut name_buf = (&buffer[os.offset as usize..(os.offset + os.size) as usize]).to_vec();
    if name_buf.last() == Some(&0) {
        name_buf.pop();
    }

    Ok(String::from_utf8(name_buf)?)
}

// async parseChunk
pub fn parse_rest(data_c: &mut Cursor<&Vec<u8>>, fname: &str) -> Result<(), Error> {
    // const listfile = core.view.casc.listfile;
    // let baseName = listfile.getByID(this.fileID);
    // baseName = baseName.substring(0, baseName.length - 3);
    let base_name = &fname[..fname.len() - 3];
    dbg!(base_name);
    // this.skins = new Array(this.viewCount);
    // for (let i = 0; i < this.viewCount; i++)
    // 	this.skins[i] = new Skin(listfile.getByFilename(`${baseName}${i.toString().padStart(2, 0)}.skin`));
    //
    // // for (let i = 0, n = this.textures.length; i < n; i++)
    // // 	this.textures[i].fileDataID = this.data.readUInt32LE();
    //
    // this.skeletonFileID = listfile.getByFilename(`${baseName}.skel`);
    Ok(())
}

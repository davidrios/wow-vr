use bevy::{
    image::Image,
    math::{Vec2, Vec3},
    render::{
        mesh,
        render_resource::{Extent3d, TextureDimension, TextureFormat},
    },
};
use std::io::Cursor;
use wow_m2::{
    blp::{BlpCompressionType, BlpPixelFormat},
    common::{C2Vector, C3Vector},
};

use custom_debug::Debug;

use crate::{
    errors::{Error, Result},
    mpq::{MpqCollection, ReadFromMpq},
};

#[derive(Debug)]
pub struct M2 {
    pub model: Box<wow_m2::M2Model>,
    pub skins: Vec<Box<wow_m2::OldSkin>>,
}

fn c3_to_vec3(vec: C3Vector) -> Vec3 {
    Vec3 {
        x: vec.x,
        y: vec.y,
        z: vec.z,
    }
}

fn c2_to_vec2(vec: C2Vector) -> Vec2 {
    Vec2 { x: vec.x, y: vec.y }
}

impl TryFrom<M2> for mesh::Mesh {
    type Error = Error;

    fn try_from(value: M2) -> std::result::Result<Self, Self::Error> {
        let vertex_count = value.model.vertices.len();

        let mut vertices = Vec::with_capacity(vertex_count);
        let mut uvs = Vec::with_capacity(vertex_count);
        let mut normals = Vec::with_capacity(vertex_count);

        for v in &value.model.vertices {
            vertices.push(c3_to_vec3(v.position));
            uvs.push(c2_to_vec2(v.tex_coords));
            normals.push(c3_to_vec3(v.normal));
        }

        let mut triangles = Vec::with_capacity(value.skins[0].triangles.len());
        for skin in &value.skins {
            for t in &(&skin).triangles {
                triangles.push(*t as u32);
            }

            // for sm in &(&skin).submeshes {
            //     for vi in 0..sm.triangle_count {
            //         triangles.push(
            //             skin.indices
            //                 [skin.triangles[sm.triangle_start as usize + vi as usize] as usize]
            //                 as u32,
            //         )
            //     }
            // }
        }

        Ok(mesh::Mesh::new(
            mesh::PrimitiveTopology::TriangleList,
            bevy::asset::RenderAssetUsages::default(),
        )
        .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_POSITION, vertices)
        .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_UV_0, uvs)
        .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_NORMAL, normals)
        .with_inserted_indices(mesh::Indices::U32(triangles)))
    }
}

impl ReadFromMpq<wow_m2::BlpTexture> for MpqCollection {
    fn read_file(&mut self, name: &str) -> Result<wow_m2::BlpTexture> {
        let blpdata: Vec<u8> = self.read_file(&name)?;
        let mut reader = Cursor::new(&blpdata);
        Ok(wow_m2::BlpTexture::parse(&mut reader)?)
    }
}

impl ReadFromMpq<Image> for MpqCollection {
    fn read_file(&mut self, name: &str) -> Result<Image> {
        let mut blp: wow_m2::BlpTexture = self.read_file(name)?;
        let mip = &mut blp.mipmaps[0];

        let texture_format = match blp.header.compression_type {
            BlpCompressionType::Dxt => match blp.header.pixel_format {
                BlpPixelFormat::Dxt1 => TextureFormat::Bc1RgbaUnorm,
                _ => return Err(Error::Generic("unsupported texture format")),
            },
            _ => return Err(Error::Generic("unsupported texture format")),
        };

        let mut image = Image::default();
        image.texture_descriptor.size = Extent3d {
            width: mip.width,
            height: mip.height,
            depth_or_array_layers: 1,
        }
        .physical_size(texture_format);

        let mut data = Vec::with_capacity(mip.data.len());
        data.append(&mut mip.data);

        image.texture_descriptor.mip_level_count = 1;
        image.texture_descriptor.format = texture_format;
        image.texture_descriptor.dimension = TextureDimension::D2;
        image.data = Some(data);

        Ok(image)
    }
}

impl ReadFromMpq<wow_m2::OldSkin> for MpqCollection {
    fn read_file(&mut self, name: &str) -> Result<wow_m2::OldSkin> {
        let skin_data: Vec<u8> = self.read_file(&name)?;
        let mut skin_reader = Cursor::new(skin_data);
        Ok(wow_m2::OldSkin::parse(&mut skin_reader)?)
    }
}

impl ReadFromMpq<M2> for MpqCollection {
    fn read_file(&mut self, name: &str) -> Result<M2> {
        let m2data: Vec<u8> = self.read_file(name)?;
        let mut reader = Cursor::new(&m2data);
        let mut model = Box::new(wow_m2::M2Model::parse(&mut reader)?);

        for v in &mut model.vertices {
            let y = v.position.z;
            v.position.z = v.position.y * -1.;
            v.position.y = y;

            let ny = v.normal.z;
            v.normal.z = v.normal.y * -1.;
            v.normal.y = ny;
        }

        let base_file_name = &name[..name.len() - 3];

        let num_skins = model.header.num_skin_profiles.unwrap_or(0) as usize;
        let mut skins = Vec::<Box<wow_m2::OldSkin>>::with_capacity(num_skins);
        for i in 0..num_skins {
            let fname = format!("{}{:02}.skin", base_file_name, i);
            skins.push(Box::new(self.read_file(&fname)?));
        }

        Ok(M2 { skins, model })
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;

    #[test]
    fn load_m2_with_skins() {
        let base_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("Data");

        let mut mpq_col = MpqCollection::load(&vec![
            base_path.join("common.MPQ").as_path(),
            base_path.join("common-2.MPQ").as_path(),
        ])
        .unwrap();

        let fname = "World\\GENERIC\\HUMAN\\PASSIVE DOODADS\\Bottles\\Bottle01.m2";

        let m2: M2 = mpq_col.read_file(fname).unwrap();
        dbg!(&m2.model);
        dbg!(&m2.skins);

        // assert_eq!(0, 1);
    }
}

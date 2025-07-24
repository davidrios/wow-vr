use bevy::{
    image::Image,
    math::{Vec2, Vec3},
    render::{
        mesh,
        render_resource::{Extent3d, TextureDimension, TextureFormat},
    },
};
use std::{collections::VecDeque, io::Cursor};
use wow_m2::{
    blp::{BlpCompressionType, BlpPixelFormat},
    common::{C2Vector, C3Vector},
};

use custom_debug::Debug;

use crate::{
    errors::{Error, Result},
    mpq::MPQCollection,
};

#[derive(Debug)]
pub struct M2 {
    model: Box<wow_m2::M2Model>,
    skins: Vec<wow_m2::OldSkin>,
    pub textures: VecDeque<Image>,
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

impl M2 {
    pub fn to_mesh(&mut self) -> Result<mesh::Mesh> {
        let mut vertices: Vec<Vec3> = vec![];
        let mut uvs: Vec<Vec2> = vec![];
        let mut normals: Vec<Vec3> = vec![];

        for v in &self.model.vertices {
            vertices.push(c3_to_vec3(v.position));
            uvs.push(c2_to_vec2(v.tex_coords));
            normals.push(c3_to_vec3(v.normal));
        }

        let mut triangles: Vec<u32> = vec![];
        for skin in &self.skins {
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

    pub fn load_textures(&mut self, mpq_col: &mut MPQCollection) -> Result<()> {
        if self.textures.len() != 0 {
            return Err(Error::Generic("textures already loaded"));
        }

        for t in &self.model.textures {
            let filename = t.filename.string.to_string_lossy();
            let blpdata = mpq_col.read_file(&filename)?;
            let mut reader = Cursor::new(&blpdata);
            let blp = wow_m2::BlpTexture::parse(&mut reader)?;

            self.textures.push_back(blp_to_image(blp)?);
        }

        Ok(())
    }
}

pub fn blp_to_image(mut blp: wow_m2::BlpTexture) -> Result<Image> {
    dbg!(&blp);
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

pub fn load_from_mpq(mpq_col: &mut MPQCollection, fname: &str) -> Result<M2> {
    let m2data = mpq_col.read_file(fname)?;
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

    let base_file_name = &fname[..fname.len() - 3];

    let num_skins = model.header.num_skin_profiles.unwrap_or(0) as usize;
    let mut skins = Vec::<wow_m2::OldSkin>::with_capacity(num_skins);
    for i in 0..num_skins {
        let fname = format!("{}{:02}.skin", base_file_name, i);
        let skin_data = mpq_col.read_file(&fname)?;
        let mut skin_reader = Cursor::new(skin_data);
        let parsed_skin = wow_m2::OldSkin::parse(&mut skin_reader)?;
        skins.push(parsed_skin);
    }

    Ok(M2 {
        skins,
        textures: VecDeque::with_capacity((&model.textures).len()),
        model,
    })
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

        let mut mpq_col = MPQCollection::load(&vec![
            base_path.join("common.MPQ").as_path(),
            base_path.join("common-2.MPQ").as_path(),
        ])
        .unwrap();

        let fname = "World\\GENERIC\\HUMAN\\PASSIVE DOODADS\\Bottles\\Bottle01.m2";

        let m2 = load_from_mpq(&mut mpq_col, fname).unwrap();
        dbg!(&m2.model);
        dbg!(&m2.skins);

        // assert_eq!(0, 1);
    }
}

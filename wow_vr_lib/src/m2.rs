use bevy::{
    math::{Vec2, Vec3},
    render::mesh,
};
use std::io::Cursor;
use wow_m2::common::{C2Vector, C3Vector};

use custom_debug::Debug;

use crate::errors::Error;

#[derive(Debug)]
pub struct M2 {
    model: Box<wow_m2::M2Model>,
    skins: Vec<wow_m2::OldSkin>,
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
    pub fn to_mesh(&mut self) -> Result<mesh::Mesh, Error> {
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
}

pub fn load_from_mpq(mpq_file: &mut wow_mpq::Archive, fname: &str) -> Result<M2, Error> {
    let m2data = mpq_file.read_file(fname)?;
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
        let skin_data = mpq_file.read_file(&fname)?;
        let mut skin_reader = Cursor::new(skin_data);
        let parsed_skin = wow_m2::OldSkin::parse(&mut skin_reader)?;
        skins.push(parsed_skin);
    }

    Ok(M2 { model, skins })
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;

    #[test]
    fn load_m2_with_skins() {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("Data")
            .join("common-2.MPQ");

        let mut mpq_file = wow_mpq::OpenOptions::new().open(&path).unwrap();
        dbg!(mpq_file.header());

        dbg!(mpq_file.list().unwrap());

        let fname =
            "World\\AZEROTH\\BOOTYBAY\\PASSIVEDOODAD\\BOOTYENTRANCE\\BootyBayEntrance_02.m2";

        let m2 = load_from_mpq(&mut mpq_file, fname).unwrap();
        dbg!(&m2.model.header);
        dbg!(&m2.skins[0].header);

        assert_eq!(0, 1);
    }
}

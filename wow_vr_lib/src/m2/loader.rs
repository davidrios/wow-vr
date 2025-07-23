/*!
   work based on wow.export (https://github.com/Kruithne/wow.export)
   by Authors: Kruithne <kruithne@gmail.com>, Marlamin <marlamin@marlamin.com>
   Licensed on MIT
*/

use bevy::{
    math::{U8Vec4, Vec2, Vec3},
    render::mesh,
};
use num_enum::TryFromPrimitive;
use std::{collections::HashMap, io::Cursor, sync::Arc};
use wow_m2::common::{C2Vector, C3Vector};

use byteorder::{LittleEndian, ReadBytesExt};
use custom_debug::Debug;

use crate::{
    errors::Error,
    utils::{self, read_u8vec4, read_vec2, read_vec3},
};

use super::md20;

#[repr(u32)]
#[derive(Debug, PartialEq, Eq, TryFromPrimitive)]
pub enum ChunkType {
    MD20 = Magic::MD20 as u32,
    MD21 = Magic::MD21 as u32,
    SFID = 0x44494653,
    TXID = 0x44495854,
    SKID = 0x44494B53,
    BFID = 0x44494642,
    AFID = 0x44494641,
}

#[repr(u32)]
#[derive(Debug, PartialEq, Eq, TryFromPrimitive)]
pub enum Magic {
    MD21 = 0x3132444D,
    MD20 = 0x3032444D,
}

#[derive(Debug)]
pub struct M2Old {
    #[debug(with = utils::buf_len_fmt)]
    raw_data: Arc<Vec<u8>>,
    file_id: String,
    data: Box<md20::Data>,
}

impl M2Old {
    pub fn to_mesh(&mut self) -> Result<mesh::Mesh, Error> {
        let vertice_data = self.data.vertice_data()?;

        Ok(mesh::Mesh::new(
            mesh::PrimitiveTopology::TriangleList,
            bevy::asset::RenderAssetUsages::default(),
        )
        .with_inserted_attribute(
            mesh::Mesh::ATTRIBUTE_POSITION,
            vertice_data.vertices().clone(),
        )
        .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_UV_0, vertice_data.uv().clone())
        .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_NORMAL, vertice_data.normals().clone())
        .with_inserted_indices(mesh::Indices::U32(self.data.triangles()?)))
    }

    pub fn skin_files(&mut self) -> Vec<String> {
        let count = self.data.view_count();
        let base_file_name = &self.file_id[..self.file_id.len() - 3];
        let mut files = Vec::<String>::with_capacity(count);
        for i in 0..count {
            files.push(format!("{}{:02}.skin", base_file_name, i));
        }
        files
    }
}

pub fn load(raw_data: Vec<u8>, file_id: String) -> Result<M2Old, Error> {
    let raw_data = Arc::new(raw_data);

    let mut data_c = Cursor::new(raw_data.as_ref());
    let chunk_id: ChunkType = data_c.read_u32::<LittleEndian>()?.try_into()?;
    let mut data: Option<Box<md20::Data>> = None;

    if chunk_id == ChunkType::MD20 {
        data_c.set_position(0);
        data = Some(md20::parse_chunk(&raw_data, 0)?);
        // 	this.parseRestMD20();
    } else {
        data_c.set_position(0);
        //
        // 	while (this.data.remainingBytes > 0) {
        let cursor = &mut data_c;
        while cursor.position() < cursor.get_ref().len() as u64 {
            // 		const chunkID = this.data.readUInt32LE();
            // 		const chunkSize = this.data.readUInt32LE();
            let chunk_size = cursor.read_u32::<LittleEndian>()? as usize;
            // 		const nextChunkPos = this.data.offset + chunkSize;
            let next_chunk_pos = cursor.position() + chunk_size as u64;
            //
            // 		switch (chunkID) {
            match chunk_id {
                ChunkType::MD20 => unreachable!(),
                // 			case constants.MAGIC.MD21: await this.parseChunk_MD21(); break;
                ChunkType::MD21 => {
                    if data.is_some() {
                        return Err(Error::CorruptedFile);
                    }
                    data = Some(md20::parse_chunk(&raw_data, cursor.position())?);
                }
                // 			case CHUNK_SFID: this.parseChunk_SFID(chunkSize); break;
                ChunkType::SFID => {}
                // 			case CHUNK_TXID: this.parseChunk_TXID(); break;
                ChunkType::TXID => {}
                // 			case CHUNK_SKID: this.parseChunk_SKID(); break;
                ChunkType::SKID => {}
                // 			case CHUNK_BFID: this.parseChunk_BFID(chunkSize); break;
                ChunkType::BFID => {}
                // 			case CHUNK_AFID: this.parseChunk_AFID(chunkSize); break;
                ChunkType::AFID => {}
            }
            //
            // 		// Ensure that we start at the next chunk exactly.
            // 		this.data.seek(nextChunkPos);
            cursor.set_position(next_chunk_pos);
        }
        unreachable!("not implemented");
    }

    let data = data.ok_or(Error::CorruptedFile)?;

    Ok(M2Old {
        raw_data,
        file_id,
        data,
    })
}

#[derive(Debug, Clone)]
pub struct M2Vertex {
    pub position: Vec3,
    pub bone_weights: U8Vec4,
    pub bone_indices: U8Vec4,
    pub normal: Vec3,
    pub tex_coords: Vec2,
    pub tex_coords2: Option<Vec2>,
}

#[derive(Debug)]
pub struct M2 {
    model: Box<wow_m2::M2Model>,
    skins: Vec<wow_m2::OldSkin>,
    vertices: Vec<M2Vertex>,
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

#[derive(Hash, Eq, PartialEq, PartialOrd, Ord, Debug, Copy, Clone)]
struct VertexIndices {
    pub v: usize,
    pub vt: usize,
    pub vn: usize,
}

struct Face(VertexIndices, VertexIndices, VertexIndices);

fn export_faces(
    pos: &[f32],
    v_color: &[f32],
    texcoord: &[f32],
    normal: &[f32],
    faces: &[Face],
    mat_id: Option<usize>,
    load_options: &tobj::LoadOptions,
) -> Result<tobj::Mesh, tobj::LoadError> {
    let mut index_map = HashMap::new();
    let mut mesh = tobj::Mesh {
        material_id: mat_id,
        ..Default::default()
    };
    let is_all_triangles = true;

    for f in faces {
        add_vertex(
            &mut mesh,
            &mut index_map,
            &f.0,
            pos,
            v_color,
            texcoord,
            normal,
        )?;
        add_vertex(
            &mut mesh,
            &mut index_map,
            &f.1,
            pos,
            v_color,
            texcoord,
            normal,
        )?;
        add_vertex(
            &mut mesh,
            &mut index_map,
            &f.2,
            pos,
            v_color,
            texcoord,
            normal,
        )?;
        if !load_options.triangulate {
            mesh.face_arities.push(3);
        }
    }

    if is_all_triangles {
        // This is a triangle-only mesh.
        mesh.face_arities = Vec::new();
    }

    Ok(mesh)
}

fn add_vertex(
    mesh: &mut tobj::Mesh,
    index_map: &mut HashMap<VertexIndices, u32>,
    vert: &VertexIndices,
    pos: &[f32],
    v_color: &[f32],
    texcoord: &[f32],
    normal: &[f32],
) -> Result<(), tobj::LoadError> {
    match index_map.get(vert) {
        Some(&i) => mesh.indices.push(i),
        None => {
            let v = vert.v;
            if v.saturating_mul(3).saturating_add(2) >= pos.len() {
                return Err(tobj::LoadError::FaceVertexOutOfBounds);
            }
            // Add the vertex to the mesh
            mesh.positions.push(pos[v * 3]);
            mesh.positions.push(pos[v * 3 + 1]);
            mesh.positions.push(pos[v * 3 + 2]);
            if !texcoord.is_empty() && vert.vt != usize::MAX {
                let vt = vert.vt;
                if vt * 2 + 1 >= texcoord.len() {
                    return Err(tobj::LoadError::FaceTexCoordOutOfBounds);
                }
                mesh.texcoords.push(texcoord[vt * 2]);
                mesh.texcoords.push(texcoord[vt * 2 + 1]);
            }
            if !normal.is_empty() && vert.vn != usize::MAX {
                let vn = vert.vn;
                if vn * 3 + 2 >= normal.len() {
                    return Err(tobj::LoadError::FaceNormalOutOfBounds);
                }
                mesh.normals.push(normal[vn * 3]);
                mesh.normals.push(normal[vn * 3 + 1]);
                mesh.normals.push(normal[vn * 3 + 2]);
            }
            if !v_color.is_empty() {
                if v * 3 + 2 >= v_color.len() {
                    return Err(tobj::LoadError::FaceColorOutOfBounds);
                }
                mesh.vertex_color.push(v_color[v * 3]);
                mesh.vertex_color.push(v_color[v * 3 + 1]);
                mesh.vertex_color.push(v_color[v * 3 + 2]);
            }
            let next = index_map.len() as u32;
            mesh.indices.push(next);
            index_map.insert(*vert, next);
        }
    }
    Ok(())
}

struct MeshConverter {
    meshes: Vec<tobj::Mesh>,
}

impl MeshConverter {
    pub fn convert(&self, settings: &bevy_obj::ObjSettings) -> mesh::Mesh {
        let mut mesh = mesh::Mesh::new(
            mesh::PrimitiveTopology::TriangleList,
            bevy::asset::RenderAssetUsages::default(),
        );

        mesh.insert_indices(mesh::Indices::U32(self.indices()));
        mesh.insert_attribute(mesh::Mesh::ATTRIBUTE_POSITION, self.position());

        if self.has_uv() {
            mesh.insert_attribute(mesh::Mesh::ATTRIBUTE_UV_0, self.uv());
        }

        if self.has_normal() && !settings.force_compute_normals {
            mesh.insert_attribute(mesh::Mesh::ATTRIBUTE_NORMAL, self.normal());
        } else if settings.prefer_flat_normals {
            mesh.duplicate_vertices();
            mesh.compute_flat_normals();
        } else {
            mesh.compute_normals();
        }

        mesh
    }

    fn new(meshes: Vec<tobj::Mesh>) -> Self {
        Self { meshes }
    }

    fn indices(&self) -> Vec<u32> {
        let count = self.meshes.iter().map(|m| m.indices.len()).sum();
        let mut data = Vec::with_capacity(count);
        let mut offset = 0;

        for mesh in &self.meshes {
            data.extend(mesh.indices.iter().map(|i| i + offset));
            offset += (mesh.positions.len() / 3) as u32;
        }

        data
    }

    fn position(&self) -> Vec<[f32; 3]> {
        let count = self.meshes.iter().map(|m| m.positions.len() / 3).sum();
        let mut data = Vec::with_capacity(count);

        for mesh in &self.meshes {
            data.append(&mut convert_vec3(&mesh.positions));
        }

        data
    }

    fn has_normal(&self) -> bool {
        !self.meshes.iter().any(|m| m.normals.is_empty())
    }

    fn normal(&self) -> Vec<[f32; 3]> {
        let count = self.meshes.iter().map(|m| m.normals.len() / 3).sum();
        let mut data = Vec::with_capacity(count);

        for mesh in &self.meshes {
            data.append(&mut convert_vec3(&mesh.normals));
        }

        data
    }

    fn has_uv(&self) -> bool {
        !self.meshes.iter().any(|m| m.texcoords.is_empty())
    }

    fn uv(&self) -> Vec<[f32; 2]> {
        let count = self.meshes.iter().map(|m| m.texcoords.len() / 2).sum();
        let mut data = Vec::with_capacity(count);

        for mesh in &self.meshes {
            data.append(&mut convert_uv(&mesh.texcoords));
        }

        data
    }
}

fn convert_vec3(vec: &[f32]) -> Vec<[f32; 3]> {
    vec.chunks_exact(3).map(|v| [v[0], v[1], v[2]]).collect()
}

fn convert_uv(uv: &[f32]) -> Vec<[f32; 2]> {
    uv.chunks_exact(2).map(|t| [t[0], 1.0 - t[1]]).collect()
}

impl M2 {
    pub fn to_mesh3(&mut self) -> Result<mesh::Mesh, Error> {
        let mut vertices: Vec<Vec3> = vec![];
        let mut uvs: Vec<Vec2> = vec![];
        let mut normals: Vec<Vec3> = vec![];

        for v in &self.vertices {
            vertices.push(v.position);
            uvs.push(v.tex_coords);
            normals.push(v.normal);
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

    pub fn to_mesh4(&mut self) -> Result<mesh::Mesh, Error> {
        let mut mesh = tobj::Mesh::default();
        for v in &self.model.vertices {
            mesh.positions.push(v.position.x);
            mesh.positions.push(v.position.z * -1.);
            mesh.positions.push(v.position.y);
            mesh.texcoords.push(v.tex_coords.x);
            mesh.texcoords.push(v.tex_coords.y);
            mesh.normals.push(v.normal.x);
            mesh.normals.push(v.normal.z * -1.);
            mesh.normals.push(v.normal.y);
        }

        // let mut triangles: Vec<u32> = vec![];
        let mut meshes = Vec::<tobj::Mesh>::new();
        for skin in &self.skins {
            // for t in &(&skin).triangles {
            //     triangles.push(*t as u32);
            // }

            // for t in &(&skin).indices {
            //     triangles.push(*t as u32);
            // }

            for sm in &(&skin).submeshes {
                // let mut triangles: Vec<u32> = vec![];
                let mut faces = Vec::<Face>::new();
                for vi in 0..sm.triangle_count / 3 {
                    let index = skin.indices
                        [skin.triangles[sm.triangle_start as usize + vi as usize] as usize]
                        as usize;
                    let index2 = skin.indices
                        [skin.triangles[sm.triangle_start as usize + vi as usize + 1] as usize]
                        as usize;
                    let index3 = skin.indices
                        [skin.triangles[sm.triangle_start as usize + vi as usize + 2] as usize]
                        as usize;
                    // let index = skin.triangles[sm.triangle_start as usize + vi as usize] as usize;
                    // let index2 =
                    //     skin.triangles[sm.triangle_start as usize + vi as usize + 1] as usize;
                    // let index3 =
                    //     skin.triangles[sm.triangle_start as usize + vi as usize + 2] as usize;

                    faces.push(Face(
                        VertexIndices {
                            v: index,
                            vt: index,
                            vn: index,
                        },
                        VertexIndices {
                            v: index2,
                            vt: index2,
                            vn: index2,
                        },
                        VertexIndices {
                            v: index3,
                            vt: index3,
                            vn: index3,
                        },
                    ));
                    // triangles.push(index)
                }
                meshes.push(
                    export_faces(
                        &mesh.positions,
                        &[],
                        &mesh.texcoords,
                        &mesh.normals,
                        &faces,
                        None,
                        &tobj::LoadOptions::default(),
                    )
                    .unwrap(),
                );
            }
        }

        Ok(
            MeshConverter::new(meshes).convert(&bevy_obj::ObjSettings {
                force_compute_normals: false,
                prefer_flat_normals: false,
            }), // mesh::Mesh::new(
                //     mesh::PrimitiveTopology::TriangleList,
                //     bevy::asset::RenderAssetUsages::default(),
                // ), // .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_POSITION, vertices)
                // .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_UV_0, uvs)
                // .with_inserted_attribute(mesh::Mesh::ATTRIBUTE_NORMAL, normals)
                // .with_inserted_indices(mesh::Indices::U32(triangles))
        )
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

    dbg!(&model.header);

    let mut vertices = Vec::<M2Vertex>::with_capacity(model.header.vertices.count as usize);
    reader.set_position(model.header.vertices.offset as u64);
    for _ in 0..model.header.vertices.count {
        vertices.push(M2Vertex {
            position: read_vec3(&mut reader)?,
            bone_weights: read_u8vec4(&mut reader)?,
            bone_indices: read_u8vec4(&mut reader)?,
            normal: read_vec3(&mut reader)?,
            tex_coords: read_vec2(&mut reader)?,
            tex_coords2: Some(read_vec2(&mut reader)?),
        });
    }

    let num_skins = model.header.num_skin_profiles.unwrap_or(0) as usize;
    let mut skins = Vec::<wow_m2::OldSkin>::with_capacity(num_skins);
    for i in 0..num_skins {
        let fname = format!("{}{:02}.skin", base_file_name, i);
        let skin_data = mpq_file.read_file(&fname)?;
        let mut skin_reader = Cursor::new(skin_data);
        let parsed_skin = wow_m2::OldSkin::parse(&mut skin_reader)?;
        dbg!(&parsed_skin.header);
        skins.push(parsed_skin);
    }

    Ok(M2 {
        model,
        skins,
        vertices,
    })
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

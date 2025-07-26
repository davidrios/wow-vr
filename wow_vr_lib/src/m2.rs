use bevy::{
    prelude::*,
    render::{
        mesh,
        render_resource::{Extent3d, TextureDimension, TextureFormat},
    },
};
use bevy_asset::{AssetLoader, AssetPath, LoadContext, RenderAssetUsages, io::Reader};
use serde::{Deserialize, Serialize};
use std::io::Cursor;
use std::result::Result as StdResult;
use wow_m2::{
    blp::{BlpCompressionType, BlpPixelFormat},
    common::{C2Vector, C3Vector},
};

use custom_debug::Debug;

use crate::{
    errors::{Error, Result},
    mpq::{MpqCollection, ReadFromMpq},
};

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

impl ReadFromMpq<wow_m2::BlpTexture> for MpqCollection {
    fn read_file(&self, name: &str) -> Result<wow_m2::BlpTexture> {
        let blpdata: Vec<u8> = self.read_file(&name)?;
        let mut reader = Cursor::new(&blpdata);
        Ok(wow_m2::BlpTexture::parse(&mut reader)?)
    }
}

impl ReadFromMpq<Image> for MpqCollection {
    fn read_file(&self, name: &str) -> Result<Image> {
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
    fn read_file(&self, name: &str) -> Result<wow_m2::OldSkin> {
        let skin_data: Vec<u8> = self.read_file(&name)?;
        let mut skin_reader = Cursor::new(skin_data);
        Ok(wow_m2::OldSkin::parse(&mut skin_reader)?)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum M2RelatedAsset {
    Skin(u32),
}

impl core::fmt::Display for M2RelatedAsset {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::Skin(index) => f.write_str(&format!("{:02}.skin", index)),
        }
    }
}

impl M2RelatedAsset {
    pub fn from_asset(&self, path: impl Into<AssetPath<'static>>) -> AssetPath<'static> {
        let path: AssetPath = path.into();
        let path_str = path.path().to_str().unwrap();
        let base_file_name: String = path_str[..path_str.len() - 3].into();

        AssetPath::parse(&format!("{}{}", &base_file_name, self.to_string()))
            .with_source(path.source())
            .clone_owned()
    }
}

#[derive(Asset, TypePath, Debug)]
pub struct M2Asset {
    pub model: wow_m2::M2Model,
    pub base_name: String,
    pub skins: Vec<Handle<SkinAsset>>,
    mesh: Option<Handle<Mesh>>,
}

impl M2Asset {
    pub fn load_mesh(
        &mut self,
        skin: &SkinAsset,
        meshes: &mut ResMut<Assets<Mesh>>,
    ) -> Result<&Handle<Mesh>> {
        Ok(self.mesh.get_or_insert_with(|| {
            let vertex_count = self.model.vertices.len();

            let mut vertices = Vec::with_capacity(vertex_count);
            let mut uvs = Vec::with_capacity(vertex_count);
            let mut normals = Vec::with_capacity(vertex_count);

            for v in &self.model.vertices {
                vertices.push(c3_to_vec3(v.position));
                uvs.push(c2_to_vec2(v.tex_coords));
                normals.push(c3_to_vec3(v.normal));
            }

            let triangles = Vec::with_capacity(skin.skin.triangles.len());
            // for skin in &value.skins {
            //     for t in &(&skin).triangles {
            //         triangles.push(*t as u32);
            //     }
            //
            //     // for sm in &(&skin).submeshes {
            //     //     for vi in 0..sm.triangle_count {
            //     //         triangles.push(
            //     //             skin.indices
            //     //                 [skin.triangles[sm.triangle_start as usize + vi as usize] as usize]
            //     //                 as u32,
            //     //         )
            //     //     }
            //     // }
            // }

            meshes.add(
                Mesh::new(
                    mesh::PrimitiveTopology::TriangleList,
                    bevy::asset::RenderAssetUsages::default(),
                )
                .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, vertices)
                .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, uvs)
                .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, normals)
                .with_inserted_indices(mesh::Indices::U32(triangles)),
            )
        }))
    }
}

#[derive(Serialize, Deserialize, Default, Debug, Clone)]
pub struct M2LoaderSettings {
    pub asset_usage: RenderAssetUsages,
    pub skin_index: usize,
}

#[derive(Asset, TypePath)]
pub struct M2TAsset {}

#[derive(Clone)]
pub struct M2Loader {}

impl AssetLoader for M2Loader {
    type Asset = M2Asset;
    type Settings = ();
    type Error = Error;

    async fn load(
        &self,
        reader: &mut dyn Reader,
        _settings: &Self::Settings,
        load_context: &mut LoadContext<'_>,
    ) -> StdResult<Self::Asset, Self::Error> {
        dbg!("load m2", load_context.asset_path());
        dbg!(
            "load m2 label",
            load_context.asset_path().label().unwrap_or(&"no label")
        );
        let mut bytes = Vec::new();
        reader.read_to_end(&mut bytes).await?;
        let mut reader = Cursor::new(&bytes);
        let mut model = wow_m2::M2Model::parse(&mut reader)?;

        for v in &mut model.vertices {
            let y = v.position.z;
            v.position.z = v.position.y * -1.;
            v.position.y = y;

            let ny = v.normal.z;
            v.normal.z = v.normal.y * -1.;
            v.normal.y = ny;
        }

        let name = load_context
            .asset_path()
            .path()
            .to_str()
            .ok_or_else(|| Error::Generic("error converting path to str"))?;
        let base_file_name: String = name[..name.len() - 3].into();

        let num_skins = if let Some(num_skins) = model.header.num_skin_profiles {
            num_skins
        } else {
            0
        };

        let mut skins = Vec::with_capacity(num_skins as usize);
        for i in 0..num_skins {
            skins.push(
                load_context.load(M2RelatedAsset::Skin(i).from_asset(load_context.asset_path())),
            );
        }

        Ok(M2Asset {
            model,
            base_name: base_file_name,
            skins,
            mesh: None,
        })
    }
}

#[derive(Asset, TypePath, Debug)]
pub struct SkinAsset {
    pub skin: wow_m2::OldSkin,
}

#[derive(Serialize, Deserialize, Default, Debug, Clone)]
pub struct SkinLoaderSettings {
    pub asset_usage: RenderAssetUsages,
}

#[derive(Clone)]
pub struct SkinLoader;

impl AssetLoader for SkinLoader {
    type Asset = SkinAsset;
    type Settings = ();
    type Error = Error;

    async fn load(
        &self,
        reader: &mut dyn Reader,
        _settings: &Self::Settings,
        _load_context: &mut LoadContext<'_>,
    ) -> StdResult<Self::Asset, Self::Error> {
        dbg!("load skin", _load_context.asset_path());
        let mut bytes = Vec::new();
        reader.read_to_end(&mut bytes).await?;
        let mut reader = Cursor::new(&bytes);

        Ok(SkinAsset {
            skin: wow_m2::OldSkin::parse(&mut reader)?,
        })
    }
}

#[derive(Default)]
pub struct M2Plugin {}

impl Plugin for M2Plugin {
    fn build(&self, app: &mut App) {
        app.init_asset::<SkinAsset>()
            .preregister_asset_loader::<SkinLoader>(&["skin"])
            .init_asset::<M2Asset>()
            .preregister_asset_loader::<M2Loader>(&["m2"]);
    }

    fn finish(&self, app: &mut App) {
        app.register_asset_loader(SkinLoader)
            .register_asset_loader(M2Loader {});
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

        let mpq_col = MpqCollection::load(&vec![
            base_path.join("common.MPQ").as_path(),
            base_path.join("common-2.MPQ").as_path(),
        ])
        .unwrap();

        let fname = "World\\GENERIC\\HUMAN\\PASSIVE DOODADS\\Bottles\\Bottle01.m2";

        // let m2: M2_old = mpq_col.read_file(fname).unwrap();
        // dbg!(&m2.model);
        // dbg!(&m2.skins);

        assert_eq!(0, 1);
    }
}

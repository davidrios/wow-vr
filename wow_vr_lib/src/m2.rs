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

use crate::errors::{Error, Result};

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

fn blp_to_image(blp: &mut wow_m2::BlpTexture) -> Result<Image> {
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum M2AssetLabel {
    Skin(u32),
    Mesh(u32),
    Texture(u32),
}

impl core::fmt::Display for M2AssetLabel {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::Skin(index) => f.write_str(&format!("skin{}", index)),
            Self::Mesh(index) => f.write_str(&format!("mesh{}", index)),
            Self::Texture(index) => f.write_str(&format!("texture{}", index)),
        }
    }
}

#[derive(Asset, TypePath, Debug)]
pub struct M2Asset {
    pub model: wow_m2::M2Model,
    // pub base_name: String,
    pub skins: Vec<Handle<SkinAsset>>,
    pub meshes: Vec<Handle<Mesh>>,
    pub textures: Vec<Handle<Image>>,
    pub materials: Vec<Handle<StandardMaterial>>,
}

impl M2Asset {
    pub async fn new(model: wow_m2::M2Model, load_context: &mut LoadContext<'_>) -> Result<Self> {
        let num_skins = if let Some(num_skins) = model.header.num_skin_profiles {
            num_skins
        } else {
            0
        };

        let mut skin_handles = Vec::with_capacity(num_skins as usize);
        let mut mesh_handles = Vec::with_capacity(num_skins as usize);

        if num_skins > 0 {
            let vertex_count = model.vertices.len();
            let mut vertices = Vec::with_capacity(vertex_count);
            let mut uvs = Vec::with_capacity(vertex_count);
            let mut normals = Vec::with_capacity(vertex_count);

            for v in &model.vertices {
                vertices.push(c3_to_vec3(v.position));
                uvs.push(c2_to_vec2(v.tex_coords));
                normals.push(c3_to_vec3(v.normal));
            }

            for i in 0..num_skins {
                let skin_path = M2RelatedAsset::Skin(i).from_asset(load_context.asset_path());
                let bytes = load_context.read_asset_bytes(skin_path).await?;
                let mut reader = Cursor::new(&bytes);

                let skin_asset = SkinAsset {
                    skin: wow_m2::OldSkin::parse(&mut reader)?,
                };

                let mut triangles = Vec::with_capacity(skin_asset.skin.triangles.len());
                for t in &(&skin_asset.skin).triangles {
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

                let mesh = Mesh::new(
                    mesh::PrimitiveTopology::TriangleList,
                    bevy::asset::RenderAssetUsages::default(),
                )
                .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, vertices.clone())
                .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, uvs.clone())
                .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, normals.clone())
                .with_inserted_indices(mesh::Indices::U32(triangles));

                skin_handles.push(
                    load_context.add_labeled_asset(M2AssetLabel::Skin(i).to_string(), skin_asset),
                );
                mesh_handles
                    .push(load_context.add_labeled_asset(M2AssetLabel::Mesh(i).to_string(), mesh));
            }
        }

        let mut texture_handles = Vec::with_capacity(model.textures.len());
        let mut material_handles = Vec::with_capacity(model.textures.len());
        for (i, texture) in model.textures.iter().enumerate() {
            let blp_path = AssetPath::parse(&texture.filename.string.to_string_lossy())
                .with_source(load_context.asset_path().source())
                .clone_owned();
            dbg!(&blp_path, &texture.filename.string.to_string_lossy());
            let bytes = load_context.read_asset_bytes(blp_path).await?;
            let mut reader = Cursor::new(&bytes);
            let mut blp = wow_m2::BlpTexture::parse(&mut reader)?;

            let texture_handle = load_context.add_labeled_asset(
                M2AssetLabel::Texture(i as u32).to_string(),
                blp_to_image(&mut blp)?,
            );

            let material = StandardMaterial {
                base_color_texture: Some(texture_handle.clone()),
                ..default()
            };

            texture_handles.push(texture_handle);
            material_handles
                .push(load_context.add_labeled_asset(format!("material{}", i), material));
        }

        Ok(Self {
            model,
            skins: skin_handles,
            meshes: mesh_handles,
            textures: texture_handles,
            materials: material_handles,
        })
    }
}

#[derive(Serialize, Deserialize, Default, Debug, Clone)]
pub struct M2LoaderSettings {
    pub asset_usage: RenderAssetUsages,
    pub skin_index: usize,
}

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

        Ok(M2Asset::new(model, load_context).await?)
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
    // use std::path::PathBuf;
    //
    // use crate::mpq::MpqCollection;
    //
    // use super::*;
    //
    // #[test]
    // fn load_m2_with_skins() {
    //     let base_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
    //         .join("..")
    //         .join("Data");
    //
    //     let mpq_col = MpqCollection::load(&vec![
    //         base_path.join("common.MPQ").as_path(),
    //         base_path.join("common-2.MPQ").as_path(),
    //     ])
    //     .unwrap();
    //
    //     let fname = "World\\GENERIC\\HUMAN\\PASSIVE DOODADS\\Bottles\\Bottle01.m2";
    //
    //     // let m2: M2_old = mpq_col.read_file(fname).unwrap();
    //     // dbg!(&m2.model);
    //     // dbg!(&m2.skins);
    //
    //     assert_eq!(0, 1);
    // }
}

use bevy::{
    platform::collections::HashMap,
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
use wow_blp::{BlpContent, BlpContentTag, BlpImage, CompressionType, parser::load_blp_from_buf};
use wow_m2::{
    chunks::material::{M2BlendMode, M2RenderFlags},
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

fn blp_to_image(blp: &mut BlpImage) -> Result<Image> {
    let texture_format = match blp.header.content {
        BlpContentTag::Direct => match blp.compression_type() {
            CompressionType::Dxt1 => TextureFormat::Bc1RgbaUnorm,
            CompressionType::Dxt3 => TextureFormat::Bc2RgbaUnorm,
            CompressionType::Dxt5 => TextureFormat::Bc3RgbaUnorm,
            _ => {
                dbg!(&blp);
                return Err(Error::Generic("unsupported texture format"));
            }
        },
        _ => {
            dbg!(&blp);
            return Err(Error::Generic("unsupported texture format"));
        }
    };

    let mut image = Image::default();

    match blp.content {
        BlpContent::Dxt1(_) | BlpContent::Dxt3(_) | BlpContent::Dxt5(_) => {
            let content = match blp.content {
                BlpContent::Dxt1(_) => blp.content.dxt1(),
                BlpContent::Dxt3(_) => blp.content_dxt3(),
                BlpContent::Dxt5(_) => blp.content_dxt5(),
                _ => unreachable!(),
            }
            .unwrap();

            let mip = &blp.mipmap_info()[0];
            let contentimg = &content.images[0];

            image.texture_descriptor.size = Extent3d {
                width: mip.width,
                height: mip.height,
                depth_or_array_layers: 1,
            }
            .physical_size(texture_format);

            image.data = Some(contentimg.content.clone());
        }
        _ => return Err(Error::Generic("unsupported texture format")),
    };

    image.texture_descriptor.mip_level_count = 1;
    image.texture_descriptor.format = texture_format;
    image.texture_descriptor.dimension = TextureDimension::D2;

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
    Mesh(u32, u32),
    Texture(u32),
    Material(u32, (u16, u16)),
}

impl core::fmt::Display for M2AssetLabel {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::Skin(index) => f.write_str(&format!("skin{}", index)),
            Self::Mesh(skin_index, mesh_index) => {
                f.write_str(&format!("skin{}+mesh{}", skin_index, mesh_index))
            }
            Self::Texture(index) => f.write_str(&format!("texture{}", index)),
            Self::Material(skin_index, (material_index, texture_index)) => f.write_str(&format!(
                "skin{}+material{:x}_{:x}",
                skin_index, material_index, texture_index
            )),
        }
    }
}

#[derive(Debug)]
pub struct M2Mesh {
    pub mesh: Handle<Mesh>,
    pub material: (u16, u16),
}

#[derive(Asset, TypePath, Debug)]
pub struct M2Asset {
    pub model: wow_m2::M2Model,
    pub skins: Vec<Handle<SkinAsset>>,
    pub meshes: HashMap<u32, Vec<M2Mesh>>,
    pub textures: Vec<Handle<Image>>,
    pub materials: Vec<HashMap<(u16, u16), Handle<StandardMaterial>>>,
}

impl M2Asset {
    pub async fn new(model: wow_m2::M2Model, load_context: &mut LoadContext<'_>) -> Result<Self> {
        let num_skins = if let Some(num_skins) = model.header.num_skin_profiles {
            num_skins
        } else {
            0
        };

        let mut skin_handles = Vec::with_capacity(num_skins as usize);
        let mut mesh_handles = HashMap::with_capacity(num_skins as usize);

        let mut texture_handles = Vec::with_capacity(model.textures.len());
        for (i, texture) in model.textures.iter().enumerate() {
            let orig_path = texture.filename.string.to_string_lossy();
            if orig_path.len() == 0 {
                continue;
            }
            let blp_path = AssetPath::parse(&orig_path)
                .with_source(load_context.asset_path().source())
                .clone_owned();
            let bytes = load_context.read_asset_bytes(blp_path).await?;
            let mut blp = load_blp_from_buf(&bytes).map_err(|e| {
                dbg!(e);
                Error::Generic("blp error")
            })?;

            let texture_handle = load_context.add_labeled_asset(
                M2AssetLabel::Texture(i as u32).to_string(),
                blp_to_image(&mut blp)?,
            );

            texture_handles.push(texture_handle);
        }

        let mut material_handles = Vec::new();

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
                let mut material_map = HashMap::new();

                let skin_path = M2RelatedAsset::Skin(i).from_asset(load_context.asset_path());
                let bytes = load_context.read_asset_bytes(skin_path).await?;
                let mut reader = Cursor::new(&bytes);

                let skin_asset = SkinAsset {
                    skin: wow_m2::OldSkin::parse(&mut reader)?,
                };

                let mut submeshes = Vec::with_capacity(skin_asset.skin.submeshes.len());

                for (mi, submesh) in skin_asset.skin.submeshes.iter().enumerate() {
                    let mut triangles = Vec::with_capacity(skin_asset.skin.triangles.len());
                    for vi in 0..submesh.triangle_count {
                        triangles.push(
                            skin_asset.skin.indices[skin_asset.skin.triangles
                                [submesh.triangle_start as usize + vi as usize]
                                as usize] as u32,
                        )
                    }

                    let mesh = Mesh::new(
                        mesh::PrimitiveTopology::TriangleList,
                        bevy::asset::RenderAssetUsages::default(),
                    )
                    .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, vertices.clone())
                    .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, uvs.clone())
                    .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, normals.clone())
                    .with_inserted_indices(mesh::Indices::U32(triangles));

                    submeshes.push(M2Mesh {
                        mesh: load_context
                            .add_labeled_asset(M2AssetLabel::Mesh(i, mi as u32).to_string(), mesh),
                        material: (0, 0),
                    });
                }

                for (_, texture_unit) in skin_asset.skin.extra_array.iter().enumerate() {
                    let submesh = submeshes
                        .get_mut(texture_unit.skin_section_index as usize)
                        .unwrap();

                    if !material_map.contains_key(&(
                        texture_unit.material_index,
                        texture_unit.texture_combo_index,
                    )) {
                        let material_opts = &model.materials[texture_unit.material_index as usize];
                        let texture_handle =
                            texture_handles.get(texture_unit.texture_combo_index as usize);

                        let material = StandardMaterial {
                            base_color_texture: if let Some(texture_handle) = texture_handle {
                                Some(texture_handle.clone())
                            } else {
                                None
                            },
                            double_sided: material_opts
                                .flags
                                .contains(M2RenderFlags::NO_BACKFACE_CULLING),
                            cull_mode: if material_opts
                                .flags
                                .contains(M2RenderFlags::NO_BACKFACE_CULLING)
                            {
                                None
                            } else {
                                Some(bevy::render::render_resource::Face::Back)
                            },
                            alpha_mode: match material_opts.blend_mode {
                                M2BlendMode::ALPHA_KEY => AlphaMode::Mask(0.5),
                                _ => AlphaMode::Opaque,
                            },
                            ..default()
                        };
                        let key = (
                            texture_unit.material_index,
                            texture_unit.texture_combo_index,
                        );
                        material_map.insert(
                            key,
                            load_context.add_labeled_asset(
                                M2AssetLabel::Material(i, key).to_string(),
                                material,
                            ),
                        );
                    }

                    submesh.material = (
                        texture_unit.material_index,
                        texture_unit.texture_combo_index,
                    );
                }

                skin_handles.push(
                    load_context.add_labeled_asset(M2AssetLabel::Skin(i).to_string(), skin_asset),
                );
                mesh_handles.insert(i, submeshes);
                material_handles.push(material_map);
            }
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

    use crate::mpq::MpqCollection;

    use super::*;

    fn get_reader(mpq_col: &mut MpqCollection, fname: &str) -> Cursor<Vec<u8>> {
        let bytes = mpq_col.read_file(fname).unwrap();
        Cursor::new(bytes)
    }

    #[test]
    fn load_m2_with_skins() {
        let base_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("Data");

        let mut mpq_col = MpqCollection::load(&vec![
            base_path.join("common.MPQ").as_path(),
            base_path.join("common-2.MPQ").as_path(),
            base_path.join("expansion.MPQ").as_path(),
            base_path.join("lichking.MPQ").as_path(),
            base_path.join("patch.MPQ").as_path(),
            base_path.join("patch-2.MPQ").as_path(),
            base_path.join("patch-3.MPQ").as_path(),
            base_path.join("enUS/locale-enUS.MPQ").as_path(),
            base_path.join("enUS/patch-enUS.MPQ").as_path(),
            base_path.join("enUS/patch-enUS-2.MPQ").as_path(),
            base_path.join("enUS/patch-enUS-3.MPQ").as_path(),
        ])
        .unwrap();

        let fname = "creature/ghoul/ghoul.m2";

        let m2 = wow_m2::M2Model::parse(&mut get_reader(&mut mpq_col, fname)).unwrap();
        dbg!(&m2);
        for i in 0..m2.header.num_skin_profiles.unwrap_or(0) {
            let sfname = M2RelatedAsset::Skin(i).from_asset(fname);
            let skin =
                wow_m2::OldSkin::parse(&mut get_reader(&mut mpq_col, &sfname.to_string())).unwrap();
            dbg!(&skin);
        }
        for i in m2.textures {
            let sfname = i.filename.string.to_string_lossy();
            if sfname.len() == 0 {
                continue;
            }
            let texture =
                load_blp_from_buf(&mpq_col.read_file(&sfname.to_string()).unwrap()).unwrap();
            dbg!(&texture);
        }

        assert_eq!(0, 1);
    }
}

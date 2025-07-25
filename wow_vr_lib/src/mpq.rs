use std::{
    collections::HashMap,
    fmt,
    path::Path,
    sync::{Arc, Mutex},
};

use bevy::ecs::resource::Resource;
use bevy_image::Image;
use custom_debug::Debug;
use wow_mpq::Archive;

use crate::{
    errors::{Error, Result},
    m2::M2,
};

pub fn header_fmt(archives: &Vec<Box<Archive>>, f: &mut fmt::Formatter) -> fmt::Result {
    write!(f, "[\n")?;
    for a in archives {
        write!(f, "{:?}: {:#?},\n", a.path(), a.header())?;
    }
    write!(f, "]")?;
    Ok(())
}

#[derive(Debug)]
pub struct MpqCollection {
    #[debug(with = header_fmt)]
    pub archives: Vec<Box<Archive>>,

    #[debug(skip)]
    pub file_map: HashMap<String, usize>,
}

fn format_file_name(val: &str) -> String {
    val.to_lowercase().replace("\\", "/")
}

pub trait ReadFromMpq<T> {
    fn read_file(&mut self, name: &str) -> Result<T>;
}

impl MpqCollection {
    pub fn load(paths: &[&Path]) -> Result<MpqCollection> {
        let mut archives = Vec::with_capacity(paths.len());
        let mut file_map = HashMap::new();
        for idx in 0..paths.len() {
            let mut archive = Box::new(wow_mpq::OpenOptions::new().open(&paths[idx])?);
            for file_info in &archive.list()? {
                file_map.insert(format_file_name(&file_info.name), idx);
            }
            archives.push(archive);
        }

        Ok(MpqCollection { archives, file_map })
    }
}

impl ReadFromMpq<Vec<u8>> for MpqCollection {
    fn read_file(&mut self, name: &str) -> Result<Vec<u8>> {
        let fname = format_file_name(name);
        let index = self
            .file_map
            .get(&fname)
            .ok_or_else(|| wow_mpq::Error::FileNotFound(fname))?;

        if let Some(archive) = self.archives.get_mut(*index) {
            Ok(archive.read_file(name)?)
        } else {
            Err(Error::Generic("self.archives is invalid"))
        }
    }
}

type ResourceMap<T> = Arc<Mutex<HashMap<String, Arc<T>>>>;

trait CacheProvider<T> {
    fn get_cache(&self) -> &ResourceMap<T>;
}

#[derive(Resource)]
pub struct MpqResource {
    mpq_collection: MpqCollection,
    loaded_m2s: ResourceMap<M2>,
    loaded_images: ResourceMap<Image>,
}

impl CacheProvider<M2> for MpqResource {
    fn get_cache(&self) -> &ResourceMap<M2> {
        &self.loaded_m2s
    }
}

impl CacheProvider<Image> for MpqResource {
    fn get_cache(&self) -> &ResourceMap<Image> {
        &self.loaded_images
    }
}

impl MpqResource {
    pub fn new(mpq_collection: MpqCollection) -> Self {
        return Self {
            mpq_collection,
            loaded_m2s: Arc::new(Mutex::new(HashMap::new())),
            loaded_images: Arc::new(Mutex::new(HashMap::new())),
        };
    }

    pub fn from_paths(mpq_paths: &[&Path]) -> Result<Self> {
        Ok(Self::new(MpqCollection::load(mpq_paths)?))
    }

    fn get_or_load<T: 'static>(&mut self, name: &str) -> Result<Arc<T>>
    where
        Self: CacheProvider<T>,
        MpqCollection: ReadFromMpq<T>,
    {
        let cache = self.get_cache();
        let loaded_ref = Arc::clone(cache);
        let mut loaded = loaded_ref.lock().unwrap();
        Ok(if let Some(entry) = loaded.get(name) {
            Arc::clone(entry)
        } else {
            let obj: Arc<T> = Arc::new(self.mpq_collection.read_file(name)?);
            loaded.insert(String::from(name), Arc::clone(&obj));
            obj
        })
    }

    pub fn get_m2(&mut self, name: &str) -> Result<Arc<M2>> {
        self.get_or_load(name)
    }

    pub fn get_image(&mut self, name: &str) -> Result<Arc<Image>> {
        self.get_or_load(name)
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
        let data: Vec<u8> = mpq_col.read_file(fname).unwrap();
        assert!(data.len() > 0);

        let fname = "world/generic/human/passive doodads/bottles/Glass2Bottle02.blp";
        let data: Vec<u8> = mpq_col.read_file(fname).unwrap();
        assert!(data.len() > 0);
    }
}

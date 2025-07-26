use std::{collections::HashMap, fmt, io, path::Path, result::Result as StdResult, sync::Mutex};

use bevy::prelude::*;
use bevy_asset::io::{AssetReader, AssetReaderError, PathStream, VecReader};
use custom_debug::Debug;
use wow_mpq::Archive;

use crate::errors::{Error, Result};

pub fn header_fmt(archives: &Vec<Mutex<Archive>>, f: &mut fmt::Formatter) -> fmt::Result {
    write!(f, "[\n")?;
    for (i, am) in archives.iter().enumerate() {
        if let Ok(a) = am.try_lock() {
            write!(f, "{:?}: {:#?},\n", a.path(), a.header())?;
        } else {
            write!(f, "locked_archive({}),\n", i)?;
        }
    }
    write!(f, "]")?;
    Ok(())
}

#[derive(Debug)]
pub struct MpqCollection {
    #[debug(with = header_fmt)]
    pub archives: Vec<Mutex<Archive>>,

    #[debug(skip)]
    pub file_map: HashMap<String, usize>,
}

fn format_file_name(val: &str) -> String {
    val.to_lowercase().replace("\\", "/")
}

impl MpqCollection {
    pub fn load(paths: &[&Path]) -> Result<MpqCollection> {
        let mut archives = Vec::with_capacity(paths.len());
        let mut file_map = HashMap::new();
        for idx in 0..paths.len() {
            let archive = Mutex::new(wow_mpq::OpenOptions::new().open(&paths[idx])?);
            for file_info in &archive.lock().unwrap().list()? {
                file_map.insert(format_file_name(&file_info.name), idx);
            }
            archives.push(archive);
        }

        Ok(MpqCollection { archives, file_map })
    }

    pub fn read_file(&self, name: &str) -> Result<Vec<u8>> {
        let fname = format_file_name(name);
        let index = self
            .file_map
            .get(&fname)
            .ok_or_else(|| wow_mpq::Error::FileNotFound(fname))?;

        if let Some(archive) = self.archives.get(*index) {
            Ok(archive.lock().unwrap().read_file(name)?)
        } else {
            Err(Error::Generic("self.archives is invalid"))
        }
    }
}

pub struct MpqAssetReader {
    mpq_collection: MpqCollection,
}

impl MpqAssetReader {
    pub fn new(mpq_paths: &[&Path]) -> Self {
        Self {
            mpq_collection: MpqCollection::load(mpq_paths).unwrap(),
        }
    }
}

impl AssetReader for MpqAssetReader {
    async fn read<'a>(&'a self, path: &'a Path) -> StdResult<VecReader, AssetReaderError> {
        let bytes: Vec<u8> = self
            .mpq_collection
            .read_file(&path.to_str().unwrap())
            .map_err(|err| match err {
                Error::MpqError(err) => match err {
                    wow_mpq::Error::Io(err) => err,
                    _ => io::Error::new(io::ErrorKind::Other, err),
                },
                _ => io::Error::new(io::ErrorKind::Other, err),
            })?;

        Ok(VecReader::new(bytes))
    }

    async fn read_meta<'a>(&'a self, path: &'a Path) -> StdResult<VecReader, AssetReaderError> {
        Err(AssetReaderError::NotFound(path.into()))
    }

    async fn read_directory<'a>(
        &'a self,
        _path: &'a Path,
    ) -> StdResult<Box<PathStream>, AssetReaderError> {
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "unsupported operation read_directory",
        )
        .into())
    }

    async fn is_directory<'a>(&'a self, _path: &'a Path) -> StdResult<bool, AssetReaderError> {
        Ok(false)
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
        let data: Vec<u8> = mpq_col.read_file(fname).unwrap();
        assert!(data.len() > 0);

        let fname = "world/generic/human/passive doodads/bottles/Glass2Bottle02.blp";
        let data: Vec<u8> = mpq_col.read_file(fname).unwrap();
        assert!(data.len() > 0);
    }
}

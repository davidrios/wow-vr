/*!
   work based on wow.export (https://github.com/Kruithne/wow.export)
   by Authors: Kruithne <kruithne@gmail.com>, Marlamin <marlamin@marlamin.com>
   Licensed on MIT
*/

use std::io::{self, Cursor};

use byteorder::{LittleEndian, ReadBytesExt};

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("Invalid MPQ file")]
    InvalidFile,

    #[error("Invalid magic {0}")]
    InvalidMagic(u32),

    #[error("io error")]
    Io(#[from] io::Error),
}

#[repr(u32)]
#[derive(Debug, Copy, Clone)]
enum ChunkType {
    SFID = 0x44494653,
    TXID = 0x44495854,
    SKID = 0x44494B53,
    BFID = 0x44494642,
    AFID = 0x44494641,
}

#[repr(u32)]
#[derive(Debug, PartialEq, Eq)]
pub enum Magic {
    MD21 = 0x3132444D,
    MD20 = 0x3032444D,
}

impl TryFrom<u32> for Magic {
    type Error = Error;

    fn try_from(value: u32) -> Result<Self, Error> {
        match value {
            value if Self::MD20 as u32 == value => Ok(Self::MD20),
            value if Self::MD21 as u32 == value => Ok(Self::MD21),
            _ => Err(Error::InvalidMagic(value)),
        }
    }
}

#[derive(Debug)]
pub struct Loader {
    data: Vec<u8>,
    file_id: String,
    is_loaded: bool,
}

impl Loader {
    pub fn new(data: Vec<u8>, file_id: String) -> Self {
        Self {
            data,
            file_id,
            is_loaded: false,
        }
    }
    // async load() {
    pub fn load(self: &mut Self) -> Result<(), Error> {
        // Prevent multiple loading of the same M2.
        // if (this.isLoaded === true)
        // 	return;
        if self.is_loaded {
            return Ok(());
        }

        let mut data_c = Cursor::new(&self.data);
        //
        // const magic = this.data.readUInt32LE();
        let magic: Magic = data_c.read_u32::<LittleEndian>()?.try_into()?;
        // if (magic === constants.MAGIC.MD20) {
        if magic == Magic::MD20 {
            // 	this.data.seek(0);
            // 	await this.parseChunk_MD21();
            self.parse_chunk_md21(&mut data_c)?;
            // 	this.parseRestMD20();
            self.parse_rest_md20(&mut data_c)?;
            // } else {
        } else {
            // 	this.data.seek(0);
            //
            // 	while (this.data.remainingBytes > 0) {
            // 		const chunkID = this.data.readUInt32LE();
            // 		const chunkSize = this.data.readUInt32LE();
            // 		const nextChunkPos = this.data.offset + chunkSize;
            //
            // 		switch (chunkID) {
            // 			case constants.MAGIC.MD21: await this.parseChunk_MD21(); break;
            // 			case CHUNK_SFID: this.parseChunk_SFID(chunkSize); break;
            // 			case CHUNK_TXID: this.parseChunk_TXID(); break;
            // 			case CHUNK_SKID: this.parseChunk_SKID(); break;
            // 			case CHUNK_BFID: this.parseChunk_BFID(chunkSize); break;
            // 			case CHUNK_AFID: this.parseChunk_AFID(chunkSize); break;
            // 		}
            //
            // 		// Ensure that we start at the next chunk exactly.
            // 		this.data.seek(nextChunkPos);
            // 	}
        }

        self.is_loaded = true;
        Ok(())
    }

    // async parseChunk_MD21() {
    fn parse_chunk_md21(self: &Self, cursor: &mut Cursor<&Vec<u8>>) -> Result<(), Error> {
        // const ofs = this.data.offset;
        let ofs = cursor.position();
        //
        // const magic = this.data.readUInt32LE();
        // if (magic !== constants.MAGIC.MD20)
        // 	throw new Error('Invalid M2 magic: ' + magic);
        //
        // this.version = this.data.readUInt32LE();
        // this.parseChunk_MD21_modelName(ofs);
        // this.flags = this.data.readUInt32LE();
        // this.parseChunk_MD21_globalLoops(ofs);
        // this.parseChunk_MD21_animations(ofs);
        // this.parseChunk_MD21_animationLookup(ofs);
        // this.parseChunk_MD21_bones(ofs);
        // this.data.move(8);
        // this.parseChunk_MD21_vertices(ofs);
        // this.viewCount = this.data.readUInt32LE();
        // this.parseChunk_MD21_colors(ofs);
        // this.parseChunk_MD21_textures(ofs);
        // this.parseChunk_MD21_textureWeights(ofs);
        // this.parseChunk_MD21_textureTransforms(ofs);
        // this.parseChunk_MD21_replaceableTextureLookup(ofs);
        // this.parseChunk_MD21_materials(ofs);
        // this.data.move(2 * 4); // boneCombos
        // this.parseChunk_MD21_textureCombos(ofs);
        // this.data.move(8); // textureTransformBoneMap
        // this.parseChunk_MD21_transparencyLookup(ofs);
        // this.parseChunk_MD21_textureTransformLookup(ofs);
        // this.parseChunk_MD21_collision(ofs);
        // this.parseChunk_MD21_attachments(ofs);
        // // this.data.move(8); // attachmentIndicesByID / attachment_lookup_table
        // // this.data.move(8); // events
        // // this.data.move(8); // lights
        // // this.data.move(8); // cameras
        // // this.data.move(8); // camera_lookup_table
        // // this.data.move(8); // ribbon_emitters
        // // this.data.move(8); // particle_emitters
        //
        // // // if 0x8 is set, textureCombinerCombos
        // // if (this.flags & 0x8)
        // // 	this.data.move(8);

        Ok(())
    }

    fn parse_rest_md20(self: &Self, cursor: &mut Cursor<&Vec<u8>>) -> Result<(), Error> {
        // const listfile = core.view.casc.listfile;
        // let baseName = listfile.getByID(this.fileID);
        // baseName = baseName.substring(0, baseName.length - 3);
        //
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
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use crate::mpq::MPQFile;

    use super::*;

    #[test]
    fn parse_m2() {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("Data")
            .join("common-2.MPQ");

        let mut mpq_file = MPQFile::new(&path);
        mpq_file.load().unwrap();

        let data = mpq_file
            .read_file(
                "World\\AZEROTH\\BOOTYBAY\\PASSIVEDOODAD\\BOOTYENTRANCE\\BootyBayEntrance_02.m2",
            )
            .unwrap();

        let m2 = Loader::new(data, "".to_string());

        dbg!(m2.data.len());

        assert_eq!(1, 0);
    }
}

/*!
   work based on wow.export (https://github.com/Kruithne/wow.export)
   by Authors: Kruithne <kruithne@gmail.com>, Marlamin <marlamin@marlamin.com>
   Licensed on MIT
*/

use std::{
    io::{self, Cursor},
    string::FromUtf8Error,
};

use byteorder::{LittleEndian, ReadBytesExt};
use num_enum::{TryFromPrimitive, TryFromPrimitiveError};
use once_cell::race::OnceBox;

use super::md20;

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("File not loaded, load it first")]
    NotLoaded,

    #[error("Invalid magic {0:#X}")]
    InvalidMagicValue(u32),

    #[error("Invalid magic {0}")]
    InvalidMagic(#[from] TryFromPrimitiveError<Magic>),

    #[error("Invalid chunk type {0}")]
    InvalidChunkType(#[from] TryFromPrimitiveError<ChunkType>),

    #[error("Cell is full")]
    FullCell,

    #[error("io error")]
    Io(#[from] io::Error),

    #[error("UTF8 conversion error")]
    FromUtf8Error(#[from] FromUtf8Error),
}

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
pub struct Loader {
    raw_data: Vec<u8>,
    file_id: String,
    is_loaded: bool,
    data: OnceBox<md20::Data>,
}

impl Loader {
    pub fn new(raw_data: Vec<u8>, file_id: String) -> Self {
        Self {
            raw_data,
            file_id,
            is_loaded: false,
            data: OnceBox::new(),
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

        let mut data_c = Cursor::new(&self.raw_data);
        //
        // const magic = this.data.readUInt32LE();
        let chunk_id: ChunkType = data_c.read_u32::<LittleEndian>()?.try_into()?;
        dbg!(&chunk_id);
        // if (magic === constants.MAGIC.MD20) {
        if chunk_id == ChunkType::MD20 {
            // 	this.data.seek(0);
            data_c.set_position(0);
            // 	await this.parseChunk_MD21();
            if !self.data.set(md20::parse_chunk(&mut data_c)?).is_ok() {
                return Err(Error::FullCell);
            }
            // 	this.parseRestMD20();
            let rest = md20::parse_rest(&mut data_c, &self.file_id)?;
            // } else {
        } else {
            // 	this.data.seek(0);
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
                    ChunkType::MD20 => {}
                    // 			case constants.MAGIC.MD21: await this.parseChunk_MD21(); break;
                    ChunkType::MD21 => {
                        let md21 = md20::parse_chunk(cursor);
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
            }
        }

        self.is_loaded = true;
        Ok(())
    }

    pub fn data(self: &mut Self) -> Result<&md20::Data, Error> {
        if let Some(val) = self.data.get() {
            Ok(val)
        } else {
            Err(Error::NotLoaded)
        }

        // let val = self.data.get();
        // if let Some(val) = val {
        //     return Ok(val);
        // } else {
        //     // return Ok(self.data.get().as_ref());
        //     return Err(Error::InvalidFile);
        // }
        // match &self.data.get() {
        //     Some(val) => Ok(val),
        //     None => {
        //         self.load()?;
        //         Ok(self.data.get().unwrap())
        //     }
        // }
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use crate::mpq;

    use super::*;

    #[test]
    fn parse_m2() {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("Data")
            .join("common-2.MPQ");

        let mpq_file = mpq::load(&path).unwrap();

        let fname =
            "World\\AZEROTH\\BOOTYBAY\\PASSIVEDOODAD\\BOOTYENTRANCE\\BootyBayEntrance_02.m2";
        let data = mpq_file.read_file(fname).unwrap();

        let mut m2 = Loader::new(data, fname.to_string());

        dbg!(m2.raw_data.len());

        m2.load().unwrap();

        let md20_data = m2.data.get().unwrap();
        assert_eq!(md20_data.model_name(), "BootyBayEntrance_02");

        assert_eq!(1, 0);
    }
}

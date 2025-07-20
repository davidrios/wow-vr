/*!
   work based on wow.export (https://github.com/Kruithne/wow.export)
   by Authors: Kruithne <kruithne@gmail.com>, Marlamin <marlamin@marlamin.com>
   Licensed on MIT
*/

use std::{
    io::{self, Cursor},
    string::FromUtf8Error,
    sync::Arc,
};

use byteorder::{LittleEndian, ReadBytesExt};
use custom_debug::Debug;
use num_enum::{TryFromPrimitive, TryFromPrimitiveError};

use crate::{common_types, utils};

use super::md20;

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("File not loaded, load it first")]
    NotLoaded,

    #[error("Corrupted file")]
    CorruptedFile,

    #[error("Invalid magic {0:#X}")]
    InvalidMagicValue(u32),

    #[error("Type conversion error {0}")]
    TypeConversionError(#[from] common_types::Error),

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
pub struct M2 {
    #[debug(with = utils::buf_len_fmt)]
    raw_data: Arc<Vec<u8>>,
    file_id: String,
    data: Box<md20::Data>,
}

pub fn load(raw_data: Vec<u8>, file_id: String) -> Result<M2, Error> {
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

    Ok(M2 {
        raw_data,
        file_id,
        data: data.ok_or(Error::CorruptedFile)?,
    })
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

        let mut m2 = load(data, fname.to_string()).unwrap();

        dbg!(m2.raw_data.len());

        dbg!(&m2);
        assert_eq!(m2.data.model_name(), "BootyBayEntrance_02");

        assert_eq!(1, 0);
    }
}

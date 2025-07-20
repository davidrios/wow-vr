use std::io::{self, Cursor};

use byteorder::{LittleEndian, ReadBytesExt};

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("io error")]
    Io(#[from] io::Error),
}

#[derive(Debug)]
pub struct C3Vector {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

impl C3Vector {
    pub fn from(reader: &mut Cursor<&Vec<u8>>) -> Result<Self, Error> {
        Ok(Self {
            x: reader.read_f32::<LittleEndian>()?,
            y: reader.read_f32::<LittleEndian>()?,
            z: reader.read_f32::<LittleEndian>()?,
        })
    }
}

#[derive(Debug)]
pub struct CAaBox {
    pub min: C3Vector,
    pub max: C3Vector,
}

impl CAaBox {
    pub fn from(mut reader: &mut Cursor<&Vec<u8>>) -> Result<Self, Error> {
        Ok(Self {
            min: C3Vector::from(&mut reader)?,
            max: C3Vector::from(&mut reader)?,
        })
    }
}

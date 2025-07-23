use std::{io, string::FromUtf8Error};

use num_enum::TryFromPrimitiveError;

use crate::{common_types, m2, mpq};

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("MpqError")]
    MpqError(#[from] wow_mpq::Error),

    #[error("M2Error")]
    M2Error(#[from] wow_m2::M2Error),

    #[error("Invalid file")]
    InvalidFile,

    #[error("File not loaded, load it first")]
    NotLoaded,

    #[error("Corrupted file")]
    CorruptedFile,

    #[error("Error decoding value")]
    Decode,

    #[error("Generic error: {0}")]
    Generic(&'static str),

    #[error("File not found in hash table")]
    FileNotFound,

    #[error("Invalid magic {0:#X}")]
    InvalidMagicValue(u32),

    #[error("Invalid compression type {0}")]
    InvalidCompressionType(#[from] TryFromPrimitiveError<mpq::CompressionType>),

    #[error("Unsupported compression {0:?}")]
    UnsupportedCompression(mpq::CompressionType),

    #[error("Unknown compression {0}")]
    UnknownCompression(u8),

    #[error("Type conversion error {0}")]
    TypeConversionError(#[from] common_types::Error),

    #[error("Invalid M2 magic {0}")]
    InvalidM2Magic(#[from] TryFromPrimitiveError<m2::Magic>),

    #[error("Invalid M2 chunk type {0}")]
    InvalidM2ChunkType(#[from] TryFromPrimitiveError<m2::ChunkType>),

    #[error("io error")]
    Io(#[from] io::Error),

    #[error("UTF8 conversion error")]
    FromUtf8Error(#[from] FromUtf8Error),
}

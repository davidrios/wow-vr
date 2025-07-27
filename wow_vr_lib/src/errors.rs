use std::{io, string::FromUtf8Error};

use bevy_asset::ReadAssetBytesError;

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("MpqError")]
    MpqError(#[from] wow_mpq::Error),

    #[error("M2Error")]
    M2Error(#[from] wow_m2::M2Error),

    #[error("DdsError")]
    DdsError(#[from] ddsfile::Error),

    #[error("BevyTextureError")]
    BevyTextureError(#[from] bevy_image::TextureError),

    #[error("Asset not found {0}")]
    AssetNotFound(String),

    #[error("Generic error: {0}")]
    Generic(&'static str),

    #[error("Unsupported asset label: {0}")]
    UnsupportedAssetLabel(String),

    #[error("Read error: {0}")]
    ReadAssetBytesError(#[from] ReadAssetBytesError),

    #[error("io error")]
    Io(#[from] io::Error),

    #[error("UTF8 conversion error")]
    FromUtf8Error(#[from] FromUtf8Error),
}

pub type Result<T> = std::result::Result<T, Error>;

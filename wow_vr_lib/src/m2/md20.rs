use std::io::{Cursor, Read};

use bitflags::bitflags;
use byteorder::{LittleEndian, ReadBytesExt};

use super::{Error, Magic};

bitflags! {
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct Flags: u32 {
        const ANIM_IS_CHUNKED = 0x200000;
    }
}

#[derive(Debug)]
pub struct Data {
    version: u32,
    model_name: String,
    flags: Flags,
}

impl Data {
    pub fn version(&self) -> u32 {
        self.version
    }

    pub fn model_name(&self) -> &str {
        &self.model_name
    }

    pub fn flags(&self) -> &Flags {
        &self.flags
    }
}

pub fn parse_chunk(data_c: &mut Cursor<&Vec<u8>>) -> Result<Box<Data>, Error> {
    // const ofs = this.data.offset;
    let ofs = data_c.position();
    //
    // const magic = this.data.readUInt32LE();
    let magic: Magic = data_c.read_u32::<LittleEndian>()?.try_into()?;
    // if (magic !== constants.MAGIC.MD20)
    // 	throw new Error('Invalid M2 magic: ' + magic);
    if magic != Magic::MD20 {
        return Err(Error::InvalidMagicValue(magic as u32));
    }

    // this.version = this.data.readUInt32LE();
    let version = data_c.read_u32::<LittleEndian>()?;
    // this.parseChunk_MD21_modelName(ofs);
    let name = parse_model_name(data_c, ofs)?;
    // this.flags = this.data.readUInt32LE();
    let flags = Flags::from_bits_retain(data_c.read_u32::<LittleEndian>()?);
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

    Ok(Box::new(Data {
        version,
        model_name: name,
        flags,
    }))
}

// parseChunk_MD21_modelName(ofs) {
pub fn parse_model_name(data_c: &mut Cursor<&Vec<u8>>, chunk_ofs: u64) -> Result<String, Error> {
    // const modelNameLength = this.data.readUInt32LE();
    let model_name_length = data_c.read_u32::<LittleEndian>()? as usize;
    // const modelNameOfs = this.data.readUInt32LE();
    let model_name_ofs = data_c.read_u32::<LittleEndian>()? as u64;
    //
    // const base = this.data.offset;
    let base = data_c.position();
    // this.data.seek(modelNameOfs + ofs);
    data_c.set_position(chunk_ofs + model_name_ofs);
    //
    // // Always followed by single 0x0 character, -1 to trim).
    // this.data.seek(modelNameOfs + ofs);
    // this.name = this.data.readString(modelNameLength - 1);
    let mut name_buf = vec![0; model_name_length];
    data_c.read_exact(&mut name_buf)?;
    if name_buf.last() == Some(&0) {
        name_buf.pop();
    }
    let model_name = String::from_utf8(name_buf)?;
    //
    // this.data.seek(base);
    data_c.set_position(base);
    Ok(model_name)
}

// async parseChunk
pub fn parse_rest(data_c: &mut Cursor<&Vec<u8>>, fname: &str) -> Result<(), Error> {
    // const listfile = core.view.casc.listfile;
    // let baseName = listfile.getByID(this.fileID);
    // baseName = baseName.substring(0, baseName.length - 3);
    let base_name = &fname[..fname.len() - 3];
    dbg!(base_name);
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

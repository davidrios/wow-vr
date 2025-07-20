use bitflags::bitflags;
use byteorder::{LittleEndian, ReadBytesExt, WriteBytesExt};
use flate2::read::ZlibDecoder;
use std::{
    collections::HashMap,
    fs,
    io::{self, Cursor, Read, Seek, SeekFrom, Write},
    os::unix::fs::FileExt,
    path::{Path, PathBuf},
    string::FromUtf8Error,
};

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("Invalid MPQ file")]
    InvalidFile,

    #[error("File not loaded, load it first")]
    NotLoaded,

    #[error("Error decoding value")]
    Decode,

    #[error("File not found in hash table")]
    FileNotFound,

    #[error("Generic error: {0}")]
    Generic(&'static str),

    #[error("Unsupported compression {0:?}")]
    UnsupportedCompression(CompressionType),

    #[error("Unknown compression {0}")]
    UnknownCompression(u8),

    #[error("io error")]
    Io(#[from] io::Error),

    #[error("UTF8 conversion error")]
    FromUtf8Error(#[from] FromUtf8Error),
}

static ENCRYPTION_TABLE: once_cell::sync::Lazy<Vec<u32>> = once_cell::sync::Lazy::new(|| {
    let mut seed = 0x00100001;
    let mut temp1: u32;
    let mut temp2: u32;
    let mut table = vec![0u32; 256 * 5];

    for i in 0..256 {
        let mut index = i;
        for _ in 0..5 {
            seed = (seed * 125 + 3) % 0x2AAAAB;
            temp1 = (seed & 0xFFFF) << 0x10;

            seed = (seed * 125 + 3) % 0x2AAAAB;
            temp2 = seed & 0xFFFF;

            table[index] = temp1 | temp2;

            index += 0x100;
        }
    }

    table
});

fn decrypt(data: &[u8], key: u32) -> Result<Vec<u8>, Error> {
    let mut data_c = Cursor::new(data);
    let mut seed1 = key as u64;
    let mut seed2 = 0xEEEEEEEE;
    let mut value: u64;
    let mut result = vec![0u8; data.len()];
    let mut result_c = Cursor::new(&mut result);
    for _ in 0..data.len() / 4 {
        seed2 += ENCRYPTION_TABLE[(0x400 + (seed1 & 0xFF)) as usize] as u64;
        seed2 &= 0xFFFFFFFF;
        value = data_c.read_u32::<LittleEndian>()? as u64;
        value = (value ^ (seed1 + seed2)) & 0xFFFFFFFF;
        seed1 = ((!seed1 << 0x15) + 0x11111111) | (seed1 >> 0x0b);
        seed1 &= 0xFFFFFFFF;
        seed2 = value + seed2 + (seed2 << 5) + 3 & 0xFFFFFFFF;
        result_c.write_u32::<LittleEndian>(value as u32)?;
    }
    Ok(result)
}

#[derive(Debug, Copy, Clone)]
pub enum CompressionType {
    Huffmann = 0x01,
    Zlib = 0x02,
    Pkware = 0x08,
    Bzip2 = 0x10,
    Sparse = 0x20,
    AdpcmMono = 0x40,
    AdpcmStereo = 0x80,
}

impl TryFrom<u8> for CompressionType {
    type Error = Error;

    fn try_from(value: u8) -> Result<Self, Error> {
        match value {
            0x01 => Ok(CompressionType::Huffmann),
            0x02 => Ok(CompressionType::Zlib),
            0x08 => Ok(CompressionType::Pkware),
            0x10 => Ok(CompressionType::Bzip2),
            0x20 => Ok(CompressionType::Sparse),
            0x40 => Ok(CompressionType::AdpcmMono),
            0x80 => Ok(CompressionType::AdpcmStereo),
            _ => Err(Error::UnknownCompression(value)),
        }
    }
}

fn decompress(compression_type: CompressionType, data: &[u8]) -> Result<Vec<u8>, Error> {
    match compression_type {
        CompressionType::Zlib => {
            let mut decoder = ZlibDecoder::new(data);
            let mut decompressed = Vec::new();
            decoder.read_to_end(&mut decompressed)?;
            Ok(decompressed)
        }
        _ => Err(Error::UnsupportedCompression(compression_type)),
    }
}

enum HashType {
    TableOffset,
    HashA,
    HashB,
    Table,
}

fn hash(string: &str, hash_type: HashType) -> Result<u32, Error> {
    let mut seed1 = 0x7FED7FED;
    let mut seed2 = 0xEEEEEEEE;
    let mut value: u64;
    let string_u = string.to_uppercase();
    let string_b = string_u.as_bytes();
    let hash_type = hash_type as u32;
    for i in 0..string_b.len() {
        let ch = string_b[i];
        value = ENCRYPTION_TABLE[((hash_type << 8) + ch as u32) as usize] as u64;
        seed1 = (value ^ (seed1 + seed2)) & 0xFFFFFFFF;
        seed2 = ch as u64 + seed1 + seed2 + (seed2 << 5) + 3 & 0xFFFFFFFF;
    }
    Ok(seed1 as u32)
}

#[derive(Debug)]
struct HashTableEntry {
    hash_a: u32,
    hash_b: u32,
    locale: u16,
    platform: u16,
    block_table_index: u32,
}

impl HashTableEntry {
    fn from(data: &[u8]) -> Result<Self, Error> {
        let mut reader = Cursor::new(data);
        Ok(Self {
            hash_a: reader.read_u32::<LittleEndian>()?,
            hash_b: reader.read_u32::<LittleEndian>()?,
            locale: reader.read_u16::<LittleEndian>()?,
            platform: reader.read_u16::<LittleEndian>()?,
            block_table_index: reader.read_u32::<LittleEndian>()?,
        })
    }
}

#[derive(Debug)]
struct BlockTableEntry {
    offset: u32,
    archive_size: u32,
    size: u32,
    flags: u32,
}

impl BlockTableEntry {
    fn from(data: &[u8]) -> Result<Self, Error> {
        let mut reader = Cursor::new(data);
        Ok(Self {
            offset: reader.read_u32::<LittleEndian>()?,
            archive_size: reader.read_u32::<LittleEndian>()?,
            size: reader.read_u32::<LittleEndian>()?,
            flags: reader.read_u32::<LittleEndian>()?,
        })
    }
}

fn hash_table_key(hash_a: u32, hash_b: u32) -> String {
    format!("{}-{}", hash_a.to_string(), hash_b.to_string())
}

#[derive(Debug, PartialEq)]
pub struct FileHeader {
    pub header_size: u32,
    pub archive_size: u32,
    pub format_version: u16,
    pub sector_shift: u16,
    pub hash_table_offset: u32,
    pub block_table_offset: u32,
    pub hash_table_entries: u32,
    pub block_table_entries: u32,
    pub extended_block_table_offset: Option<u64>,
    pub hash_table_offset_high: Option<u16>,
    pub hash_table_offset_low: Option<u16>,
}

const MAGIC_STRING: &[u8; 4] = b"MPQ\x1a";

bitflags! {
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    struct BlockFlags: u32 {
        const COMPRESS = 0x00000200;
        const ENCRYPTED = 0x00010000;
        const SINGLE_UNIT = 0x01000000;
        const SECTOR_CRC = 0x04000000;
        const EXISTS = 0x80000000;
    }
}

pub struct MPQFile {
    file_path: PathBuf,
    header: Option<FileHeader>,
    hash_table: Option<HashMap<String, HashTableEntry>>,
    block_table: Option<Vec<BlockTableEntry>>,
}

impl MPQFile {
    pub fn new(file_path: &Path) -> MPQFile {
        MPQFile {
            file_path: file_path.to_owned(),
            header: None,
            hash_table: None,
            block_table: None,
        }
    }

    pub fn load(self: &mut Self) -> Result<(), Error> {
        if self.header.is_some() {
            return Ok(());
        }

        let mut reader = fs::OpenOptions::new().read(true).open(&self.file_path)?;

        let mut magic_buf = [0u8; 4];
        reader.read_exact(&mut magic_buf)?;
        if &magic_buf != MAGIC_STRING {
            return Err(Error::InvalidFile);
        }

        let mut header = FileHeader {
            header_size: reader.read_u32::<LittleEndian>()?,
            archive_size: reader.read_u32::<LittleEndian>()?,
            format_version: reader.read_u16::<LittleEndian>()?,
            sector_shift: reader.read_u16::<LittleEndian>()?,
            hash_table_offset: reader.read_u32::<LittleEndian>()?,
            block_table_offset: reader.read_u32::<LittleEndian>()?,
            hash_table_entries: reader.read_u32::<LittleEndian>()?,
            block_table_entries: reader.read_u32::<LittleEndian>()?,
            extended_block_table_offset: None,
            hash_table_offset_high: None,
            hash_table_offset_low: None,
        };
        if header.format_version == 1 {
            header.extended_block_table_offset = Some(reader.read_u64::<LittleEndian>()?);
            header.hash_table_offset_high = Some(reader.read_u16::<LittleEndian>()?);
            header.hash_table_offset_low = Some(reader.read_u16::<LittleEndian>()?);
        }
        self.header = Some(header);

        self.read_hash_table()?;
        self.read_block_table()?;

        Ok(())
    }

    fn read_hash_table(self: &mut Self) -> Result<(), Error> {
        let key = hash("(hash table)", HashType::Table);
        let Some(ref header) = self.header else {
            return Err(Error::NotLoaded);
        };

        let reader = fs::OpenOptions::new().read(true).open(&self.file_path)?;
        let mut data_e = vec![0u8; header.hash_table_entries as usize * 16];
        reader.read_exact_at(&mut data_e, header.hash_table_offset as u64)?;
        let data = decrypt(&data_e, key?)?;

        let mut res: HashMap<String, HashTableEntry> =
            HashMap::with_capacity(header.hash_table_entries as usize);

        for i in 0..header.hash_table_entries {
            let start = i as usize * 16;
            let entry = HashTableEntry::from(&data[start..start + 16])?;
            let key = hash_table_key(entry.hash_a, entry.hash_b);
            res.insert(key, entry);
        }

        self.hash_table = Some(res);

        Ok(())
    }

    fn read_block_table(self: &mut Self) -> Result<(), Error> {
        let key = hash("(block table)", HashType::Table);
        let Some(ref header) = self.header else {
            return Err(Error::NotLoaded);
        };

        let reader = fs::OpenOptions::new().read(true).open(&self.file_path)?;
        let mut data_e = vec![0u8; header.block_table_entries as usize * 16];
        reader.read_exact_at(&mut data_e, header.block_table_offset as u64)?;
        let data = decrypt(&data_e, key?)?;

        let mut res: Vec<BlockTableEntry> = Vec::with_capacity(header.block_table_entries as usize);

        for i in 0..header.block_table_entries {
            let start = i as usize * 16;
            let entry = BlockTableEntry::from(&data[start..start + 16])?;
            res.push(entry);
        }
        self.block_table = Some(res);
        Ok(())
    }

    fn get_hash_table_entry(self: &Self, filename: &str) -> Result<&HashTableEntry, Error> {
        let hash_a = hash(filename, HashType::HashA)?;
        let hash_b = hash(filename, HashType::HashB)?;

        let Some(ref hash_table) = self.hash_table else {
            return Err(Error::NotLoaded);
        };

        if let Some(entry) = hash_table.get(&hash_table_key(hash_a, hash_b)) {
            Ok(entry)
        } else {
            Err(Error::FileNotFound)
        }
    }

    pub fn read_file(self: &Self, filename: &str) -> Result<Vec<u8>, Error> {
        let entry = self.get_hash_table_entry(filename)?;
        let (Some(header), Some(block_table)) = (&self.header, &self.block_table) else {
            return Err(Error::NotLoaded);
        };

        let block = &block_table[entry.block_table_index as usize];

        let block_flags = BlockFlags::from_bits_retain(block.flags);
        if !block_flags.contains(BlockFlags::EXISTS) {
            return Err(Error::Generic("no file exists flag"));
        }
        if block_flags.contains(BlockFlags::ENCRYPTED) {
            return Err(Error::Generic("encryption not supported"));
        }

        if block.archive_size == 0 {
            return Ok(vec![]);
        }
        let reader = fs::OpenOptions::new().read(true).open(&self.file_path)?;
        let mut raw_data = vec![0u8; block.archive_size as usize];
        reader.read_exact_at(&mut raw_data, block.offset as u64)?;

        //
        // const isCompressed = (blockEntry.flags & MPQ_FILE_COMPRESS) === MPQ_FILE_COMPRESS;
        let is_compressed = block_flags.contains(BlockFlags::COMPRESS);

        if block_flags.contains(BlockFlags::SINGLE_UNIT) {
            return Ok(if is_compressed && block.size > block.archive_size {
                decompress(raw_data[0].try_into()?, &raw_data[1..])?
            } else {
                raw_data
            });
        }
        //
        // const sectorSize = BigInt(512) << BigInt(this.header.sectorShift);
        let sector_size = 512 << header.sector_shift;
        // let sectors = Number(BigInt(blockEntry.size) / sectorSize) + 1;
        let sectors = (block.size as usize / sector_size) + 1;
        //
        // const positions = [];
        let mut data_c = Cursor::new(&raw_data);
        let mut positions = Vec::with_capacity(sectors + 1);
        // for (let i = 0; i < sectors + 1; i++)
        // 	positions.push(fileData.readUInt32LE());
        for _ in 0..sectors + 1 {
            positions.push(data_c.read_u32::<LittleEndian>()?);
        }
        let mut res = Vec::with_capacity(block.size as usize);
        let mut res_c = Cursor::new(&mut res);
        //
        // const result = BufferWrapper.alloc(blockEntry.size);
        let mut compr_algo: (u8, Option<CompressionType>) = (0, None);
        // for (let i = 0; i < sectors; i++) {
        for i in 0..sectors {
            // 	fileData.seek(positions[i]);
            data_c.seek(SeekFrom::Start(positions[i] as u64))?;
            // 	let sector = fileData.readBuffer(positions[i + 1] - positions[i]);
            let size = positions[i + 1] - positions[i];
            let mut sector = vec![0; size as usize];
            data_c.read_exact(&mut sector)?;
            // 	if (isCompressed && sector.byteLength > 0 && blockEntry.size > blockEntry.archiveSize) {
            if is_compressed && size > 0 && block.size > block.archive_size {
                if compr_algo.0 == 0 {
                    compr_algo.0 = sector[0];
                    compr_algo.1 = sector[0].try_into().ok();
                }

                if compr_algo.0 == sector[0] {
                    if let Some(algo) = compr_algo.1 {
                        sector = decompress(algo, &sector[1..])?;
                    }
                }
            }

            res_c.write_all(&sector)?;
        }
        // result.seek(0);
        // return result;
        Ok(res)
    }

    pub fn get_file_list(self: &mut Self) -> Result<Vec<String>, Error> {
        let buf = self.read_file("(listfile)")?;
        buf.split(|&byte| byte == b'\n')
            .map(|part| {
                if part.len() == 0 {
                    Ok("".to_string())
                } else {
                    String::from_utf8(part[0..part.len() - 1].to_vec()).map_err(Error::from)
                }
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;

    #[test]
    fn encryption_table_is_correct() {
        assert_eq!(ENCRYPTION_TABLE[65], 1285056048);
        assert_eq!(ENCRYPTION_TABLE[806], 1010723398);
        assert_eq!(ENCRYPTION_TABLE[1279], 1929586796);
    }

    #[test]
    fn hash_works() {
        assert_eq!(hash("(hash table)", HashType::Table).unwrap(), 3283040112);
        assert_eq!(
            hash("THE QUICK BROWN FOX", HashType::Table).unwrap(),
            4192734097
        );
    }

    #[test]
    fn test_decrypt_works() {
        let data: [u8; 16] = [
            51, 120, 177, 93, 195, 125, 252, 226, 88, 231, 123, 79, 46, 102, 8, 227,
        ];
        assert_eq!(
            decrypt(&data, 3283040112).unwrap(),
            [
                255, 183, 141, 219, 20, 163, 166, 161, 0, 0, 0, 0, 45, 83, 0, 0
            ]
        );
    }

    #[test]
    fn mpq_works() {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("tests")
            .join("fixtures")
            .join("test.mpq");

        let mut mpq_file = MPQFile::new(&path);
        mpq_file.load().unwrap();
        dbg!(&mpq_file.header);
        let entry = mpq_file.get_hash_table_entry("(listfile)").unwrap();
        dbg!(entry);
        let block = &mpq_file.block_table.as_ref().unwrap()[entry.block_table_index as usize];
        dbg!(block);
        dbg!(BlockFlags::from_bits_retain(block.flags));
        dbg!(
            mpq_file
                .get_file_list()
                .unwrap()
                .iter()
                .take(20)
                .collect::<Vec<_>>()
        );

        dbg!(
            mpq_file
                .get_hash_table_entry("Character\\BROKEN\\HAIR00_09.BLP")
                .unwrap()
        );

        // assert_eq!(0, 1);
    }
}

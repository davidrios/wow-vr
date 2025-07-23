use std::{cmp, fmt, io::Cursor, sync::Arc};

use bevy::math::{U8Vec4, Vec2, Vec3};
use byteorder::{LittleEndian, ReadBytesExt};

use crate::errors::Error;

pub fn hex_fmt<T: fmt::Debug>(n: &T, f: &mut fmt::Formatter) -> fmt::Result {
    write!(f, "0x{:02X?}", n)
}

pub trait HasLength {
    type Item: fmt::Debug;

    fn len2(&self) -> usize;
    fn first_three(&self) -> &[Self::Item];
}

impl<T: fmt::Debug> HasLength for &[T] {
    type Item = T;
    fn len2(&self) -> usize {
        self.len()
    }
    fn first_three(&self) -> &[Self::Item] {
        let end = cmp::min(3, self.len());
        &self[..end]
    }
}

impl<T: fmt::Debug> HasLength for Vec<T> {
    type Item = T;
    fn len2(&self) -> usize {
        self.len()
    }
    fn first_three(&self) -> &[Self::Item] {
        let end = cmp::min(3, self.len());
        &self[..end]
    }
}

impl<T: ?Sized + HasLength> HasLength for Arc<T> {
    type Item = T::Item;
    fn len2(&self) -> usize {
        self.as_ref().len2()
    }
    fn first_three(&self) -> &[Self::Item] {
        self.as_ref().first_three()
    }
}

pub fn buf_len_fmt<T: HasLength>(n: &T, f: &mut fmt::Formatter) -> fmt::Result {
    let first_three = n.first_three();
    write!(
        f,
        "{:#?} + {} elements",
        first_three,
        cmp::max(0, n.len2() - first_three.len())
    )
}

#[derive(Debug)]
pub struct OffsetSize {
    pub offset: u64,
    pub size: u64,
}

pub fn read_vec3(reader: &mut Cursor<&Vec<u8>>) -> Result<Vec3, Error> {
    Ok(Vec3 {
        x: reader.read_f32::<LittleEndian>()?,
        z: reader.read_f32::<LittleEndian>()? * -1.,
        y: reader.read_f32::<LittleEndian>()?,
    })
}

pub fn read_vec2(reader: &mut Cursor<&Vec<u8>>) -> Result<Vec2, Error> {
    Ok(Vec2 {
        x: reader.read_f32::<LittleEndian>()?,
        y: reader.read_f32::<LittleEndian>()?,
    })
}

pub fn read_u8vec4(reader: &mut Cursor<&Vec<u8>>) -> Result<U8Vec4, Error> {
    Ok(U8Vec4 {
        x: reader.read_u8()?,
        y: reader.read_u8()?,
        z: reader.read_u8()?,
        w: reader.read_u8()?,
    })
}

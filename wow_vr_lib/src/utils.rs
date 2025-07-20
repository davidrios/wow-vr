use std::{any::type_name_of_val, fmt, sync::Arc};

pub fn hex_fmt<T: fmt::Debug>(n: &T, f: &mut fmt::Formatter) -> fmt::Result {
    write!(f, "0x{:02X?}", n)
}

pub trait HasLength {
    fn len2(&self) -> usize;
}

impl HasLength for &[u8] {
    fn len2(&self) -> usize {
        self.len()
    }
}

impl HasLength for Arc<Vec<u8>> {
    fn len2(&self) -> usize {
        self.as_ref().len()
    }
}

pub fn buf_len_fmt<T: HasLength>(n: &T, f: &mut fmt::Formatter) -> fmt::Result {
    write!(f, "[{}; {}]", type_name_of_val(n), n.len2())
}

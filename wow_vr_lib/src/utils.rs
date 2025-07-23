use std::{cmp, fmt, sync::Arc};

const FIRST_N_ELEMENTS: usize = 7;

pub fn hex_fmt<T: fmt::Debug>(n: &T, f: &mut fmt::Formatter) -> fmt::Result {
    write!(f, "0x{:02X?}", n)
}

pub trait HasLength {
    type Item: fmt::Debug;

    fn len2(&self) -> usize;
    fn get_first_n(&self, elements: usize) -> &[Self::Item];
}

impl<T: fmt::Debug> HasLength for &[T] {
    type Item = T;
    fn len2(&self) -> usize {
        self.len()
    }
    fn get_first_n(&self, elements: usize) -> &[Self::Item] {
        let end = cmp::min(elements, self.len());
        &self[..end]
    }
}

impl<T: fmt::Debug> HasLength for Vec<T> {
    type Item = T;
    fn len2(&self) -> usize {
        self.len()
    }
    fn get_first_n(&self, elements: usize) -> &[Self::Item] {
        let end = cmp::min(elements, self.len());
        &self[..end]
    }
}

impl<T: ?Sized + HasLength> HasLength for Arc<T> {
    type Item = T::Item;
    fn len2(&self) -> usize {
        self.as_ref().len2()
    }
    fn get_first_n(&self, elements: usize) -> &[Self::Item] {
        self.as_ref().get_first_n(elements)
    }
}

#[cfg(not(feature = "debug-print-all"))]
pub fn trimmed_collection_fmt<T: HasLength>(n: &T, f: &mut fmt::Formatter) -> fmt::Result {
    let first_three = n.get_first_n(FIRST_N_ELEMENTS);
    write!(
        f,
        "{:#?} + {} elements",
        first_three,
        cmp::max(0, n.len2() - first_three.len())
    )
}

#[cfg(feature = "debug-print-all")]
pub fn trimmed_collection_fmt<T: HasLength + fmt::Debug>(
    n: &T,
    f: &mut fmt::Formatter,
) -> fmt::Result {
    write!(f, "{:#?}", n)
}

use std::{cmp, fmt, sync::Arc};

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

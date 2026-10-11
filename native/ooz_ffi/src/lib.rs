//! C ABI over oozextract: one call, one Oodle block.
use oozextract::Extractor;

/// Decompress `src_len` bytes at `src` into exactly `dst_len` bytes at `dst`.
/// Returns the number of bytes written, or -1 on failure.
#[no_mangle]
pub unsafe extern "C" fn ooz_decompress(src: *const u8, src_len: usize, dst: *mut u8, dst_len: usize) -> i64 {
    if src.is_null() || dst.is_null() {
        return -1;
    }
    let input = std::slice::from_raw_parts(src, src_len);
    let output = std::slice::from_raw_parts_mut(dst, dst_len);
    match Extractor::new().read_from_slice(input, output) {
        Ok(n) => n as i64,
        Err(_) => -1,
    }
}
